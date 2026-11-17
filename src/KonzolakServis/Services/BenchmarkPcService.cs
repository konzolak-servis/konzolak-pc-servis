using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>
/// Benchmark celého PC zdarma: vlastní test procesoru (1 jádro / všechna jádra) +
/// vestavěný nástroj Windows WinSAT (procesor, paměť, grafika, disk) se srovnatelným skóre 1,0–9,9.
/// </summary>
public static class BenchmarkPcService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    private static bool NaBaterii() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    public static async Task<VysledekAkce> SpustAsync(IProgress<string> log, CancellationToken ct)
    {
        var z = await ZpravaAsync(log, ct);
        return new VysledekAkce(z.Stav, z.Shrnuti) { Zprava = z };
    }

    public static async Task<Zprava> ZpravaAsync(IProgress<string> log, CancellationToken ct)
    {
        var z = new Zprava("Benchmark PC");
        var baterie = NaBaterii();

        // ---------- 1) vlastní test procesoru ----------
        log.Report("Test procesoru – jedno jádro (3 s)…");
        var jedno = await Task.Run(() => MerCpu(1, ct), ct);
        var vlaken = Environment.ProcessorCount;
        log.Report($"Test procesoru – všechna vlákna ({vlaken}) (3 s)…");
        var vse = await Task.Run(() => MerCpu(vlaken, ct), ct);
        var skalovani = jedno > 0 ? vse / jedno : 0;
        log.Report($"Jedno jádro: {jedno:0.0} mil. operací/s · všechna vlákna: {vse:0.0} mil. operací/s · {skalovani:0.0}×");

        // ---------- 2) WinSAT ----------
        string? xml = null;
        int rc = -1;
        if (baterie)
            log.Report("! Notebook běží na baterii – Windows test WinSAT na baterii nespustí. Připojte nabíječku.");
        else
        {
            log.Report("Spouštím test Windows (WinSAT) – procesor, paměť, grafika, disk. Trvá 1–3 minuty, obrazovka může krátce probliknout…");
            var start = DateTime.Now.AddSeconds(-5);
            try { rc = await ProcessRunner.RunAsync("winsat.exe", "formal", log, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.Report($"! WinSAT nelze spustit: {ex.Message}"); }
            xml = NejnovejsiXml(start);
        }

        var winsat = xml != null ? Nacti(xml) : null;

        // ---------- výsledek ----------
        var c = z.PridejSekci("Procesor – vlastní test Konzolák");
        c.Popis = "Výpočetní test (celá čísla + desetinná čísla) po 3 s. Čím vyšší číslo, tím rychlejší procesor.";
        c.Pridej("Jedno jádro", $"{jedno:0.0} mil. operací/s", null,
            "Rozhoduje o svižnosti běžné práce – Windows, prohlížeč, kancelář, většina her.");
        c.Pridej($"Všechna vlákna ({vlaken})", $"{vse:0.0} mil. operací/s", null,
            "Rozhoduje u náročné práce – střih videa, komprese, renderování.");
        c.Pridej("Využití více jader", $"{skalovani:0.0}× výkon jednoho jádra", skalovani < vlaken * 0.35 && vlaken > 2 ? Stav.Varovani : Stav.Ok,
            skalovani < vlaken * 0.35 && vlaken > 2
                ? "Procesor při plné zátěži výrazně zpomaluje – možné přehřívání (prach, vyschlá teplovodivá pasta) nebo úsporný režim napájení."
                : "Procesor rozkládá práci na jádra očekávaně.");

        if (winsat != null)
        {
            var s = z.PridejSekci("Skóre Windows (WinSAT)");
            s.Popis = "Stupnice 1,0–9,9 jako ve starém Indexu výkonu Windows. Celkové skóre = nejslabší část počítače.";
            void Skore(string nazev, double hodnota, string vysvetleni)
            {
                if (hodnota <= 0) return;
                s.Pridej(nazev, $"{hodnota:0.0} / 9,9 – {Slovne(hodnota)}  {Pruh(hodnota)}", StavSkore(hodnota), vysvetleni);
            }
            Skore("Procesor", winsat.Cpu, "Výpočty, komprese, šifrování.");
            Skore("Paměť RAM", winsat.Pamet, "Propustnost operační paměti.");
            Skore("Grafika (pracovní plocha)", winsat.Grafika, "Plynulost Windows, videa a oken.");
            Skore("Grafika (hry, 3D)", winsat.Hry, "Orientační 3D výkon – pro hry rozhodují spíše konkrétní hry a ovladač.");
            Skore("Systémový disk", winsat.Disk, "Rychlost startu Windows a programů.");

            var nejslabsi = new[] { ("procesor", winsat.Cpu), ("paměť RAM", winsat.Pamet), ("grafika", winsat.Grafika), ("disk", winsat.Disk) }
                .Where(x => x.Item2 > 0).OrderBy(x => x.Item2).FirstOrDefault();
            var celkem = winsat.Celkem > 0 ? winsat.Celkem : nejslabsi.Item2;
            s.Radky.Insert(0, new Radek("Celkové skóre PC", $"{celkem:0.0} / 9,9 – {Slovne(celkem)}", StavSkore(celkem),
                nejslabsi.Item1 != null ? $"Nejslabší část: {nejslabsi.Item1} ({nejslabsi.Item2:0.0})." : ""));

            if (winsat.Metriky.Count > 0)
            {
                var m = z.PridejSekci("Naměřené hodnoty");
                m.Sbalena = true;
                foreach (var (jmenoMetriky, hodnotaMetriky) in winsat.Metriky) m.Pridej(jmenoMetriky, hodnotaMetriky);
            }

            var dop = z.PridejSekci("Doporučení");
            if (winsat.Disk > 0 && winsat.Disk < 6)
                dop.Pridej("Systém je na pomalém disku – výměna za SSD je nejlevnější a nejvýraznější zrychlení.", "", Stav.Varovani);
            if (winsat.Pamet > 0 && winsat.Pamet < 5.5)
                dop.Pridej("Paměť je pomalá – zkontrolujte, zda běží ve dvoukanálovém režimu (Přehled → Paměť RAM → Podrobnosti) a zda je jí dost.", "", Stav.Varovani);
            if (winsat.Cpu > 0 && winsat.Cpu < 5)
                dop.Pridej("Procesor je slabší – pro plynulost omezte programy po spuštění a doplňky prohlížeče.", "", Stav.Info);
            if (winsat.Grafika > 0 && winsat.Grafika < 5)
                dop.Pridej("Grafika je slabá – aktualizujte ovladač grafiky, u notebooku zkontrolujte, že se používá výkonnější grafika.", "", Stav.Info);
            if (dop.Radky.Count == 0)
                dop.Pridej("Počítač nemá výrazně slabé místo.", "", Stav.Ok);
            dop.Pridej("Pro přesný výsledek měřte s připojenou nabíječkou, v režimu napájení „Vysoký výkon“ a bez dalších spuštěných programů.", "", Stav.Info);

            z.Stav = StavSkore(celkem);
            z.Shrnuti = $"Skóre PC {celkem:0.0} / 9,9 ({Slovne(celkem)}) · procesor {winsat.Cpu:0.0} · RAM {winsat.Pamet:0.0} · grafika {winsat.Grafika:0.0} · disk {winsat.Disk:0.0}";
        }
        else
        {
            var s = z.PridejSekci("Skóre Windows (WinSAT)");
            s.Pridej(baterie ? "Neměřeno – notebook běží na baterii" : "Test Windows se nepodařilo dokončit", rc >= 0 ? $"kód {rc}" : "",
                Stav.Varovani,
                baterie ? "Připojte nabíječku a spusťte benchmark znovu." :
                    "WinSAT může chybět v některých edicích Windows nebo ho blokuje jiný běžící test. Výsledek procesoru výše platí.");
            z.Stav = Stav.Info;
            z.Shrnuti = $"Procesor: 1 jádro {jedno:0.0} mil. op./s, všechna vlákna {vse:0.0} mil. op./s · skóre Windows neměřeno";
        }
        // ---------- porovnání s typickými výsledky a historie ----------
        log.Report("Porovnávám s typickými výsledky pro tento hardware…");
        var hw = BenchmarkSrovnani.ZjistiHw();
        if (winsat != null)
        {
            var p = z.PridejSekci("Porovnání s obvyklými výsledky pro tento hardware");
            p.Popis = "Orientační rozpětí skóre, kterého běžně dosahují počítače se stejnou třídou hardwaru. Tlačítko otevře srovnání konkrétního modelu na internetu.";
            BenchmarkSrovnani.Porovnej(p, $"Procesor – {hw.Procesor}", winsat.Cpu, BenchmarkSrovnani.TypickyProcesor(hw.Procesor),
                "přehřívání (prach, pasta), úsporný režim napájení, notebook na baterii, programy na pozadí, starší generace procesoru.", hw.Procesor);
            BenchmarkSrovnani.Porovnej(p, $"Paměť RAM – {hw.TypRam}", winsat.Pamet, BenchmarkSrovnani.TypickaPamet(hw.TypRam),
                "paměť jen v jednom kanálu (jeden modul), nízký takt, vypnutý XMP/EXPO v BIOSu.");
            BenchmarkSrovnani.Porovnej(p, $"Grafika – {hw.Grafika}", winsat.Grafika, BenchmarkSrovnani.TypickaGrafika(hw.SamostatnaGrafika),
                "starý nebo obecný ovladač grafiky, notebook používá integrovanou grafiku místo výkonné, úsporný režim.", hw.Grafika);
            BenchmarkSrovnani.Porovnej(p, $"Systémový disk – {hw.Disk}", winsat.Disk, BenchmarkSrovnani.TypickyDisk(hw.TypDisku),
                "téměř plný disk, opotřebení nebo vadná místa (viz Disky → Kvalita), řadič v režimu IDE místo AHCI, pomalý slot.", hw.Disk);
            if (p.Radky.Count == 0) z.Sekce.Remove(p);
        }

        var zaznam = new ZaznamBenchmarku(DateTime.Now, jedno, vse, winsat?.Celkem ?? 0, winsat?.Cpu ?? 0, winsat?.Pamet ?? 0,
            winsat?.Grafika ?? 0, winsat?.Disk ?? 0, hw.Procesor);
        var historie = BenchmarkSrovnani.Historie();
        BenchmarkSrovnani.PridejHistorii(z, zaznam, historie);
        BenchmarkSrovnani.Uloz(zaznam);
        log.Report($"Výsledek uložen do historie: {BenchmarkSrovnani.Slozka}");
        return z;
    }

    private static string Slovne(double s) => s switch
    {
        >= 8.5 => "výborné", >= 7 => "dobré", >= 5 => "průměrné", _ => "slabé"
    };

    private static Stav StavSkore(double s) => s >= 7 ? Stav.Ok : s >= 5 ? Stav.Info : Stav.Varovani;

    private static string Pruh(double s) => new string('█', (int)Math.Clamp(Math.Round(s * 2), 1, 20));

    /// <summary>Výpočetní smyčka – celá čísla (xorshift) i desetinná čísla. Vrací miliony iterací za sekundu.</summary>
    private static double MerCpu(int vlaken, CancellationToken ct)
    {
        var konec = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
        long celkem = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, vlaken, new ParallelOptions { MaxDegreeOfParallelism = vlaken, CancellationToken = ct }, _ =>
        {
            ulong x = 88172645463325252UL;
            double f = 1.0001;
            long n = 0;
            while (Stopwatch.GetTimestamp() < konec)
            {
                for (var i = 0; i < 100_000; i++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    f = f * 1.0000001 + (x & 0xFF) * 1e-9;
                    if (f > 1e6) f = 1.0001;
                }
                n += 100_000;
            }
            if (f < 0 || x == 0) n++;   // zabrání kompilátoru smyčku vyhodit
            Interlocked.Add(ref celkem, n);
        });
        return celkem / sw.Elapsed.TotalSeconds / 1e6;
    }

    private static string? NejnovejsiXml(DateTime po)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Performance", "WinSAT", "DataStore");
            if (!Directory.Exists(dir)) return null;
            return new DirectoryInfo(dir).GetFiles("*Formal.Assessment*.WinSAT.xml")
                .Where(f => f.LastWriteTime >= po)
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    private sealed record WinSat(double Celkem, double Cpu, double Pamet, double Grafika, double Hry, double Disk,
        List<(string Nazev, string Hodnota)> Metriky);

    private static readonly Dictionary<string, string> NazvyMetrik = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CompressionMetric"] = "Procesor – komprese",
        ["EncryptionMetric"] = "Procesor – šifrování AES",
        ["Compression2Metric"] = "Procesor – komprese (2. test)",
        ["Encryption2Metric"] = "Procesor – šifrování SHA1",
        ["DshowEncodeTime"] = "Procesor – převod videa",
        ["Bandwidth"] = "Paměť RAM – propustnost",
        ["DWMFps"] = "Grafika – snímky pracovní plochy",
        ["VideoMemBandwidth"] = "Grafika – propustnost video paměti",
        ["MFVideoDecodeDur"] = "Grafika – dekódování videa",
        ["AllFps"] = "Grafika 3D – snímky/s",
    };

    private static WinSat? Nacti(string cesta)
    {
        try
        {
            var doc = XDocument.Load(cesta);
            double D(string jmeno) =>
                double.TryParse(doc.Descendants(jmeno).FirstOrDefault()?.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            var metriky = new List<(string, string)>();
            var m = doc.Descendants("Metrics").FirstOrDefault();
            if (m != null)
            {
                foreach (var e in m.Descendants().Where(e => !e.HasElements))
                {
                    var jednotky = (string?)e.Attribute("units") ?? "";
                    if (!double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
                    var druh = (string?)e.Attribute("kind");
                    string? nazev = null;
                    if (e.Name.LocalName == "AvgThroughput" && druh != null)
                        nazev = "Disk – " + druh switch
                        {
                            "Sequential Read" => "sekvenční čtení",
                            "Sequential Write" => "sekvenční zápis",
                            "Random Read" => "náhodné čtení",
                            "Random Write" => "náhodný zápis",
                            _ => druh
                        } + ((string?)e.Attribute("ioSize") is { } io && long.TryParse(io, out var b) ? $" ({b / 1024} KB bloky)" : "");
                    else if (NazvyMetrik.TryGetValue(e.Name.LocalName, out var n))
                        nazev = n;
                    if (nazev != null) metriky.Add((nazev, $"{v:0.##} {jednotky}".Trim()));
                }
            }
            return new WinSat(D("SystemScore"), D("CpuScore"), D("MemoryScore"), D("GraphicsScore"), D("GamingScore"), D("DiskScore"), metriky);
        }
        catch { return null; }
    }
}
