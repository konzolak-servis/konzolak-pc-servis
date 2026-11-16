# Konzolák Servis

Samostatná servisní aplikace pro Windows 10/11: diagnostika stavu PC (postavená na `perfmon /report`),
kontrola a optimalizace disků, opravy systému, čištění, síť, přehled hardwaru a ukládání protokolů. Celá v češtině.

## Technologie
- **C# / .NET 8 + WPF**, výstup je **jeden `KonzolakServis.exe`** (self-contained, nic se neinstaluje, ~70 MB).
- Při spuštění si **sám řekne o práva správce** (UAC) – bez nich nejdou sfc, DISM, chkdsk ani perfmon.
- Žádné cizí nástroje, jen vestavěné příkazy Windows (perfmon, logman, sfc, DISM, chkdsk, defrag, powercfg, WMI, PowerShell).

## Použití na cizích PC
Aplikace je určená pro servis libovolných zákaznických počítačů:
- Windows 10 a 11 (x64), desktopy i notebooky, bez instalace (stačí spustit z flashky).
- Nezávislá na jazyku Windows: zprávu perfmon čte podle vnitřní struktury (`Section`/`Table`/`Item`/`Data`, sekce `advice`), texty bere z překladů přímo ve zprávě; nepoužívá potvrzování typu „A/Y“.
- Systémový disk zjišťuje z `%SystemDrive%`, nepředpokládá `C:`.
- Funkce, které na daném PC nedávají smysl (baterie u desktopu), jen ohlásí, že nic nenašly.

## Sestavení
Na Windows s .NET 8 SDK:
```
dotnet publish src/KonzolakServis/KonzolakServis.csproj -c Release -o publish
```
Nebo po nahrání na GitHub se `.exe` sestaví samo (záložka Actions → artefakt *KonzolakServis*).

## Funkce (verze 0.1)
| Sekce | Akce |
|---|---|
| Diagnostika | Kompletní diagnostika přes perfmon /report (chyby a varování ze zprávy), kritické události za 7 dní, programy po spuštění, zpráva o baterii |
| Disky | Zdraví disků SMART (teplota, opotřebení SSD, chyby), chkdsk /scan, chkdsk /f /r při restartu, optimalizace/defragmentace (TRIM pro SSD) |
| Opravy | Bod obnovení, DISM RestoreHealth, sfc /scannow, reset Windows Update |
| Čištění | Dočasné soubory, koš, vyčištění součástí Windows |
| Síť | Test připojení (brána, DNS, internet), reset DNS + Winsock |
| Hardware | CPU, RAM, deska, BIOS, grafika, disky, oddíly, síť, baterie |
| Protokol | Vše se zapisuje do konzole v aplikaci, uložení do TXT/HTML v `Dokumenty\Konzolák Servis\Logy` |

Akce, které mění systém, se před spuštěním vždy zeptají.

## Přebarvení na značku
Barvy (navy #0F2038 a zlatá #C8992E) a logo jsou převzaté z webu servis.konzolak.com. Barvy jsou v `src/KonzolakServis/Themes/Theme.xaml`, logo v `src/KonzolakServis/Assets/logo.png`.

## Plán dalších verzí
1. Vydávání verzí (GitHub Releases) místo dočasných artefaktů.
2. **Doladění parseru perfmon zprávy** na skutečném `report.xml` + české vysvětlení a tlačítko „Opravit“ u známých problémů.
3. **Přehledová obrazovka „Zdraví PC“** – skóre 0–100 a semafor (disk, RAM, teploty, místo, aktualizace, ovladače).
4. **Jedno tlačítko „Rychlý servis“** – bod obnovení → čištění → DISM → SFC → optimalizace → protokol.
5. **Servisní protokol pro zákazníka** – HTML/PDF s logem Konzolák, stav před a po, údaje o PC.
6. Další nápady: test RAM (mdsched), kontrola ovladačů s chybou, správa programů po spuštění (vypnout), odinstalace bloatwaru, stav antiviru a firewallu, kontrola aktualizací, export klíče/licence Windows, zálohování ovladačů (`pnputil /export-driver`).
