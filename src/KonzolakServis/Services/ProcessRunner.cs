using System.Diagnostics;
using System.Text;

namespace KonzolakServis.Services;

/// <summary>Spouští systémové příkazy a průběžně posílá jejich výstup do logu.</summary>
public static class ProcessRunner
{
    /// <summary>OEM kódová stránka konzole (v české Windows CP852).</summary>
    public static Encoding Oem =>
        Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

    public static async Task<int> RunAsync(string exe, string args, IProgress<string> log,
        CancellationToken ct, Encoding? encoding = null)
    {
        log.Report($"> {exe} {args}");
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Oem,
            StandardErrorEncoding = encoding ?? Oem,
            // Bezpečný pracovní adresář. Když aplikace běží ze složky na OneDrive (Plocha, Dokumenty…),
            // selhalo by spuštění powershell.exe s „Přístup byl odepřen" kvůli zděděnému pracovnímu adresáři.
            WorkingDirectory = Environment.SystemDirectory,
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report(Clean(e.Data)); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report("! " + Clean(e.Data)); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
            await p.WaitForExitAsync(ct);
        log.Report($"Návratový kód: {p.ExitCode}");
        return p.ExitCode;
    }

    // sfc a DISM vypisují průběh s \r a nulovými znaky – uklidíme
    private static string Clean(string s) => s.Replace("\0", "").Replace("\r", "").TrimEnd();

    /// <summary>Plná cesta k powershell.exe (System32\WindowsPowerShell\v1.0) – spolehlivější než spoléhat na PATH.</summary>
    private static readonly string PowerShellCesta =
        System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static Task<int> PowerShellAsync(string script, IProgress<string> log, CancellationToken ct)
    {
        // -EncodedCommand = žádné problémy s uvozovkami a diakritikou
        var full = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + script;
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        log.Report("> PowerShell: " + script);
        return RunAsync(PowerShellCesta, "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + b64,
            new Filtr(log), ct, Encoding.UTF8);
    }

    private sealed class Filtr(IProgress<string> inner) : IProgress<string>
    {
        public void Report(string l) { if (!l.StartsWith("> ")) inner.Report(l); }
    }
}
