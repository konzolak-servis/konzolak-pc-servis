namespace KonzolakServis.Models;

/// <summary>Jeden řádek výsledku: popis, hodnota, volitelně stav (barevná tečka) a podrobnosti.</summary>
public sealed record Radek(string Popis, string Hodnota = "", Stav? Stav = null, string Detail = "");

public sealed class SekceZpravy
{
    public SekceZpravy(string nadpis) => Nadpis = nadpis;
    public string Nadpis { get; }
    public List<Radek> Radky { get; } = new();
    public SekceZpravy Pridej(string popis, string hodnota = "", Stav? stav = null, string detail = "")
    {
        Radky.Add(new Radek(popis, hodnota, stav, detail));
        return this;
    }
}

/// <summary>Čitelný výsledek akce – zobrazí se v aplikaci a dá se uložit jako HTML/TXT.</summary>
public sealed class Zprava
{
    public Zprava(string nadpis) => Nadpis = nadpis;
    public string Nadpis { get; }
    public string Shrnuti { get; set; } = "";
    public Stav Stav { get; set; } = Stav.Info;
    public DateTime Cas { get; } = DateTime.Now;
    public string Pocitac { get; } = Environment.MachineName;
    public List<SekceZpravy> Sekce { get; } = new();

    public SekceZpravy PridejSekci(string nadpis)
    {
        var s = new SekceZpravy(nadpis);
        Sekce.Add(s);
        return s;
    }
}
