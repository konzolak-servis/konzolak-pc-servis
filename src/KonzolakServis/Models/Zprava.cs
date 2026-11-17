namespace KonzolakServis.Models;

/// <summary>Tlačítko nápravy u řádku výsledku (např. „Vypnout“, „Vrátit původní“).</summary>
public sealed record AkceRadku(string Text, Func<IProgress<string>, CancellationToken, Task<string>> Spustit)
{
    /// <summary>Před spuštěním se zeptat (změna systému).</summary>
    public string? Potvrzeni { get; init; }
    /// <summary>Zvýrazněné tlačítko = doporučená akce.</summary>
    public bool Doporucena { get; init; }
}

/// <summary>Jeden řádek výsledku: popis, hodnota, volitelně stav (barevná tečka), podrobnosti a tlačítka nápravy.</summary>
public sealed record Radek(string Popis, string Hodnota = "", Stav? Stav = null, string Detail = "")
{
    public IReadOnlyList<AkceRadku> Akce { get; init; } = Array.Empty<AkceRadku>();
    /// <summary>Volitelný barevný pruh pod hodnotou (0–1), barva podle stavu.</summary>
    public double? Pruh { get; init; }
}

/// <summary>Jeden čtvereček mapy povrchu disku: stav a popisek (oblast, doba čtení).</summary>
public sealed record BunkaMapy(Stav Stav, string Popis);

public sealed class SekceZpravy
{
    public SekceZpravy(string nadpis) => Nadpis = nadpis;
    public string Nadpis { get; }
    public string Popis { get; set; } = "";
    /// <summary>Sekce se zobrazí sbalená – rozbalí se kliknutím na nadpis.</summary>
    public bool Sbalena { get; set; }
    public List<Radek> Radky { get; } = new();
    /// <summary>Volitelná mapa bloků (test povrchu disku) – barevné čtverečky.</summary>
    public IReadOnlyList<BunkaMapy>? Mapa { get; set; }
    public SekceZpravy Pridej(string popis, string hodnota = "", Stav? stav = null, string detail = "",
        params AkceRadku[] akce)
    {
        Radky.Add(new Radek(popis, hodnota, stav, detail) { Akce = akce });
        return this;
    }

    /// <summary>Řádek s barevným pruhem (podíl 0–1), např. skóre nebo rychlost.</summary>
    public SekceZpravy PridejPruh(string popis, string hodnota, double podil, Stav? stav = null, string detail = "")
    {
        Radky.Add(new Radek(popis, hodnota, stav, detail) { Pruh = Math.Clamp(podil, 0.02, 1) });
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
    /// <summary>Tlačítka nad celou zprávou (např. „Použít všechna doporučení“).</summary>
    public List<AkceRadku> HromadneAkce { get; } = new();
    /// <summary>Jak zprávu znovu načíst po provedení nápravy (aktuální stav).</summary>
    public Func<IProgress<string>, CancellationToken, Task<Zprava>>? Obnovit { get; set; }

    public SekceZpravy PridejSekci(string nadpis)
    {
        var s = new SekceZpravy(nadpis);
        Sekce.Add(s);
        return s;
    }
}
