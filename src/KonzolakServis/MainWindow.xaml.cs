using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using KonzolakServis.Models;
using KonzolakServis.Services;

namespace KonzolakServis;

public partial class MainWindow : Window
{
    private readonly LogService _log = new();
    private CancellationTokenSource? _cts;

    private static readonly (Kategorie Kat, string Nazev)[] Sekce =
    {
        (Kategorie.Diagnostika, "Diagnostika"),
        (Kategorie.Disky, "Disky"),
        (Kategorie.Opravy, "Opravy systému"),
        (Kategorie.Cisteni, "Čištění"),
        (Kategorie.Sit, "Síť"),
        (Kategorie.HwInfo, "Hardware"),
    };

    public MainWindow()
    {
        InitializeComponent();
        _log.Radek += l => Dispatcher.Invoke(() => { Konzole.AppendText(l + Environment.NewLine); Konzole.ScrollToEnd(); });

        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        AdminInfo.Text = admin ? "Spuštěno jako správce ✔" : "⚠ Bez práv správce – opravy nebudou fungovat";

        foreach (var (kat, nazev) in Sekce)
        {
            var rb = new RadioButton { Content = nazev, GroupName = "menu", Style = (Style)FindResource("PolozkaMenu") };
            rb.Checked += (_, _) => Zobraz(kat, nazev);
            Menu.Children.Add(rb);
        }
        ((RadioButton)Menu.Children[0]).IsChecked = true;
        _log.Zapis("Konzolák Servis připraven.");
    }

    private void Zobraz(Kategorie kat, string nazev)
    {
        Nadpis.Text = nazev;
        Akce.Children.Clear();
        foreach (var a in ServisniKatalog.Vse.Where(a => a.Kategorie == kat))
        {
            var obsah = new StackPanel();
            obsah.Children.Add(new TextBlock { Text = a.Nazev, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            obsah.Children.Add(new TextBlock { Text = a.Popis, TextWrapping = TextWrapping.Wrap, MaxWidth = 240,
                Opacity = 0.75, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) });
            var b = new Button { Content = obsah, Style = (Style)FindResource("TlacitkoAkce"), Width = 270 };
            b.Click += async (_, _) => await SpustAsync(a);
            Akce.Children.Add(b);
        }
    }

    private async Task SpustAsync(ServisniAkce a)
    {
        if (_cts != null) { MessageBox.Show("Počkej, až doběhne předchozí akce.", "Konzolák Servis"); return; }
        if (a.MeniSystem)
        {
            var text = $"Akce „{a.Nazev}“ provede změny v systému.\n{a.Varovani}\n\nPokračovat?";
            if (MessageBox.Show(text, "Konzolák Servis", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        _cts = new CancellationTokenSource();
        Prubeh.Visibility = Visibility.Visible; Zrusit.IsEnabled = true; Akce.IsEnabled = false;
        StavText.Text = $"Probíhá: {a.Nazev}…";
        _log.Zapis($"===== {a.Nazev} =====");
        var progress = new Progress<string>(_log.Zapis);
        try
        {
            var v = await Task.Run(() => a.Spustit(progress, _cts.Token));
            _log.Zapis($"VÝSLEDEK [{PerfmonReportService.Popisek(v.Stav)}]: {v.Shrnuti}");
            StavText.Text = v.Shrnuti;
        }
        catch (OperationCanceledException) { _log.Zapis("Akce přerušena uživatelem."); StavText.Text = "Přerušeno"; }
        catch (Exception ex) { _log.Zapis($"CHYBA: {ex.Message}"); StavText.Text = "Chyba – viz log"; }
        finally
        {
            _cts.Dispose(); _cts = null;
            Prubeh.Visibility = Visibility.Collapsed; Zrusit.IsEnabled = false; Akce.IsEnabled = true;
        }
    }

    private void Zrusit_Click(object s, RoutedEventArgs e) => _cts?.Cancel();
    private void UlozTxt_Click(object s, RoutedEventArgs e) => Ulozeno(_log.UlozTxt());
    private void UlozHtml_Click(object s, RoutedEventArgs e) => Ulozeno(_log.UlozHtml());
    private void Ulozeno(string path) { _log.Zapis($"Log uložen: {path}"); StavText.Text = "Log uložen"; }

    private void OtevritLogy_Click(object s, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(LogService.SlozkaLogu);
        Process.Start(new ProcessStartInfo("explorer.exe", LogService.SlozkaLogu) { UseShellExecute = true });
    }
}
