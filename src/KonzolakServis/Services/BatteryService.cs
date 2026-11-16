using System.IO;
using System.Xml.Linq;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>Zpráva o baterii – powercfg /batteryreport /xml, převedené do češtiny a přehledně.</summary>
public static class BatteryService
{
    public static async Task<VysledekAkce> ZpravaAsync(IProgress<string> log, CancellationToken ct)
    {
        var zprava = new Zprava("Zpráva o baterii");
        var xml = Path.Combine(Path.GetTempPath(), $"konzolak_baterie_{Guid.NewGuid():N}.xml");
        var rc = await ProcessRunner.RunAsync("powercfg.exe", $"/batteryreport /xml /output \"{xml}\"", log, ct);
        if (rc != 0 || !File.Exists(xml))
            return Bez(zprava, "Zprávu o baterii se nepodařilo vytvořit – počítač nejspíš nemá baterii.");

        XDocument doc;
        try { doc = XDocument.Load(xml); }
        finally { try { File.Delete(xml); } catch { } }

        static string V(XElement? e, string jmeno) =>
            e?.Elements().FirstOrDefault(x => x.Name.LocalName == jmeno)?.Value.Trim() ?? "";

        var sys = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SystemInformation");
        var pc = zprava.PridejSekci("Počítač");
        pc.Pridej("Výrobce", V(sys, "SystemManufacturer"));
        pc.Pridej("Model", V(sys, "SystemProductName"));
        pc.Pridej("BIOS", V(sys, "BIOSVersion"));

        var baterie = doc.Descendants().Where(e => e.Name.LocalName == "Battery").ToList();
        if (baterie.Count == 0)
            return Bez(zprava, "Počítač nemá baterii (stolní PC).");

        var nejhorsi = Stav.Ok; var shrnuti = new List<string>();
        var n = 0;
        foreach (var b in baterie)
        {
            n++;
            long.TryParse(V(b, "DesignCapacity"), out var navrh);
            long.TryParse(V(b, "FullChargeCapacity"), out var plna);
            var cykly = V(b, "CycleCount");
            var zdravi = navrh > 0 ? plna * 100.0 / navrh : 0;
            var stav = navrh == 0 ? Stav.Info : zdravi >= 80 ? Stav.Ok : zdravi >= 50 ? Stav.Varovani : Stav.Chyba;
            if (Poradi(stav) < Poradi(nejhorsi)) nejhorsi = stav;

            var s = zprava.PridejSekci(baterie.Count > 1 ? $"Baterie {n}" : "Baterie");
            s.Pridej("Stav baterie", navrh > 0 ? $"{zdravi:0} % původní kapacity" : "nelze určit", stav, Hodnoceni(zdravi, navrh));
            s.Pridej("Původní (projektovaná) kapacita", Mwh(navrh));
            s.Pridej("Aktuální plná kapacita", Mwh(plna));
            s.Pridej("Ztráta kapacity", navrh > 0 ? $"{Math.Max(0, navrh - plna):N0} mWh" : "–");
            if (cykly.Length > 0 && cykly != "0") s.Pridej("Počet nabíjecích cyklů", cykly);
            s.Pridej("Výrobce baterie", V(b, "Manufacturer"));
            s.Pridej("Označení", V(b, "Id"));
            s.Pridej("Typ článků", Chemie(V(b, "Chemistry")));
            if (navrh > 0) shrnuti.Add($"{zdravi:0} %");
        }
        zprava.Stav = nejhorsi;
        zprava.Shrnuti = $"Baterie má {string.Join(", ", shrnuti)} původní kapacity – {Kratce(nejhorsi)}";
        return new VysledekAkce(nejhorsi, zprava.Shrnuti) { Zprava = zprava };
    }

    private static VysledekAkce Bez(Zprava z, string text)
    {
        z.Stav = Stav.Info; z.Shrnuti = text;
        return new VysledekAkce(Stav.Info, text) { Zprava = z };
    }

    private static int Poradi(Stav s) => PerfmonReportService.Poradi(s);
    private static string Mwh(long v) => v > 0 ? $"{v:N0} mWh" : "neznámá";

    private static string Hodnoceni(double z, long navrh) => navrh == 0 ? "Windows neposkytl údaje o kapacitě."
        : z >= 80 ? "Baterie je v dobrém stavu."
        : z >= 50 ? "Baterie je opotřebená, výdrž je znatelně kratší. Výměnu zvažte podle potřeby."
        : "Baterie je silně opotřebená – doporučujeme výměnu.";

    private static string Kratce(Stav s) => s switch
    {
        Stav.Ok => "v dobrém stavu.", Stav.Varovani => "opotřebená.", Stav.Chyba => "doporučena výměna.", _ => "stav nelze určit."
    };

    private static string Chemie(string c) => c.ToUpperInvariant() switch
    {
        "LION" or "LI-I" or "LI-ION" => "Li-Ion (lithium-iontová)",
        "LIP" or "LIPO" or "LI-POLY" => "Li-Pol (lithium-polymerová)",
        "NIMH" => "NiMH",
        "" => "neuvedeno",
        _ => c
    };
}
