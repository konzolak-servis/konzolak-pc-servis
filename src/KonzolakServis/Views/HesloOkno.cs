using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KonzolakServis.Models;
using KonzolakServis.Services;

namespace KonzolakServis.Views;

/// <summary>Okno pro zadání hesla při spuštění aplikace. Vrátí true, když uživatel zadal správné heslo.</summary>
public static class HesloOkno
{
    public static bool Overit()
    {
        Brush B(string k) => Application.Current.TryFindResource(k) as Brush ?? Brushes.Gray;

        var w = new Window
        {
            Title = "Konzolak service app", Width = 420, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = B("Pozadi"), Foreground = B("Text"), ShowInTaskbar = true, Icon = Ikona.Aplikace
        };

        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock
        {
            Text = "Konzolak service app", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = B("Akcent")
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Zadejte heslo pro spuštění.", Margin = new Thickness(0, 10, 0, 6),
            Foreground = B("TextTlumeny"), TextWrapping = TextWrapping.Wrap
        });

        var heslo = new PasswordBox { Padding = new Thickness(6, 5, 6, 5), FontSize = 15 };
        panel.Children.Add(heslo);

        var chyba = new TextBlock
        {
            Foreground = B("Chyba"), Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(chyba);

        var tlacitka = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var ok = new Button { Content = "Spustit", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var zrusit = new Button { Content = "Zavřít", IsCancel = true };
        if (Application.Current.TryFindResource("TlacitkoNaprava") is Style sn) ok.Style = sn;
        if (Application.Current.TryFindResource("TlacitkoMale") is Style sm) zrusit.Style = sm;

        ok.Click += (_, _) =>
        {
            if (HesloService.JeSpravne(heslo.Password)) { w.DialogResult = true; }
            else
            {
                chyba.Text = "Nesprávné heslo.";
                chyba.Visibility = Visibility.Visible;
                heslo.Clear();
                heslo.Focus();
            }
        };
        tlacitka.Children.Add(ok);
        tlacitka.Children.Add(zrusit);
        panel.Children.Add(tlacitka);

        w.Content = panel;
        w.Loaded += (_, _) => heslo.Focus();
        return w.ShowDialog() == true;
    }

    /// <summary>Dialog pro změnu hesla pro spuštění. Vrátí čitelný výsledek. Volatelné z libovolného vlákna.</summary>
    public static Zprava Zmenit() => Application.Current.Dispatcher.Invoke(() =>
    {
        Brush B(string k) => Application.Current.TryFindResource(k) as Brush ?? Brushes.Gray;

        var w = new Window
        {
            Title = "Změnit heslo pro spuštění", Width = 440, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = B("Pozadi"), Foreground = B("Text"), Owner = Application.Current.MainWindow, ShowInTaskbar = false,
            Icon = Ikona.Aplikace
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Změnit heslo pro spuštění", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = B("Akcent") });
        panel.Children.Add(new TextBlock
        {
            Text = "Nové heslo se uloží jen do tohoto počítače (není součástí aplikace). Platí pro příští spuštění.",
            Margin = new Thickness(0, 8, 0, 10), Foreground = B("TextTlumeny"), TextWrapping = TextWrapping.Wrap
        });

        PasswordBox Pole(string popis)
        {
            panel.Children.Add(new TextBlock { Text = popis, Margin = new Thickness(0, 8, 0, 3) });
            var pb = new PasswordBox { Padding = new Thickness(6, 4, 6, 4), FontSize = 14 };
            panel.Children.Add(pb);
            return pb;
        }
        var stare = Pole("Současné heslo:");
        var nove = Pole("Nové heslo:");
        var nove2 = Pole("Nové heslo znovu:");

        var chyba = new TextBlock { Foreground = B("Chyba"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(chyba);

        var tlacitka = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ulozit = new Button { Content = "Uložit", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var zrusit = new Button { Content = "Zrušit", IsCancel = true };
        if (Application.Current.TryFindResource("TlacitkoNaprava") is Style sn) ulozit.Style = sn;
        if (Application.Current.TryFindResource("TlacitkoMale") is Style sm) zrusit.Style = sm;
        void Chyba(string t) { chyba.Text = t; chyba.Visibility = Visibility.Visible; }
        ulozit.Click += (_, _) =>
        {
            if (!HesloService.JeSpravne(stare.Password)) { Chyba("Současné heslo není správné."); return; }
            if (nove.Password.Length < 4) { Chyba("Nové heslo musí mít aspoň 4 znaky."); return; }
            if (nove.Password != nove2.Password) { Chyba("Nová hesla se neshodují."); return; }
            w.DialogResult = true;
        };
        tlacitka.Children.Add(ulozit);
        tlacitka.Children.Add(zrusit);
        panel.Children.Add(tlacitka);
        w.Content = panel;

        var z = new Zprava("Změna hesla pro spuštění");
        if (w.ShowDialog() == true)
        {
            try
            {
                HesloService.Nastav(nove.Password);
                z.Stav = Stav.Ok; z.Shrnuti = "Heslo pro spuštění bylo změněno.";
                z.PridejSekci("Hotovo").Pridej("Nové heslo platí od příštího spuštění aplikace na tomto počítači.");
            }
            catch (Exception ex)
            {
                z.Stav = Stav.Chyba; z.Shrnuti = "Heslo se nepodařilo uložit.";
                z.PridejSekci("Chyba").Pridej(ex.Message, "", Stav.Chyba);
            }
        }
        else { z.Stav = Stav.Info; z.Shrnuti = "Změna hesla zrušena."; }
        return z;
    });
}
