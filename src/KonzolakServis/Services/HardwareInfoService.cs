using System.Management;

namespace KonzolakServis.Services;

/// <summary>Přehled hardwaru přes WMI – vše česky a v lidských jednotkách.</summary>
public static class HardwareInfoService
{
    public static Task VypisAsync(IProgress<string> log) => Task.Run(() =>
    {
        Sekce(log, "Operační systém", "Win32_OperatingSystem", o =>
            $"{o["Caption"]} {o["OSArchitecture"]}, sestavení {o["BuildNumber"]}, " +
            $"spuštěno od {ManagementDateTimeConverter.ToDateTime((string)o["LastBootUpTime"]):d. M. yyyy HH:mm}");
        Sekce(log, "Základní deska", "Win32_BaseBoard", o => $"{o["Manufacturer"]} {o["Product"]}");
        Sekce(log, "BIOS", "Win32_BIOS", o => $"{o["Manufacturer"]} {o["SMBIOSBIOSVersion"]}");
        Sekce(log, "Procesor", "Win32_Processor", o =>
            $"{((string)o["Name"]).Trim()} – {o["NumberOfCores"]} jader / {o["NumberOfLogicalProcessors"]} vláken, {o["MaxClockSpeed"]} MHz");
        Sekce(log, "Operační paměť", "Win32_PhysicalMemory", o =>
            $"{Gb(o["Capacity"])} {o["Manufacturer"]} {o["Speed"]} MHz ({o["DeviceLocator"]})");
        Sekce(log, "Grafická karta", "Win32_VideoController", o =>
            $"{o["Name"]}, ovladač {o["DriverVersion"]}");
        Sekce(log, "Disky", "Win32_DiskDrive", o =>
            $"{o["Model"]} – {Gb(o["Size"])}, rozhraní {o["InterfaceType"]}, stav: {Cesky((string?)o["Status"])}");
        Sekce(log, "Oddíly", "Win32_LogicalDisk WHERE DriveType=3", o =>
            $"{o["DeviceID"]} {o["FileSystem"]} – volno {Gb(o["FreeSpace"])} z {Gb(o["Size"])}");
        Sekce(log, "Síťové adaptéry", "Win32_NetworkAdapter WHERE PhysicalAdapter=True", o =>
            $"{o["Name"]} ({o["MACAddress"]})");
        Sekce(log, "Baterie", "Win32_Battery", o => $"{o["Name"]}, nabití {o["EstimatedChargeRemaining"]} %");
    });

    private static void Sekce(IProgress<string> log, string nadpis, string dotaz, Func<ManagementBaseObject, string> f)
    {
        log.Report($"== {nadpis} ==");
        try
        {
            var parts = dotaz.Split(" WHERE ", 2);
            var q = $"SELECT * FROM {parts[0]}" + (parts.Length > 1 ? $" WHERE {parts[1]}" : "");
            using var s = new ManagementObjectSearcher(q);
            var any = false;
            foreach (var o in s.Get()) { log.Report("  " + f(o)); any = true; }
            if (!any) log.Report("  (nenalezeno)");
        }
        catch (Exception ex) { log.Report($"  Nelze zjistit: {ex.Message}"); }
    }

    private static string Gb(object? bytes) =>
        bytes is null ? "?" : $"{Convert.ToDouble(bytes) / 1024 / 1024 / 1024:0.#} GB";

    private static string Cesky(string? s) => s switch
    {
        "OK" => "v pořádku", "Pred Fail" => "HROZÍ SELHÁNÍ", null => "neznámý", _ => s
    };
}
