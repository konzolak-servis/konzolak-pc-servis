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

        // ---------- 3) každá grafická karta zvlášť (notebooky s hybridní grafikou) ----------
        var gpuTesty = new List<TestGpu>();
        var grafiky = BenchmarkSrovnani.Grafiky();
        if (!baterie && grafiky.Count >= 2)
        {
            log.Report($"Nalezeny {grafiky.Count} grafiky: {string.Join(", ", grafiky)} – testuji každou zvlášť…");
            foreach (var (pref, rezim) in new[] { (2, "výkonná"), (1, "úsporná") })
            {
                ct.ThrowIfCancellationRequested();
                var t = await TestujGpuAsync(pref, rezim, log, ct);
                if (t != null) gpuTesty.Add(t);
            }
        }

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
                s.PridejPruh(nazev, $"{hodnota:0.0} / 9,9 – {Slovne(hodnota)}", hodnota / 9.9, StavSkore(hodnota), vysvetleni);
            }
            Skore("Procesor", winsat.Cpu, "Výpočty, komprese, šifrování.");
            Skore("Paměť RAM", winsat.Pamet, "Propustnost operační paměti.");
            Skore("Grafika" + (winsat.Adapter.Length > 0 ? $" – {winsat.Adapter}" : ""), winsat.Grafika,
                "Plynulost Windows, videa a oken. Testována grafika, kterou Windows použil pro tento test" + (winsat.Adapter.Length > 0 ? $": {winsat.Adapter}." : "."));
            Skore("Systémový disk", winsat.Disk, "Rychlost startu Windows a programů.");

            var nejslabsi = new[] { ("procesor", winsat.Cpu), ("paměť RAM", winsat.Pamet), ("grafika", winsat.Grafika), ("disk", winsat.Disk) }
                .Where(x => x.Item2 > 0).OrderBy(x => x.Item2).FirstOrDefault();
            var celkem = winsat.Celkem > 0 ? winsat.Celkem : nejslabsi.Item2;
            s.Radky.Insert(0, new Radek("Celkové skóre PC", $"{celkem:0.0} / 9,9 – {Slovne(celkem)}", StavSkore(celkem),
                nejslabsi.Item1 != null ? $"Nejslabší část: {nejslabsi.Item1} ({nejslabsi.Item2:0.0})." : "") { Pruh = Math.Clamp(celkem / 9.9, 0.02, 1) });

            if (grafiky.Count >= 2) PridejGpuSekci(z, grafiky, gpuTesty);

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
            var testGpu = winsat.Adapter.Length > 0 ? winsat.Adapter : hw.Grafika;
            BenchmarkSrovnani.Porovnej(p, $"Grafika – {testGpu}", winsat.Grafika, BenchmarkSrovnani.TypickaGrafika(BenchmarkSrovnani.Samostatna(testGpu)),
                "starý nebo obecný ovladač grafiky, notebook používá integrovanou grafiku místo výkonné, úsporný režim.", testGpu);
            foreach (var g in gpuTesty.Where(x => x.Skore > 0 && !string.Equals(x.Adapter, testGpu, StringComparison.OrdinalIgnoreCase)))
                BenchmarkSrovnani.Porovnej(p, $"Grafika – {g.Adapter}", g.Skore, BenchmarkSrovnani.TypickaGrafika(BenchmarkSrovnani.Samostatna(g.Adapter)),
                    "starý ovladač grafiky, úsporný režim napájení, notebook na baterii.", g.Adapter);
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

    private sealed record TestGpu(string Rezim, string Adapter, double Skore, double Plocha, double PametMBs, List<(string, string)> Metriky);

    /// <summary>
    /// Spustí grafický test WinSAT s vynucenou grafikou (Nastavení → Grafika = HKCU\…\DirectX\UserGpuPreferences,
    /// 1 = úsporná, 2 = výkonná). Původní nastavení se vždy vrátí.
    /// </summary>
    private static async Task<TestGpu?> TestujGpuAsync(int pref, string rezim, IProgress<string> log, CancellationToken ct)
    {
        const string Klic = @"Software\Microsoft\DirectX\UserGpuPreferences";
        var exe = Path.Combine(Environment.SystemDirectory, "winsat.exe");
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Klic);
        var puvodni = k?.GetValue(exe) as string;
        try
        {
            k?.SetValue(exe, $"GpuPreference={pref};");
            log.Report($"Test grafiky – {rezim} karta…");
            var start = DateTime.Now;
            await ProcessRunner.RunAsync("winsat.exe", "graphicsformal", log, ct);
            var cesta = NejnovejsiXml(start, "*.WinSAT.xml");
            if (cesta == null) return null;
            var w = Nacti(cesta);
            if (w == null) return null;
            double Cislo(string jmeno) => w.Metriky.Where(m => m.Nazev == jmeno).Select(m =>
                double.TryParse(m.Hodnota.Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var v) ? v : 0)
                .FirstOrDefault();
            var vysledek = new TestGpu(rezim, w.Adapter.Length > 0 ? w.Adapter : $"{rezim} grafika", w.Grafika,
                Cislo(NazvyMetrik["DWMFps"]), Cislo(NazvyMetrik["VideoMemBandwidth"]), w.Metriky);
            log.Report($"Testováno: {vysledek.Adapter} · skóre {vysledek.Skore:0.0}");
            return vysledek;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log.Report($"! Test grafiky ({rezim}) selhal: {ex.Message}"); return null; }
        finally
        {
            try
            {
                if (puvodni == null) k?.DeleteValue(exe, false);
                else k?.SetValue(exe, puvodni);
            }
            catch { }
        }
    }

    private static void PridejGpuSekci(Zprava z, List<string> grafiky, List<TestGpu> testy)
    {
        var g = z.PridejSekci("Grafické karty – každá zvlášť");
        g.Popis = $"Počítač má {grafiky.Count} grafiky ({string.Join(", ", grafiky)}). Windows pro běžnou práci používá úspornou a výkonnou zapíná "
            + "jen pro náročné programy. Test proto proběhl dvakrát s vynucenou volbou grafiky (nastavení se po testu vrátilo).";
        if (testy.Count == 0)
        {
            g.Pridej("Test jednotlivých grafik se nepodařil", "", Stav.Varovani, "Podrobnosti jsou v záložce Podrobný log.");
            return;
        }
        var maxPamet = testy.Max(t => t.PametMBs);
        foreach (var t in testy)
        {
            var detail = $"Režim Windows: {t.Rezim} grafika."
                + (t.Plocha > 0 ? $" Vykreslování plochy: {t.Plocha:0} snímků/s." : "")
                + (t.PametMBs > 0 ? $" Propustnost video paměti: {t.PametMBs / 1000:0.#} GB/s." : "");
            if (t.Skore > 0)
                g.PridejPruh(t.Adapter, $"{t.Skore:0.0} / 9,9 – {Slovne(t.Skore)}", t.Skore / 9.9, StavSkore(t.Skore), detail);
            else
                g.Pridej(t.Adapter, "otestováno", Stav.Info, detail);
            if (t.PametMBs > 0 && maxPamet > 0)
                g.PridejPruh($"   video paměť – {t.Adapter}", $"{t.PametMBs / 1000:0.#} GB/s", t.PametMBs / maxPamet, null,
                    "Rychlost paměti grafiky – hlavní rozdíl mezi integrovanou a samostatnou kartou.");
        }
        if (testy.Count >= 2 && string.Equals(testy[0].Adapter, testy[1].Adapter, StringComparison.OrdinalIgnoreCase))
            g.Pridej("Windows použil v obou testech stejnou grafiku", "", Stav.Info,
                "Druhou kartu se nepodařilo vynutit – notebook má nejspíš grafiku přepnutou napevno (MUX / „pouze dedikovaná GPU“ v BIOSu nebo aplikaci výrobce). "
                + "V tom případě je testovaná karta ta, která skutečně běží.");
        g.Pridej("Herní výkon", "", null,
            "Test Windows měří plochu a propustnost paměti grafiky, ne herní 3D výkon. Ten ukáže až test ve hře nebo specializovaný 3D benchmark.");
    }

    private static string Slovne(double s) => s switch
    {
        >= 8.5 => "výborné", >= 7 => "dobré", >= 5 => "průměrné", _ => "slabé"
    };

    private static Stav StavSkore(double s) => s >= 7 ? Stav.Ok : s >= 5 ? Stav.Info : Stav.Varovani;


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

    private static string? NejnovejsiXml(DateTime po, string maska = "*Formal.Assessment*.WinSAT.xml")
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Performance", "WinSAT", "DataStore");
            if (!Directory.Exists(dir)) return null;
            return new DirectoryInfo(dir).GetFiles(maska)
                .Where(f => f.LastWriteTime >= po)
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    private sealed record WinSat(double Celkem, double Cpu, double Pamet, double Grafika, double Hry, double Disk,
        List<(string Nazev, string Hodnota)> Metriky, string Adapter);

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
            var adapter = doc.Descendants("AdapterDescription").FirstOrDefault()?.Value.Trim() ?? "";
            return new WinSat(D("SystemScore"), D("CpuScore"), D("MemoryScore"), D("GraphicsScore"), D("GamingScore"), D("DiskScore"), metriky, adapter);
        }
        catch { return null; }
    }
}
