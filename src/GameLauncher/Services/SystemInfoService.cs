using System.IO;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using GameLauncher.Models;
using Microsoft.Win32;

namespace GameLauncher.Services;

/// <summary>One line of the My PC page: a label, its value and an optional hint underneath (why it matters).</summary>
public sealed record SystemInfoItem(string Label, string Value, string Hint = "");

/// <summary>A card on the My PC page: a title, an icon name (a WPF-UI symbol) and its lines.</summary>
public sealed record SystemInfoSection(string Title, string Icon, IReadOnlyList<SystemInfoItem> Items);

/// <summary>What the My PC page shows: a one-line summary and the sections. Plain data, so the page and "Copy" read the same thing.</summary>
public sealed class SystemInfoSnapshot(string headline, IReadOnlyList<SystemInfoSection> sections)
{
    public string Headline { get; } = headline;

    public IReadOnlyList<SystemInfoSection> Sections { get; } = sections;

    /// <summary>The whole page as plain text, for pasting into a support message or a forum post.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine("My PC - Axis Game Launcher");
        if (Headline.Length > 0)
            text.AppendLine(Headline);

        foreach (var section in Sections)
        {
            text.AppendLine();
            text.AppendLine(section.Title.ToUpperInvariant());
            foreach (var item in section.Items)
                text.AppendLine($"  {item.Label}: {item.Value}");
        }

        return text.ToString();
    }
}

/// <summary>The wording rules for hardware facts, kept apart from the Windows calls so they can be tested.</summary>
public static class SystemInfoText
{
    /// <summary>DDR generation from the SMBIOS memory type number Windows reports.</summary>
    public static string MemoryType(int smbios) => smbios switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        30 => "LPDDR4",
        _ => "RAM",
    };

    /// <summary>"On" / "Off" / "Not supported" for Secure Boot, from the firmware type and the registry flag.</summary>
    public static string SecureBoot(bool uefi, int? flag) => !uefi ? "Not available (Legacy BIOS)" : flag switch
    {
        1 => "On",
        0 => "Off",
        _ => "Not supported by this board",
    };

    public static string Megahertz(double mhz) => mhz >= 1000 ? $"{mhz / 1000:0.0#} GHz" : $"{mhz:0} MHz";

    public static string Uptime(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{span.Minutes} min";

    /// <summary>Tidies a name Windows pads or doubles up: "  Intel(R) Core(TM) i7   CPU " becomes "Intel(R) Core(TM) i7 CPU".</summary>
    public static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The first line of the headline: the processor, the memory and the graphics card, each only when known.</summary>
    public static string Headline(string cpu, string memory, string gpu) =>
        string.Join(" · ", new[] { cpu, memory, gpu }.Where(part => part.Length > 0));
}

/// <summary>Reads what is in this PC - the board, firmware, processor, memory, graphics card, drives and a few gaming settings - from Windows
/// (WMI and the registry). Every section is read on its own and a failure leaves that section out (or a line as "Unknown") instead of failing the page.
/// Nothing is sent anywhere.</summary>
public static class SystemInfoService
{
    private const string Unknown = "Unknown";

    public static SystemInfoSnapshot Collect()
    {
        string cpuName = string.Empty, memory = string.Empty, gpuName = string.Empty;

        // Each section asks Windows its own questions, and Windows answers slowly (a few seconds in all if asked one after another), so they run side by side.
        var results = new SystemInfoSection?[7];
        Task.WaitAll(
            Task.Run(() => results[0] = Read("System", "Desktop24", () => ReadSystem())),
            Task.Run(() => results[1] = Read("Processor", "Gauge24", () => ReadProcessor(out cpuName))),
            Task.Run(() => results[2] = Read("Motherboard and firmware", "DeveloperBoard24", ReadFirmware)),
            Task.Run(() => results[3] = Read("Memory", "Cube24", () => ReadMemory(out memory))),
            Task.Run(() => results[4] = Read("Graphics", "Games24", () => ReadGraphics(out gpuName))),
            Task.Run(() => results[5] = Read("Storage", "Storage24", ReadStorage)),
            Task.Run(() => results[6] = Read("Gaming settings", "XboxController24", ReadGaming)));

        var shortCpu = SystemInfoText.Clean(cpuName).Replace("(R)", string.Empty, StringComparison.Ordinal).Replace("(TM)", string.Empty, StringComparison.Ordinal);
        return new SystemInfoSnapshot(SystemInfoText.Headline(SystemInfoText.Clean(shortCpu), memory, SystemInfoText.Clean(gpuName)), results.OfType<SystemInfoSection>().ToList());
    }

    private static SystemInfoSection? Read(string title, string icon, Func<List<SystemInfoItem>> read)
    {
        try
        {
            var items = read();
            return items.Count > 0 ? new SystemInfoSection(title, icon, items) : null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"My PC: couldn't read the {title.ToLowerInvariant()} section.", ex);
            return null;
        }
    }

    // ---- WMI helpers ------------------------------------------------------------------------------------------------

    private static List<ManagementBaseObject> Query(string className, string scope = @"root\CIMV2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, $"SELECT * FROM {className}");
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex)
        {
            Logger.Warn($"My PC: the {className} query failed.", ex);
            return [];
        }
    }

    private static string Text(ManagementBaseObject? row, string property) =>
        SystemInfoText.Clean(row?[property]?.ToString());

    private static ulong Number(ManagementBaseObject? row, string property)
    {
        try
        {
            return row?[property] is { } value ? Convert.ToUInt64(value, CultureInfo.InvariantCulture) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static T? ReadRegistry<T>(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) is T value ? value : default;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static string OrUnknown(string value) => value.Length == 0 ? Unknown : value;

    // ---- Sections ---------------------------------------------------------------------------------------------------

    private static List<SystemInfoItem> ReadSystem()
    {
        var items = new List<SystemInfoItem>();
        var computer = Query("Win32_ComputerSystem").FirstOrDefault();
        var os = Query("Win32_OperatingSystem").FirstOrDefault();
        var maker = Text(computer, "Manufacturer");
        var model = Text(computer, "Model");
        items.Add(new("Computer", OrUnknown($"{maker} {model}".Trim())));

        const string current = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var caption = Text(os, "Caption");
        var display = ReadRegistry<string>(Microsoft.Win32.Registry.LocalMachine, current, "DisplayVersion") ?? string.Empty;
        var build = ReadRegistry<string>(Microsoft.Win32.Registry.LocalMachine, current, "CurrentBuild") ?? Text(os, "BuildNumber");
        items.Add(new("Windows", OrUnknown($"{caption} {display} (build {build})".Replace("Microsoft ", string.Empty, StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal).Trim())));
        items.Add(new("System type", Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));
        items.Add(new("Up for", SystemInfoText.Uptime(TimeSpan.FromMilliseconds(Environment.TickCount64)),
            "Windows keeps running while the PC is on; Fast Startup counts a shutdown as sleep, so a restart gives a truly fresh start."));
        return items;
    }

    private static List<SystemInfoItem> ReadProcessor(out string name)
    {
        var items = new List<SystemInfoItem>();
        var cpu = Query("Win32_Processor").FirstOrDefault();
        name = Text(cpu, "Name");
        if (cpu is null)
            return items;

        items.Add(new("Processor", OrUnknown(name)));
        var cores = Number(cpu, "NumberOfCores");
        var threads = Number(cpu, "NumberOfLogicalProcessors");
        items.Add(new("Cores / threads", cores > 0 ? $"{cores} cores, {threads} threads" : $"{Environment.ProcessorCount} threads"));
        var mhz = Number(cpu, "MaxClockSpeed");
        if (mhz > 0)
            items.Add(new("Base speed", SystemInfoText.Megahertz(mhz)));

        var l3 = Number(cpu, "L3CacheSize");
        if (l3 > 0)
            items.Add(new("L3 cache", ByteFormat.Size((long)l3 * 1024)));

        var socket = Text(cpu, "SocketDesignation");
        if (socket.Length > 0)
            items.Add(new("Socket", socket));

        if (cpu["VirtualizationFirmwareEnabled"] is bool virtualization)
            items.Add(new("Virtualization", virtualization ? "On" : "Off", "Needed for the Windows Subsystem for Linux, Android emulators and some anti-cheat checks."));

        return items;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out int firmwareType);

    private static List<SystemInfoItem> ReadFirmware()
    {
        var items = new List<SystemInfoItem>();
        var board = Query("Win32_BaseBoard").FirstOrDefault();
        var bios = Query("Win32_BIOS").FirstOrDefault();

        var boardName = $"{Text(board, "Manufacturer")} {Text(board, "Product")}".Trim();
        items.Add(new("Motherboard", OrUnknown(boardName)));
        var revision = Text(board, "Version");
        if (revision.Length > 0 && !revision.Contains("None", StringComparison.OrdinalIgnoreCase) && !revision.Contains("Default", StringComparison.OrdinalIgnoreCase))
            items.Add(new("Board revision", revision));

        items.Add(new("BIOS vendor", OrUnknown(Text(bios, "Manufacturer"))));
        items.Add(new("BIOS version", OrUnknown(Text(bios, "SMBIOSBIOSVersion"))));
        items.Add(new("BIOS date", BiosDate(bios)));

        var uefi = GetFirmwareType(out var type) && type == 2;
        items.Add(new("Boot mode", uefi ? "UEFI" : "Legacy BIOS"));
        var flag = ReadRegistry<int?>(Microsoft.Win32.Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        items.Add(new("Secure Boot", SystemInfoText.SecureBoot(uefi, flag),
            "Some anti-cheat (Vanguard, FACEIT, Battlefield) will not start without Secure Boot and TPM 2.0. You turn it on in the BIOS."));
        items.Add(new("TPM", ReadTpm()));
        return items;
    }

    private static string BiosDate(ManagementBaseObject? bios)
    {
        try
        {
            return bios?["ReleaseDate"] is string raw && raw.Length >= 8
                ? ManagementDateTimeConverter.ToDateTime(raw).ToString("d MMM yyyy", CultureInfo.CurrentCulture)
                : Unknown;
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    /// <summary>Asks the TPM Base Services (no administrator needed, and instant, unlike the WMI class for it) which TPM version is present.</summary>
    private static string ReadTpm()
    {
        try
        {
            var result = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out var info);
            if (result != 0)
                return "Not found (it may be turned off in the BIOS)";

            return info.TpmVersion switch
            {
                2 => "Version 2.0, present",
                1 => "Version 1.2, present (Windows 11 and some anti-cheat want 2.0)",
                _ => "Present",
            };
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    private static List<SystemInfoItem> ReadMemory(out string summary)
    {
        summary = string.Empty;
        var items = new List<SystemInfoItem>();
        var computer = Query("Win32_ComputerSystem").FirstOrDefault();
        var os = Query("Win32_OperatingSystem").FirstOrDefault();
        var total = (long)Number(computer, "TotalPhysicalMemory");
        if (total > 0)
        {
            summary = ByteFormat.Size(total).Replace(".0", string.Empty, StringComparison.Ordinal) + " RAM";
            items.Add(new("Installed", ByteFormat.Size(total)));
        }

        var free = (long)Number(os, "FreePhysicalMemory") * 1024;
        if (total > 0 && free > 0)
            items.Add(new("In use now", $"{ByteFormat.Size(total - free)} of {ByteFormat.Size(total)}"));

        var slot = 0;
        foreach (var module in Query("Win32_PhysicalMemory"))
        {
            slot++;
            var speed = Number(module, "ConfiguredClockSpeed");
            if (speed == 0)
                speed = Number(module, "Speed");

            var kind = SystemInfoText.MemoryType((int)Number(module, "SMBIOSMemoryType"));
            var maker = Text(module, "Manufacturer");
            var label = $"Module {slot}";
            var value = $"{ByteFormat.Size((long)Number(module, "Capacity"))} {kind}{(speed > 0 ? $"-{speed}" : string.Empty)}";
            if (maker.Length > 0 && !maker.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                value += $" · {maker}";

            items.Add(new(label, value));
        }

        return items;
    }

    private static List<SystemInfoItem> ReadGraphics(out string firstName)
    {
        firstName = string.Empty;
        var items = new List<SystemInfoItem>();
        var cards = Query("Win32_VideoController").Where(c => !Text(c, "Name").Contains("Basic", StringComparison.OrdinalIgnoreCase)).ToList();
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var suffix = cards.Count > 1 ? $" {i + 1}" : string.Empty;
            var name = Text(card, "Name");
            if (i == 0)
                firstName = name;

            items.Add(new($"Graphics card{suffix}", OrUnknown(name)));
            var vram = VideoMemory(name) ?? (long)Number(card, "AdapterRAM");
            if (vram > 0)
                items.Add(new($"Video memory{suffix}", ByteFormat.Size(vram)));

            var version = Text(card, "DriverVersion");
            var date = card["DriverDate"] is string raw && raw.Length >= 8 ? SafeDate(raw) : string.Empty;
            if (version.Length > 0)
                items.Add(new($"Driver{suffix}", date.Length > 0 ? $"{version} ({date})" : version));

            var width = Number(card, "CurrentHorizontalResolution");
            var height = Number(card, "CurrentVerticalResolution");
            var refresh = Number(card, "CurrentRefreshRate");
            if (width > 0 && height > 0)
                items.Add(new($"Display{suffix}", $"{width} × {height}{(refresh > 1 ? $" at {refresh} Hz" : string.Empty)}"));
        }

        return items;
    }

    private static string SafeDate(string raw)
    {
        try
        {
            return ManagementDateTimeConverter.ToDateTime(raw).ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>WMI reports video memory in 32 bits, which stops at 4 GB; the driver's own registry entry has the real size.</summary>
    private static long? VideoMemory(string adapterName)
    {
        try
        {
            using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null)
                return null;

            foreach (var subKeyName in classKey.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var key = classKey.OpenSubKey(subKeyName);
                if (key?.GetValue("DriverDesc") is not string description || !string.Equals(SystemInfoText.Clean(description), adapterName, StringComparison.OrdinalIgnoreCase))
                    continue;

                return key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long big => big,
                    ulong unsignedBig => (long)unsignedBig,
                    int small => small,
                    _ => null,
                };
            }
        }
        catch (Exception)
        {
            // fall back to WMI's figure
        }

        return null;
    }

    private static List<SystemInfoItem> ReadStorage()
    {
        var items = new List<SystemInfoItem>();
        var physical = Query("MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");
        var index = 0;
        foreach (var disk in Query("Win32_DiskDrive"))
        {
            index++;
            var model = Text(disk, "Model");
            var kind = KindOfDisk(physical, disk, model);
            var size = (long)Number(disk, "Size");
            items.Add(new($"Drive {index}", $"{OrUnknown(model)} · {(size > 0 ? ByteFormat.Size(size) : Unknown)}{(kind.Length > 0 ? " · " + kind : string.Empty)}"));
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            items.Add(new($"{drive.Name.TrimEnd('\\')} {SafeLabel(drive)}".Trim(), $"{ByteFormat.Size(drive.AvailableFreeSpace)} free of {ByteFormat.Size(drive.TotalSize)}"));
        }

        return items;
    }

    private static string SafeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel.Length > 0 ? $"({drive.VolumeLabel})" : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string KindOfDisk(List<ManagementBaseObject> physical, ManagementBaseObject disk, string model)
    {
        // Win32_DiskDrive.Index is the disk number and MSFT_PhysicalDisk.DeviceId is the same number. Matching on the first word of the model would give two
        // disks from one maker (a Samsung NVMe and a Samsung hard disk) the same answer.
        var number = Text(disk, "Index");
        var match = number.Length > 0 ? physical.FirstOrDefault(p => Text(p, "DeviceId") == number) : null;
        match ??= physical.FirstOrDefault(p => model.Length > 0 && string.Equals(Text(p, "FriendlyName"), model, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return string.Empty;

        var bus = (int)Number(match, "BusType");
        var media = (int)Number(match, "MediaType");
        return (bus, media) switch
        {
            (17, _) => "NVMe SSD",
            (_, 4) => "SSD",
            (_, 3) => "Hard disk",
            _ => string.Empty,
        };
    }

    private static List<SystemInfoItem> ReadGaming()
    {
        var items = new List<SystemInfoItem>();
        var gameMode = ReadRegistry<int?>(Microsoft.Win32.Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
        items.Add(new("Game Mode", gameMode == 0 ? "Off" : "On", "Windows gives a running game priority over background work."));
        var scheduling = ReadRegistry<int?>(Microsoft.Win32.Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        items.Add(new("Hardware-accelerated GPU scheduling", scheduling == 2 ? "On" : scheduling == 1 ? "Off" : "Not supported or not set"));
        var transparency = ReadRegistry<int?>(Microsoft.Win32.Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency");
        items.Add(new("Window transparency", transparency == 0 ? "Off" : "On"));
        return items;
    }
}
