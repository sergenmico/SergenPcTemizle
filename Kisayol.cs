// Genel kısayol (oyundayken de çalışır), bildirim sesi ve tam ekran algılama.
// Kısayol Windows'un RegisterHotKey'i ile alınır: klavye kancası kurulmaz, tuşlar dinlenmez; hile önleyiciler
// buna takılmaz. Tuş program açıkken (tepside de) yalnızca bizimdir; o yüzden değiştirilebilir ve kapatılabilir.
// Ses exe'nin içinde üretilir (kısa, yumuşak iki ton); düzeyi Sağlık sekmesinden ayarlanır, 0 = kapalı.

using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class KisayolAyari
{
    const string AyarAdi = "kisayol";
    public const Keys Varsayilan = Keys.Control | Keys.Alt | Keys.O;

    // Kayıtlı kısayol; Keys.None = kapalı. Bozuk / izin verilmeyen değer varsayılana döner.
    public static Keys Oku()
    {
        int d = Ayarlar.OkuSayi(AyarAdi, (int)Varsayilan);
        if (d == 0) return Keys.None;
        return Gecerli((Keys)d) ? (Keys)d : Varsayilan;
    }

    public static void Yaz(Keys k) { Ayarlar.YazSayi(AyarAdi, Gecerli(k) ? (int)k : 0); }

    // Ctrl ya da Alt şart: tek başına harf / Shift+harf alınırsa her yerde yazı yazmak bozulur.
    // Win tuşu Windows'un kendi kısayollarına ayrılmış olduğu için kullanılmaz.
    public static bool Gecerli(Keys k)
    {
        Keys tus = k & Keys.KeyCode;
        if (tus == Keys.None || (k & (Keys.Control | Keys.Alt)) == 0) return false;
        if (tus == Keys.ControlKey || tus == Keys.Menu || tus == Keys.ShiftKey || tus == Keys.LWin || tus == Keys.RWin
            || tus == Keys.LControlKey || tus == Keys.RControlKey || tus == Keys.LMenu || tus == Keys.RMenu
            || tus == Keys.LShiftKey || tus == Keys.RShiftKey) return false;
        if (tus == Keys.Delete && (k & Keys.Control) != 0 && (k & Keys.Alt) != 0) return false;   // Ctrl+Alt+Del
        return true;
    }

    public static string Metin(Keys k)
    {
        if (k == Keys.None) return "kapalı";
        var p = new List<string>();
        if ((k & Keys.Control) != 0) p.Add("Ctrl");
        if ((k & Keys.Alt) != 0) p.Add("Alt");
        if ((k & Keys.Shift) != 0) p.Add("Shift");
        p.Add(TusAdi(k & Keys.KeyCode));
        return string.Join("+", p);
    }

    static string TusAdi(Keys tus)
    {
        if (tus >= Keys.D0 && tus <= Keys.D9) return ((char)('0' + (tus - Keys.D0))).ToString();
        if (tus >= Keys.NumPad0 && tus <= Keys.NumPad9) return "Num" + (tus - Keys.NumPad0);
        if (tus >= Keys.A && tus <= Keys.Z) return ((char)('A' + (tus - Keys.A))).ToString();
        // Klavye düzenine bağlı tuşlar (Türkçe Ğ, Ü, Ş...): tuşun o düzende yazdığı harf.
        uint c = MapVirtualKey((uint)tus, 2 /* MAPVK_VK_TO_CHAR */) & 0x7FFFFFFF;
        if (c > 32) return char.ToUpper((char)c).ToString();
        switch (tus)
        {
            case Keys.Space: return "Boşluk";
            case Keys.Return: return "Enter";
            case Keys.Back: return "Geri";
            case Keys.Prior: return "PageUp";
            case Keys.Next: return "PageDown";
            case Keys.Insert: return "Insert";
            case Keys.Delete: return "Delete";
            case Keys.Home: return "Home";
            case Keys.End: return "End";
            case Keys.Up: return "Yukarı";
            case Keys.Down: return "Aşağı";
            case Keys.Left: return "Sol";
            case Keys.Right: return "Sağ";
        }
        return tus.ToString();
    }

    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint kod, uint tur);
}

// Görünmeyen, yalnızca ileti alan bir pencere; WM_HOTKEY gelince Basildi çağrılır.
// Arayüz iş parçacığında oluşturulmalı (iletiler o iş parçacığının döngüsüne gelir).
class GenelKisayol : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312, Kimlik = 1;
    bool kayitli;

    public event Action Basildi;
    public Keys Tus { get; private set; }

    public GenelKisayol()
    {
        CreateHandle(new CreateParams { Caption = "SergenPcTemizle_kisayol", Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
    }

    // false: tuş başka bir program tarafından alınmış (ya da Windows'a ayrılmış). Keys.None = kısayolu kaldır.
    public bool Ayarla(Keys k)
    {
        Birak();
        Tus = k;
        if (k == Keys.None || Handle == IntPtr.Zero) return k == Keys.None;
        uint mod = 0x4000;   // MOD_NOREPEAT: basılı tutunca tekrar tekrar tetiklenmesin
        if ((k & Keys.Alt) != 0) mod |= 0x1;
        if ((k & Keys.Control) != 0) mod |= 0x2;
        if ((k & Keys.Shift) != 0) mod |= 0x4;
        kayitli = RegisterHotKey(Handle, Kimlik, mod, (uint)(k & Keys.KeyCode));
        return kayitli;
    }

    public bool Calisiyor { get { return kayitli; } }

    // Kısayol değiştirilirken geçici olarak bırakılır (yoksa yeni tuşu yakalamaya çalışırken eskisi optimize başlatır).
    public void Birak()
    {
        if (kayitli && Handle != IntPtr.Zero) UnregisterHotKey(Handle, Kimlik);
        kayitli = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == Kimlik)
        {
            var b = Basildi;
            if (b != null) try { b(); } catch { }
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Birak();
        try { if (Handle != IntPtr.Zero) DestroyHandle(); } catch { }
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int kimlik, uint mod, uint tus);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int kimlik);
}

enum SesTuru { Tamam, Uyari }

// Kısa bildirim sesi: Tamam = yükselen iki ton ("bip-bip"), Uyari = alçalan iki ton (sanal bellek kritik).
// Saf sinüs, yumuşak giriş/çıkış (tık sesi olmasın). Düzey 0–100; 100'de bile tam ölçeğin yarısı:
// oyun sesinin üstüne bağırmasın. Windows'un ses düzeyiyle çarpılır.
static class Ses
{
    const string AyarAdi = "ses_duzeyi";
    public const int VarsayilanDuzey = 30;
    static readonly object kilit = new object();
    static readonly Dictionary<string, byte[]> onbellek = new Dictionary<string, byte[]>();
    static SoundPlayer calan;

    public static int Duzey
    {
        get { return Math.Max(0, Math.Min(100, Ayarlar.OkuSayi(AyarAdi, VarsayilanDuzey))); }
        set { Ayarlar.YazSayi(AyarAdi, Math.Max(0, Math.Min(100, value))); }
    }

    public static void Cal(SesTuru tur) { Cal(tur, Duzey); }

    public static void Cal(SesTuru tur, int duzey)
    {
        if (duzey <= 0) return;
        try
        {
            byte[] wav;
            lock (kilit)
            {
                string anahtar = tur + "/" + duzey;
                if (!onbellek.TryGetValue(anahtar, out wav)) { wav = Uret(tur, duzey); onbellek[anahtar] = wav; }
                // SoundPlayer akışı çalarken okur: önceki çalar bitmeden akışı kapatılmasın diye yalnızca durdurulur.
                if (calan != null) { try { calan.Stop(); calan.Dispose(); } catch { } }
                calan = new SoundPlayer(new MemoryStream(wav));
                calan.Play();   // eşzamansız; arayüzü bekletmez
            }
        }
        catch { }
    }

    static byte[] Uret(SesTuru tur, int duzey)
    {
        const int Hz = 44100;
        double genlik = 0.5 * duzey / 100.0;
        // Tamam: A5 → D6 (yükselen, "oldu"). Uyari: E5 → A4 (alçalan, "dikkat").
        double[] frekans = tur == SesTuru.Tamam ? new[] { 880.0, 1174.66 } : new[] { 659.26, 440.0 };
        int tonUzunluk = Hz * 85 / 1000, ara = Hz * 55 / 1000, sonSessizlik = Hz * 30 / 1000;
        int toplam = tonUzunluk * 2 + ara + sonSessizlik;
        var ornek = new short[toplam];
        for (int t = 0; t < 2; t++)
        {
            int bas = t * (tonUzunluk + ara);
            int yumusak = Hz * 12 / 1000;
            for (int i = 0; i < tonUzunluk; i++)
            {
                double zarf = Math.Min(1.0, Math.Min(i / (double)yumusak, (tonUzunluk - i) / (double)yumusak));
                zarf = zarf * zarf * (3 - 2 * zarf);   // yumuşak eğri
                double d = Math.Sin(2 * Math.PI * frekans[t] * i / Hz) * genlik * zarf;
                ornek[bas + i] = (short)(d * short.MaxValue);
            }
        }
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            int veri = toplam * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + veri); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(Hz); w.Write(Hz * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(veri);
            foreach (var s in ornek) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }
    }
}

// Önde tam ekran bir program (oyun, tam ekran video, sunum) var mı? Windows'un kendi "rahatsız etme" kararı
// (SHQueryUserNotificationState) ve yedek olarak öndeki pencerenin ekranı tamamen kaplaması. Kenarlıksız tam ekran
// oyunlar da böyle yakalanır. Masaüstü / görev çubuğu ve bu programın kendi penceresi sayılmaz.
static class TamEkran
{
    public static bool Var()
    {
        try
        {
            int durum;
            if (SHQueryUserNotificationState(out durum) == 0
                && (durum == 2 /* QUNS_BUSY */ || durum == 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */ || durum == 4 /* QUNS_PRESENTATION_MODE */))
                return true;
        }
        catch { }
        try { return OndekiPencereEkraniKapliyor(); } catch { return false; }
    }

    // Öndeki pencerenin süreç kimliği (yoksa 0). Oyun menüdeyken ekran kartını az kullanabilir; RAM boşaltılırken
    // öndeki program her durumda atlanır.
    public static int OndekiSurec()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            int pid;
            Yerel.GetWindowThreadProcessId(h, out pid);
            return pid;
        }
        catch { return 0; }
    }

    static bool OndekiPencereEkraniKapliyor()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        var sinif = new StringBuilder(64);
        GetClassName(h, sinif, sinif.Capacity);
        string s = sinif.ToString();
        if (s == "Progman" || s == "WorkerW" || s == "Shell_TrayWnd" || s == "Shell_SecondaryTrayWnd") return false;
        int pid;
        Yerel.GetWindowThreadProcessId(h, out pid);
        using (var p = System.Diagnostics.Process.GetCurrentProcess()) if (pid == p.Id) return false;
        RECT r;
        if (!GetWindowRect(h, out r)) return false;
        var ekran = Screen.FromHandle(h).Bounds;
        return r.Sol <= ekran.Left && r.Ust <= ekran.Top && r.Sag >= ekran.Right && r.Alt >= ekran.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Sol, Ust, Sag, Alt; }
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int durum);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
}

#if DENEME
// Yalnızca deneme derlemesinde: olanları %TEMP%\sergen-deneme.log dosyasına yazar.
static class Deneme
{
    public static void Yaz(string s)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "sergen-deneme.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); }
        catch { }
    }
}
#endif
