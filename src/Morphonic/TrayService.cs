using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Morphonic;

// Windows tray icon via the Win32 shell API directly (Shell_NotifyIcon) —
// no UI framework involved. A hidden window on a dedicated thread receives
// the icon's mouse callbacks and the shell's TaskbarCreated broadcast (so
// the icon survives an explorer.exe restart). Left-click or double-click
// raises OnOpen; the right-click menu offers Open and Exit. Closing the
// main window hides to this icon and the voice keeps running.
public sealed class TrayService : IDisposable
{
    public event Action? OnOpen;
    public event Action? OnExit;

    private const uint CallbackMsg = 0x8000 + 1; // WM_APP + 1
    private const uint WmLButtonUp = 0x0202, WmLButtonDblClk = 0x0203, WmRButtonUp = 0x0205;
    private const uint WmDestroy = 0x0002, WmClose = 0x0010;
    private const int MenuOpen = 1, MenuExit = 2;

    private readonly Thread? _thread;
    private readonly WndProcDelegate _wndProc; // rooted — GC must never collect it
    private readonly string _iconPath;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private uint _taskbarCreatedMsg;
    private bool _disposed;

    public TrayService(string iconPath)
    {
        _iconPath = iconPath;
        _wndProc = WndProc;
        if (!OperatingSystem.IsWindows()) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Morphonic tray" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
            _hIcon = LoadTrayIcon();
            var wc = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = "MorphonicTrayWindow",
            };
            RegisterClassExW(ref wc);
            _hwnd = CreateWindowExW(0, wc.lpszClassName, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;
            AddIcon();
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex) { ErrorLog.WriteEntry("TrayService", ex); }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMsg)
        {
            switch ((uint)(long)lParam)
            {
                case WmLButtonUp:
                case WmLButtonDblClk:
                    OnOpen?.Invoke();
                    break;
                case WmRButtonUp:
                    ShowMenu(hwnd);
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            AddIcon(); // explorer restarted — the icon must be re-added
            return IntPtr.Zero;
        }
        if (msg == WmDestroy)
        {
            RemoveIcon();
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, 0x0000, MenuOpen, "Open Morphonic");
            AppendMenuW(menu, 0x0800, 0, null);          // MF_SEPARATOR
            AppendMenuW(menu, 0x0000, MenuExit, "Exit");
            // Without the foreground call the menu refuses to dismiss when
            // the user clicks elsewhere (shell-documented requirement).
            SetForegroundWindow(hwnd);
            GetCursorPos(out var pt);
            int cmd = TrackPopupMenu(menu, 0x0100 | 0x0002, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
            PostMessageW(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (cmd == MenuOpen) OnOpen?.Invoke();
            else if (cmd == MenuExit) OnExit?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    private void AddIcon()
    {
        var data = MakeIconData();
        if (!Shell_NotifyIconW(0 /* NIM_ADD */, ref data))
            Shell_NotifyIconW(1 /* NIM_MODIFY */, ref data);
    }

    private void RemoveIcon()
    {
        var data = MakeIconData();
        Shell_NotifyIconW(2 /* NIM_DELETE */, ref data);
    }

    private NOTIFYICONDATAW MakeIconData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = 0x1 | 0x2 | 0x4, // NIF_MESSAGE | NIF_ICON | NIF_TIP
        uCallbackMessage = CallbackMsg,
        hIcon = _hIcon,
        szTip = "Morphonic — voice changer",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr LoadTrayIcon()
    {
        if (File.Exists(_iconPath))
        {
            var h = LoadImageW(IntPtr.Zero, _iconPath, 1 /* IMAGE_ICON */, 16, 16, 0x0010 /* LR_LOADFROMFILE */);
            if (h != IntPtr.Zero) return h;
        }
        return LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, int id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr LoadIconW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? module);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
}
