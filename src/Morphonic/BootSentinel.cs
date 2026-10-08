using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Morphonic;

// <data dir>/boot.inprogress exists only while a start is under way:
// written before the risky work (native loads, window creation) and
// deleted the moment the page connects. Finding it at the next start means
// the previous start never reached the window — it crashed, or was killed
// — so that start gets the OS crash record copied into error.log
// (CrashRecord) and a safe boot: nothing starts converting until a human
// presses Start. A crash can no longer be silent, and cannot loop.
public static class BootSentinel
{
    // Tests point this at a temp file so they never touch the real marker.
    internal static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? Path.Combine(AppPaths.DataDir, "boot.inprogress");

    public sealed record Unfinished(string Version, DateTime StartedAt, int Pid);

    // Records this start; returns the previous start if it never finished.
    public static Unfinished? Arm(string version)
    {
        Unfinished? previous = null;
        try
        {
            if (File.Exists(FilePath)) previous = Parse(File.ReadAllText(FilePath));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"{version}|{DateTime.Now:o}|{Environment.ProcessId}");
        }
        catch { /* diagnostics must never affect startup */ }
        return previous;
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { }
    }

    // A marker with unreadable contents still means an unfinished start.
    internal static Unfinished Parse(string text)
    {
        var parts = (text ?? "").Trim().Split('|');
        if (parts.Length != 3) return new Unfinished("?", DateTime.MinValue, 0);
        DateTime.TryParse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at);
        int.TryParse(parts[2], out var pid);
        return new Unfinished(parts[0], at, pid);
    }
}

// The operating system keeps the only record of a native crash: Windows'
// Application log ("Application Error" 1000 and ".NET Runtime" 1026
// events, read as language-neutral XML through wevtutil), Linux's
// coredumpctl and the journal. After an unfinished start, the entries
// naming this app are copied into error.log, where a user finds them
// without knowing where the system keeps them.
public static class CrashRecord
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    public static void CollectInBackground(BootSentinel.Unfinished previous)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var since = previous.StartedAt == DateTime.MinValue
                    ? DateTime.Now.AddDays(-1)
                    : previous.StartedAt.AddMinutes(-1);
                string who = $"start of {previous.Version} at {previous.StartedAt:yyyy-MM-dd HH:mm:ss} (pid {previous.Pid}) never reached the window";
                List<string> lines = OperatingSystem.IsWindows()
                    ? FilterXml(QueryWindows(since), "Morphonic")
                    : FilterLines(QueryLinux(since), "Morphonic");
                ErrorLog.WriteNote("PreviousStart", lines.Count == 0
                    ? who + "; no crash record found — it was probably closed or killed before the window loaded"
                    : who + "; crash record:" + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, lines));
            }
            catch (Exception ex) { ErrorLog.WriteEntry("CrashRecord", ex); }
        });
    }

    private static string QueryWindows(DateTime since)
    {
        long windowMs = (long)Math.Clamp((DateTime.Now - since).TotalMilliseconds, 60_000, 7L * 24 * 3600 * 1000);
        var psi = new ProcessStartInfo("wevtutil.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        psi.ArgumentList.Add("qe");
        psi.ArgumentList.Add("Application");
        psi.ArgumentList.Add("/q:*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime']) " +
                             $"and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]");
        psi.ArgumentList.Add("/f:xml");
        psi.ArgumentList.Add("/rd:true");
        psi.ArgumentList.Add("/c:30");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("wevtutil did not start");
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } }
        return output;
    }

    private static string QueryLinux(DateTime since)
    {
        var stamp = since.ToString("yyyy-MM-dd HH:mm:ss");
        var sb = new StringBuilder();
        sb.Append(LinuxHost.Capture("coredumpctl",
            new[] { "list", "--no-pager", "--no-legend", "--since=" + stamp, ProcessComm() }, 10_000));
        sb.Append('\n');
        sb.Append(LinuxHost.Capture("journalctl",
            new[] { "--no-pager", "-q", "-o", "short-iso", "--since=" + stamp, "--grep=Morphonic" }, 10_000));
        return sb.ToString();
    }

    // coredumpctl matches the process name exactly, and the kernel keeps 15
    // bytes of it: "Morphonic-1.0.0-linux-x64" is "Morphonic-1.0.0-lin".
    internal static string ProcessComm(string? processPath = null)
    {
        var name = Path.GetFileName(processPath ?? Environment.ProcessPath ?? "Morphonic");
        if (name.Length == 0) name = "Morphonic";
        return name.Length > 15 ? name[..15] : name;
    }

    // wevtutil emits a bare sequence of <Event> elements. Keeps the events
    // whose data names the app, as "Provider (id) at time" plus the data
    // fields, trimmed to the lines that matter. The System block's computer
    // and account identifiers are never copied.
    internal static List<string> FilterXml(string xml, string appName, int maxLinesPerEvent = 40)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(xml)) return result;
        XDocument doc;
        try { doc = XDocument.Parse("<Events>" + xml + "</Events>"); }
        catch (Exception ex)
        {
            result.Add("    (crash record unreadable: " + ex.Message + ")");
            return result;
        }
        foreach (var ev in doc.Root!.Elements(Ns + "Event"))
        {
            var data = ev.Element(Ns + "EventData")?.Elements(Ns + "Data").ToList() ?? new List<XElement>();
            if (!data.Any(d => d.Value.Contains(appName, StringComparison.OrdinalIgnoreCase))) continue;
            var sys = ev.Element(Ns + "System");
            var provider = sys?.Element(Ns + "Provider")?.Attribute("Name")?.Value ?? "?";
            var id = sys?.Element(Ns + "EventID")?.Value ?? "?";
            var time = sys?.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value ?? "?";
            var lines = new List<string> { $"{provider} ({id}) at {time}" };
            foreach (var d in data)
            {
                var value = d.Value.Trim();
                if (value.Length == 0) continue;
                var name = d.Attribute("Name")?.Value;
                if (name != null) lines.Add($"  {name}: {value}");
                else
                    foreach (var raw in value.Split('\n'))
                    {
                        var line = raw.Trim();
                        if (line.Length > 0) lines.Add("  " + line);
                    }
            }
            result.Add(string.Join(Environment.NewLine, lines.Take(maxLinesPerEvent).Select(l => "    " + l)));
        }
        return result;
    }

    // Keeps the lines that name the app, trimmed and capped — a tool's own
    // chatter ("No coredumps found", permission hints) never qualifies.
    internal static List<string> FilterLines(string text, string appName, int maxLines = 40)
    {
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !line.Contains(appName, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add("    " + line);
            if (result.Count >= maxLines) break;
        }
        return result;
    }
}
