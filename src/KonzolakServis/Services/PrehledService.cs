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
    /// <summary>Podrobnosti zobrazené po rozkliknutí karty.</summary>
    public List<DetailKarty> Detail { get; } = new();
    public DetailKarty D(string nadpis)
    {
        var d = Detail.FirstOrDefault(x => x.Nadpis == nadpis);
        if (d == null) { d = new DetailKarty(nadpis); Detail.Add(d); }
        return d;
    }
}

public sealed class DetailKarty
{
    public DetailKarty(string nadpis) => Nadpis = nadpis;
    public string Nadpis { get; }
    public List<(string Popis, string Hodnota, Stav? Stav)> Radky { get; } = new();
    public DetailKarty R(string popis, string? hodnota, Stav? stav = null)
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
    private static string Datum(object? wmi, bool cas = false)
    {
        try { return ManagementDateTimeConverter.ToDateTime(S(wmi)).ToString(cas ? "d. M. yyyy HH:mm" : "d. M. yyyy"); } catch { return ""; }
    }

    /// <summary>Pole čísel (UTF-16 kódy) z WmiMonitorID → text.</summary>
    private static string Pole(object? o) => o is ushort[] a ? new string(a.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() : S(o);

    private static string Skrin(object? o)
    {
        if (o is not ushort[] t || t.Length == 0) return "";
        return t[0] switch
        {
            3 or 4 or 5 or 6 or 7 or 15 or 16 or 35 or 36 => "stolní počítač",
            8 or 9 or 10 or 14 or 31 or 32 => "notebook",
            13 => "All-in-One", 30 => "tablet", 17 or 23 or 28 => "server", _ => $"typ {t[0]}"
        };
    }

    public static List<KartaPrehledu> Nacti()
    {
        var karty = new List<KartaPrehledu>();

        // --- Počítač a systém ---
        var cs = Wmi("SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber, TotalPhysicalMemory, Domain, PartOfDomain, Workgroup, UserName FROM Win32_ComputerSystem").FirstOrDefault();
        var os = Wmi("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, RegisteredUser, Organization, SerialNumber, OSLanguage, SystemDrive FROM Win32_OperatingSystem").FirstOrDefault();
        const string NT = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var displayVer = ZalohaService.CtiReg("HKLM", NT, "DisplayVersion");
        var ubr = ZalohaService.CtiReg("HKLM", NT, "UBR");
        var edice = ZalohaService.CtiReg("HKLM", NT, "EditionID");
        var spusteno = "";
        try { var up = TimeSpan.FromMilliseconds(Environment.TickCount64); spusteno = $"{(int)up.TotalDays} d {up.Hours} h {up.Minutes} min"; } catch { }
        var sys = new KartaPrehledu("Systém", $"{S(os?["Caption"]).Replace("Microsoft ", "")} {displayVer}".Trim())
            .R("Sestavení", $"{S(os?["BuildNumber"])}.{ubr} ({S(os?["OSArchitecture"])})")
            .R("Počítač", $"{Environment.MachineName} · {S(cs?["Manufacturer"])} {S(cs?["Model"])}")
            .R("Nainstalováno", Datum(os?["InstallDate"]))
            .R("Běží bez restartu", spusteno, Environment.TickCount64 > 14L * 24 * 3600 * 1000 ? Stav.Varovani : null);
        sys.D("Windows")
            .R("Edice", edice).R("Verze", $"{displayVer} (build {S(os?["BuildNumber"])}.{ubr})")
            .R("Architektura", S(os?["OSArchitecture"])).R("Systémový disk", S(os?["SystemDrive"]))
            .R("Nainstalováno", Datum(os?["InstallDate"])).R("Posledně spuštěno", Datum(os?["LastBootUpTime"], true))
            .R("Registrovaný uživatel", S(os?["RegisteredUser"])).R("Organizace", S(os?["Organization"]))
            .R("ID produktu", S(os?["SerialNumber"]));
        sys.D("Počítač")
            .R("Název", Environment.MachineName).R("Výrobce", S(cs?["Manufacturer"])).R("Model", S(cs?["Model"]))
            .R("Řada", S(cs?["SystemFamily"])).R("SKU", S(cs?["SystemSKUNumber"]))
            .R(cs?["PartOfDomain"] is true ? "Doména" : "Pracovní skupina", cs?["PartOfDomain"] is true ? S(cs?["Domain"]) : S(cs?["Workgroup"]))
            .R("Přihlášený uživatel", S(cs?["UserName"]));
        karty.Add(sys);

        // --- Procesor ---
        foreach (var c in Wmi("SELECT * FROM Win32_Processor"))
        {
            var l2 = Convert.ToInt32(c["L2CacheSize"] ?? 0); var l3 = Convert.ToInt32(c["L3CacheSize"] ?? 0);
            var cpu = new KartaPrehledu("Procesor", S(c["Name"]))
                .R("Jádra / vlákna", $"{S(c["NumberOfCores"])} / {S(c["NumberOfLogicalProcessors"])}")
                .R("Základní takt", $"{Convert.ToDouble(c["MaxClockSpeed"] ?? 0) / 1000:0.0#} GHz")
                .R("Cache L3", l3 > 0 ? $"{l3 / 1024.0:0.#} MB" : "")
                .R("Patice", S(c["SocketDesignation"]));
            var virt = c["VirtualizationFirmwareEnabled"] is bool vb ? (vb ? "zapnutá v BIOSu" : "VYPNUTÁ v BIOSu") : "";
            cpu.D("Podrobnosti")
                .R("Výrobce", S(c["Manufacturer"])).R("Popis", S(c["Description"]))
                .R("Jádra (aktivní)", $"{S(c["NumberOfCores"])} ({S(c["NumberOfEnabledCore"])} aktivních)")
                .R("Logické procesory", S(c["NumberOfLogicalProcessors"]))
                .R("Základní takt", $"{S(c["MaxClockSpeed"])} MHz").R("Aktuální takt", $"{S(c["CurrentClockSpeed"])} MHz")
                .R("Cache L2", l2 > 0 ? $"{l2 / 1024.0:0.#} MB" : "").R("Cache L3", l3 > 0 ? $"{l3 / 1024.0:0.#} MB" : "")
                .R("Patice", S(c["SocketDesignation"])).R("Napětí", Convert.ToDouble(c["CurrentVoltage"] ?? 0) > 0 ? $"{Convert.ToDouble(c["CurrentVoltage"]) / 10:0.0#} V" : "")
                .R("Virtualizace (VT-x / AMD-V)", virt, virt.StartsWith("VYP") ? Stav.Info : null)
                .R("ID procesoru", S(c["ProcessorId"]));
            karty.Add(cpu);
        }

        // --- Grafika ---
        foreach (var g in Wmi("SELECT * FROM Win32_VideoController"))
        {
            var nazev = S(g["Name"]);
            if (nazev.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || nazev.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            var vram = Vram(nazev) ?? Convert.ToDouble(g["AdapterRAM"] ?? 0);
            var rozl = S(g["CurrentHorizontalResolution"]).Length > 0 ? $"{S(g["CurrentHorizontalResolution"])} × {S(g["CurrentVerticalResolution"])} @ {S(g["CurrentRefreshRate"])} Hz" : "";
            var gpu = new KartaPrehledu("Grafická karta", nazev)
                .R("Paměť grafiky", vram > 0 ? Gb(vram) : "")
                .R("Ovladač", $"{S(g["DriverVersion"])}" + (Datum(g["DriverDate"]) is { Length: > 0 } d ? $" ({d})" : ""))
                .R("Rozlišení", rozl);
            var stariOvl = 0.0;
            try { stariOvl = (DateTime.Now - ManagementDateTimeConverter.ToDateTime(S(g["DriverDate"]))).TotalDays; } catch { }
            gpu.D("Podrobnosti")
                .R("Výrobce", S(g["AdapterCompatibility"])).R("Čip", S(g["VideoProcessor"]))
                .R("Paměť grafiky", vram > 0 ? Gb(vram) : "").R("Typ paměti / DAC", S(g["AdapterDACType"]))
                .R("Verze ovladače", S(g["DriverVersion"]))
                .R("Datum ovladače", Datum(g["DriverDate"]), stariOvl > 365 ? Stav.Varovani : null)
                .R("Aktuální rozlišení", rozl).R("Barevná hloubka", S(g["CurrentBitsPerPixel"]) is { Length: > 0 } bpp ? $"{bpp} bitů" : "")
                .R("Max. obnovovací frekvence", S(g["MaxRefreshRate"]) is { Length: > 0 } mr && mr != "0" ? $"{mr} Hz" : "")
                .R("Stav", S(g["Status"]) == "OK" ? "v pořádku" : S(g["Status"]))
                .R("ID zařízení", S(g["PNPDeviceID"]));
            if (stariOvl > 365) gpu.D("Doporučení").R("Ovladač", "Ovladač je starší než rok – stáhněte aktuální od NVIDIA / AMD / Intel (lepší výkon ve hrách, opravy chyb).", Stav.Varovani);
            karty.Add(gpu);
        }

        // --- Monitory ---
        var mon = Wmi("SELECT InstanceName, ManufacturerName, UserFriendlyName, SerialNumberID, YearOfManufacture, WeekOfManufacture FROM WmiMonitorID", @"root\wmi");
        var monVel = Wmi("SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", @"root\wmi");
        if (mon.Count > 0)
        {
            var km = new KartaPrehledu("Monitory", mon.Count == 1 ? (Pole(mon[0]["UserFriendlyName"]) is { Length: > 0 } jm ? jm : "Monitor") : $"{mon.Count} monitory");
            var i = 0;
            foreach (var m in mon)
            {
                i++;
                var vel = monVel.FirstOrDefault(v => S(v["InstanceName"]) == S(m["InstanceName"]));
                var sirka = Convert.ToDouble(vel?["MaxHorizontalImageSize"] ?? 0); var vyska = Convert.ToDouble(vel?["MaxVerticalImageSize"] ?? 0);
                var uhl = sirka > 0 ? Math.Sqrt(sirka * sirka + vyska * vyska) / 2.54 : 0;
                var nazevM = Pole(m["UserFriendlyName"]) is { Length: > 0 } n ? n : $"Monitor {i}";
                km.R(nazevM, uhl > 0 ? $"{uhl:0.#}\" ({sirka:0}×{vyska:0} cm)" : "");
                km.D(nazevM)
                    .R("Výrobce (kód)", Pole(m["ManufacturerName"])).R("Model", Pole(m["UserFriendlyName"]))
                    .R("Úhlopříčka", uhl > 0 ? $"{uhl:0.#} palců" : "")
                    .R("Sériové číslo", Pole(m["SerialNumberID"]))
                    .R("Vyrobeno", S(m["YearOfManufacture"]) is { Length: > 0 } rok ? $"{rok}, týden {S(m["WeekOfManufacture"])}" : "");
            }
            karty.Add(km);
        }

        // --- Paměť ---
        karty.Add(Ram(cs));

        // --- Základní deska ---
        var bb = Wmi("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Wmi("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion, SerialNumber FROM Win32_BIOS").FirstOrDefault();
        var skrin = Wmi("SELECT ChassisTypes, SerialNumber FROM Win32_SystemEnclosure").FirstOrDefault();
        var sb = ZalohaService.CtiReg("HKLM", @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        var tpm = Wmi("SELECT SpecVersion FROM Win32_Tpm", @"root\CIMV2\Security\MicrosoftTpm").FirstOrDefault();
        var stariBios = 0.0;
        try { stariBios = (DateTime.Now - ManagementDateTimeConverter.ToDateTime(S(bios?["ReleaseDate"]))).TotalDays; } catch { }
        var deska = new KartaPrehledu("Základní deska", $"{S(bb?["Manufacturer"])} {S(bb?["Product"])}".Trim())
            .R("BIOS / UEFI", $"{S(bios?["SMBIOSBIOSVersion"])}" + (Datum(bios?["ReleaseDate"]) is { Length: > 0 } bd ? $" ({bd})" : ""))
            .R("Režim", sb == null ? "Legacy BIOS" : sb == "1" ? "UEFI, Secure Boot zapnutý" : "UEFI, Secure Boot vypnutý");
        deska.D("Základní deska")
            .R("Výrobce", S(bb?["Manufacturer"])).R("Model", S(bb?["Product"])).R("Revize", S(bb?["Version"]))
            .R("Sériové číslo desky", S(bb?["SerialNumber"]))
            .R("Typ skříně", Skrin(skrin?["ChassisTypes"]))
            .R("Sériové číslo / Service Tag", S(skrin?["SerialNumber"]) is { Length: > 0 } sn ? sn : S(bios?["SerialNumber"]));
        deska.D("BIOS / UEFI")
            .R("Výrobce", S(bios?["Manufacturer"])).R("Verze", S(bios?["SMBIOSBIOSVersion"]))
            .R("Datum vydání", Datum(bios?["ReleaseDate"]), stariBios > 3 * 365 ? Stav.Info : null)
            .R("SMBIOS", $"{S(bios?["SMBIOSMajorVersion"])}.{S(bios?["SMBIOSMinorVersion"])}")
            .R("Režim spouštění", sb == null ? "Legacy BIOS (CSM)" : "UEFI")
            .R("Secure Boot", sb == null ? "nepodporováno" : sb == "1" ? "zapnutý" : "vypnutý")
            .R("TPM", tpm == null ? "nenalezen" : $"verze {S(tpm["SpecVersion"]).Split(',')[0]}");
        if (stariBios > 3 * 365)
            deska.D("Doporučení").R("BIOS", "BIOS je starší než 3 roky. Aktualizace od výrobce desky může zlepšit stabilitu, podporu nových CPU a opravit bezpečnostní chyby. Aktualizujte opatrně (nevypínat PC během aktualizace).", Stav.Info);
        karty.Add(deska);

        // --- Disky ---
        var smart = SmartDisku();
        foreach (var d in Wmi("SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, SerialNumber, FirmwareVersion, SpindleSpeed FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
        {
            var media = Convert.ToInt32(d["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", _ => "" };
            var bus = Convert.ToInt32(d["BusType"] ?? 0) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 9 => "iSCSI", 12 => "SD", 13 => "MMC", _ => "" };
            var zdravi = Convert.ToInt32(d["HealthStatus"] ?? 0);
            var zdraviStav = zdravi switch { 0 => Stav.Ok, 1 => Stav.Varovani, 2 => Stav.Chyba, _ => (Stav?)null };
            smart.TryGetValue(S(d["DeviceId"]), out var r);
            var typ = $"{(bus == "NVMe" ? "SSD" : media)} {bus}".Trim();
            var disk = new KartaPrehledu("Disk", S(d["FriendlyName"]))
                .R("Typ", typ)
                .R("Kapacita", Gb(Convert.ToDouble(d["Size"] ?? 0)))
                .R("Stav", zdravi switch { 0 => "v pořádku", 1 => "VAROVÁNÍ", 2 => "PORUCHA", _ => "neznámý" }, zdraviStav);
            if (r?.Teplota > 0) disk.R("Teplota", $"{r.Teplota} °C", r.Teplota >= 70 ? Stav.Chyba : r.Teplota >= 55 ? Stav.Varovani : null);
            if (r?.Opotrebeni >= 0) disk.R("Opotřebení", $"{r.Opotrebeni} %", r.Opotrebeni >= 90 ? Stav.Chyba : r.Opotrebeni >= 70 ? Stav.Varovani : null);
            var dd = disk.D("Podrobnosti")
                .R("Model", S(d["FriendlyName"])).R("Typ / rozhraní", typ)
                .R("Otáčky", Convert.ToInt64(d["SpindleSpeed"] ?? 0) is > 0 and < 100000 and var ot ? $"{ot} ot./min" : "")
                .R("Kapacita", Gb(Convert.ToDouble(d["Size"] ?? 0)))
                .R("Sériové číslo", S(d["SerialNumber"])).R("Firmware", S(d["FirmwareVersion"]))
                .R("Stav (SMART)", zdravi switch { 0 => "v pořádku", 1 => "VAROVÁNÍ – zálohujte data", 2 => "PORUCHA – okamžitě zálohujte a vyměňte disk", _ => "neznámý" }, zdraviStav);
            if (r != null)
                dd.R("Teplota", r.Teplota > 0 ? $"{r.Teplota} °C (max. naměřená {r.TeplotaMax} °C)" : "")
                  .R("Opotřebení (SSD)", r.Opotrebeni >= 0 ? $"{r.Opotrebeni} % životnosti spotřebováno" : "")
                  .R("Hodin v provozu", r.Hodiny > 0 ? $"{r.Hodiny:N0} h (≈ {r.Hodiny / 24 / 365.0:0.#} roku)" : "")
                  .R("Počet zapnutí", r.Zapnuti > 0 ? $"{r.Zapnuti:N0}" : "")
                  .R("Chyby čtení / zápisu", $"{r.ChybyCteni} / {r.ChybyZapisu}", r.ChybyCteni + r.ChybyZapisu > 0 ? Stav.Varovani : null);
            karty.Add(disk);
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

    // ---------- RAM ----------

    private static readonly Dictionary<string, string> JedecVyrobci = new(StringComparer.OrdinalIgnoreCase)
    {
        ["80CE"] = "Samsung", ["CE00"] = "Samsung", ["802C"] = "Micron", ["2C00"] = "Micron", ["80AD"] = "SK hynix", ["AD00"] = "SK hynix",
        ["0198"] = "Kingston", ["9801"] = "Kingston", ["029E"] = "Corsair", ["9E02"] = "Corsair", ["04CD"] = "G.Skill", ["CD04"] = "G.Skill",
        ["859B"] = "Crucial", ["9B85"] = "Crucial", ["04CB"] = "ADATA", ["CB04"] = "ADATA", ["04EF"] = "TeamGroup", ["EF04"] = "TeamGroup",
        ["8551"] = "Qimonda", ["0420"] = "Patriot", ["2004"] = "Patriot", ["8304"] = "Goodram", ["0583"] = "Goodram",
        ["059B"] = "Crucial", ["017A"] = "Apacer", ["7A01"] = "Apacer", ["0101"] = "AMD", ["8983"] = "Intel",
    };

    private static string Vyrobce(string m)
    {
        var k = m.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (k.Length >= 4 && JedecVyrobci.TryGetValue(k[..4], out var v)) return v;
        return m.Length == 0 || m.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? "neuvedeno" : m;
    }

    private static string TypRam(int t) => t switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => ""
    };

    private static string Provedeni(int f) => f switch { 8 => "DIMM (stolní PC)", 12 => "SO-DIMM (notebook)", 0 => "", _ => $"typ {f}" };

    /// <summary>Písmeno kanálu ze štítku slotu (ChannelA-DIMM0, DIMM_A1, P0 CHANNEL B, BANK 2…).</summary>
    private static string? Kanal(string slot, string banka)
    {
        foreach (var t in new[] { slot, banka })
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"(?:CHANNEL|Channel|channel)\s*([A-H])|DIMM[_\s-]?([A-H])\d|\b([A-H])[12]\b");
            if (m.Success) return (m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value).ToUpperInvariant();
        }
        return null;
    }

    private static KartaPrehledu Ram(ManagementBaseObject? cs)
    {
        var moduly = Wmi("SELECT * FROM Win32_PhysicalMemory");
        var pole = Wmi("SELECT MemoryDevices, MaxCapacity, MaxCapacityEx FROM Win32_PhysicalMemoryArray");
        var sloty = pole.Sum(o => Convert.ToInt32(o["MemoryDevices"] ?? 0));
        var maxKb = pole.Sum(o => Math.Max(Convert.ToDouble(o["MaxCapacityEx"] ?? 0), Convert.ToDouble(o["MaxCapacity"] ?? 0)));
        var celkem = moduly.Sum(m => Convert.ToDouble(m["Capacity"] ?? 0));
        var prvni = moduly.FirstOrDefault();
        var typ = TypRam(Convert.ToInt32(prvni?["SMBIOSMemoryType"] ?? 0));
        int Cislo(ManagementBaseObject? m, string p) => Convert.ToInt32(m?[p] ?? 0);
        var aktualni = Cislo(prvni, "ConfiguredClockSpeed");
        var jmenovita = moduly.Count > 0 ? moduly.Max(m => Cislo(m, "Speed")) : 0;
        var kanaly = moduly.Select(m => Kanal(S(m["DeviceLocator"]), S(m["BankLabel"]))).Where(x => x != null).Distinct().Count();
        var rezim = moduly.Count switch
        {
            0 => "",
            1 => "single-channel (jeden modul)",
            _ => kanaly >= 2 ? (kanaly >= 4 ? "quad-channel" : "dual-channel") : kanaly == 1 ? "single-channel (moduly ve stejném kanálu)" : "pravděpodobně dual-channel"
        };
        var podTaktem = aktualni > 0 && jmenovita > aktualni + 100;
        var stejne = moduly.Select(m => $"{S(m["PartNumber"])}|{S(m["Capacity"])}").Distinct().Count() <= 1;

        var k = new KartaPrehledu("Operační paměť", $"{Gb(celkem > 0 ? celkem : Convert.ToDouble(cs?["TotalPhysicalMemory"] ?? 0))} {typ}".Trim())
            .R("Moduly", $"{moduly.Count}" + (sloty > 0 ? $" z {sloty} slotů" : ""))
            .R("Režim", rezim, rezim.StartsWith("single") && sloty > 1 ? Stav.Varovani : null)
            .R("Takt", aktualni > 0 ? $"{aktualni} MT/s" + (podTaktem ? $" (moduly umí {jmenovita})" : "") : "", podTaktem ? Stav.Varovani : null);

        var souhrn = k.D("Souhrn")
            .R("Celková kapacita", Gb(celkem))
            .R("Typ", $"{typ} {Provedeni(Cislo(prvni, "FormFactor"))}".Trim())
            .R("Osazeno", sloty > 0 ? $"{moduly.Count} z {sloty} slotů ({sloty - moduly.Count} volných)" : $"{moduly.Count} modulů")
            .R("Maximální podporovaná kapacita", maxKb > 0 ? Gb(maxKb * 1024) : "", null)
            .R("Režim kanálů", rezim, rezim.StartsWith("single") && sloty > 1 ? Stav.Varovani : null)
            .R("Aktuální takt", aktualni > 0 ? $"{aktualni} MT/s" : "")
            .R("Jmenovitý takt modulů", jmenovita > 0 ? $"{jmenovita} MT/s" : "")
            .R("XMP / EXPO", podTaktem ? "pravděpodobně VYPNUTO – paměti běží pod svou rychlostí" : jmenovita > 0 ? "paměti běží na jmenovité rychlosti" : "",
                podTaktem ? Stav.Varovani : null)
            .R("Stejné moduly", moduly.Count > 1 ? (stejne ? "ano" : "NE – různé moduly (může snížit takt a stabilitu)") : "", moduly.Count > 1 && !stejne ? Stav.Varovani : null);

        var i = 0;
        foreach (var m in moduly.OrderBy(m => S(m["DeviceLocator"])))
        {
            i++;
            var mv = Cislo(m, "ConfiguredVoltage"); var minV = Cislo(m, "MinVoltage"); var maxV = Cislo(m, "MaxVoltage");
            k.D($"Modul {i} – {S(m["DeviceLocator"])}")
                .R("Slot", $"{S(m["DeviceLocator"])}" + (S(m["BankLabel"]) is { Length: > 0 } bl ? $" ({bl})" : "") +
                           (Kanal(S(m["DeviceLocator"]), S(m["BankLabel"])) is { } kn ? $" · kanál {kn}" : ""))
                .R("Kapacita", Gb(Convert.ToDouble(m["Capacity"] ?? 0)))
                .R("Výrobce", Vyrobce(S(m["Manufacturer"])))
                .R("Označení (part number)", S(m["PartNumber"]))
                .R("Sériové číslo", S(m["SerialNumber"]) is { Length: > 0 } sn && !sn.StartsWith("0000000") ? sn : "")
                .R("Typ", $"{TypRam(Cislo(m, "SMBIOSMemoryType"))} {Provedeni(Cislo(m, "FormFactor"))}".Trim())
                .R("Jmenovitý takt", Cislo(m, "Speed") > 0 ? $"{Cislo(m, "Speed")} MT/s" : "")
                .R("Aktuální takt", Cislo(m, "ConfiguredClockSpeed") > 0 ? $"{Cislo(m, "ConfiguredClockSpeed")} MT/s" : "")
                .R("Napětí", mv > 0 ? $"{mv / 1000.0:0.00#} V" + (minV > 0 && maxV > 0 && minV != maxV ? $" (rozsah {minV / 1000.0:0.00}–{maxV / 1000.0:0.00} V)" : "") : "")
                .R("Šířka sběrnice", Cislo(m, "DataWidth") > 0 ? $"{Cislo(m, "DataWidth")} bitů" + (Cislo(m, "TotalWidth") > Cislo(m, "DataWidth") ? " (ECC)" : "") : "");
        }

        var rady = new List<string>();
        if (moduly.Count == 1 && sloty > 1) rady.Add("Jen jeden modul = single-channel. Přidáním druhého stejného modulu do správného slotu (obvykle A2 + B2) stoupne propustnost paměti až o 2× – znatelné hlavně ve hrách a u integrované grafiky.");
        if (kanaly == 1 && moduly.Count > 1) rady.Add("Moduly jsou ve stejném kanálu. Přesuňte je do slotů podle manuálu desky (obvykle A2 + B2), aby běžely v dual-channel.");
        if (podTaktem) rady.Add($"Paměti běží na {aktualni} MT/s, ale umí {jmenovita} MT/s. V BIOSu zapněte profil XMP (Intel) nebo EXPO/DOCP (AMD).");
        if (moduly.Count > 1 && !stejne) rady.Add("Kombinace různých modulů může vést k nižšímu taktu nebo nestabilitě. Ideální jsou dva stejné moduly (kit).");
        if (celkem > 0 && celkem < 8.0 * 1024 * 1024 * 1024) rady.Add("Méně než 8 GB RAM je dnes pro Windows 10/11 málo – doporučeno minimálně 16 GB.");
        if (rady.Count > 0)
        {
            var d = k.D("Doporučení");
            foreach (var r in rady) d.R("•", r, Stav.Varovani);
        }
        return k;
    }

    // ---------- SMART disků (Get-StorageReliabilityCounter) ----------

    private sealed record SmartInfo(int Teplota, int TeplotaMax, int Opotrebeni, long Hodiny, long Zapnuti, long ChybyCteni, long ChybyZapisu);

    private static Dictionary<string, SmartInfo> SmartDisku()
    {
        var vysledek = new Dictionary<string, SmartInfo>();
        try
        {
            var radky = new List<string>();
            ProcessRunner.PowerShellAsync(
                "Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; " +
                "'{0};{1};{2};{3};{4};{5};{6};{7}' -f $_.DeviceId,$r.Temperature,$r.TemperatureMax,$r.Wear,$r.PowerOnHours,$r.StartStopCycleCount,$r.ReadErrorsTotal,$r.WriteErrorsTotal }",
                new Sber(radky), CancellationToken.None).GetAwaiter().GetResult();
            foreach (var r in radky)
            {
                var c = r.Split(';');
                if (c.Length != 8 || !int.TryParse(c[0], out _)) continue;
                int I(string x, int def = 0) => int.TryParse(x, out var v) ? v : def;
                long L(string x) => long.TryParse(x, out var v) ? v : 0;
                vysledek[c[0]] = new SmartInfo(I(c[1]), I(c[2]), c[3].Length > 0 ? I(c[3], -1) : -1, L(c[4]), L(c[5]), L(c[6]), L(c[7]));
            }
        }
        catch { }
        return vysledek;
    }

    private sealed class Sber(List<string> l) : IProgress<string> { public void Report(string v) => l.Add(v); }

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
            foreach (var d in k.Detail)
            {
                var sd = z.PridejSekci($"{k.Nadpis} – {d.Nadpis}");
                foreach (var (p, h, st) in d.Radky) sd.Pridej(p, h, st);
            }
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
