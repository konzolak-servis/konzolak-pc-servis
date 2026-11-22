using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KonzolakServis.Views;

/// <summary>Ikona aplikace (konzolák) pro okna vytvářená v kódu – aby měla ikonu i na hlavním panelu.</summary>
public static class Ikona
{
    private static ImageSource? _ikona;

    public static ImageSource? Aplikace
    {
        get
        {
            if (_ikona != null) return _ikona;
            try { _ikona = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/konzolak.ico")); }
            catch { /* ikona se nenačetla – okno prostě bude bez ní */ }
            return _ikona;
        }
    }
}
