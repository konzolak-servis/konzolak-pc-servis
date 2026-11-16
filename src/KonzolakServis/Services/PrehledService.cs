using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using KonzolakServis.Models;
using Microsoft.Win32;

namespace KonzolakServis.Services;

/// <summary>Jedna karta na úvodní stránce.</summary>
public sealed class KartaPrehledu
{
    public KartaPrehledu(string nadpis, string hlavni) { Nadpis = nadpis; Hlavni = hlavni; }
    public string Nadpis { get; }
    /// <summary>Hlavní řádek velkým písmem (např. název procesoru).</summary>
    public string Hlavni { get; set; }
    public List<(string Popis, string Hodnota, Stav? Stav)> Radky { get; } = new();
    public KartaPrehledu R(string popis, string? hodnota, Stav? stav = null)
    {
        if (!string.IsNullOrWhiteSpace(hodnota)) Radky.Add((popis, hodnota.Trim(), stav));
        return this;
    }
}

/// <summary>Údaje pro úvodní stránku „Přehled PC“ (WMI + registr + Win32 API).</summary>
public static class PrehledService
{
    private static List<ManagementBaseObject> Wmi(string dotaz, string scope = @"root\cimv2")
    {
        var l = new List<ManagementBaseObject>();
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(dotaz));
            foreach (var o in s.Get()) l.Add(o);
        }
        catch { }
        return l;
    }

    private static string S(object? o) => o?.ToString()?.Trim() ?? "";
    private static string Gb(double bajty) => bajty >= 1e12 ? $"{bajty / 1e12:0.##} TB" : $"{bajty / 1024 / 1024 / 1024:0.#} GB";
    private static string Datum(object? wmi)
    {
        try { return ManagementDateTimeConverter.ToDateTime(S(wmi)).ToString("d. M. yyyy"); } catch { return ""; }
    }

    public static List<KartaPrehledu> Nacti()
    {
        var karty = new List<KartaPrehledu>();

        // --- Počítač a systém ---
        var cs = Wmi("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        var os = Wmi("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
        var displayVer = ZalohaService.CtiReg("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
        var spusteno = "";
        try { var up = TimeSpan.FromMilliseconds(Environment.TickCount64); spusteno = $"{(int)up.TotalDays} d {up.Hours} h {up.Minutes} min"; } catch { }
        karty.Add(new KartaPrehledu("Systém", $"{S(os?["Caption"]).Replace("Microsoft ", "")} {displayVer}".Trim())
            .R("Sestavení", $"{S(os?["Version"])} ({S(os?["OSArchitecture"])})")
            .R("Počítač", $"{Environment.MachineName} · {S(cs?["Manufacturer"])} {S(cs?["Model"])}")
            .R("Nainstalováno", Datum(os?["InstallDate"]))
            .R("Běží bez restartu", spusteno, Environment.TickCount64 > 14L * 24 * 3600 * 1000 ? Stav.Varovani : null));

        // --- Procesor ---
        foreach (var c in Wmi("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L3CacheSize, SocketDesignation FROM Win32_Processor"))
            karty.Add(new KartaPrehledu("Procesor", S(c["Name"]))
                .R("Jádra / vlákna", $"{S(c["NumberOfCores"])} / {S(c["NumberOfLogicalProcessors"])}")
                .R("Základní takt", $"{Convert.ToDouble(c["MaxClockSpeed"] ?? 0) / 1000:0.0#} GHz")
                .R("Cache L3", Convert.ToInt32(c["L3CacheSize"] ?? 0) > 0 ? $"{Convert.ToInt32(c["L3CacheSize"]) / 1024.0:0.#} MB" : "")
                .R("Patice", S(c["SocketDesignation"])));

        // --- Grafika ---
        foreach (var g in Wmi("SELECT Name, DriverVersion, DriverDate, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            var nazev = S(g["Name"]);
            if (nazev.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || nazev.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            var vram = Vram(nazev) ?? Convert.ToDouble(g["AdapterRAM"] ?? 0);
            var rozl = S(g["CurrentHorizontalResolution"]).Length > 0 ? $"{S(g["CurrentHorizontalResolution"])} × {S(g["CurrentVerticalResolution"])} @ {S(g["CurrentRefreshRate"])} Hz" : "";
            karty.Add(new KartaPrehledu("Grafická karta", nazev)
                .R("Paměť grafiky", vram > 0 ? Gb(vram) : "")
                .R("Ovladač", $"{S(g["DriverVersion"])}" + (Datum(g["DriverDate"]) is { Length: > 0 } d ? $" ({d})" : ""))
                .R("Rozlišení", rozl));
        }

        // --- Paměť ---
        var moduly = Wmi("SELECT Capacity, ConfiguredClockSpeed, Speed, Manufacturer, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory");
        var sloty = Wmi("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(o => Convert.ToInt32(o["MemoryDevices"] ?? 0));
        var celkem = moduly.Sum(m => Convert.ToDouble(m["Capacity"] ?? 0));
        var prvni = moduly.FirstOrDefault();
        var typ = Convert.ToInt32(prvni?["SMBIOSMemoryType"] ?? 0) switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 30 => "LPDDR4", 35 => "LPDDR5", _ => "" };
        var takt = S(prvni?["ConfiguredClockSpeed"]) is { Length: > 0 } tk && tk != "0" ? tk : S(prvni?["Speed"]);
        karty.Add(new KartaPrehledu("Operační paměť", $"{Gb(celkem > 0 ? celkem : Convert.ToDouble(cs?["TotalPhysicalMemory"] ?? 0))} {typ}".Trim())
            .R("Moduly", $"{moduly.Count}" + (sloty > 0 ? $" z {sloty} slotů" : "") + (moduly.Count == 1 && sloty > 1 ? " (běží v single-channel)" : ""),
                moduly.Count == 1 && sloty > 1 ? Stav.Varovani : null)
            .R("Takt", takt.Length > 0 ? $"{takt} MT/s" : "")
            .R("Výrobce", string.Join(", ", moduly.Select(m => $"{S(m["Manufacturer"])} {S(m["PartNumber"])}".Trim()).Where(x => x.Length > 0).Distinct())));

        // --- Základní deska ---
        var bb = Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Wmi("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        var sb = ZalohaService.CtiReg("HKLM", @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        karty.Add(new KartaPrehledu("Základní deska", $"{S(bb?["Manufacturer"])} {S(bb?["Product"])}".Trim())
            .R("BIOS / UEFI", $"{S(bios?["SMBIOSBIOSVersion"])}" + (Datum(bios?["ReleaseDate"]) is { Length: > 0 } bd ? $" ({bd})" : ""))
            .R("Režim", sb == null ? "Legacy BIOS" : sb == "1" ? "UEFI, Secure Boot zapnutý" : "UEFI, Secure Boot vypnutý"));

        // --- Disky ---
        foreach (var d in Wmi("SELECT FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
        {
            var media = Convert.ToInt32(d["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", _ => "" };
            var bus = Convert.ToInt32(d["BusType"] ?? 0) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", _ => "" };
            var zdravi = Convert.ToInt32(d["HealthStatus"] ?? 0);
            karty.Add(new KartaPrehledu("Disk", S(d["FriendlyName"]))
                .R("Typ", $"{(bus == "NVMe" ? "SSD" : media)} {bus}".Trim())
                .R("Kapacita", Gb(Convert.ToDouble(d["Size"] ?? 0)))
                .R("Stav", zdravi switch { 0 => "v pořádku", 1 => "VAROVÁNÍ", 2 => "PORUCHA", _ => "neznámý" },
                    zdravi switch { 0 => Stav.Ok, 1 => Stav.Varovani, 2 => Stav.Chyba, _ => null }));
        }
        var oddily = new KartaPrehledu("Oddíly", "Volné místo");
        foreach (var dr in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
        {
            var proc = dr.TotalSize > 0 ? dr.AvailableFreeSpace * 100.0 / dr.TotalSize : 0;
            oddily.R($"{dr.Name.TrimEnd('\\')} {dr.VolumeLabel}".Trim(), $"{Gb(dr.AvailableFreeSpace)} volných z {Gb(dr.TotalSize)} ({proc:0} %)",
                proc < 10 ? Stav.Chyba : proc < 20 ? Stav.Varovani : Stav.Ok);
        }
        karty.Add(oddily);

        // --- Síť ---
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.GetIPProperties().GatewayAddresses.Count > 0)
            .OrderByDescending(n => n.Speed).FirstOrDefault();
        if (nic != null)
        {
            var ip = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            karty.Add(new KartaPrehledu("Síť", nic.Description)
                .R("Připojení", nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Kabel / jiné")
                .R("Rychlost spojení", $"{nic.Speed / 1_000_000} Mbit/s")
                .R("IP adresa", ip));
        }
        else karty.Add(new KartaPrehledu("Síť", "Bez připojení k síti").R("Stav", "nepřipojeno", Stav.Varovani));

        // --- Baterie ---
        foreach (var b in Wmi("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery"))
        {
            var nab = Convert.ToInt32(b["EstimatedChargeRemaining"] ?? 0);
            var stat = Convert.ToInt32(b["BatteryStatus"] ?? 0);
            karty.Add(new KartaPrehledu("Baterie", $"{nab} %")
                .R("Stav", stat == 2 ? "na síti / nabíjí" : stat == 1 ? "na baterii" : "—")
                .R("Opotřebení", "viz Diagnostika → Zpráva o baterii"));
        }

        // --- Teploty (jen kde je ACPI poskytuje) ---
        var teploty = Wmi("SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", @"root\wmi")
            .Select(t => (Zona: S(t["InstanceName"]).Split('\\').Last().Replace("_0", ""), C: Convert.ToDouble(t["CurrentTemperature"] ?? 0) / 10 - 273.15))
            .Where(t => t.C is > 5 and < 120).ToList();
        if (teploty.Count > 0)
        {
            var k = new KartaPrehledu("Teploty", $"{teploty.Max(t => t.C):0} °C max");
            foreach (var t in teploty) k.R($"Zóna {t.Zona}", $"{t.C:0} °C", t.C >= 90 ? Stav.Chyba : t.C >= 80 ? Stav.Varovani : Stav.Ok);
            k.R("Pozn.", "Teplotní zóny ACPI z desky; přesné teploty jader neposkytuje Windows bez ovladače výrobce.");
            karty.Add(k);
        }
        return karty;
    }

    /// <summary>Skutečná velikost paměti grafiky z registru (WMI AdapterRAM je omezené na 4 GB).</summary>
    private static double? Vram(string nazev)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return null;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") as string != nazev) continue;
                var v = k.GetValue("HardwareInformation.qwMemorySize");
                if (v is long l) return l;
                if (v is byte[] b && b.Length >= 8) return BitConverter.ToInt64(b, 0);
                if (k.GetValue("HardwareInformation.MemorySize") is byte[] b4 && b4.Length >= 4) return BitConverter.ToUInt32(b4, 0);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Převod přehledu na zprávu pro uložení do HTML/TXT.</summary>
    public static Zprava DoZpravy(List<KartaPrehledu> karty)
    {
        var z = new Zprava("Přehled počítače") { Stav = Stav.Info, Shrnuti = $"Přehled hardwaru a systému počítače {Environment.MachineName}." };
        foreach (var k in karty)
        {
            var s = z.PridejSekci($"{k.Nadpis}: {k.Hlavni}");
            foreach (var (p, h, st) in k.Radky) s.Pridej(p, h, st);
        }
        return z;
    }

    // ---------- živé vytížení (Win32 API, bez závislosti na jazyku Windows) ----------

    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public ulong V => ((ulong)High << 32) | Low; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemStatus
    {
        public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemStatus s);

    private static ulong _idle, _total;

    /// <summary>Vytížení procesoru v % od posledního volání.</summary>
    public static double CpuProcent()
    {
        if (!GetSystemTimes(out var i, out var k, out var u)) return 0;
        var idle = i.V; var total = k.V + u.V;   // kernel zahrnuje idle
        var di = idle - _idle; var dt = total - _total;
        _idle = idle; _total = total;
        return dt == 0 ? 0 : Math.Clamp(100.0 * (dt - di) / dt, 0, 100);
    }

    public static (double Procent, double PouzitoGb, double CelkemGb) Pamet()
    {
        var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        const double G = 1024.0 * 1024 * 1024;
        return (m.MemoryLoad, (m.TotalPhys - m.AvailPhys) / G, m.TotalPhys / G);
    }
}
