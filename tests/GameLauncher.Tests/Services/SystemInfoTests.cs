using GameLauncher.Converters;
using GameLauncher.Services;
using Wpf.Ui.Controls;

namespace GameLauncher.Tests.Services;

public sealed class SystemInfoTests
{
    [Theory]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(24, "DDR3")]
    [InlineData(0, "RAM")]
    public void MemoryType_NamesTheGeneration(int smbios, string expected) => Assert.Equal(expected, SystemInfoText.MemoryType(smbios));

    [Fact]
    public void SecureBoot_SaysOnOffOrWhyNot()
    {
        Assert.Equal("On", SystemInfoText.SecureBoot(uefi: true, flag: 1));
        Assert.Equal("Off", SystemInfoText.SecureBoot(uefi: true, flag: 0));
        Assert.Equal("Not supported by this board", SystemInfoText.SecureBoot(uefi: true, flag: null));
        Assert.StartsWith("Not available", SystemInfoText.SecureBoot(uefi: false, flag: null));
    }

    [Fact]
    public void Clean_CollapsesTheWhitespaceWindowsPadsNamesWith() =>
        Assert.Equal("Intel(R) Core(TM) i7 CPU @ 3.60GHz", SystemInfoText.Clean("  Intel(R) Core(TM) i7   CPU @ 3.60GHz  "));

    [Fact]
    public void Headline_SkipsWhatIsNotKnown()
    {
        Assert.Equal("Ryzen 7 · 32 GB RAM · RTX 4070", SystemInfoText.Headline("Ryzen 7", "32 GB RAM", "RTX 4070"));
        Assert.Equal("Ryzen 7 · RTX 4070", SystemInfoText.Headline("Ryzen 7", "", "RTX 4070"));
    }

    [Theory]
    [InlineData(4300, "4.3 GHz")]
    [InlineData(800, "800 MHz")]
    public void Megahertz_ChoosesTheUnit(double mhz, string expected) => Assert.Equal(expected, SystemInfoText.Megahertz(mhz));

    [Fact]
    public void Uptime_ReadsAsDaysHoursOrMinutes()
    {
        Assert.Equal("2 d 3 h", SystemInfoText.Uptime(new TimeSpan(2, 3, 10, 0)));
        Assert.Equal("5 h 7 min", SystemInfoText.Uptime(new TimeSpan(5, 7, 0)));
        Assert.Equal("12 min", SystemInfoText.Uptime(TimeSpan.FromMinutes(12)));
    }

    [Fact]
    public void Snapshot_ToText_ListsEverySectionAndLine()
    {
        var snapshot = new SystemInfoSnapshot("Ryzen 7 · 32 GB RAM",
        [
            new SystemInfoSection("Motherboard and firmware", "DeveloperBoard24", [new SystemInfoItem("BIOS version", "F31"), new SystemInfoItem("Secure Boot", "On", "hint")]),
        ]);

        var text = snapshot.ToText();

        Assert.Contains("Ryzen 7 · 32 GB RAM", text);
        Assert.Contains("MOTHERBOARD AND FIRMWARE", text);
        Assert.Contains("  BIOS version: F31", text);
        Assert.Contains("  Secure Boot: On", text);
    }

    [Fact]
    public void ASymbolNameThatIsNotASymbol_FallsBackToTheDesktopIcon()
    {
        Assert.Equal(SymbolRegular.Desktop24, SymbolNameConverter.Parse("NoSuchSymbol"));
        Assert.Equal(SymbolRegular.Desktop24, SymbolNameConverter.Parse(null));
        Assert.Equal(SymbolRegular.HardDrive24, SymbolNameConverter.Parse("HardDrive24"));
    }

    /// <summary>Reads the real machine: every section is optional, but a Windows PC always answers for the system section and the firmware boot mode.</summary>
    [Fact]
    public void Collect_ReadsThisPc_WithoutThrowing_AndEverySectionIconIsARealSymbol()
    {
        var snapshot = SystemInfoService.Collect();

        Assert.Contains(snapshot.Sections, s => s.Title == "System");
        Assert.All(snapshot.Sections, section =>
        {
            Assert.NotEmpty(section.Items);
            Assert.True(Enum.TryParse<SymbolRegular>(section.Icon, out _), $"'{section.Icon}' is not a WPF-UI symbol");
        });
        var firmware = snapshot.Sections.FirstOrDefault(s => s.Title == "Motherboard and firmware");
        if (firmware is not null)
            Assert.Contains(firmware.Items, i => i.Label == "Boot mode" && i.Value is "UEFI" or "Legacy BIOS");
    }
}
