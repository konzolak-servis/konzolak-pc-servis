## 0.5
- Každé tlačítko nápravy teď ukáže výsledek: zelené nebo červené oznámení nahoře, u chyby i okno s vysvětlením.
- Po nápravě se příslušná kontrola sama spustí znovu a ukáže nový výsledek se srovnáním „před nápravou / po nápravě“ (u Kompletní diagnostiky se celá diagnostika provede znovu).
- Restart služby ověří, že služba opravdu běží; u služeb, které se spouštějí jen na vyžádání (např. MapsBroker), vysvětlí, že samovolné ukončení je normální.
- Základní kontroly systému: u neúspěšné kontroly důvod (provázaný s nalezeným problémem), co to znamená, jak to ověřit a tlačítka nápravy (restart služby, otevření Služeb, Prohlížeče událostí, Správce zařízení…).
- Nový test rychlosti internetu: stahování, odesílání, ping, kolísání, ztráta paketů, síla Wi-Fi a doporučení. Zdarma, bez účtu.
- Kontrola aktualizací řekne, proč nejsou nové verze dostupné.

## 0.4
- Nová sekce Nástroje Windows: God Mode a rychlé spuštění 30 systémových nástrojů (Správce zařízení, Správa disků, msconfig, registry, Prohlížeč událostí, Monitor spolehlivosti, Monitor prostředků, Plánovač úloh, test RAM, dxdiag, příkazový řádek jako správce…).
- Restart do BIOSu/UEFI a do pokročilého spuštění (nouzový režim, oprava spuštění).
- Diagnostika – Zabezpečení a licence: antivirus a jeho definice, Windows Defender (aktualizace definic, rychlá kontrola), firewall (zapnutí jedním klikem), stáří aktualizací Windows, UAC, Secure Boot, TPM, BitLocker (upozornění na obnovovací klíč), aktivace Windows a OEM klíč z BIOSu.
- Diagnostika – Zařízení a ovladače: zařízení s vykřičníkem, vysvětlení kódu chyby a doporučený postup.
- Opravy – Záloha ovladačů do složky (pro obnovu po reinstalaci Windows).

## 0.3
- Nová sekce Optimalizace s tlačítky nápravy přímo u každé položky.
- Programy po spuštění: vypnutí / zapnutí jedním klikem (stejně jako ve Správci úloh), u každého programu popis, doporučení a vliv na rychlost startu. Tlačítko „Vypnout doporučené“.
- Služby Windows: telemetrie, Xbox, fax, vzdálený registr, indexování, SysMain a další – vysvětlení, doporučený režim a tlačítko „Vrátit původní“.
- Výkon a soukromí: plán napájení podle typu PC (notebook / stolní), hibernace, nahrávání her na pozadí, herní režim, vizuální efekty, rychlé spuštění, telemetrie, reklamní ID, tipy a reklamy ve Windows.
- Všechny změny jsou vratné – původní hodnoty se ukládají do zálohy.
- Diagnostika: u havarované služby tlačítko „Restartovat službu“.

## 0.2
- Výsledky se zobrazují přímo v aplikaci, přehledně a česky (záložky Výsledek / Podrobný log).
- Tlačítko „Uložit výsledek…“ – uložení do HTML (v barvách Konzolák, s logem, dá se vytisknout) nebo TXT.
- Tlačítko „Uložit celý log…“ s volbou místa a formátu.
- Zpráva o baterii: zdraví baterie v %, původní a aktuální kapacita, počet nabíjecích cyklů.
- Přehled hardwaru jako přehledná tabulka, barevné hodnocení volného místa na discích.
- Kompletní diagnostika (perfmon): nalezené problémy, základní kontroly systému a vytížení prostředků.
- Číslo verze v názvu souboru a v titulku okna.
- Automatická kontrola nové verze při spuštění a tlačítko „Aktualizovat“.
- Ikona aplikace v barvách Konzolák.
- Oprava: tlačítka ve spodní liště se už nepřekrývají s textem.

## 0.1
- První verze: diagnostika přes perfmon /report, disky (SMART, chkdsk, optimalizace), opravy (DISM, SFC, Windows Update), čištění, síť, přehled hardwaru a ukládání logů.
