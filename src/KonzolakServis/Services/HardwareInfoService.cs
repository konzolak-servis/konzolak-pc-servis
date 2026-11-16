using System.Management;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Přehled hardwaru přes WMI – vše česky a v lidských jednotkách.</summary>
public static class HardwareInfoService
{
    public static Task<VysledekAkce> ZpravaAsync(IProgress<string> log) => Task.Run(() =>
    {
        var z = new Zprava("Přehled hardwaru");
        Sekce(z, log, "Operační systém", "Win32_OperatingSystem", o => new[]
        {
            ("Systém", $"{o["Caption"]} {o["OSArchitecture"]}"),
            ("Verze / sestavení", $"{o["Version"]} (build {o["BuildNumber"]})"),
            ("Naposledy spuštěno", Datum(o["LastBootUpTime"])),
            ("Instalace systému", Datum(o["InstallDate"])),
        });
        Sekce(z, log, "Počítač", "Win32_ComputerSystem", o => new[]
        {
            ("Výrobce", $"{o["Manufacturer"]}"), ("Model", $"{o["Model"]}"),
            ("Celková paměť RAM", Gb(o["TotalPhysicalMemory"])),
        });
        Sekce(z, log, "Procesor", "Win32_Processor", o => new[]
        {
            ("Procesor", $"{o["Name"]}".Trim()),
            ("Jádra / vlákna", $"{o["NumberOfCores"]} / {o["NumberOfLogicalProcessors"]}"),
            ("Maximální frekvence", $"{o["MaxClockSpeed"]} MHz"),
        });
        Sekce(z, log, "Operační paměť", "Win32_PhysicalMemory", o => new[]
        {
            ($"Modul {o["DeviceLocator"]}", $"{Gb(o["Capacity"])}, {o["Speed"]} MHz, {o["Manufacturer"]} {o["PartNumber"]}".Trim()),
        });
        Sekce(z, log, "Základní deska a BIOS", "Win32_BaseBoard", o => new[]
        {
            ("Základní deska", $"{o["Manufacturer"]} {o["Product"]}"),
        });
        Sekce(z, log, "Základní deska a BIOS", "Win32_BIOS", o => new[]
        {
            ("BIOS / UEFI", $"{o["Manufacturer"]} {o["SMBIOSBIOSVersion"]} ({Datum(o["ReleaseDate"], false)})"),
        });
        Sekce(z, log, "Grafická karta", "Win32_VideoController", o => new[]
        {
            ($"{o["Name"]}", $"ovladač {o["DriverVersion"]}"),
        });
        Sekce(z, log, "Disky", "Win32_DiskDrive", o => new[]
        {
            ($"{o["Model"]}", $"{Gb(o["Size"])}, {o["InterfaceType"]}, stav: {Cesky($"{o["Status"]}")}"),
        });
        Sekce(z, log, "Oddíly a volné místo", "Win32_LogicalDisk WHERE DriveType=3", o =>
        {
            var vol = Convert.ToDouble(o["FreeSpace"] ?? 0); var cel = Convert.ToDouble(o["Size"] ?? 1);
            return new[] { ($"{o["DeviceID"]} {o["VolumeName"]}".Trim(),
                $"volno {Gb(o["FreeSpace"])} z {Gb(o["Size"])} ({vol * 100 / cel:0} %), {o["FileSystem"]}") };
        });
        Sekce(z, log, "Síťové adaptéry", "Win32_NetworkAdapter WHERE PhysicalAdapter=True", o => new[]
        {
            ($"{o["Name"]}", $"{o["MACAddress"]}"),
        });

        // barevné hodnocení volného místa
        var oddily = z.Sekce.FirstOrDefault(s => s.Nadpis == "Oddíly a volné místo");
        if (oddily != null)
            for (var i = 0; i < oddily.Radky.Count; i++)
            {
                var r = oddily.Radky[i];
                var m = System.Text.RegularExpressions.Regex.Match(r.Hodnota, @"\((\d+) %\)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var p))
                    oddily.Radky[i] = r with { Stav = p < 10 ? Stav.Chyba : p < 20 ? Stav.Varovani : Stav.Ok };
            }

        z.Stav = Stav.Info;
        z.Shrnuti = $"Hardware načten: {z.Sekce.Sum(s => s.Radky.Count)} položek.";
        return new VysledekAkce(Stav.Ok, z.Shrnuti) { Zprava = z };
    });

    private static void Sekce(Zprava z, IProgress<string> log, string nadpis, string dotaz,
        Func<ManagementBaseObject, (string Popis, string Hodnota)[]> f)
    {
        var sekce = z.Sekce.FirstOrDefault(s => s.Nadpis == nadpis) ?? z.PridejSekci(nadpis);
        log.Report($"== {nadpis} ==");
        try
        {
            var parts = dotaz.Split(" WHERE ", 2);
            var q = $"SELECT * FROM {parts[0]}" + (parts.Length > 1 ? $" WHERE {parts[1]}" : "");
            using var s = new ManagementObjectSearcher(q);
            foreach (var o in s.Get())
                foreach (var (p, h) in f(o))
                {
                    sekce.Pridej(p, h.Trim().Trim(','));
                    log.Report($"  {p}: {h}");
                }
        }
        catch (Exception ex)
        {
            sekce.Pridej("Nelze zjistit", ex.Message, Stav.Varovani);
            log.Report($"  Nelze zjistit: {ex.Message}");
        }
        if (sekce.Radky.Count == 0) z.Sekce.Remove(sekce);
    }

    private static string Datum(object? wmi, bool cas = true)
    {
        try
        {
            var d = ManagementDateTimeConverter.ToDateTime((string)wmi!);
            return cas ? d.ToString("d. M. yyyy HH:mm") : d.ToString("d. M. yyyy");
        }
        catch { return "neznámé"; }
    }

    private static string Gb(object? bytes) =>
        bytes is null ? "?" : $"{Convert.ToDouble(bytes) / 1024 / 1024 / 1024:0.#} GB";

    private static string Cesky(string s) => s switch
    {
        "OK" => "v pořádku", "Pred Fail" => "HROZÍ SELHÁNÍ", "" => "neznámý", _ => s
    };
}
