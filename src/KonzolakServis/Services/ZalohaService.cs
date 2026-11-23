using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace KonzolakServis.Services;

/// <summary>
/// Pamatuje si původní hodnoty před každou změnou, aby šla vrátit.
/// Uloženo v %ProgramData%\Konzolák Servis\zalohy.json (zůstává i po aktualizaci aplikace).
/// </summary>
public static class ZalohaService
{
    public const string Nic = "<neexistovalo>";
    private static readonly object Zamek = new();
    private static string Soubor => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Konzolák Servis", "zalohy.json");

    private static Dictionary<string, string> Nacti()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Soubor)) ?? new(); }
        catch { return new(); }
    }

    private static void Uloz(Dictionary<string, string> d)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Soubor)!);
        File.WriteAllText(Soubor, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Zapamatuje původní hodnotu – jen poprvé, další změny ji nepřepíšou.</summary>
    public static void Zapamatuj(string klic, string puvodni)
    {
        lock (Zamek) { var d = Nacti(); if (d.TryAdd(klic, puvodni)) Uloz(d); }
    }

    public static string? Puvodni(string klic)
    {
        lock (Zamek) return Nacti().TryGetValue(klic, out var v) ? v : null;
    }

    public static void Zapomen(string klic)
    {
        lock (Zamek) { var d = Nacti(); if (d.Remove(klic)) Uloz(d); }
    }

    /// <summary>Všechny zapamatované hodnoty, jejichž klíč začíná daným prefixem.</summary>
    public static List<KeyValuePair<string, string>> SPrefixem(string prefix)
    {
        lock (Zamek) return Nacti().Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    // ---------- registr ----------

    public static RegistryKey Koren(string hive) => hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;

    public static string? CtiReg(string hive, string cesta, string nazev)
    {
        using var k = Koren(hive).OpenSubKey(cesta);
        return k?.GetValue(nazev)?.ToString();
    }

    /// <summary>Nastaví DWORD v registru a zapamatuje si původní hodnotu.</summary>
    public static void NastavDword(string hive, string cesta, string nazev, int hodnota)
    {
        Zapamatuj($"reg:{hive}\\{cesta}\\{nazev}", CtiReg(hive, cesta, nazev) ?? Nic);
        using var k = Koren(hive).CreateSubKey(cesta, true);
        k.SetValue(nazev, hodnota, RegistryValueKind.DWord);
    }

    /// <summary>Vrátí DWORD na původní hodnotu (nebo ho smaže, pokud dřív neexistoval).</summary>
    public static void VratDword(string hive, string cesta, string nazev)
    {
        var klic = $"reg:{hive}\\{cesta}\\{nazev}";
        var puv = Puvodni(klic);
        if (puv == null) return;
        using var k = Koren(hive).CreateSubKey(cesta, true);
        if (puv == Nic) k.DeleteValue(nazev, false);
        else if (int.TryParse(puv, out var v)) k.SetValue(nazev, v, RegistryValueKind.DWord);
        Zapomen(klic);
    }
}
