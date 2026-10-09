using Morphonic.Audio;
using Xunit;

namespace Morphonic.Tests;

// The Windows virtual microphone's reading of pnputil's driver listing.
public class WindowsVirtualMicTests
{
    [Fact]
    public void FindsThePublishedNameOfOurPackage()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string listing = @"Microsoft PnP Utility

Published Name:     oem3.inf
Original Name:      vbcable.inf
Provider Name:      VB-Audio Software
Class Name:         Sound, video and game controllers

Published Name:     oem42.inf
Original Name:      morphoniccable.inf
Provider Name:      Morphonic
Class Name:         Sound, video and game controllers
Driver Version:     10/08/2026 1.0.1.0
";
        Assert.Equal("oem42.inf", WindowsVirtualMic.FindOemInf(listing, "MorphonicCable.inf"));
        Assert.Equal("oem3.inf", WindowsVirtualMic.FindOemInf(listing, "vbcable.inf"));
        Assert.Null(WindowsVirtualMic.FindOemInf(listing, "vbMmeCable64_win10.inf"));
        Assert.Null(WindowsVirtualMic.FindOemInf("", "MorphonicCable.inf"));
        // the carried VB-CABLE package is the one Install uses until Morphonic's own is signed
        Assert.Equal("VBAudioVACWDM", WindowsVirtualMic.VbCable.HardwareId);
        Assert.Equal("CABLE Output", WindowsVirtualMic.VbCable.CaptureName);
    }
}
