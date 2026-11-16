using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

/// <summary>
/// Test rychlosti internetu zdarma a bez účtu – servery Cloudflare (speed.cloudflare.com).
/// Ping a kolísání (jitter) přes ICMP, stahování a odesílání ve 4 souběžných spojeních po ~8 s.
/// </summary>
public static class RychlostSiteService
{
    private const string Server = "https://speed.cloudflare.com";
    private static readonly TimeSpan Delka = TimeSpan.FromSeconds(8);

    public static async Task<Zprava> ZpravaAsync(IProgress<string> log, CancellationToken ct)
    {
        var z = new Zprava("Test rychlosti internetu") { Obnovit = ZpravaAsync };

        log.Report("Měřím odezvu (ping)…");
        var (ping, jitter, ztraty) = await PingAsync("1.1.1.1", 10, ct);
        log.Report($"Ping {ping:0} ms, kolísání {jitter:0} ms, ztráty {ztraty} %");

        log.Report("Měřím rychlost stahování (8 s)…");
        var down = await MerAsync(true, log, ct);
        log.Report($"Stahování: {down:0.0} Mbit/s");

        log.Report("Měřím rychlost odesílání (8 s)…");
        var up = await MerAsync(false, log, ct);
        log.Report($"Odesílání: {up:0.0} Mbit/s");

        var v = z.PridejSekci("Výsledek");
        v.Pridej("Stahování (download)", $"{down:0.0} Mbit/s", Hodnot(down, 50, 10), Popis(down, true));
        v.Pridej("Odesílání (upload)", $"{up:0.0} Mbit/s", Hodnot(up, 10, 2), Popis(up, false));
        v.Pridej("Odezva (ping)", ping < 0 ? "neměřeno" : $"{ping:0} ms", ping < 0 ? Stav.Info : ping <= 30 ? Stav.Ok : ping <= 80 ? Stav.Varovani : Stav.Chyba,
            "Do 30 ms výborné (hry, hovory), do 80 ms použitelné, nad 100 ms znatelné zpoždění.");
        v.Pridej("Kolísání (jitter)", ping < 0 ? "neměřeno" : $"{jitter:0} ms", ping < 0 ? Stav.Info : jitter <= 10 ? Stav.Ok : jitter <= 30 ? Stav.Varovani : Stav.Chyba,
            "Vysoké kolísání = sekání hovorů a her, typicky slabá Wi-Fi nebo přetížená linka.");
        v.Pridej("Ztráta paketů", $"{ztraty} %", ztraty == 0 ? Stav.Ok : ztraty <= 5 ? Stav.Varovani : Stav.Chyba,
            "Mělo by být 0 %. Ztráty ukazují na rušení Wi-Fi, vadný kabel nebo problém u poskytovatele.");

        var pripojeni = z.PridejSekci("Připojení");
        var wifi = await WifiAsync(ct);
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.GetIPProperties().GatewayAddresses.Count > 0)
            .OrderByDescending(n => n.Speed).FirstOrDefault();
        if (adapter != null)
            pripojeni.Pridej("Aktivní adaptér", $"{adapter.Description}", null,
                $"Typ: {(adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "kabel / jiné")} · rychlost spojení: {adapter.Speed / 1_000_000} Mbit/s");
        if (wifi.Signal >= 0)
            pripojeni.Pridej("Síla Wi-Fi signálu", $"{wifi.Signal} %", wifi.Signal >= 70 ? Stav.Ok : wifi.Signal >= 40 ? Stav.Varovani : Stav.Chyba,
                (wifi.Pasmo.Length > 0 ? $"Pásmo / standard: {wifi.Pasmo}\n" : "") +
                "Pod 40 % je signál slabý – přibližte se k routeru, použijte pásmo 5 GHz, opakovač nebo kabel.");

        var tipy = z.PridejSekci("Doporučení");
        if (adapter?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && down < 50)
            tipy.Pridej("Zkuste test přes kabel", "", Stav.Info, "Wi-Fi bývá nejčastější brzdou. Pokud je přes kabel rychlost výrazně vyšší, problém je v Wi-Fi (router, rušení, vzdálenost).");
        if (adapter != null && adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 && adapter.Speed == 100_000_000)
            tipy.Pridej("Síťová karta běží jen na 100 Mbit/s", "", Stav.Varovani, "Gigabitová karta často spadne na 100 Mbit/s kvůli vadnému nebo starému kabelu (Cat5). Vyměňte kabel za Cat5e/Cat6.");
        tipy.Pridej("Porovnejte s tarifem", "", Stav.Info,
            "Výsledek porovnejte s rychlostí ve smlouvě. Výsledek kolem 80–90 % tarifu je běžný; výrazně nižší výsledek přes kabel řešte s poskytovatelem.");
        tipy.Pridej("Reset sítě", "", null, "Při výpadcích pomůže Síť → Reset sítě (DNS + Winsock) a restart routeru.");

        z.Stav = new[] { Hodnot(down, 50, 10), Hodnot(up, 10, 2) }.OrderBy(PerfmonReportService.Poradi).First();
        z.Shrnuti = $"Stahování {down:0.0} Mbit/s · odesílání {up:0.0} Mbit/s · ping {(ping < 0 ? "–" : $"{ping:0} ms")}";
        return z;
    }

    private static Stav Hodnot(double mbit, double dobre, double minimum) =>
        mbit >= dobre ? Stav.Ok : mbit >= minimum ? Stav.Varovani : Stav.Chyba;

    private static string Popis(double mbit, bool down) => down
        ? mbit >= 100 ? "Výborné – i 4K video a velké stahování bez čekání."
        : mbit >= 50 ? "Dobré – HD/4K video, hovory, práce z domu."
        : mbit >= 10 ? "Průměrné – stačí na web a HD video, velké soubory potrvají."
        : "Pomalé – web se může načítat pomalu, video sekat."
        : mbit >= 20 ? "Výborné – i odesílání velkých souborů a streamování."
        : mbit >= 10 ? "Dobré – videohovory v HD bez problémů."
        : mbit >= 2 ? "Dostačující pro videohovory."
        : "Pomalé – videohovory mohou sekat.";

    private static async Task<(double Ping, double Jitter, int Ztraty)> PingAsync(string cil, int pocet, CancellationToken ct)
    {
        var casy = new List<long>();
        using var p = new Ping();
        for (var i = 0; i < pocet; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await p.SendPingAsync(cil, 2000);
                if (r.Status == IPStatus.Success) casy.Add(r.RoundtripTime);
            }
            catch { }
            await Task.Delay(200, ct);
        }
        if (casy.Count == 0) return (-1, 0, 100);
        var jitter = casy.Count > 1 ? casy.Zip(casy.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
        return (casy.Average(), jitter, (pocet - casy.Count) * 100 / pocet);
    }

    /// <summary>Změří rychlost v Mbit/s – 4 souběžná spojení po dobu ~8 s.</summary>
    private static async Task<double> MerAsync(bool stahovani, IProgress<string> log, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"KonzolakServis/{AktualizaceService.VerzeText}");
        long bajty = 0;
        var sw = Stopwatch.StartNew();
        using var konec = CancellationTokenSource.CreateLinkedTokenSource(ct);
        konec.CancelAfter(Delka);
        var data = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(data);

        async Task Vlakno()
        {
            var buf = new byte[64 * 1024];
            while (!konec.IsCancellationRequested)
            {
                try
                {
                    if (stahovani)
                    {
                        using var r = await http.GetAsync($"{Server}/__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead, konec.Token);
                        await using var s = await r.Content.ReadAsStreamAsync(konec.Token);
                        int n;
                        while ((n = await s.ReadAsync(buf, konec.Token)) > 0) Interlocked.Add(ref bajty, n);
                    }
                    else
                    {
                        using var obsah = new ByteArrayContent(data);
                        using var r = await http.PostAsync($"{Server}/__up", obsah, konec.Token);
                        if (r.IsSuccessStatusCode) Interlocked.Add(ref bajty, data.Length);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { log.Report($"! {ex.Message}"); await Task.Delay(500, CancellationToken.None); }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Vlakno()));
        ct.ThrowIfCancellationRequested();
        var sekundy = Math.Max(0.5, sw.Elapsed.TotalSeconds);
        return Interlocked.Read(ref bajty) * 8 / sekundy / 1_000_000;
    }

    private static async Task<(int Signal, string Pasmo)> WifiAsync(CancellationToken ct)
    {
        var radky = new List<string>();
        try { await ProcessRunner.RunAsync("netsh.exe", "wlan show interfaces", new Sber(radky), ct); }
        catch { return (-1, ""); }
        var signal = radky.Select(r => Regex.Match(r, @":\s*(\d{1,3})\s*%")).FirstOrDefault(m => m.Success);
        var pasmo = radky.Select(r => Regex.Match(r, @":\s*(802\.11\w+)")).FirstOrDefault(m => m.Success);
        return (signal != null ? int.Parse(signal.Groups[1].Value) : -1, pasmo?.Groups[1].Value ?? "");
    }

    private sealed class Sber(List<string> l) : IProgress<string> { public void Report(string v) => l.Add(v); }
}
