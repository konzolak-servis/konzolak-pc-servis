using System.IO;
using System.Text;

namespace KonzolakServis.Services;

/// <summary>Sbírá vše, co aplikace udělá, a umí to uložit jako TXT nebo HTML protokol.</summary>
public sealed class LogService
{
    private readonly StringBuilder _buf = new();
    public event Action<string>? Radek;

    public static string SlozkaLogu => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Konzolák Servis", "Logy");

    public void Zapis(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        lock (_buf) _buf.AppendLine(line);
        Radek?.Invoke(line);
    }

    public string Obsah { get { lock (_buf) return _buf.ToString(); } }

    public string UlozTxt()
    {
        Directory.CreateDirectory(SlozkaLogu);
        var path = Path.Combine(SlozkaLogu, $"servis_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd_HH-mm}.txt");
        File.WriteAllText(path, Hlavicka() + Obsah, new UTF8Encoding(true));
        return path;
    }

    public string UlozHtml()
    {
        Directory.CreateDirectory(SlozkaLogu);
        var path = Path.Combine(SlozkaLogu, $"servis_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd_HH-mm}.html");
        var html = $$"""
            <!doctype html><html lang="cs"><meta charset="utf-8"><title>Servisní protokol Konzolák</title>
            <style>body{font-family:Segoe UI,sans-serif;background:#16181d;color:#e8eaed;padding:24px}
            pre{font-family:Consolas,monospace;white-space:pre-wrap;background:#20232a;padding:16px;border-radius:8px}
            h1{color:#3b82f6}</style>
            <h1>Servisní protokol Konzolák</h1><pre>{{System.Net.WebUtility.HtmlEncode(Hlavicka() + Obsah)}}</pre></html>
            """;
        File.WriteAllText(path, html, new UTF8Encoding(true));
        return path;
    }

    private static string Hlavicka() =>
        $"Konzolák Servis – protokol\nPočítač: {Environment.MachineName}\nUživatel: {Environment.UserName}\n" +
        $"Systém: {Environment.OSVersion}\nDatum: {DateTime.Now:d. M. yyyy HH:mm}\n{new string('-', 60)}\n";
}
