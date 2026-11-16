namespace KonzolakServis.Models;

public enum Kategorie { Diagnostika, Disky, Opravy, Cisteni, HwInfo, Sit }

public enum Stav { Ok, Varovani, Chyba, Info }

public sealed record VysledekAkce(Stav Stav, string Shrnuti);

/// <summary>Jedna servisní akce, kterou uživatel spustí tlačítkem.</summary>
public sealed class ServisniAkce
{
    public required string Nazev { get; init; }
    public required string Popis { get; init; }
    public required Kategorie Kategorie { get; init; }
    /// <summary>Akce mění systém – před spuštěním se zeptáme a nabídneme bod obnovení.</summary>
    public bool MeniSystem { get; init; }
    public string? Varovani { get; init; }
    public required Func<IProgress<string>, CancellationToken, Task<VysledekAkce>> Spustit { get; init; }
}
