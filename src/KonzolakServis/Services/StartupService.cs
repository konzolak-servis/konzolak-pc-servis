using System.IO;
using KonzolakServis.Models;
using Microsoft.Win32;

namespace KonzolakServis.Services;

/// <summary>
/// Programy po spuštění – stejné zapínání/vypínání jako ve Správci úloh (klíče StartupApproved),
/// takže změna je plně vratná a uživatel ji uvidí i ve Správci úloh.
/// </summary>
public static class StartupService
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public sealed record Polozka(string Nazev, string Prikaz, string Zdroj, string Hive, string ApprovedPodklic, bool Zapnuto);

    public static List<Polozka> Nacti()
    {
        var list = new List<Polozka>();
        void Reg(string hive, string cesta, string approved, string zdroj)
        {
            using var k = ZalohaService.Koren(hive).OpenSubKey(cesta);
            if (k == null) return;
            foreach (var n in k.GetValueNames().Where(n => n.Length > 0))
                list.Add(new Polozka(n, k.GetValue(n)?.ToString() ?? "", zdroj, hive, approved, JeZapnuto(hive, approved, n)));
        }
        void Slozka(string hive, string cesta, string zdroj)
        {
            if (!Directory.Exists(cesta)) return;
            foreach (var f in Directory.EnumerateFiles(cesta).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var n = Path.GetFileName(f);
                list.Add(new Polozka(Path.GetFileNameWithoutExtension(f), f, zdroj, hive, "StartupFolder", JeZapnuto(hive, "StartupFolder", n)) { });
            }
        }
        Reg("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "pro tohoto uživatele");
        Reg("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", "pro všechny uživatele");
        Reg("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "pro všechny uživatele (32bit)");
        Slozka("HKCU", Environment.GetFolderPath(Environment.SpecialFolder.Startup), "složka Po spuštění");
        Slozka("HKLM", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "složka Po spuštění (všichni)");

        // Položky, které jsme museli vypnout odebráním z Run klíče – ať jdou zase zapnout zpět.
        foreach (var kv in ZalohaService.SPrefixem(ZalohaPrefix))
        {
            var cast = kv.Key.Substring(ZalohaPrefix.Length).Split('\\', 3);
            if (cast.Length != 3) continue;
            var (hive, podklic, nazev) = (cast[0], cast[1], cast[2]);
            if (list.Any(p => p.Hive == hive && p.ApprovedPodklic == podklic && p.Nazev == nazev)) continue;
            var zdroj = podklic == "Run32" ? "pro všechny uživatele (32bit)" : hive == "HKLM" ? "pro všechny uživatele" : "pro tohoto uživatele";
            list.Add(new Polozka(nazev, kv.Value, zdroj, hive, podklic, false));
        }
        return list;
    }

    private const string ZalohaPrefix = "startup:";

    /// <summary>Cesta k Run klíči, ze kterého položka pochází.</summary>
    private static string RunCesta(string hive, string podklic) => podklic == "Run32"
        ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"
        : hive == "HKLM" ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
                         : @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Hodnota ve StartupApproved: sudý první bajt = zapnuto, lichý = vypnuto, chybí = zapnuto.</summary>
    private static bool JeZapnuto(string hive, string podklic, string nazev)
    {
        using var k = ZalohaService.Koren(hive).OpenSubKey(Approved + podklic);
        return k?.GetValue(nazev) is not byte[] { Length: > 0 } b || b[0] % 2 == 0;
    }

    private static string KlicHodnoty(Polozka p) => p.ApprovedPodklic == "StartupFolder" ? Path.GetFileName(p.Prikaz) : p.Nazev;

    /// <summary>
    /// Zapne/vypne program po spuštění a vrátí popis výsledku. Při neúspěchu vyhodí výjimku s přesnou chybou.
    /// U Run klíčů to děláme spolehlivě: hodnotu z Run klíče dočasně odebereme (a zapamatujeme),
    /// takže se program nespustí ani na strojích, kde samotné „StartupApproved" (jako ve Správci úloh) neúčinkuje.
    /// Zároveň nastavíme i StartupApproved, aby stav odpovídal i ve Správci úloh. Vše je plně vratné.
    /// </summary>
    public static string Nastav(Polozka p, bool zapnout)
    {
        // 1) StartupApproved (shoda se Správcem úloh) – best effort, nesmí shodit celou akci.
        try
        {
            using var k = ZalohaService.Koren(p.Hive).CreateSubKey(Approved + p.ApprovedPodklic, true);
            if (k != null)
            {
                var data = new byte[12];
                data[0] = (byte)(zapnout ? 0x02 : 0x03);
                if (!zapnout) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
                k.SetValue(KlicHodnoty(p), data, RegistryValueKind.Binary);
            }
        }
        catch { /* na StartupApproved se nespoléháme, spolehlivý je krok 2 */ }

        // 2) Spolehlivá cesta pro Run/Run32: odebrat / vrátit hodnotu přímo v Run klíči.
        if (p.ApprovedPodklic is "Run" or "Run32")
        {
            var cesta = RunCesta(p.Hive, p.ApprovedPodklic);
            var zalohaKlic = $"{ZalohaPrefix}{p.Hive}\\{p.ApprovedPodklic}\\{p.Nazev}";
            using var rk = ZalohaService.Koren(p.Hive).OpenSubKey(cesta, true)
                ?? throw new Exception($"nepodařilo se otevřít klíč registru {p.Hive}\\{cesta}.");

            if (!zapnout)
            {
                var hodnota = rk.GetValue(p.Nazev)?.ToString() ?? p.Prikaz;
                ZalohaService.Zapamatuj(zalohaKlic, hodnota);
                rk.DeleteValue(p.Nazev, false);
                rk.Flush();
                if (rk.GetValue(p.Nazev) != null)
                    throw new Exception($"{p.Nazev} nejde vypnout – hodnotu v registru se nepodařilo odebrat (chybí oprávnění?).");
                return $"{p.Nazev}: vypnuto";
            }

            var puv = ZalohaService.Puvodni(zalohaKlic) ?? p.Prikaz;
            if (rk.GetValue(p.Nazev) == null && !string.IsNullOrEmpty(puv))
                rk.SetValue(p.Nazev, puv, RegistryValueKind.String);
            rk.Flush();
            ZalohaService.Zapomen(zalohaKlic);
            if (rk.GetValue(p.Nazev) == null)
                throw new Exception($"{p.Nazev} nejde zapnout – hodnotu v registru se nepodařilo vrátit.");
            return $"{p.Nazev}: zapnuto";
        }

        // 3) Složka Po spuštění – řídí se StartupApproved (ověříme, že se zápis projevil).
        if (JeZapnuto(p.Hive, p.ApprovedPodklic, KlicHodnoty(p)) != zapnout)
            throw new Exception($"{p.Nazev}: změna se neprojevila (StartupApproved\\{p.ApprovedPodklic}).");
        return $"{p.Nazev}: {(zapnout ? "zapnuto" : "vypnuto")}";
    }

    public static Task<Zprava> ZpravaAsync(IProgress<string> log, CancellationToken ct) => Task.Run(() =>
    {
        var polozky = Nacti();
        log.Report($"Nalezeno {polozky.Count} programů po spuštění.");
        var z = new Zprava("Programy po spuštění") { Obnovit = ZpravaAsync };
        var zap = polozky.Count(p => p.Zapnuto);
        var kVypnuti = new List<Polozka>();

        var s = z.PridejSekci("Programy, které se spouštějí s Windows");
        s.Popis = "Vypnutí program neodinstaluje, jen ho nespustí automaticky. Kdykoli jde zapnout zpět (i ve Správci úloh).";
        foreach (var p in polozky.OrderBy(p => p.Nazev, StringComparer.CurrentCultureIgnoreCase))
        {
            var info = Znalosti.Program(p.Nazev, p.Prikaz);
            var stav = !p.Zapnuto ? Stav.Info
                     : info.Doporuceni == Doporuceni.Vypnout ? Stav.Varovani : Stav.Ok;
            if (p.Zapnuto && info.Doporuceni == Doporuceni.Vypnout) kVypnuti.Add(p);
            var detail = $"Co to je: {info.CoToJe}\n" +
                         $"Doporučení: {Znalosti.Text(info.Doporuceni)} – {info.Proc}\n" +
                         $"Vliv na rychlost startu: {info.Vliv}\n" +
                         $"Spouští se {p.Zdroj}: {p.Prikaz}";
            var akce = p.Zapnuto
                ? new AkceRadku("Vypnout", (_, _) => Task.FromResult(Nastav(p, false)))
                  { Doporucena = info.Doporuceni == Doporuceni.Vypnout }
                : new AkceRadku("Zapnout", (_, _) => Task.FromResult(Nastav(p, true)));
            s.Pridej(info.Nazev == p.Nazev ? p.Nazev : $"{p.Nazev} ({info.Nazev})",
                p.Zapnuto ? "Zapnuto" : "Vypnuto", stav, detail, akce);
        }
        if (polozky.Count == 0) s.Pridej("Žádné programy po spuštění nenalezeny.", "", Stav.Ok);

        if (kVypnuti.Count > 0)
            z.HromadneAkce.Add(new AkceRadku($"Vypnout doporučené ({kVypnuti.Count})", (log2, _) =>
            {
                foreach (var p in kVypnuti) log2.Report(Nastav(p, false));
                return Task.FromResult($"Vypnuto {kVypnuti.Count} programů. Projeví se při příštím spuštění Windows.");
            })
            { Doporucena = true, Potvrzeni = $"Vypnout {kVypnuti.Count} programů, které se doporučuje nespouštět automaticky?\n\n" +
                                            string.Join("\n", kVypnuti.Select(p => "• " + p.Nazev)) });

        z.Stav = kVypnuti.Count > 0 ? Stav.Varovani : Stav.Ok;
        z.Shrnuti = $"Po spuštění se zapíná {zap} z {polozky.Count} programů." +
                    (kVypnuti.Count > 0 ? $" Doporučujeme vypnout {kVypnuti.Count}." : " Nic dalšího vypínat není potřeba.") +
                    " Aplikace z Microsoft Storu (např. nové Teams) najdete v Nastavení → Aplikace → Po spuštění.";
        return z;
    }, ct);
}
