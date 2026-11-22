using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace KonzolakServis.Services;

/// <summary>
/// Heslo pro spuštění aplikace – ochrana, aby servisní nástroj nespustil sám zákazník.
/// Výchozí heslo je zabudované (uloženo jen jako hash). Technik si může na konkrétním PC
/// nastavit vlastní heslo, které se uloží do registru (HKLM) a není součástí zdrojového kódu.
/// </summary>
public static class HesloService
{
    // SHA-256 výchozího hesla "4905". Výchozí heslo je slabá ochrana (krátké a hash je ve zdrojovém kódu) –
    // slouží jako zábrana proti náhodnému spuštění zákazníkem. Pro silnější ochranu nastavte vlastní heslo.
    private const string VychoziHash = "18d470e5e7b8a3ca3e3f6f3e686b09383b3143cff853ac79ba434b3fab537133";

    private const string Klic = @"SOFTWARE\Konzolak Servis";
    private const string Hodnota = "HesloHash";

    private static string Hash(string heslo) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(heslo))).ToLowerInvariant();

    /// <summary>Platný hash – vlastní z registru, jinak výchozí.</summary>
    private static string PlatnyHash()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(Klic);
            var v = k?.GetValue(Hodnota) as string;
            if (!string.IsNullOrWhiteSpace(v)) return v!.ToLowerInvariant();
        }
        catch { /* bez práv správce nebo klíč neexistuje – použijeme výchozí */ }
        return VychoziHash;
    }

    /// <summary>Je zadané heslo správné?</summary>
    public static bool JeSpravne(string heslo) =>
        !string.IsNullOrEmpty(heslo) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(heslo)), Encoding.ASCII.GetBytes(PlatnyHash()));

    /// <summary>Nastaví nové heslo pro spuštění (uloží jen jeho hash do HKLM). Vyžaduje práva správce.</summary>
    public static void Nastav(string noveHeslo)
    {
        using var k = Registry.LocalMachine.CreateSubKey(Klic, true);
        k.SetValue(Hodnota, Hash(noveHeslo), RegistryValueKind.String);
    }
}
