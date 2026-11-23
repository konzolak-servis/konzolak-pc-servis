using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace KonzolakServis.Services;

public sealed record NovaVerze(Version Verze, string Tag, string Novinky, string UrlStazeni, string NazevSouboru);

/// <summary>
/// Kontrola a instalace aktualizací z GitHub Releases (veřejný repozitář s vydáními).
/// Repozitář se nastavuje při sestavení (vlastnost RepoAktualizaci v .csproj).
/// </summary>
public static class AktualizaceService
{
    public static Version Verze { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static string VerzeText => $"{Verze.Major}.{Verze.Minor}.{Math.Max(0, Verze.Build)}";

    public static string Repo { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoAktualizaci")?.Value ?? "";

    private static readonly HttpClient Http = Vytvor(TimeSpan.FromSeconds(15));
    // Stahování instalátoru (~70 MB) musí mít velkorysý časový limit, jinak by velký soubor „vypršel".
    private static readonly HttpClient HttpStahovani = Vytvor(TimeSpan.FromMinutes(10));

    private static HttpClient Vytvor(TimeSpan timeout)
    {
        var h = new HttpClient { Timeout = timeout };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"KonzolakServis/{VerzeText}");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>Proč poslední kontrola nedopadla (null = kontrola proběhla v pořádku).</summary>
    public static string? PosledniChyba { get; private set; }

    /// <summary>Vrátí novější verzi, nebo null (žádná novější / chyba – viz PosledniChyba).</summary>
    public static async Task<NovaVerze?> ZkontrolujAsync(CancellationToken ct = default)
    {
        PosledniChyba = null;
        if (string.IsNullOrWhiteSpace(Repo)) { PosledniChyba = "V této verzi není nastavený server aktualizací."; return null; }
        HttpResponseMessage resp;
        try { resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct); }
        catch (Exception ex) { PosledniChyba = $"Nepodařilo se připojit k serveru aktualizací (zkontrolujte internet). {ex.Message}"; return null; }
        using var _ = resp;
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            PosledniChyba = "Server aktualizací není veřejně dostupný – úložiště s vydáními (GitHub) je zatím soukromé. " +
                            "Novou verzi je zatím potřeba stáhnout ručně.";
            return null;
        }
        if ((int)resp.StatusCode == 403)
        {
            PosledniChyba = "Server aktualizací dočasně odmítl dotaz (limit GitHubu). Zkuste to za hodinu.";
            return null;
        }
        if (!resp.IsSuccessStatusCode) { PosledniChyba = $"Server aktualizací vrátil chybu {(int)resp.StatusCode}."; return null; }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var verze)) return null;
        if (Normal(verze) <= Normal(Verze)) return null;

        // Přednost má instalátor („Setup" v názvu); jinak vezmeme první .exe (zpětná kompatibilita se staršími vydáními).
        JsonElement? zaloha = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var nazev = asset.GetProperty("name").GetString() ?? "";
            if (!nazev.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (nazev.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                return new NovaVerze(verze, tag, Popis(root),
                    asset.GetProperty("browser_download_url").GetString() ?? "", nazev);
            zaloha ??= asset;
        }
        if (zaloha is { } z)
        {
            var nazev = z.GetProperty("name").GetString() ?? "";
            return new NovaVerze(verze, tag, Popis(root), z.GetProperty("browser_download_url").GetString() ?? "", nazev);
        }
        return null;
    }

    private static string Popis(JsonElement root) =>
        root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

    private static bool JeInstalator(string nazevSouboru) =>
        nazevSouboru.Contains("Setup", StringComparison.OrdinalIgnoreCase);

    private static Version Normal(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>
    /// Stáhne novou verzi a nainstaluje ji. U vydání s instalátorem („Setup") spustí instalátor tiše
    /// a aplikace se po instalaci sama restartuje – není potřeba ručně mazat starou verzi.
    /// U starších vydání (holý .exe) použije původní postup: nahradí soubor vedle sebe a spustí nový.
    /// </summary>
    public static async Task NainstalujAsync(NovaVerze nova, IProgress<string> log, CancellationToken ct)
    {
        if (JeInstalator(nova.NazevSouboru))
        {
            await NainstalujInstalatoremAsync(nova, log, ct);
            return;
        }

        var stary = Environment.ProcessPath ?? throw new InvalidOperationException("Nelze zjistit cestu k aplikaci.");
        var slozka = Path.GetDirectoryName(stary)!;
        var novy = Path.Combine(slozka, nova.NazevSouboru);
        var docasny = novy + ".stahuje";

        await StahniAsync(nova.UrlStazeni, docasny, $"Stahuji verzi {nova.Tag}…", log, ct);

        if (File.Exists(novy) && !string.Equals(novy, stary, StringComparison.OrdinalIgnoreCase)) File.Delete(novy);
        if (string.Equals(novy, stary, StringComparison.OrdinalIgnoreCase))
        {
            // stejný název souboru – přejmenujeme starý, aby šel nahradit
            var zaloha = stary + ".stara";
            if (File.Exists(zaloha)) File.Delete(zaloha);
            File.Move(stary, zaloha);
            stary = zaloha;
        }
        File.Move(docasny, novy);
        log.Report("Staženo. Spouštím novou verzi…");

        // Skript počká na ukončení této aplikace, smaže starou verzi a spustí novou.
        var skript = Path.Combine(Path.GetTempPath(), $"konzolak_aktualizace_{Guid.NewGuid():N}.cmd");
        File.WriteAllText(skript,
            "@echo off\r\n" +
            $"set PID={Environment.ProcessId}\r\n" +
            ":cekej\r\n" +
            "tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul && (timeout /t 1 /nobreak >nul & goto cekej)\r\n" +
            $"del /f /q \"{stary}\" >nul 2>&1\r\n" +
            $"start \"\" \"{novy}\"\r\n" +
            "del \"%~f0\"\r\n");
        SpustSkript(skript);
    }

    /// <summary>
    /// Stáhne instalátor do dočasné složky a přes pomocný skript ho po ukončení aplikace spustí tiše
    /// (/VERYSILENT). Instalátor přepíše aplikaci na místě; skript pak znovu spustí tutéž cestu (= novou verzi).
    /// Běžící aplikace je správce, takže instalace do Program Files proběhne bez dalšího dotazu UAC.
    /// </summary>
    private static async Task NainstalujInstalatoremAsync(NovaVerze nova, IProgress<string> log, CancellationToken ct)
    {
        var aplikace = Environment.ProcessPath ?? throw new InvalidOperationException("Nelze zjistit cestu k aplikaci.");
        var instalator = Path.Combine(Path.GetTempPath(), $"konzolak_setup_{Guid.NewGuid():N}.exe");

        await StahniAsync(nova.UrlStazeni, instalator, $"Stahuji instalátor verze {nova.Tag}…", log, ct);
        log.Report("Staženo. Spouštím instalaci a restartuji aplikaci…");

        var skript = Path.Combine(Path.GetTempPath(), $"konzolak_aktualizace_{Guid.NewGuid():N}.cmd");
        File.WriteAllText(skript,
            "@echo off\r\n" +
            $"set PID={Environment.ProcessId}\r\n" +
            ":cekej\r\n" +
            "tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul && (timeout /t 1 /nobreak >nul & goto cekej)\r\n" +
            // Tichá instalace: žádné okno ani dotazy, bez restartu Windows.
            $"\"{instalator}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL\r\n" +
            // Po instalaci spustíme tutéž cestu – instalátor ji přepsal novou verzí.
            $"start \"\" \"{aplikace}\"\r\n" +
            $"del /f /q \"{instalator}\" >nul 2>&1\r\n" +
            "del \"%~f0\"\r\n");
        SpustSkript(skript);
    }

    private static async Task StahniAsync(string url, string cil, string hlaska, IProgress<string> log, CancellationToken ct)
    {
        log.Report(hlaska);
        using var resp = await HttpStahovani.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var celkem = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(cil);
        var buf = new byte[81920]; long hotovo = 0; int n, posledni = -1;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            hotovo += n;
            var proc = celkem > 0 ? (int)(hotovo * 100 / celkem) : 0;
            if (proc / 10 != posledni) { posledni = proc / 10; log.Report($"Staženo {proc} %"); }
        }
    }

    private static void SpustSkript(string skript) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{skript}\"")
        { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
          WorkingDirectory = Environment.SystemDirectory });

    /// <summary>Seznam změn zabalený v aplikaci (Novinky.md).</summary>
    public static string NovinkyVAplikaci()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Novinky.md");
        if (s == null) return "";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
