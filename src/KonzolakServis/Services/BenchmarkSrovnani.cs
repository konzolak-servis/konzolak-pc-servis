using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Jeden uložený běh benchmarku (Dokumenty\Konzolák Servis\Historie\benchmark_*.json).</summary>
public sealed record ZaznamBenchmarku(DateTime Cas, double Cpu1, double CpuVse, double Celkem, double Cpu, double Pamet,
    double Grafika, double Disk, string Procesor);

/// <summary>Historie testů a porovnání s typickými výsledky pro daný typ hardwaru.</summary>
public static class BenchmarkSrovnani
{
    /// <summary>Dokumenty\Konzolák Servis\Historie – jeden soubor na každý test (vedle složky Logy).</summary>
    public static string Slozka => Path.Combine(Path.GetDirectoryName(LogService.SlozkaLogu)!, "Historie");

    public static List<ZaznamBenchmarku> Historie()
    {
        var l = new List<ZaznamBenchmarku>();
        try
        {
            if (!Directory.Exists(Slozka)) return l;
            foreach (var f in Directory.GetFiles(Slozka, "benchmark_*.json"))
                try { if (JsonSerializer.Deserialize<ZaznamBenchmarku>(File.ReadAllText(f)) is { } z) l.Add(z); } catch { }
        }
        catch { }
        return l.OrderBy(x => x.Cas).ToList();
    }

    public static void Uloz(ZaznamBenchmarku z)
    {
        try
        {
            Directory.CreateDirectory(Slozka);
            File.WriteAllText(Path.Combine(Slozka, $"benchmark_{z.Cas:yyyy-MM-dd_HH-mm-ss}.json"),
                JsonSerializer.Serialize(z, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static AkceRadku OtevritSlozku() => new("Otevřít složku historie", (_, _) =>
    {
        Directory.CreateDirectory(Slozka);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Slozka}\"") { UseShellExecute = true });
        return Task.FromResult("Složka s historií otevřena.");
    });

    private static string S(object? o) => o?.ToString()?.Trim() ?? "";

    private static List<ManagementBaseObject> Wmi(string dotaz)
    {
        var l = new List<ManagementBaseObject>();
        try { using var s = new ManagementObjectSearcher(dotaz); foreach (var o in s.Get()) l.Add(o); } catch { }
        return l;
    }

    public sealed record Hw(string Procesor, string Grafika, bool SamostatnaGrafika, string TypRam, string TypDisku, string Disk);

    public static Hw ZjistiHw()
    {
        var cpu = S(Wmi("SELECT Name FROM Win32_Processor").FirstOrDefault()?["Name"]);
        var gpu = Wmi("SELECT Name FROM Win32_VideoController").Select(g => S(g["Name"]))
            .Where(n => n.Length > 0 && !n.Contains("Basic", StringComparison.OrdinalIgnoreCase) && !n.Contains("Remote", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Samostatna).FirstOrDefault() ?? "";
        var typ = Wmi("SELECT SMBIOSMemoryType FROM Win32_PhysicalMemory").Select(m => Convert.ToInt32(m["SMBIOSMemoryType"] ?? 0)).FirstOrDefault() switch
        {
            24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 29 => "LPDDR3", 30 => "LPDDR4", 35 => "LPDDR5", _ => ""
        };
        var sys = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0];
        var disk = DiskService.SeznamDisku().FirstOrDefault(d => d.Pismena.Contains(char.ToUpperInvariant(sys)));
        var typDisku = disk == null ? "" : disk.Rozhrani == "NVMe" ? "NVMe" : disk.Ssd ? "SSD" : disk.Typ.StartsWith("HDD") ? "HDD" : "";
        return new Hw(cpu, gpu, Samostatna(gpu), typ, typDisku, disk?.Nazev ?? "");
    }

    private static bool Samostatna(string gpu) =>
        new[] { "GeForce", "RTX", "GTX", "Quadro", "Radeon RX", "Radeon Pro", "Arc A", "Arc B", "FirePro" }
            .Any(k => gpu.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>Typické rozpětí skóre WinSAT podle třídy hardwaru (orientační hodnoty z běžně publikovaných výsledků).</summary>
    public static (string Trida, double Od, double Do)? TypickyProcesor(string n)
    {
        bool M(params string[] k) => k.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase));
        if (M("Atom", "Celeron N", "Pentium N", "Pentium Silver", "Celeron J", "Pentium J", " N100", " N200", " N95", " N97", "Athlon Silver", "Athlon Gold 3", "A4-", "A6-", "E1-", "E2-"))
            return ("úsporný procesor (Celeron/Pentium N, Atom, Athlon)", 4.5, 6.8);
        if (M("Core 2", "Core(TM)2", "Athlon II", "Phenom", "FX-"))
            return ("starší procesor (Core 2, Phenom, FX)", 5.0, 7.3);
        if (M("i9-", "Ultra 9", "Ryzen 9", "Threadripper", "Core 9"))
            return ("nejvyšší třída (Core i9 / Ultra 9 / Ryzen 9)", 9.2, 9.9);
        if (M("i7-", "Ultra 7", "Ryzen 7", "Core 7"))
            return ("vyšší třída (Core i7 / Ultra 7 / Ryzen 7)", 8.6, 9.7);
        if (M("i5-", "Ultra 5", "Ryzen 5", "Core 5"))
            return ("střední třída (Core i5 / Ultra 5 / Ryzen 5)", 8.1, 9.4);
        if (M("i3-", "Ryzen 3", "Core 3"))
            return ("základní třída (Core i3 / Ryzen 3)", 7.4, 8.8);
        if (M("Celeron", "Pentium", "Athlon"))
            return ("základní procesor (Celeron / Pentium / Athlon)", 6.3, 8.1);
        if (M("Xeon", "EPYC"))
            return ("serverový procesor (Xeon / EPYC)", 7.8, 9.8);
        return null;
    }

    public static (string Trida, double Od, double Do)? TypickaPamet(string typ) => typ switch
    {
        "DDR3" or "LPDDR3" => ("DDR3", 7.0, 8.1),
        "DDR4" or "LPDDR4" => ("DDR4", 8.0, 9.0),
        "DDR5" or "LPDDR5" => ("DDR5", 8.7, 9.7),
        _ => null
    };

    public static (string Trida, double Od, double Do)? TypickyDisk(string typ) => typ switch
    {
        "HDD" => ("klasický HDD", 5.5, 7.0),
        "SSD" => ("SATA SSD", 7.7, 8.4),
        "NVMe" => ("NVMe SSD", 8.2, 9.9),
        _ => null
    };

    public static (string Trida, double Od, double Do) TypickaGrafika(bool samostatna) =>
        samostatna ? ("samostatná grafická karta", 8.0, 9.9) : ("integrovaná grafika", 6.0, 8.8);

    /// <summary>Řádek porovnání: kde je naměřená hodnota vůči typickému rozpětí.</summary>
    public static void Porovnej(SekceZpravy s, string co, double skore, (string Trida, double Od, double Do)? typ, string priciny,
        string? hledat = null)
    {
        if (skore <= 0 || typ == null) return;
        var (trida, od, dok) = typ.Value;
        var (text, stav) = skore < od - 0.3 ? ("POD OČEKÁVÁNÍM", Stav.Varovani)
            : skore > dok ? ("nad očekáváním", Stav.Ok) : ("odpovídá", Stav.Ok);
        var akce = hledat is { Length: > 0 }
            ? new[] { new AkceRadku("Porovnat na internetu", (_, _) =>
                {
                    Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={Uri.EscapeDataString(hledat + " benchmark")}") { UseShellExecute = true });
                    return Task.FromResult("Výsledky srovnání otevřeny v prohlížeči.");
                }) }
            : Array.Empty<AkceRadku>();
        s.Radky.Add(new Radek(co, $"{skore:0.0} – {text} (typicky {od:0.0}–{dok:0.0})", stav,
            $"Srovnávací třída: {trida}." + (stav == Stav.Varovani ? " Možné příčiny: " + priciny : "")) { Akce = akce });
    }

    /// <summary>Historie: posledních 10 běhů a změna proti minulému testu.</summary>
    public static void PridejHistorii(Zprava z, ZaznamBenchmarku aktualni, List<ZaznamBenchmarku> predchozi)
    {
        var h = z.PridejSekci("Historie testů na tomto PC");
        h.Popis = $"Každý test se ukládá do složky {Slozka}.";
        h.Radky.Add(new Radek("Uložené testy", $"{predchozi.Count + 1}", null, Slozka) { Akce = new[] { OtevritSlozku() } });
        if (predchozi.Count == 0)
        {
            h.Pridej("Toto je první test", "", Stav.Info, "Další testy se porovnají s tímto výsledkem – např. před a po vyčištění nebo upgradu.");
            return;
        }
        var minule = predchozi[^1];
        string Zmena(double ted, double pred) => pred <= 0 || ted <= 0 ? "" : $"{(ted - pred) / pred * 100:+0;-0;0} %";
        var cpuZmena = minule.Cpu1 > 0 ? (aktualni.Cpu1 - minule.Cpu1) / minule.Cpu1 * 100 : 0;
        h.Pridej($"Proti minulému testu ({minule.Cas:d. M. yyyy HH:mm})",
            $"procesor 1 jádro {Zmena(aktualni.Cpu1, minule.Cpu1)} · všechna jádra {Zmena(aktualni.CpuVse, minule.CpuVse)}"
            + (aktualni.Celkem > 0 && minule.Celkem > 0 ? $" · skóre {minule.Celkem:0.0} → {aktualni.Celkem:0.0}" : ""),
            cpuZmena < -15 ? Stav.Varovani : cpuZmena > 5 ? Stav.Ok : null,
            cpuZmena < -15 ? "Výkon výrazně klesl – zkontrolujte teploty, prach v chladiči, režim napájení a programy na pozadí."
                : !string.Equals(minule.Procesor, aktualni.Procesor, StringComparison.OrdinalIgnoreCase) ? "Pozor: minulý test proběhl s jiným procesorem." : "");
        foreach (var r in predchozi.AsEnumerable().Reverse().Take(10))
            h.Pridej($"{r.Cas:d. M. yyyy HH:mm}",
                $"1 jádro {r.Cpu1:0} · všechna {r.CpuVse:0} mil. op./s" + (r.Celkem > 0 ? $" · skóre {r.Celkem:0.0} (CPU {r.Cpu:0.0}, RAM {r.Pamet:0.0}, grafika {r.Grafika:0.0}, disk {r.Disk:0.0})" : ""));
    }
}
