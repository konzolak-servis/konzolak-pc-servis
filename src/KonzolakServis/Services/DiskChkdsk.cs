using System.Text.RegularExpressions;
using KonzolakServis.Models;

namespace KonzolakServis.Services;

public static partial class DiskService
{
    private static string Sys => (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\').ToUpperInvariant();

    /// <summary>Kontrola souborového systému všech pevných jednotek (chkdsk, jen čtení) s tlačítky opravy.</summary>
    public static async Task<Zprava> ChkdskAsync(IProgress<string> log, CancellationToken ct)
    {
        var z = new Zprava("Kontrola disků (chkdsk)") { Obnovit = ChkdskAsync };
        var svazky = Wmi("SELECT DriveLetter, Label, FileSystem, Capacity, FreeSpace, DirtyBitSet FROM Win32_Volume WHERE DriveType = 3")
            .Where(v => S(v["DriveLetter"]).Length > 0)
            .OrderBy(v => S(v["DriveLetter"]))
            .ToList();
        var nejhorsi = Stav.Ok;
        var souhrn = new List<string>();

        foreach (var v in svazky)
        {
            ct.ThrowIfCancellationRequested();
            var pis = S(v["DriveLetter"]).ToUpperInvariant();
            var fs = S(v["FileSystem"]).ToUpperInvariant();
            var jmeno = S(v["Label"]);
            var systemovy = pis == Sys;
            var spinavy = v["DirtyBitSet"] is bool b && b;
            var s = z.PridejSekci($"Jednotka {pis}" + (jmeno.Length > 0 ? $" ({jmeno})" : "") + (systemovy ? " – systémová" : ""));
            s.Popis = $"{fs} · {Gb(L(v["Capacity"]))}, volných {Gb(L(v["FreeSpace"]))}";

            if (fs == "REFS")
            {
                s.Pridej("Kontrola", "není potřeba", Stav.Info, "Souborový systém ReFS se kontroluje a opravuje sám za běhu.");
                continue;
            }

            log.Report($"Kontroluji jednotku {pis} ({fs})…");
            var radky = new List<string>();
            var arg = fs == "NTFS" ? $"{pis} /scan" : pis;     // FAT/exFAT: bez parametrů = jen kontrola
            var rc = await ProcessRunner.RunAsync("chkdsk.exe", arg, new Sber(radky, log), ct);

            var (stav, text, detail) = rc switch
            {
                0 => (Stav.Ok, "bez chyb", "Souborový systém je v pořádku."),
                1 => (Stav.Ok, "chyby nalezeny a opraveny", "Chkdsk drobné chyby opravil."),
                2 => (Stav.Ok, "bez chyb", "Nalezeny jen drobnosti k úklidu (např. nepoužité indexy) – nejde o poškození."),
                3 => (Stav.Chyba, "NALEZENY CHYBY – je potřeba oprava",
                    "Souborový systém obsahuje chyby. Mohou způsobovat mizení nebo poškození souborů, pády a pomalý start. Použijte tlačítko opravy."),
                _ => (Stav.Varovani, $"kontrolu nelze dokončit (kód {rc})", "Jednotka může být zamčená jiným programem, šifrovaná nebo odpojená.")
            };
            if (fs != "NTFS" && rc == 3)
                detail = "Na jednotce FAT/exFAT byly nalezeny chyby. Oprava vyžaduje její krátké odpojení.";
            s.Pridej("Výsledek kontroly", text, stav, detail);

            var vadneKb = VadneSektoryKb(radky);
            if (vadneKb != null)
                s.Pridej("Vadné sektory", vadneKb == 0 ? "žádné" : $"{vadneKb:N0} KB", vadneKb == 0 ? Stav.Ok : Stav.Chyba,
                    vadneKb > 0 ? "Souborový systém má označená vadná místa. Zkontrolujte Kvalitu disku a Test povrchu – disk se může rozpadat." : "");
            if (spinavy)
                s.Pridej("Kontrola při restartu", "naplánována", Stav.Info,
                    "Windows při dalším startu provede kontrolu a opravu této jednotky (může trvat i desítky minut – nevypínejte počítač).");

            // tlačítka opravy – vždy s potvrzením
            var opravy = new List<AkceRadku>();
            if (systemovy)
            {
                if (!spinavy)
                    opravy.Add(new AkceRadku("Opravit při restartu (chkdsk /f)", async (lg, c) =>
                    {
                        var r = await ProcessRunner.RunAsync("fsutil.exe", $"dirty set {pis}", lg, c);
                        return r == 0 ? $"Oprava jednotky {pis} naplánována – restartujte počítač. Oprava proběhne před přihlášením."
                                      : $"CHYBA: opravu se nepodařilo naplánovat (kód {r})";
                    })
                    {
                        Potvrzeni = $"Systémovou jednotku {pis} nelze opravit za běhu Windows.\n\nOprava se provede při příštím restartu "
                            + "a může trvat i desítky minut. Během ní počítač nevypínejte.\n\nNaplánovat opravu?",
                        Doporucena = rc == 3
                    });
            }
            else
            {
                string Potvrz(string co) => $"{co}\n\nJednotka {pis} se během opravy odpojí – zavřete všechny soubory a programy, které z ní "
                    + "pracují (neuložená data v nich by se ztratila).\n\nPokračovat?";
                opravy.Add(new AkceRadku("Opravit nyní (chkdsk /f)", async (lg, c) => VysledekOpravy(
                    await ProcessRunner.RunAsync("chkdsk.exe", $"{pis} /f /x", lg, c), pis))
                {
                    Potvrzeni = Potvrz($"Opraví chyby souborového systému na jednotce {pis}."),
                    Doporucena = rc == 3
                });
                if (fs == "NTFS")
                    opravy.Add(new AkceRadku("Opravit včetně vadných sektorů (chkdsk /r)", async (lg, c) => VysledekOpravy(
                        await ProcessRunner.RunAsync("chkdsk.exe", $"{pis} /r /x", lg, c), pis))
                    {
                        Potvrzeni = Potvrz($"Opraví souborový systém a projde celý povrch jednotky {pis}; data z vadných míst přesune jinam.\n"
                            + "Může trvat i několik hodin podle velikosti disku.")
                    });
            }
            if (opravy.Count > 0)
                s.Radky.Add(new Radek("Oprava", systemovy ? "při restartu počítače" : "ihned, jednotka se krátce odpojí", null,
                    "Oprava zapisuje do souborového systému – proto se vždy nejdřív zeptáme.") { Akce = opravy });

            var vystup = string.Join("\n", radky.Select(x => x.Trim())
                .Where(x => x.Length > 0 && !x.StartsWith(">") && !Regex.IsMatch(x, @"\d+\s*%") && !x.StartsWith("Návratový"))
                .Take(60));
            if (vystup.Length > 0)
            {
                var sv = z.PridejSekci($"Výstup chkdsk – {pis}");
                sv.Sbalena = true;
                sv.Pridej(vystup);
            }

            if (stav > nejhorsi && stav != Stav.Info) nejhorsi = stav;
            souhrn.Add($"{pis} {text}");
        }

        if (svazky.Count == 0)
        {
            z.Stav = Stav.Varovani;
            z.Shrnuti = "Nenalezena žádná pevná jednotka s písmenem.";
            return z;
        }
        var info = z.PridejSekci("Co kontrola dělá");
        info.Sbalena = true;
        info.Pridej("Kontrola (chkdsk /scan) jen čte – nic nemění a běží za plného provozu.");
        info.Pridej("Oprava /f opraví strukturu souborového systému (ztracené clustery, chybné indexy, poškozené záznamy souborů).");
        info.Pridej("Oprava /r navíc projde celý povrch a data z vadných sektorů přesune – trvá dlouho, vhodná při podezření na vadný disk.");
        info.Pridej("Chkdsk opravuje souborový systém, ne fyzický disk. Pokud se chyby opakují, zkontrolujte Kvalitu disku (SMART).");

        z.Stav = nejhorsi;
        z.Shrnuti = nejhorsi == Stav.Ok ? "Všechny jednotky jsou bez chyb · " + string.Join(", ", svazky.Select(x => S(x["DriveLetter"])))
                                        : string.Join(" · ", souhrn);
        return z;
    }

    private static string VysledekOpravy(int rc, string pis) => rc switch
    {
        0 => $"Jednotka {pis} zkontrolována – bez chyb.",
        1 => $"Chyby na jednotce {pis} byly opraveny.",
        2 => $"Jednotka {pis} zkontrolována, proveden drobný úklid.",
        _ => $"CHYBA: chkdsk jednotku {pis} neopravil (kód {rc}). Je jednotka používaná jiným programem?"
    };

    /// <summary>Řádek „… KB v chybných sektorech“ – podle klíčových slov, jinak 4. řádek s KB (pořadí je ve všech jazycích stejné).</summary>
    private static long? VadneSektoryKb(List<string> radky)
    {
        var kb = radky.Where(r => Regex.IsMatch(r, @"^\s*[\d\s  .,']+\s*KB\b", RegexOptions.IgnoreCase)).ToList();
        var klic = kb.FirstOrDefault(r => Regex.IsMatch(r, "bad|chybn|vadn|fehlerhaft|défectueux|uszkodz|hibás|poškoden", RegexOptions.IgnoreCase));
        var radek = klic ?? (kb.Count >= 7 ? kb[3] : null);
        if (radek == null) return null;
        var cislo = Regex.Replace(Regex.Split(radek, "KB", RegexOptions.IgnoreCase)[0], @"[^\d]", "");
        return long.TryParse(cislo, out var n) ? n : null;
    }
}
