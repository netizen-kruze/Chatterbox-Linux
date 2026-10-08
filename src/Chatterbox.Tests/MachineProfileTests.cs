using Chatterbox;
using Xunit;

namespace Chatterbox.Tests;

public class MachineProfileTests
{
    [Fact]
    public void OsLabelReadsLikeOneClause()
    {
        Assert.Equal("Fedora Linux 44 (Workstation Edition), kernel 6.15.4-200.fc44.x86_64, GNOME on wayland",
            MachineProfile.OsLabel("Fedora Linux 44 (Workstation Edition)", "6.15.4-200.fc44.x86_64", "wayland", "GNOME"));
        Assert.Equal("Fedora Linux 44, kernel 6.15.4", MachineProfile.OsLabel("Fedora Linux 44", "6.15.4", "", ""));
        Assert.Equal("Linux, x11", MachineProfile.OsLabel("", "", "x11", ""));
    }

    [Fact]
    public void PciLinesParseIntoSlotClassVendorAndDevice()
    {
        var f = MachineProfile.PciFields(
            "01:00.0 \"VGA compatible controller\" \"NVIDIA Corporation\" \"GB203 [GeForce RTX 5080]\" -ra1 \"Micro-Star International Co., Ltd. [MSI]\" \"Device 5321\"");
        Assert.NotNull(f);
        Assert.Equal("01:00.0", f!.Value.Slot);
        Assert.Equal("VGA compatible controller", f.Value.Class);
        Assert.Equal("NVIDIA Corporation", f.Value.Vendor);
        Assert.Equal("GeForce RTX 5080", MachineProfile.DeviceLabel(f.Value.Device));
        Assert.Equal("Device 7d55", MachineProfile.DeviceLabel("Device 7d55"));
        Assert.Null(MachineProfile.PciFields(""));
        Assert.Null(MachineProfile.PciFields("00:00.0"));
    }

    [Fact]
    public void VendorsGetTheirShortNames()
    {
        Assert.Equal("NVIDIA", MachineProfile.ShortVendor("NVIDIA Corporation"));
        Assert.Equal("AMD", MachineProfile.ShortVendor("Advanced Micro Devices, Inc. [AMD/ATI]"));
        Assert.Equal("Intel", MachineProfile.ShortVendor("Intel Corporation"));
        Assert.Equal("Matrox", MachineProfile.ShortVendor("Matrox Electronics Systems Ltd."));
    }

    [Fact]
    public void DescribeIsOneNonEmptyLine()
    {
        var line = MachineProfile.Describe();
        Assert.False(string.IsNullOrWhiteSpace(line));
        Assert.DoesNotContain("\n", line);
        Assert.Contains("threads", line);
        Assert.Contains("tier", line);
    }
}
