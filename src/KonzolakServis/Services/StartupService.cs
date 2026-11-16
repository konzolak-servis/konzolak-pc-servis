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
        return list;
    }

    /// <summary>Hodnota ve StartupApproved: sudý první bajt = zapnuto, lichý = vypnuto, chybí = zapnuto.</summary>
    private static bool JeZapnuto(string hive, string podklic, string nazev)
    {
        using var k = ZalohaService.Koren(hive).OpenSubKey(Approved + podklic);
        return k?.GetValue(nazev) is not byte[] { Length: > 0 } b || b[0] % 2 == 0;
    }

    private static string KlicHodnoty(Polozka p) => p.ApprovedPodklic == "StartupFolder" ? Path.GetFileName(p.Prikaz) : p.Nazev;

    public static void Nastav(Polozka p, bool zapnout)
    {
        using var k = ZalohaService.Koren(p.Hive).CreateSubKey(Approved + p.ApprovedPodklic, true);
        var data = new byte[12];
        data[0] = (byte)(zapnout ? 0x02 : 0x03);
        if (!zapnout) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        k.SetValue(KlicHodnoty(p), data, RegistryValueKind.Binary);
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
        foreach (var p in polozky.OrderByDescending(p => p.Zapnuto).ThenBy(p => p.Nazev))
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
                ? new AkceRadku("Vypnout", (_, _) => { Nastav(p, false); return Task.FromResult($"{p.Nazev}: vypnuto"); })
                  { Doporucena = info.Doporuceni == Doporuceni.Vypnout }
                : new AkceRadku("Zapnout", (_, _) => { Nastav(p, true); return Task.FromResult($"{p.Nazev}: zapnuto"); });
            s.Pridej(info.Nazev == p.Nazev ? p.Nazev : $"{p.Nazev} ({info.Nazev})",
                p.Zapnuto ? "Zapnuto" : "Vypnuto", stav, detail, akce);
        }
        if (polozky.Count == 0) s.Pridej("Žádné programy po spuštění nenalezeny.", "", Stav.Ok);

        if (kVypnuti.Count > 0)
            z.HromadneAkce.Add(new AkceRadku($"Vypnout doporučené ({kVypnuti.Count})", (log2, _) =>
            {
                foreach (var p in kVypnuti) { Nastav(p, false); log2.Report($"{p.Nazev}: vypnuto"); }
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
