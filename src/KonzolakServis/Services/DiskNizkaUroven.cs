using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KonzolakServis.Services;

/// <summary>
/// Přímý přístup k disku přes Win32 (bez mezipaměti Windows), aby měření odpovídalo skutečnému disku.
/// Fyzický disk se otevírá VÝHRADNĚ pro čtení – zápis se testuje jen do dočasného souboru.
/// </summary>
internal sealed class DiskNizkaUroven : IDisposable
{
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3, CREATE_ALWAYS = 2;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000;
    private const uint FILE_FLAG_DELETE_ON_CLOSE = 0x04000000, FILE_ATTRIBUTE_HIDDEN = 2;
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const int Zarovnani = 4096;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, out int read, IntPtr ovl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, out int written, IntPtr ovl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long newPos, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inLen,
        out long outBuf, int outLen, out int returned, IntPtr ovl);

    private readonly SafeFileHandle _h;
    private readonly IntPtr _surovy;
    public IntPtr Buffer { get; }
    public int MaxDelka { get; }
    public long Velikost { get; }

    private DiskNizkaUroven(SafeFileHandle h, int maxDelka, long velikost)
    {
        _h = h;
        MaxDelka = maxDelka;
        Velikost = velikost;
        _surovy = Marshal.AllocHGlobal(maxDelka + Zarovnani);
        // NO_BUFFERING vyžaduje buffer zarovnaný na sektor
        Buffer = new IntPtr((_surovy.ToInt64() + Zarovnani - 1) & ~(long)(Zarovnani - 1));
    }

    /// <summary>Otevře fyzický disk pouze pro čtení.</summary>
    public static DiskNizkaUroven OtevriDisk(int cislo, int maxDelka)
    {
        var h = CreateFileW($@"\\.\PhysicalDrive{cislo}", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"Disk {cislo} nelze otevřít (chyba Windows {Marshal.GetLastWin32Error()}). Je aplikace spuštěná jako správce?");
        if (!DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, out long delka, 8, out _, IntPtr.Zero))
        {
            h.Dispose();
            throw new IOException($"Nelze zjistit velikost disku {cislo} (chyba {Marshal.GetLastWin32Error()}).");
        }
        return new DiskNizkaUroven(h, maxDelka, delka);
    }

    /// <summary>Vytvoří skrytý dočasný soubor pro test zápisu – po zavření ho Windows sám smaže.</summary>
    public static DiskNizkaUroven VytvorTestovaciSoubor(string cesta, int maxDelka)
    {
        var h = CreateFileW(cesta, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE, IntPtr.Zero,
            CREATE_ALWAYS, FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH | FILE_FLAG_DELETE_ON_CLOSE | FILE_ATTRIBUTE_HIDDEN,
            IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"Testovací soubor nelze vytvořit (chyba Windows {Marshal.GetLastWin32Error()}).");
        return new DiskNizkaUroven(h, maxDelka, 0);
    }

    /// <summary>Přečte blok z dané pozice. Vrací false při chybě čtení (např. vadný sektor).</summary>
    public bool Cti(long pozice, int delka)
    {
        if (!SetFilePointerEx(_h, pozice, out _, 0)) return false;
        return ReadFile(_h, Buffer, delka, out var precteno, IntPtr.Zero) && precteno == delka;
    }

    public bool Zapis(long pozice, int delka)
    {
        if (!SetFilePointerEx(_h, pozice, out _, 0)) return false;
        return WriteFile(_h, Buffer, delka, out var zapsano, IntPtr.Zero) && zapsano == delka;
    }

    public static int PosledniChyba => Marshal.GetLastWin32Error();

    /// <summary>Naplní buffer náhodnými daty (aby SSD nemohlo data „zkomprimovat“ a zkreslit měření).</summary>
    public void NaplnNahodne()
    {
        var pole = new byte[MaxDelka];
        Random.Shared.NextBytes(pole);
        Marshal.Copy(pole, 0, Buffer, MaxDelka);
    }

    public void Dispose()
    {
        _h.Dispose();
        Marshal.FreeHGlobal(_surovy);
    }
}
