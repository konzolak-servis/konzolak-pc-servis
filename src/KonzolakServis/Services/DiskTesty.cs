using System.Diagnostics;
using System.IO;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

public static partial class DiskService
{
    /// <summary>Průběžná mapa povrchu během testu (aplikace ji živě vykresluje).</summary>
    public static event Action<IReadOnlyList<BunkaMapy>, string>? ZivaMapa;

    private const int PocetBunek = 500;
    private const int Blok = 1024 * 1024;          // 1 MB na jedno čtení při testu povrchu
    private const double PomalyMs = 150;           // čtení 1 MB delší než toto = pomalý blok

    private static double Ms(long od) => (Stopwatch.GetTimestamp() - od) * 1000.0 / Stopwatch.Frequency;

    private static long Zarovnej(long x) => x / DiskNizkaUroven.Zarovnani * DiskNizkaUroven.Zarovnani;

    // ================== Test povrchu ==================

    public static async Task<VysledekAkce> TestPovrchuAsync(IProgress<string> log, CancellationToken ct)
    {
        var volba = await Task.Run(() => Views.VyberDisku.Zobraz("Test povrchu disku", true));
        if (volba == null) throw new OperationCanceledException();
        var z = await Task.Run(() => TestPovrchu(volba.Value.Disk, volba.Value.Uplny, log, ct));
        return new VysledekAkce(z.Stav, z.Shrnuti) { Zprava = z };
    }

    private static Zprava TestPovrchu(InfoDisku d, bool uplny, IProgress<string> log, CancellationToken ct)
    {
        using var disk = DiskNizkaUroven.OtevriDisk(d.Cislo, Blok);
        var velikost = disk.Velikost;
        log.Report($"Test povrchu: {d.Popisek}, velikost {Gb(velikost)}, režim {(uplny ? "úplný" : "rychlý")} (jen čtení)");

        var stav = new Stav?[PocetBunek];
        var maxMs = new double[PocetBunek];
        var chyby = new List<long>();
        var pomale = 0;
        long precteno = 0;
        var start = Stopwatch.GetTimestamp();
        var posledniZiva = 0L;
        var posledniLog = -1;
        var preruseno = false;
        double nejpomalejsi = 0;

        // seznam čtení: (pozice, buňka)
        IEnumerable<(long Pozice, int Bunka)> Cteni()
        {
            if (uplny)
            {
                for (long poz = 0; poz < velikost; poz += Blok)
                    yield return (poz, (int)Math.Min(PocetBunek - 1, poz * PocetBunek / velikost));
            }
            else
            {
                // rychlý režim: 4 vzorky po 1 MB v každé buňce (≈ 2 GB celkem)
                for (var b = 0; b < PocetBunek; b++)
                {
                    var od = velikost * b / PocetBunek;
                    var delka = velikost / PocetBunek;
                    for (var k = 0; k < 4; k++)
                        yield return (Zarovnej(od + delka * k / 4), b);
                }
            }
        }
        long celkem = uplny ? (velikost + Blok - 1) / Blok : PocetBunek * 4L;
        long hotovo = 0;

        void Hlas(bool vynutit)
        {
            if (!vynutit && Ms(posledniZiva) < 250) return;
            posledniZiva = Stopwatch.GetTimestamp();
            var cas = Ms(start) / 1000;
            var text = $"{hotovo * 100.0 / celkem:0.0} % · {precteno / Math.Max(cas, 0.001) / 1e6:0} MB/s · pomalé {pomale} · chyby {chyby.Count}";
            ZivaMapa?.Invoke(Bunky(stav, maxMs, velikost, chyby.Count > 0 ? chyby : null), text);
        }

        foreach (var (poz, bunka) in Cteni())
        {
            if (ct.IsCancellationRequested) { preruseno = true; break; }
            var delka = (int)Math.Min(Blok, Zarovnej(velikost - poz));
            if (delka <= 0) { hotovo++; continue; }
            var t = Stopwatch.GetTimestamp();
            var ok = disk.Cti(poz, delka);
            var ms = Ms(t);
            if (!ok || ms > PomalyMs)
            {
                // druhý pokus – vyloučí náhodné zdržení jinou činností Windows
                t = Stopwatch.GetTimestamp();
                var ok2 = disk.Cti(poz, delka);
                var ms2 = Ms(t);
                if (ok2) { ms = ok ? Math.Min(ms, ms2) : ms2; ok = true; }
            }
            hotovo++;
            if (!ok)
            {
                chyby.Add(poz);
                stav[bunka] = Stav.Chyba;
                log.Report($"! Chyba čtení na pozici {Gb(poz)} (sektor {poz / 512:N0}), chyba Windows {DiskNizkaUroven.PosledniChyba}");
            }
            else
            {
                precteno += delka;
                if (ms > maxMs[bunka]) maxMs[bunka] = ms;
                if (ms > nejpomalejsi) nejpomalejsi = ms;
                if (ms > PomalyMs) { pomale++; if (stav[bunka] != Stav.Chyba) stav[bunka] = Stav.Varovani; }
                else stav[bunka] ??= Stav.Ok;
            }
            var proc = (int)(hotovo * 100 / celkem);
            if (proc != posledniLog && (proc % 5 == 0))
            {
                posledniLog = proc;
                log.Report($"Test povrchu: {proc} % · pomalé bloky {pomale} · chyby {chyby.Count}");
            }
            Hlas(false);
        }
        Hlas(true);

        var doba = Ms(start) / 1000;
        var rychlost = precteno / Math.Max(doba, 0.001) / 1e6;
        var z = new Zprava($"Test povrchu – disk {d.Cislo}");
        var mapa = z.PridejSekci("Mapa povrchu");
        mapa.Popis = "Každý čtvereček = 1/500 disku (≈ " + Gb(velikost / PocetBunek) + "). Zelená = v pořádku, žlutá = pomalé čtení (nad "
            + $"{PomalyMs:0} ms na 1 MB), červená = chyba čtení (vadné sektory), šedá = netestováno. Najetím myší zobrazíte podrobnosti.";
        mapa.Mapa = Bunky(stav, maxMs, velikost, chyby);

        var s = z.PridejSekci("Výsledek");
        s.Pridej("Disk", d.Popisek);
        s.Pridej("Režim", uplny ? "úplný – přečten celý povrch" : "rychlý – 2 000 vzorků rovnoměrně po celém disku",
            null, uplny ? "" : "Rychlý test odhalí rozsáhlejší poškození. Pro jistotu u podezřelého disku spusťte úplný test.");
        s.Pridej("Prověřeno", $"{Gb(precteno)} za {Cas(doba)}" + (preruseno ? $" (přerušeno na {hotovo * 100.0 / celkem:0} %)" : ""),
            preruseno ? Stav.Info : null);
        s.Pridej("Průměrná rychlost čtení", $"{rychlost:0} MB/s");
        s.Pridej("Nejpomalejší čtení 1 MB", $"{nejpomalejsi:0} ms", nejpomalejsi > PomalyMs ? Stav.Varovani : Stav.Ok);
        s.Pridej("Chybné bloky (nečitelné)", chyby.Count.ToString(), chyby.Count > 0 ? Stav.Chyba : Stav.Ok,
            chyby.Count > 0 ? "Pozice: " + string.Join(", ", chyby.Take(30).Select(x => $"{Gb(x)} (LBA {x / 512:N0})")) + (chyby.Count > 30 ? " …" : "") : "");
        s.Pridej("Pomalé bloky", pomale.ToString(), pomale > 0 ? Stav.Varovani : Stav.Ok,
            pomale > 0 ? "Pomalé čtení znamená, že disk musel čtení opakovat – slabé místo povrchu, nebo souběžná činnost Windows." : "");

        var dop = z.PridejSekci("Co dál");
        if (chyby.Count > 0)
        {
            dop.Pridej("Disk má nečitelná místa. Okamžitě zálohujte důležitá data a disk vyměňte.", "", Stav.Chyba);
            dop.Pridej("Soubory ležící na vadných místech mohou být poškozené. Kontrola disku (chkdsk /r) je přesune a označí sektory jako vadné.", "", Stav.Varovani);
        }
        else if (pomale > PocetBunek / 50)
            dop.Pridej("Hodně pomalých míst – disk může začínat selhávat. Zálohujte a zkontrolujte Kvalitu disku (SMART).", "", Stav.Varovani);
        else if (pomale > 0)
            dop.Pridej("Několik pomalých míst je u HDD běžné (souběžná činnost Windows). Pokud se jich při opakování přibývá, sledujte disk.", "", Stav.Info);
        else
            dop.Pridej("Povrch disku je v pořádku.", "", Stav.Ok);
        dop.Pridej("Test pouze četl data – nic na disku nezměnil.", "", Stav.Info);

        z.Stav = chyby.Count > 0 ? Stav.Chyba : pomale > PocetBunek / 50 ? Stav.Varovani : preruseno ? Stav.Info : Stav.Ok;
        z.Shrnuti = chyby.Count > 0 ? $"Nalezeno {chyby.Count} nečitelných bloků – zálohujte data a vyměňte disk"
            : (preruseno ? "Test přerušen. " : "") + (pomale > 0 ? $"Bez chyb čtení, {pomale} pomalých bloků" : "Povrch disku je bez chyb")
              + $" · {rychlost:0} MB/s";
        return z;
    }

    private static string Cas(double s) => s >= 3600 ? $"{s / 3600:0} h {s % 3600 / 60:0} min" : s >= 60 ? $"{s / 60:0} min {s % 60:0} s" : $"{s:0.#} s";

    private static List<BunkaMapy> Bunky(Stav?[] stav, double[] maxMs, long velikost, List<long>? chyby)
    {
        var l = new List<BunkaMapy>(stav.Length);
        for (var i = 0; i < stav.Length; i++)
        {
            var od = velikost * i / stav.Length;
            var dok = velikost * (i + 1) / stav.Length;
            var popis = $"{Gb(od)} – {Gb(dok)}: " + stav[i] switch
            {
                Stav.Chyba => "CHYBA ČTENÍ",
                null => "netestováno",
                _ => $"nejpomalejší čtení {maxMs[i]:0} ms"
            };
            l.Add(new BunkaMapy(stav[i] ?? Stav.Info, popis));
        }
        return l;
    }

    // ================== Benchmark ==================

    public static async Task<VysledekAkce> BenchmarkAsync(IProgress<string> log, CancellationToken ct)
    {
        var volba = await Task.Run(() => Views.VyberDisku.Zobraz("Měření rychlosti disku", false));
        if (volba == null) throw new OperationCanceledException();
        var z = await Task.Run(() => Benchmark(volba.Value.Disk, log, ct));
        return new VysledekAkce(z.Stav, z.Shrnuti) { Zprava = z };
    }

    private static Zprava Benchmark(InfoDisku d, IProgress<string> log, CancellationToken ct)
    {
        const int VelkyBlok = 4 * 1024 * 1024;
        var z = new Zprava($"Rychlost disku – disk {d.Cislo}");
        var sekv = new List<(string Kde, double MBs)>();
        double pristupMs = 0;
        var mereni = 0;

        using (var disk = DiskNizkaUroven.OtevriDisk(d.Cislo, VelkyBlok))
        {
            var velikost = disk.Velikost;
            log.Report($"Měření rychlosti: {d.Popisek} (čtení přímo z disku, bez zápisu)");
            // sekvenční čtení na 5 místech – u HDD je začátek rychlejší než konec
            var mista = new[] { ("Začátek disku", 0.0), ("25 %", 0.25), ("Polovina", 0.5), ("75 %", 0.75), ("Konec disku", 1.0) };
            foreach (var (kde, podil) in mista)
            {
                ct.ThrowIfCancellationRequested();
                const long Usek = 512L * 1024 * 1024;
                var od = Zarovnej((long)Math.Max(0, (velikost - Usek) * podil));
                long bajtu = 0;
                var t = Stopwatch.GetTimestamp();
                for (long poz = od; poz < od + Usek && poz + VelkyBlok <= velikost; poz += VelkyBlok)
                {
                    if (!disk.Cti(poz, VelkyBlok)) { log.Report($"! Chyba čtení na {Gb(poz)}"); break; }
                    bajtu += VelkyBlok;
                    if (Ms(t) > 4000) break;
                }
                var mbs = bajtu / Math.Max(Ms(t) / 1000, 0.001) / 1e6;
                sekv.Add((kde, mbs));
                log.Report($"Sekvenční čtení – {kde}: {mbs:0} MB/s");
            }

            // náhodný přístup 4 KB po celém disku
            ct.ThrowIfCancellationRequested();
            var tc = Stopwatch.GetTimestamp();
            double soucet = 0;
            while (mereni < 400 && Ms(tc) < 8000)
            {
                var poz = Zarovnej((long)(Random.Shared.NextDouble() * (velikost - 4096)));
                var t = Stopwatch.GetTimestamp();
                if (disk.Cti(poz, 4096)) { soucet += Ms(t); mereni++; }
            }
            pristupMs = mereni > 0 ? soucet / mereni : 0;
            log.Report($"Přístupová doba (náhodné čtení 4 KB): {pristupMs:0.00} ms");
        }

        // zápis – jen do dočasného souboru na jednotce tohoto disku
        double? zapisMBs = null, zapis4kMs = null;
        string zapisInfo;
        var jednotka = d.Pismena.Select(pis => new DriveInfo(pis + ":\\"))
            .Where(x => { try { return x.IsReady && x.AvailableFreeSpace > 2L * 1024 * 1024 * 1024; } catch { return false; } })
            .OrderByDescending(x => x.AvailableFreeSpace).FirstOrDefault();
        if (jednotka == null)
            zapisInfo = d.Pismena.Count == 0 ? "Disk nemá žádnou jednotku s písmenem – zápis se netestoval."
                                             : "Na disku není volné 2 GB místa – zápis se netestoval.";
        else
        {
            var cesta = Path.Combine(jednotka.RootDirectory.FullName, $"KonzolakServis_test_{Environment.ProcessId}.tmp");
            zapisInfo = $"Test zápisu do dočasného souboru na {jednotka.Name.TrimEnd('\\')} (1 GB, po testu se sám smaže).";
            try
            {
                using var f = DiskNizkaUroven.VytvorTestovaciSoubor(cesta, VelkyBlok);
                f.NaplnNahodne();
                log.Report(zapisInfo);
                long zapsano = 0;
                var t = Stopwatch.GetTimestamp();
                for (long poz = 0; poz < 1024L * 1024 * 1024; poz += VelkyBlok)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!f.Zapis(poz, VelkyBlok)) throw new IOException($"zápis selhal (chyba {DiskNizkaUroven.PosledniChyba})");
                    zapsano += VelkyBlok;
                    if (Ms(t) > 15000) break;
                }
                zapisMBs = zapsano / Math.Max(Ms(t) / 1000, 0.001) / 1e6;
                log.Report($"Sekvenční zápis: {zapisMBs:0} MB/s");

                double s4 = 0; var n4 = 0;
                var t4 = Stopwatch.GetTimestamp();
                while (n4 < 200 && Ms(t4) < 6000)
                {
                    var poz = Zarovnej((long)(Random.Shared.NextDouble() * (zapsano - 4096)));
                    var t1 = Stopwatch.GetTimestamp();
                    if (f.Zapis(poz, 4096)) { s4 += Ms(t1); n4++; }
                }
                if (n4 > 0) zapis4kMs = s4 / n4;
                log.Report($"Náhodný zápis 4 KB: {zapis4kMs:0.00} ms");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { zapisInfo = $"Test zápisu se nepodařil: {ex.Message}"; log.Report("! " + zapisInfo); }
            finally { try { if (File.Exists(cesta)) File.Delete(cesta); } catch { } }
        }

        // --- vyhodnocení podle typu disku ---
        var (ocekCteni, ocekPristup, typText) = d.Rozhrani == "NVMe" ? (1500.0, 0.3, "NVMe SSD: obvykle 1 500–7 000 MB/s, přístup pod 0,2 ms")
            : d.Ssd ? (400.0, 0.5, "SATA SSD: obvykle 450–560 MB/s, přístup pod 0,3 ms")
            : d.Rozhrani == "USB" ? (30.0, 25.0, "USB disk: podle typu 30–1 000 MB/s")
            : (80.0, 22.0, "HDD: obvykle 80–250 MB/s, přístup 10–20 ms");
        var prumer = sekv.Count > 0 ? sekv.Average(x => x.MBs) : 0;
        var maxSek = Math.Max(sekv.Count > 0 ? sekv.Max(x => x.MBs) : 0, zapisMBs ?? 0);

        var s = z.PridejSekci("Sekvenční čtení (velké soubory)");
        s.Popis = "Čtení 512 MB na pěti místech disku. U HDD je konec disku přirozeně pomalejší než začátek.";
        foreach (var (kde, mbs) in sekv)
            s.PridejPruh(kde, $"{mbs:0} MB/s", mbs / Math.Max(maxSek, 0.001), mbs < ocekCteni * 0.6 ? Stav.Varovani : Stav.Ok);
        s.Pridej("Průměr", $"{prumer:0} MB/s", prumer < ocekCteni * 0.6 ? Stav.Varovani : Stav.Ok, typText);

        var p = z.PridejSekci("Přístupová doba a malé soubory");
        p.Pridej("Přístupová doba (náhodné čtení 4 KB)", $"{pristupMs:0.00} ms · {(pristupMs > 0 ? 1000 / pristupMs : 0):N0} operací/s",
            pristupMs > ocekPristup * 2 ? Stav.Varovani : Stav.Ok,
            "Jak rychle disk najde a přečte malý kousek dat – rozhoduje o rychlosti startu Windows a programů.");

        var w = z.PridejSekci("Zápis");
        w.Popis = zapisInfo;
        if (zapisMBs is { } zm)
            w.PridejPruh("Sekvenční zápis", $"{zm:0} MB/s", zm / Math.Max(maxSek, 0.001), zm < ocekCteni * 0.4 ? Stav.Varovani : Stav.Ok,
                d.Ssd ? "U SSD může zápis po zaplnění rychlé mezipaměti (SLC cache) klesnout – to je normální." : "");
        if (zapis4kMs is { } z4)
            w.Pridej("Náhodný zápis 4 KB", $"{z4:0.00} ms · {1000 / z4:N0} operací/s", z4 > ocekPristup * 4 ? Stav.Varovani : Stav.Ok);

        var pomaly = prumer < ocekCteni * 0.6 || pristupMs > ocekPristup * 2;
        var dop = z.PridejSekci("Hodnocení");
        if (pomaly)
        {
            dop.Pridej("Disk je pomalejší, než je u tohoto typu obvyklé.", "", Stav.Varovani);
            dop.Pridej("Možné příčiny: zaplněný nebo přehřátý disk, souběžná činnost (aktualizace, antivirus), úsporný režim napájení, "
                + (d.Ssd ? "disk v pomalém slotu / režimu SATA IDE místo AHCI." : "opotřebení nebo vadná místa povrchu (spusťte Test povrchu)."), "", Stav.Info);
        }
        else dop.Pridej("Rychlost odpovídá typu disku.", "", Stav.Ok);
        if (!d.Ssd && d.Rozhrani != "USB")
            dop.Pridej("Výměna HDD za SSD zrychlí přístup k datům zhruba 100×.", "", Stav.Info);
        dop.Pridej("Čtení probíhalo přímo z disku, zápis jen do dočasného souboru – data na disku zůstala beze změny.", "", Stav.Info);

        z.Stav = pomaly ? Stav.Varovani : Stav.Ok;
        z.Shrnuti = $"Čtení {prumer:0} MB/s" + (zapisMBs is { } zz ? $" · zápis {zz:0} MB/s" : "") + $" · přístup {pristupMs:0.00} ms"
            + (pomaly ? " – pomalejší než obvykle" : "");
        return z;
    }
}
