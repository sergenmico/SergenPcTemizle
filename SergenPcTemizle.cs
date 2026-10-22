// SergenPcTemizle — basit sistem izleyici, RAM / işlemci / ekran kartı rahatlatıcı ve disk temizleyici.
// "Sistem" sekmesi: işlemci, ekran kartı, RAM, VRAM, sanal bellek ve ısı göstergeleri; altta kapatılabilir
// programlar (işlemci, bellek, GPU, VRAM kullanımıyla, canlı). "Temizle" işaretlileri kapatır ve süreçlerin
// çalışma kümesini (working set) boşaltır. "Disk" sekmesi: gereksiz dosya temizliği (normal / derin) ve
// depolama analizi. Yönetici izni yalnızca sanal bellek ve Windows kalıntıları için, o an istenir.
// Derleme: derle.cmd  (Windows'taki hazır .NET Framework csc.exe, C# 5)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // "Sanal belleği büyüt / geri al" kendini yönetici olarak bu argümanlarla açar; pencere göstermeden ayarı yazıp çıkar.
        int ilk, enCok;
        if (args.Length == 3 && args[0] == "--sanal-bellek" && int.TryParse(args[1], out ilk) && int.TryParse(args[2], out enCok))
            return ilk >= 1024 && enCok >= ilk ? SanalBellek.Uygula(ilk, enCok) : 1;
        if (args.Length >= 1 && args[0] == "--sanal-bellek-geri")
            return SanalBellek.GeriYukle(args.Skip(1).ToArray());
        if (args.Length == 2 && args[0] == "--nvidia-kur")
            return NvidiaSurucu.Kur(args[1]);
        if (args.Length == 2 && args[0] == "--ag-dns")
            return AgAyarlari.YoneticiDns(args.Skip(1).ToArray());
        if (args.Length == 1 && args[0] == "--ag-sifirla")
            return AgAyarlari.YoneticiSifirla();
        if (args.Length >= 2 && args[0] == "--windows-kayit")
            return WindowsAyarlari.YoneticiYaz(args.Skip(1).ToArray());
        if (args.Length >= 2 && args[0] == "--turbo")
            return TurboAyarlari.YoneticiYaz(args.Skip(1).ToArray());
        if (args.Length >= 1 && args[0] == "--disk-temizle")
            return DiskTemizlik.YoneticiTemizle(args.Skip(1).ToArray());
        if (args.Length == 4 && args[0] == "--baslangic")
            return BaslangicYoneticisi.YoneticiAyarla(args.Skip(1).ToArray());

        // "--arkaplan": Windows açılışında (Run girdisi) pencere açmadan, yalnızca saatin yanındaki simgeyle başlar.
        bool arkaplan = args.Contains("--arkaplan");

        // "--guncellendi <pid>": güncelleyici yeni sürümü başlattı. Eski örnek kapanmadan mutex alınamaz: önce onu bekle.
        int eskiPid;
        int gi = Array.IndexOf(args, "--guncellendi");
        if (gi >= 0)
        {
            Guncelleyici.Guncellendi = true;
            if (gi + 1 < args.Length && int.TryParse(args[gi + 1], out eskiPid)) GuncellemeMotoru.EskiOrnegiBekle(eskiPid);
        }

        // Tek örnek: program tepside çalışırken yeniden açılırsa ikinci bir pencere/simge çıkmaz; çalışan örneğe
        // "kendini göster" denir. Sinyal önce açılır ki ilk örnek henüz dinlemeye başlamadıysa da kaybolmasın.
        // Çalışan örnek "Yönetici olarak çalıştır" ile açıldıysa olay/mutex yalnızca yöneticiye açıktır: normal
        // açılış erişim reddi alır (çökmesin); sinyal de gönderilemez, kullanıcıya nerede olduğu söylenir.
        EventWaitHandle sinyalNesnesi;
        Mutex kilitNesnesi;
        bool ilkOrnek;
        try
        {
            sinyalNesnesi = new EventWaitHandle(false, EventResetMode.AutoReset, SinyalAdi);
            try { kilitNesnesi = new Mutex(true, KilitAdi, out ilkOrnek); }
            catch (UnauthorizedAccessException) { sinyalNesnesi.Dispose(); throw; }
        }
        catch (UnauthorizedAccessException)
        {
            if (!arkaplan) MessageBox.Show("Program zaten yönetici olarak açık; saatin yanındaki simgesinden açabilirsin.", "Sergen PC Temizle");
            return 0;
        }
        using (var sinyal = sinyalNesnesi)
        {
            using (var kilit = kilitNesnesi)
            {
                if (!ilkOrnek)
                {
                    if (!arkaplan)
                    {
                        try { AllowSetForegroundWindow(-1 /* ASFW_ANY: çalışan örnek öne gelebilsin */); } catch { }
                        sinyal.Set();
                    }
                    return 0;
                }

                // Önceki güncellemeden kalan .eski / .yeni dosyalar (yalnızca ilk örnek siler).
                GuncellemeMotoru.ArtiklariSil();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new AnaForm { GizliBasla = arkaplan };
                // Sinyal ancak pencere tutamacı oluşunca dinlenir: önceden gelmiş sinyal de GosterIstegi'nde
                // (IsHandleCreated) atılıp kaybolmasın. Tutamaç yeniden oluşursa ikinci kez kaydedilmez.
                RegisteredWaitHandle bekleyen = null;
                Action dinle = delegate
                {
                    if (bekleyen == null)
                        bekleyen = ThreadPool.RegisterWaitForSingleObject(sinyal,
                            delegate { form.GosterIstegi(); }, null, Timeout.Infinite, false);
                };
                form.HandleCreated += delegate { dinle(); };
                if (form.IsHandleCreated) dinle();
                try { Application.Run(form); }
                finally
                {
                    if (bekleyen != null) bekleyen.Unregister(null);
                    try { kilit.ReleaseMutex(); } catch { }
                }
                return 0;
            }
        }
    }

    const string KilitAdi = "Local\\SergenPcTemizle";
    const string SinyalAdi = "Local\\SergenPcTemizle_goster";

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int surecKimligi);
}

// Sayfa dosyası ayarı: HKLM\...\Memory Management\PagingFiles. Her satır "C:\pagefile.sys ilkMB enCokMB";
// "?:\pagefile.sys" = Windows otomatik yönetiyor, "0 0" = o sürücüde Windows yönetiyor, hiç satır yok = kapalı.
static class SanalBellek
{
    const string Anahtar = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    public static readonly string Surucu = Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');

    // RAM'e göre boyut: RAM + sayfa dosyası en az ~32 GB olsun (güncel oyunlar bunu ister), 32 GB'tan az RAM'de
    // başlangıç en az 16 GB (Windows'un kendi ayarladığından küçük olmasın), büyük RAM'de RAM'in dörtte biri yeter.
    // Başlangıç 8–24 GB, üst sınır bunun iki katı (16–32 GB).
    //   8 GB RAM → 24 / 32 GB    16 / 24 GB RAM → 16 / 32 GB    32 GB RAM → 8 / 16 GB    64 GB RAM → 16 / 32 GB
    public static void Hedef(ulong ramBayt, out int ilkMB, out int enCokMB)
    {
        int ramGB = (int)Math.Round(ramBayt / 1073741824.0);
        int ilkGB = Math.Max(8, Math.Min(24, Math.Max(ramGB < 32 ? 16 : 0, Math.Max(32 - ramGB, ramGB / 4))));
        ilkMB = ilkGB * 1024;
        enCokMB = Math.Max(16, Math.Min(32, ilkGB * 2)) * 1024;
    }

    public static string[] Oku()
    {
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(Anahtar))
                return (k == null ? null : k.GetValue("PagingFiles") as string[]) ?? new string[0];
        }
        catch { return new string[0]; }
    }

    static string[] Parcala(string satir)
    {
        return satir.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }

    public static string Anlat(string[] satirlar)
    {
        var parcalar = new List<string>();
        foreach (var s in satirlar)
        {
            var p = Parcala(s);
            if (p.Length == 0) continue;
            if (p[0].StartsWith("?:")) { parcalar.Add("Windows otomatik yönetiyor"); continue; }
            string sur = p[0].Length >= 2 ? p[0].Substring(0, 2) : p[0];
            int a, b;
            if (p.Length >= 3 && int.TryParse(p[1], out a) && int.TryParse(p[2], out b) && a > 0)
                parcalar.Add(string.Format("{0} sürücüsünde {1:0.#}–{2:0.#} GB", sur, a / 1024.0, b / 1024.0));
            else
                parcalar.Add(sur + " sürücüsünde, boyutu Windows yönetiyor");
        }
        return parcalar.Count == 0 ? "KAPALI (programların \"bellek yetersiz\" diye kapanmasının en sık sebebi)" : string.Join(", ", parcalar);
    }

    // Elle verilmiş başlangıç boyutlarının toplamı (MB); Windows yönetiyorsa ya da kapalıysa 0.
    public static int ElleIlkMB(string[] satirlar)
    {
        int toplam = 0;
        foreach (var s in satirlar)
        {
            var p = Parcala(s);
            int a;
            if (p.Length >= 3 && !p[0].StartsWith("?:") && int.TryParse(p[1], out a) && a > 0) toplam += a;
        }
        return toplam;
    }

    // Büyütmeden önceki ayar burada durur; dosya varsa düğme "geri al" olur, geri alınınca silinir.
    static readonly string YedekYolu = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SergenPcTemizle", "sanal-bellek-yedek.txt");

    public static bool YedekVar { get { return File.Exists(YedekYolu); } }

    public static void Yedekle(string[] satirlar)
    {
        try
        {
            if (File.Exists(YedekYolu)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(YedekYolu));
            File.WriteAllLines(YedekYolu, new[] { "# Sergen PC Temizle öncesi sayfa dosyası ayarı (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ")" }.Concat(satirlar));
        }
        catch { }
    }

    public static string[] YedekOku()
    {
        try { return File.ReadAllLines(YedekYolu).Where(s => s.Trim().Length > 0 && !s.StartsWith("#")).ToArray(); }
        catch { return null; }
    }

    public static void YedekSil() { try { File.Delete(YedekYolu); } catch { } }

    // Yönetici olarak çalışır. Sistem sürücüsündeki ve otomatik satırları yenisiyle değiştirir,
    // diğer sürücülerdeki sayfa dosyalarına dokunmaz. 0 = tamam, 1 = hata.
    public static int Uygula(int ilkMB, int enCokMB)
    {
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(Anahtar, true))
            {
                var eski = (k.GetValue("PagingFiles") as string[]) ?? new string[0];
                var yeni = eski.Where(s =>
                {
                    var t = s.Trim();
                    return t.Length > 0 && !t.StartsWith("?:") && !t.StartsWith(Surucu, StringComparison.OrdinalIgnoreCase);
                }).ToList();
                yeni.Insert(0, string.Format("{0}\\pagefile.sys {1} {2}", Surucu, ilkMB, enCokMB));
                k.SetValue("PagingFiles", yeni.ToArray(), RegistryValueKind.MultiString);
            }
            return 0;
        }
        catch { return 1; }
    }

    // Yönetici olarak çalışır; yedekteki satırları olduğu gibi geri yazar. Yalnızca sayfa dosyası
    // satırı biçimindekileri kabul eder. 0 = tamam, 1 = hata.
    public static int GeriYukle(string[] satirlar)
    {
        var bicim = new System.Text.RegularExpressions.Regex(@"^([A-Za-z]|\?):\\pagefile\.sys(\s+\d+\s+\d+)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (satirlar.Any(s => !bicim.IsMatch(s.Trim()))) return 1;
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(Anahtar, true))
                k.SetValue("PagingFiles", satirlar.Select(s => s.Trim()).ToArray(), RegistryValueKind.MultiString);
            return 0;
        }
        catch { return 1; }
    }
}

class Temizlik
{
    public string Anahtar, Ad, Aciklama;
    public bool Yonetici, Varsayilan, Derin;
    public string Ozel;                                     // "geridonusum", "hazirda", "windows": klasör silme yerine özel işlem
    public List<string> Klasorler = new List<string>();     // içleri silinir, klasörlerin kendisi kalır
    public List<string> Dosyalar = new List<string>();
    public string[] Surecler = new string[0];               // biri açıksa bu madde atlanır (dosyaları kilitli / bozulabilir)
    public int EnAzGun;                                     // bundan yeni dosyalara dokunulmaz
    public long Boyut = -1;                                 // -1: yönetici izni olmadan okunamıyor
    public string AcikSurec;
}

// Silinecek her şeyin tek listesi. Yönetici gerektiren maddeler, exe kendini "--disk-temizle" ile
// yönetici olarak açınca aynı listeden anahtarla seçilip silinir.
static class DiskTemizlik
{
    // cleanmgr'nin (Disk Temizleme) kendi güvenli temizlikleri; "Windows ESD" bilerek yok (PC'yi sıfırla için gerekir).
    static readonly string[] WindowsTemizlikleri =
    {
        "Previous Installations", "Update Cleanup", "Temporary Setup Files", "Windows Upgrade Log Files",
        "Setup Log Files", "Device Driver Packages", "Delivery Optimization Files"
    };

    public static List<Temizlik> Liste(bool derin)
    {
        string yerel = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string yerelLow = Path.Combine(Path.GetDirectoryName(yerel), "LocalLow");
        string dolasan = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string ortak = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string sistem = Path.GetPathRoot(Environment.SystemDirectory);
        int gun = derin ? 0 : 1;
        var steam = SteamKutuphaneleri();
        var l = new List<Temizlik>();
        Temizlik t;

        t = Ekle(l, "temp", "Geçici dosyalar (%TEMP%)", derin ? "Programların geçici dosyaları, yenileri dahil"
                                                              : "Programların bıraktığı geçici dosyalar (son 24 saat hariç)", true);
        t.EnAzGun = gun;
        t.Klasorler.Add(Path.GetTempPath());

        Chromium(l, "chrome", "Chrome önbelleği", Path.Combine(yerel, @"Google\Chrome\User Data"), "chrome");
        Chromium(l, "edge", "Edge önbelleği", Path.Combine(yerel, @"Microsoft\Edge\User Data"), "msedge");
        Chromium(l, "brave", "Brave önbelleği", Path.Combine(yerel, @"BraveSoftware\Brave-Browser\User Data"), "brave");
        Chromium(l, "operagx", "Opera GX önbelleği", Path.Combine(yerel, @"Opera Software\Opera GX Stable"), "opera");
        Chromium(l, "opera", "Opera önbelleği", Path.Combine(yerel, @"Opera Software\Opera Stable"), "opera");

        t = Ekle(l, "firefox", "Firefox önbelleği", "Site önbelleği; şifreler, çerezler ve geçmiş silinmez", true);
        t.Surecler = new[] { "firefox" };
        foreach (var p in AltKlasorler(Path.Combine(yerel, @"Mozilla\Firefox\Profiles"))) t.Klasorler.Add(Path.Combine(p, "cache2"));

        t = Ekle(l, "discord", "Discord önbelleği", "Resim ve video önbelleği; mesajlar ve ayarlar silinmez", true);
        t.Surecler = new[] { "Discord" };
        foreach (var a in new[] { "Cache", "Code Cache", "GPUCache" }) t.Klasorler.Add(Path.Combine(dolasan, "discord", a));

        t = Ekle(l, "steamweb", "Steam mağaza önbelleği", "Mağaza ve topluluk sayfalarının önbelleği; oyunlar silinmez", true);
        t.Surecler = new[] { "steam", "steamwebhelper" };
        t.Klasorler.Add(Path.Combine(yerel, @"Steam\htmlcache"));
        if (steam.Count > 0) t.Klasorler.Add(Path.Combine(steam[0], @"appcache\httpcache"));

        t = Ekle(l, "epicweb", "Epic Games önbelleği", "Başlatıcının web önbelleği; oyunlar silinmez", true);
        t.Surecler = new[] { "EpicGamesLauncher" };
        foreach (var p in AltKlasorler(Path.Combine(yerel, @"EpicGamesLauncher\Saved")))
            if (Path.GetFileName(p).StartsWith("webcache", StringComparison.OrdinalIgnoreCase)) t.Klasorler.Add(p);

        t = Ekle(l, "cokme", "Program çökme kayıtları", "Bir oyunun neden çöktüğünü bulmak için gerekebilir", derin);
        t.Klasorler.Add(Path.Combine(yerel, "CrashDumps"));
        t.Klasorler.Add(Path.Combine(yerel, @"Microsoft\Windows\WER\ReportArchive"));
        t.Klasorler.Add(Path.Combine(yerel, @"Microsoft\Windows\WER\ReportQueue"));

        t = Ekle(l, "geridonusum", "Geri dönüşüm kutusu", "Çöpe atılan dosyalar kalıcı olarak silinir", false);
        t.Ozel = "geridonusum";

        if (derin)
        {
            t = Ekle(l, "shader", "Ekran kartı shader önbellekleri", "Oyunlar ilk açılışta takılır, shader'lar yeniden derlenir", true);
            t.Derin = true;
            foreach (var y in new[] {
                Path.Combine(yerel, @"NVIDIA\DXCache"), Path.Combine(yerel, @"NVIDIA\GLCache"),
                Path.Combine(yerelLow, @"NVIDIA\PerDriverVersion\DXCache"), Path.Combine(yerelLow, @"NVIDIA\PerDriverVersion\GLCache"),
                Path.Combine(yerel, @"AMD\DxCache"), Path.Combine(yerel, @"AMD\DxcCache"), Path.Combine(yerel, @"AMD\GLCache"),
                Path.Combine(yerel, @"AMD\VkCache"), Path.Combine(yerelLow, @"Intel\ShaderCache"), Path.Combine(yerel, "D3DSCache") })
                t.Klasorler.Add(y);

            t = Ekle(l, "steamshader", "Steam oyun shader önbelleği", "Oyunlar ilk açılışta takılır; Steam gerekirse yeniden indirir", true);
            t.Derin = true;
            foreach (var k in steam) t.Klasorler.Add(Path.Combine(k, @"steamapps\shadercache"));
        }

        t = Ekle(l, "wintemp", "Windows geçici dosyaları", "Windows ve kurulumların geçici dosyaları", true);
        t.Yonetici = true; t.EnAzGun = gun;
        t.Klasorler.Add(Path.Combine(win, "Temp"));

        t = Ekle(l, "wu", "Windows Update indirmeleri", "Kurulmuş güncellemelerin indirme dosyaları", true);
        t.Yonetici = true; t.EnAzGun = gun;
        t.Klasorler.Add(Path.Combine(win, @"SoftwareDistribution\Download"));

        t = Ekle(l, "sistemcokme", "Sistem çökme dökümleri", "Mavi ekran sebebini bulmak için gerekebilir", derin);
        t.Yonetici = true;
        t.Klasorler.Add(Path.Combine(win, "Minidump"));
        t.Klasorler.Add(Path.Combine(win, "LiveKernelReports"));
        t.Klasorler.Add(Path.Combine(ortak, @"Microsoft\Windows\WER\ReportArchive"));
        t.Klasorler.Add(Path.Combine(ortak, @"Microsoft\Windows\WER\ReportQueue"));
        t.Dosyalar.Add(Path.Combine(win, "MEMORY.DMP"));

        if (derin)
        {
            t = Ekle(l, "windows", "Eski Windows ve güncelleme kalıntıları",
                "Windows.old, güncelleme yedekleri, Teslim İyileştirme, eski sürücüler; geri alma kaybolur. Windows'un aracıyla, uzun sürebilir", true);
            t.Yonetici = true; t.Derin = true; t.Ozel = "windows";
            t.Klasorler.Add(Path.Combine(sistem, "Windows.old"));

            t = Ekle(l, "winlog", "Windows günlük dosyaları", "Kurulum ve bakım günlükleri; sorun araştırmak zorlaşır", true);
            t.Yonetici = true; t.Derin = true;
            t.Klasorler.Add(Path.Combine(win, "Logs"));

            long hazirda = DosyaBoyutu(sistem, "hiberfil.sys");
            if (hazirda > 0)
            {
                t = Ekle(l, "hazirda", "Hazırda bekletme dosyası",
                    "Hazırda bekletme ve Hızlı Başlatma kapanır; geri açmak için yönetici CMD'de: powercfg /h on", false);
                t.Yonetici = true; t.Derin = true; t.Ozel = "hazirda"; t.Boyut = hazirda;
            }
        }

        l.RemoveAll(x => x.Ozel == null && !x.Klasorler.Any(Directory.Exists) && !x.Dosyalar.Any(File.Exists));
        return l;
    }

    static Temizlik Ekle(List<Temizlik> l, string anahtar, string ad, string aciklama, bool varsayilan)
    {
        var t = new Temizlik { Anahtar = anahtar, Ad = ad, Aciklama = aciklama, Varsayilan = varsayilan };
        l.Add(t);
        return t;
    }

    // Chromium tabanlı tarayıcılar: kökte ya da profil klasörlerinde (Default, Profile 1...) önbellek klasörleri.
    static void Chromium(List<Temizlik> l, string anahtar, string ad, string kok, string surec)
    {
        if (!Directory.Exists(kok)) return;
        var t = Ekle(l, anahtar, ad, "Site önbelleği; şifreler, çerezler ve geçmiş silinmez", true);
        t.Surecler = new[] { surec };
        var profiller = new List<string> { kok };
        profiller.AddRange(AltKlasorler(kok));
        foreach (var p in profiller)
            foreach (var a in new[] { "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache" })
            {
                var y = Path.Combine(p, a);
                if (Directory.Exists(y)) t.Klasorler.Add(y);
            }
    }

    static string[] AltKlasorler(string yol)
    {
        try { return Directory.Exists(yol) ? Directory.GetDirectories(yol) : new string[0]; }
        catch { return new string[0]; }
    }

    // Kök klasörü listeleyerek boyut okur; hiberfil.sys gibi kilitli dosyalarda da çalışır.
    static long DosyaBoyutu(string klasor, string ad)
    {
        try
        {
            var f = new DirectoryInfo(klasor).GetFiles(ad);
            return f.Length > 0 ? f[0].Length : 0;
        }
        catch { return 0; }
    }

    static List<string> SteamKutuphaneleri()
    {
        var l = new List<string>();
        string steam = null;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (k != null) steam = k.GetValue("SteamPath") as string;
        }
        catch { }
        if (string.IsNullOrEmpty(steam)) return l;
        l.Add(steam.Replace('/', '\\'));
        try
        {
            string vdf = Path.Combine(l[0], @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    string y = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (!l.Any(x => x.TrimEnd('\\').Equals(y.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) l.Add(y);
                }
        }
        catch { }
        return l;
    }

    public static void Hesapla(Temizlik t)
    {
        t.AcikSurec = null;
        foreach (var s in t.Surecler)
        {
            var ps = Process.GetProcessesByName(s);
            bool acik = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            if (acik) { t.AcikSurec = s; break; }
        }
        if (t.Ozel == "geridonusum")
        {
            var b = new Yerel.SHQUERYRBINFO();
            b.cbSize = Marshal.SizeOf(b);
            t.Boyut = Yerel.SHQueryRecycleBin(null, ref b) == 0 ? b.i64Size : -1;
            return;
        }
        if (t.Ozel == "hazirda") return;   // boyutu listeyi kurarken okundu

        long toplam = 0;
        bool okunamayan = false;
        foreach (var k in t.Klasorler)
        {
            long b = Gez(k, t.EnAzGun, false);
            if (b < 0) okunamayan = true; else toplam += b;
        }
        foreach (var f in t.Dosyalar)
        {
            try { var fi = new FileInfo(f); if (fi.Exists) toplam += fi.Length; } catch { okunamayan = true; }
        }
        t.Boyut = okunamayan && toplam == 0 ? -1 : toplam;
    }

    public static long Temizle(Temizlik t)
    {
        switch (t.Ozel)
        {
            case "geridonusum":
                Hesapla(t);
                Yerel.SHEmptyRecycleBin(IntPtr.Zero, null, 0x7);   // onay, ilerleme penceresi ve ses yok
                return Math.Max(0, t.Boyut);
            case "hazirda":
                Calistir("powercfg.exe", "/h off");
                return 0;
            case "windows":
                WindowsTemizligi();
                return 0;
        }
        long toplam = 0;
        foreach (var k in t.Klasorler) toplam += Math.Max(0, Gez(k, t.EnAzGun, true));
        foreach (var f in t.Dosyalar)
        {
            try { var fi = new FileInfo(f); if (fi.Exists) { long b = fi.Length; fi.Delete(); toplam += b; } } catch { }
        }
        return toplam;
    }

    // Yönetici olarak çalışır: args[0] "derin"/"normal", kalanlar madde anahtarları.
    // "sanal=ilkMB,enCokMB" sayfa dosyasını da ayarlar; dönen kod o işin sonucudur (yoksa 0).
    public static int YoneticiTemizle(string[] args)
    {
        if (args.Length < 1) return 1;
        bool derin = args[0] == "derin";
        int sonuc = 0;
        var anahtarlar = new HashSet<string>();
        foreach (var a in args.Skip(1))
        {
            var p = a.Split('=', ',');
            int ilk, enCok;
            if (p.Length == 3 && p[0] == "sanal" && int.TryParse(p[1], out ilk) && int.TryParse(p[2], out enCok))
                sonuc = SanalBellek.Uygula(ilk, enCok);
            else
                anahtarlar.Add(a);
        }
        // Normal modda da hazırda bekletme gibi yalnızca derin listede olan işler istenebilir (Full optimize).
        var liste = Liste(derin);
        if (!derin) liste.AddRange(Liste(true).Where(t => t.Derin && !liste.Any(x => x.Anahtar == t.Anahtar)));
        foreach (var t in liste)
            if (t.Yonetici && anahtarlar.Contains(t.Anahtar))
                try { Temizle(t); } catch { }
        return sonuc;
    }

    public static long HazirdaBoyutu() { return DosyaBoyutu(Path.GetPathRoot(Environment.SystemDirectory), "hiberfil.sys"); }

    // cleanmgr'ye hangi temizlikleri yapacağını kayıt defterinden söyleyip sessiz çalıştırır, sonra izleri siler.
    static void WindowsTemizligi()
    {
        const string Kok = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\";
        var ayarlanan = new List<string>();
        foreach (var ad in WindowsTemizlikleri)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(Kok + ad, true))
                    if (k != null) { k.SetValue("StateFlags0777", 2, RegistryValueKind.DWord); ayarlanan.Add(ad); }
            }
            catch { }
        }
        if (ayarlanan.Count > 0) Calistir("cleanmgr.exe", "/sagerun:777");
        foreach (var ad in ayarlanan)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(Kok + ad, true))
                    if (k != null) k.DeleteValue("StateFlags0777", false);
            }
            catch { }
        }
    }

    static void Calistir(string program, string argumanlar)
    {
        try
        {
            var bilgi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, program), argumanlar)
            {
                UseShellExecute = false, CreateNoWindow = true
            };
            using (var p = Process.Start(bilgi)) p.WaitForExit();
        }
        catch { }
    }

    // Klasörün içini gezer; bağlantı noktalarına (junction/symlink) girmez, böylece başka yerlere taşmaz.
    // sil=true ise siler. Dönen: silinen (ya da sayılan) bayt; kök okunamıyorsa -1. Kökün kendisi silinmez.
    static long Gez(string kok, int enAzGun, bool sil)
    {
        if (!Directory.Exists(kok)) return 0;
        try { return GezIc(new DirectoryInfo(kok), DateTime.UtcNow.AddDays(-enAzGun), sil, true); }
        catch { return -1; }
    }

    static long GezIc(DirectoryInfo d, DateTime sinir, bool sil, bool kok)
    {
        FileSystemInfo[] ogeler;
        try { ogeler = d.GetFileSystemInfos(); }
        catch { if (kok) throw; return 0; }
        long toplam = 0;
        foreach (var o in ogeler)
        {
            try
            {
                if ((o.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var alt = o as DirectoryInfo;
                if (alt != null)
                {
                    toplam += GezIc(alt, sinir, sil, false);
                    // Tarih, gezmeden önceki değerdir (FileSystemInfo önbellekler); boş kaldıysa silinir.
                    if (sil && alt.LastWriteTimeUtc <= sinir) { try { alt.Delete(false); } catch { } }
                    continue;
                }
                var f = (FileInfo)o;
                if (f.LastWriteTimeUtc > sinir) continue;
                long boyut = f.Length;
                if (sil)
                {
                    if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes = FileAttributes.Normal;
                    f.Delete();   // kullanımdaysa hata verir, atlanır
                }
                toplam += boyut;
            }
            catch { }
        }
        return toplam;
    }
}

static class Yerel
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHQueryRecycleBin(string kok, ref SHQUERYRBINFO bilgi);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBin(IntPtr h, string kok, uint bayrak);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    public delegate bool PencereGeri(IntPtr h, IntPtr l);

    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out long olusma, out long cikis, out long cekirdek, out long kullanici);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int bayrak, StringBuilder ad, ref int boyut);
    [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(PencereGeri geri, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int komut);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint komut);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int ozellik, out int deger, int boyut);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int ozellik, ref int deger, int boyut);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr h, string uygulama, string kimlik);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll")] public static extern IntPtr CreateToolhelp32Snapshot(uint bayrak, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] public static extern bool Process32First(IntPtr h, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] public static extern bool Process32Next(IntPtr h, ref PROCESSENTRY32 e);

    // Komut satırı kabukları ve terminaller. Bunlardan başlatılan süreçlerin kaydedilmemiş işi olabilir
    // (derleme, betik, yerel sunucu, uzun süren komut); hangi araç olduğu bilinmese de yapıdan tanınır.
    static readonly HashSet<string> KonsolKabuklari = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "conhost.exe", "OpenConsole.exe", "WindowsTerminal.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "bash.exe", "sh.exe", "wsl.exe", "mintty.exe"
    };

    // Konsol süreçleri: kendi konsolu olanlar (çocuğu conhost / OpenConsole) ve atalarından biri kabuk ya da
    // terminal olanlar. Masaüstü kabuğuna (explorer) varınca yukarı çıkılmaz. Okunamazsa boş küme.
    public static HashSet<int> KonsolSurecleri()
    {
        var sonuc = new HashSet<int>();
        var ust = new Dictionary<int, int>();
        var adlar = new Dictionary<int, string>();
        try
        {
            IntPtr h = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return sonuc;
            try
            {
                var e = new PROCESSENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                for (bool devam = Process32First(h, ref e); devam; devam = Process32Next(h, ref e))
                {
                    ust[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                    adlar[(int)e.th32ProcessID] = e.szExeFile ?? "";
                }
            }
            finally { CloseHandle(h); }

            var konsolSahibi = new HashSet<int>();
            foreach (var s in adlar)
            {
                int u;
                if ((s.Value.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) || s.Value.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase))
                    && ust.TryGetValue(s.Key, out u)) konsolSahibi.Add(u);
            }
            foreach (var pid in adlar.Keys)
            {
                if (konsolSahibi.Contains(pid)) { sonuc.Add(pid); continue; }
                int simdiki = pid;
                for (int adim = 0; adim < 16; adim++)
                {
                    int u;
                    string a;
                    if (!ust.TryGetValue(simdiki, out u) || u == 0 || u == simdiki || !adlar.TryGetValue(u, out a)) break;
                    if (a.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) break;
                    if (konsolSahibi.Contains(u) || KonsolKabuklari.Contains(a)) { sonuc.Add(pid); break; }
                    simdiki = u;
                }
            }
        }
        catch { }
        return sonuc;
    }

    public static MEMORYSTATUSEX Bellek()
    {
        var m = new MEMORYSTATUSEX();
        m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        GlobalMemoryStatusEx(ref m);
        return m;
    }

    // Görev çubuğunda görünen türden penceresi olan süreçler (görünür, sahipsiz, başlıklı, gizlenmemiş).
    public static HashSet<int> PencereliSurecler()
    {
        var sonuc = new HashSet<int>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (IsWindowVisible(h) && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero && GetWindowTextLength(h) > 0)
            {
                int gizli;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out gizli, 4) != 0 || gizli == 0)
                {
                    int pid;
                    GetWindowThreadProcessId(h, out pid);
                    sonuc.Add(pid);
                }
            }
            return true;
        }, IntPtr.Zero);
        return sonuc;
    }
}

// Ekran kartları kayıt defterindeki Display sınıfı anahtarından okunur. Bu anahtarda sökülmüş / değiştirilmiş
// kartların girdileri de kalır; yalnızca şu an takılı (DIGCF_PRESENT) aygıtların sürücü anahtarları sayılır.
static class EkranKartlari
{
    public const string SinifAnahtari = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid sinif, IntPtr numaralayici, IntPtr pencere, uint bayrak);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr kume, uint sira, ref SP_DEVINFO_DATA veri);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr kume, ref SP_DEVINFO_DATA veri, uint ozellik, out uint tur, byte[] tampon, uint boyut, out uint gereken);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr kume);

    // Takılı ekran bağdaştırıcılarının sınıf anahtarındaki alt anahtar adları ("0000", "0001"). SPDRP_DRIVER değeri
    // "{4d36e968-...}\0000" biçimindedir. Okunamazsa null: çağıran tüm girdileri kullanır.
    public static HashSet<string> Takililar()
    {
        try
        {
            var sinif = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
            IntPtr kume = SetupDiGetClassDevs(ref sinif, IntPtr.Zero, IntPtr.Zero, 0x2 /* DIGCF_PRESENT */);
            if (kume == IntPtr.Zero || kume == new IntPtr(-1)) return null;
            var sonuc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var veri = new SP_DEVINFO_DATA();
                veri.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                var tampon = new byte[1024];
                for (uint i = 0; SetupDiEnumDeviceInfo(kume, i, ref veri); i++)
                {
                    uint tur, gereken;
                    if (!SetupDiGetDeviceRegistryProperty(kume, ref veri, 9 /* SPDRP_DRIVER */, out tur, tampon, (uint)tampon.Length, out gereken)) continue;
                    string s = Encoding.Unicode.GetString(tampon, 0, (int)Math.Min(gereken, (uint)tampon.Length)).TrimEnd('\0');
                    int b = s.LastIndexOf('\\');
                    if (b >= 0 && b + 1 < s.Length) sonuc.Add(s.Substring(b + 1));
                }
            }
            finally { SetupDiDestroyDeviceInfoList(kume); }
            return sonuc.Count > 0 ? sonuc : null;
        }
        catch { return null; }
    }

    // Sürücünün yazdığı VRAM boyutu (bayt); yoksa 0.
    public static long Vram(RegistryKey k)
    {
        object q = k.GetValue("HardwareInformation.qwMemorySize");
        object d = k.GetValue("HardwareInformation.MemorySize");
        if (q is long) return (long)q;
        if (d is int) return (uint)(int)d;
        if (d is byte[] && ((byte[])d).Length >= 4) return BitConverter.ToUInt32((byte[])d, 0);
        return 0;
    }
}

static class Renk
{
    public static readonly Color Arka = Color.FromArgb(18, 20, 24);
    public static readonly Color Panel = Color.FromArgb(28, 31, 37);
    public static readonly Color Cizgi = Color.FromArgb(48, 52, 60);
    public static readonly Color Yazi = Color.FromArgb(230, 232, 235);
    public static readonly Color Soluk = Color.FromArgb(140, 146, 156);
    public static readonly Color Yesil = Color.FromArgb(76, 175, 80);
    public static readonly Color Turuncu = Color.FromArgb(255, 152, 0);
    public static readonly Color Kirmizi = Color.FromArgb(229, 57, 53);
    public static readonly Color Vurgu = Color.FromArgb(198, 40, 40);
}

// Logo exe'ye gömülü: /win32icon Gezgin ve görev çubuğu için, /resource pencere, tepsi ve sekme şeridi için
// (bkz. derle.cmd). Kaynak bulunamazsa Windows'un standart simgesine düşer.
static class Logo
{
    static Icon simge;
    static Image resim;

    public static Icon Simge()
    {
        if (simge == null)
            try { using (var s = Kaynak("SergenPcTemizle.ico")) if (s != null) simge = new Icon(s); } catch { }
        return simge ?? SystemIcons.Application;
    }

    // .ico içindeki en yakın boyutu seçer (tepsi için küçük boyut).
    public static Icon Simge(Size boyut)
    {
        try { return new Icon(Simge(), boyut); } catch { return Simge(); }
    }

    public static Image Resim()
    {
        if (resim == null)
            try { using (var s = Kaynak("logo.png")) if (s != null) using (var i = Image.FromStream(s)) resim = new Bitmap(i); } catch { }
        return resim;
    }

    static Stream Kaynak(string ad) { return typeof(Logo).Assembly.GetManifestResourceStream(ad); }
}

class Grup
{
    public string Ad;          // süreç adı (chrome)
    public string Gorunen;     // dosya açıklaması (Google Chrome)
    public string Klasor;
    public List<int> Pidler = new List<int>();
    public long Bellek, Vram;  // Bellek: özel (commit) bellek; sanal belleği dolduran budur
    public double Cpu, Gpu;
    public bool Pencereli;
    public bool Bagli;         // açık bir pencerenin klasöründe: oyunun/uygulamanın parçası
    public bool Konsol;        // komut satırından / terminalden başlatılmış ya da kendi konsolu var
}

class Olcum
{
    public Yerel.MEMORYSTATUSEX Bellek;
    public double CpuYuzde = -1, GpuYuzde = -1, CpuIsi = -1, GpuIsi = -1;
    public long VramKullanilan = -1, VramToplam = -1;
    public List<Grup> Gruplar;
}

// Arka planda çalışır; her çağrıda bir önceki örnekle farka bakarak yüzdeleri hesaplar.
class Olcer
{
    static readonly string WinKlasoru = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    public readonly string CpuAdi, GpuAdi;
    readonly long kayitliVram;
    readonly int benim, oturum, cekirdekSayisi = Environment.ProcessorCount;
    readonly Stopwatch saat = Stopwatch.StartNew();
    double oncekiSaniye;
    long oncekiBos, oncekiCekirdek, oncekiKullanici;
    Dictionary<int, long> oncekiSurecZamani = new Dictionary<int, long>();
    Dictionary<string, CounterSample> oncekiGpu = new Dictionary<string, CounterSample>();
    readonly Dictionary<string, string> aciklamalar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    bool gpuSayaciYok, isiSayaciYok;
    string nvidiaSmi;
    DateTime sonNvidia = DateTime.MinValue;
    double nvIsi = -1;
    long nvKullanilan = -1, nvToplam = -1;

    public Olcer()
    {
        using (var p = Process.GetCurrentProcess()) { benim = p.Id; oturum = p.SessionId; }
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                if (k != null) CpuAdi = ((k.GetValue("ProcessorNameString") as string) ?? "").Trim();
        }
        catch { }
        EkranKartiBul(out GpuAdi, out kayitliVram);
        foreach (var yol in new[] {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe") })
            if (File.Exists(yol)) { nvidiaSmi = yol; break; }
        Yerel.GetSystemTimes(out oncekiBos, out oncekiCekirdek, out oncekiKullanici);
    }

    public Olcum Topla()
    {
        var o = new Olcum { Bellek = Yerel.Bellek() };

        double simdi = saat.Elapsed.TotalSeconds, gecen = simdi - oncekiSaniye;
        oncekiSaniye = simdi;

        long bos, cekirdek, kullanici;
        Yerel.GetSystemTimes(out bos, out cekirdek, out kullanici);
        long dBos = bos - oncekiBos, dToplam = (cekirdek - oncekiCekirdek) + (kullanici - oncekiKullanici);
        oncekiBos = bos; oncekiCekirdek = cekirdek; oncekiKullanici = kullanici;
        if (dToplam > 0) o.CpuYuzde = Math.Max(0, Math.Min(100, 100.0 * (dToplam - dBos) / dToplam));

        var gpuPid = new Dictionary<int, double>();
        var vramPid = new Dictionary<int, long>();
        GpuOku(o, gpuPid, vramPid);
        o.CpuIsi = CpuIsisiOku();
        NvidiaOku(o);
        o.Gruplar = SurecleriTopla(gecen, gpuPid, vramPid);
        return o;
    }

    // Görev Yöneticisi'nin yöntemi: her motorun (3D, video, kopyalama...) yükü süreçlerin toplamıdır,
    // ekran kartı yüzdesi en yüklü motordur.
    void GpuOku(Olcum o, Dictionary<int, double> gpuPid, Dictionary<int, long> vramPid)
    {
        if (gpuSayaciYok) return;
        try
        {
            var motorlar = new Dictionary<string, double>();
            var yeni = new Dictionary<string, CounterSample>();
            bool oncekiVar = oncekiGpu.Count > 0;
            var kullanim = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
            if (kullanim != null)
            {
                foreach (InstanceData d in kullanim.Values)
                {
                    yeni[d.InstanceName] = d.Sample;
                    CounterSample eski;
                    if (!oncekiGpu.TryGetValue(d.InstanceName, out eski)) continue;
                    float y = CounterSample.Calculate(eski, d.Sample);
                    int pid; string motor;
                    if (!(y > 0) || !PidAyir(d.InstanceName, out pid, out motor)) continue;
                    double t;
                    motorlar.TryGetValue(motor, out t);
                    motorlar[motor] = t + y;
                    double p;
                    if (!gpuPid.TryGetValue(pid, out p) || y > p) gpuPid[pid] = y;
                }
            }
            oncekiGpu = yeni;
            if (oncekiVar) o.GpuYuzde = motorlar.Count == 0 ? 0 : Math.Min(100, motorlar.Values.Max());

            var surecBellek = new PerformanceCounterCategory("GPU Process Memory").ReadCategory()["Dedicated Usage"];
            if (surecBellek != null)
            {
                foreach (InstanceData d in surecBellek.Values)
                {
                    int pid; string kalan;
                    if (!PidAyir(d.InstanceName, out pid, out kalan)) continue;
                    long v;
                    vramPid.TryGetValue(pid, out v);
                    vramPid[pid] = v + d.RawValue;
                }
            }

            var kartBellek = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory()["Dedicated Usage"];
            if (kartBellek != null)
                foreach (InstanceData d in kartBellek.Values) o.VramKullanilan = Math.Max(o.VramKullanilan, d.RawValue);
        }
        catch (InvalidOperationException) { gpuSayaciYok = true; }  // eski Windows: sayaç yok
        catch { }
    }

    // "pid_1234_luid_0x..._eng_0_engtype_3D" → 1234, "luid_0x..._eng_0_engtype_3D"
    static bool PidAyir(string ad, out int pid, out string kalan)
    {
        pid = 0; kalan = null;
        if (ad == null || !ad.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return false;
        int i = ad.IndexOf('_', 4);
        if (i < 0 || !int.TryParse(ad.Substring(4, i - 4), out pid)) return false;
        kalan = ad.Substring(i + 1);
        return true;
    }

    // ACPI ısı bölgesi: yönetici izni istemez ama her bilgisayarda gerçek işlemci ısısını vermez.
    // Masaüstü kartlarda çoğu zaman sabit ~28 °C döner; 30 °C altını "ölçülemiyor" sayıyoruz.
    double CpuIsisiOku()
    {
        if (isiSayaciYok) return -1;
        try
        {
            var veri = new PerformanceCounterCategory("Thermal Zone Information").ReadCategory()["Temperature"];
            double enCok = -1;
            if (veri != null)
                foreach (InstanceData d in veri.Values) enCok = Math.Max(enCok, d.RawValue - 273.15);
            return enCok > 30 && enCok < 115 ? enCok : -1;
        }
        catch (InvalidOperationException) { isiSayaciYok = true; return -1; }
        catch { return -1; }
    }

    // NVIDIA kartlarda ısı ve VRAM'i sürücüyle gelen nvidia-smi verir; 5 saniyede bir sorulur.
    void NvidiaOku(Olcum o)
    {
        if (nvidiaSmi != null && (DateTime.UtcNow - sonNvidia).TotalSeconds >= 5)
        {
            sonNvidia = DateTime.UtcNow;
            long enCokToplam = 0;
            try
            {
                var bilgi = new ProcessStartInfo(nvidiaSmi, "--query-gpu=temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
                };
                using (var p = Process.Start(bilgi))
                {
                    var okuma = p.StandardOutput.ReadToEndAsync();
                    if (!okuma.Wait(4000)) { try { p.Kill(); } catch { } }
                    else
                    {
                        // Her satır bir NVIDIA kartı: en çok VRAM'i olan (asıl kart) gösterilir.
                        foreach (var satir in okuma.Result.Split('\n'))
                        {
                            var parca = satir.Split(',');
                            double isi; long kul, top;
                            if (parca.Length >= 3
                                && double.TryParse(parca[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out isi)
                                && long.TryParse(parca[1].Trim(), out kul) && long.TryParse(parca[2].Trim(), out top)
                                && top * 1048576 > enCokToplam)
                            {
                                enCokToplam = top * 1048576;
                                nvIsi = isi; nvKullanilan = kul * 1048576; nvToplam = top * 1048576;
                            }
                        }
                    }
                }
            }
            catch { nvidiaSmi = null; }
        }
        if (nvIsi >= 0) o.GpuIsi = nvIsi;
        if (nvToplam > 0) { o.VramKullanilan = nvKullanilan; o.VramToplam = nvToplam; }
        else if (kayitliVram > 0) o.VramToplam = kayitliVram;
    }

    // Takılı ekran kartlarından en çok belleği olanın adı ve VRAM boyutu (sürücünün yazdığı kayıt değerleri).
    static void EkranKartiBul(out string ad, out long vram)
    {
        ad = null; vram = 0;
        try
        {
            var takili = EkranKartlari.Takililar();
            using (var sinif = Registry.LocalMachine.OpenSubKey(EkranKartlari.SinifAnahtari))
            {
                if (sinif == null) return;
                foreach (var alt in sinif.GetSubKeyNames())
                {
                    if (alt.Length != 4) continue;
                    if (takili != null && !takili.Contains(alt)) continue;
                    try
                    {
                        using (var k = sinif.OpenSubKey(alt))
                        {
                            if (k == null) continue;
                            long boyut = EkranKartlari.Vram(k);
                            if (boyut > vram) { vram = boyut; ad = k.GetValue("DriverDesc") as string; }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    List<Grup> SurecleriTopla(double gecen, Dictionary<int, double> gpuPid, Dictionary<int, long> vramPid)
    {
        var pencereli = Yerel.PencereliSurecler();
        var konsol = Yerel.KonsolSurecleri();
        var gruplar = new Dictionary<string, Grup>(StringComparer.OrdinalIgnoreCase);
        var yeniZaman = new Dictionary<int, long>();
        var pencereKlasorleri = new List<string>();
        var tampon = new StringBuilder(1024);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == benim || p.SessionId != oturum) continue;
                    string ad = p.ProcessName;
                    if (AnaForm.Korunuyor(ad)) continue;

                    // Kapatma izni yoksa (yönetici olarak çalışan, korumalı) listelemenin anlamı yok.
                    const uint SORGU = 0x1000, SONLANDIR = 0x0001;
                    IntPtr h = Yerel.OpenProcess(SORGU | SONLANDIR, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    string yol = null;
                    long zaman = -1;
                    try
                    {
                        int boyut = tampon.Capacity;
                        if (Yerel.QueryFullProcessImageName(h, 0, tampon, ref boyut)) yol = tampon.ToString();
                        long a, b, c, d;
                        if (Yerel.GetProcessTimes(h, out a, out b, out c, out d)) zaman = c + d;
                    }
                    finally { Yerel.CloseHandle(h); }

                    // Windows klasöründen çalışan her şey (sistem, sürücü yardımcıları) dokunulmaz.
                    if (string.IsNullOrEmpty(yol) || yol.StartsWith(WinKlasoru, StringComparison.OrdinalIgnoreCase)) continue;
                    // Dosyaya bakan kurallar: üretici yazılımı (yayıncıdan), WinDivert taşıyan bağlantı araçları.
                    if (AnaForm.Korunuyor(ad, yol)) continue;

                    double cpu = 0;
                    if (zaman >= 0)
                    {
                        yeniZaman[p.Id] = zaman;
                        long eski;
                        if (gecen > 0 && oncekiSurecZamani.TryGetValue(p.Id, out eski))
                            cpu = Math.Max(0, (zaman - eski) / (gecen * 1e7 * cekirdekSayisi) * 100);
                    }

                    Grup g;
                    if (!gruplar.TryGetValue(ad, out g))
                    {
                        g = new Grup { Ad = ad, Gorunen = Aciklama(yol, ad), Klasor = Path.GetDirectoryName(yol) };
                        gruplar[ad] = g;
                    }
                    g.Pidler.Add(p.Id);
                    g.Bellek += p.PrivateMemorySize64;
                    g.Cpu += cpu;
                    double gp; if (gpuPid.TryGetValue(p.Id, out gp)) g.Gpu += gp;
                    long vr; if (vramPid.TryGetValue(p.Id, out vr)) g.Vram += vr;
                    if (pencereli.Contains(p.Id)) { g.Pencereli = true; pencereKlasorleri.Add(Path.GetDirectoryName(yol)); }
                    if (konsol.Contains(p.Id)) g.Konsol = true;
                }
                catch { }
            }
        }
        oncekiSurecZamani = yeniZaman;

        foreach (var g in gruplar.Values)
        {
            g.Cpu = Math.Min(100, g.Cpu);
            g.Gpu = Math.Min(100, g.Gpu);
            if (!g.Pencereli) g.Bagli = AyniUygulama(g.Klasor, pencereKlasorleri);
        }
        return gruplar.Values.ToList();
    }

    string Aciklama(string yol, string ad)
    {
        string a;
        if (aciklamalar.TryGetValue(yol, out a)) return a;
        a = ad;
        try
        {
            string d = FileVersionInfo.GetVersionInfo(yol).FileDescription;
            if (!string.IsNullOrWhiteSpace(d)) a = d.Trim();
        }
        catch { }
        aciklamalar[yol] = a;
        return a;
    }

    // Arka plan süreci açık bir pencerenin klasöründe (ya da üst klasöründe) mi? Oyunların yardımcı
    // süreçleri böyle durur; kapatılırsa oyun çöker. "C:\Program Files" gibi sığ klasörler sayılmaz.
    static bool AyniUygulama(string klasor, List<string> pencereKlasorleri)
    {
        if (string.IsNullOrEmpty(klasor)) return false;
        foreach (var pk in pencereKlasorleri)
        {
            if (string.IsNullOrEmpty(pk)) continue;
            if ((Derinlik(pk) >= 3 && Icinde(klasor, pk)) || (Derinlik(klasor) >= 3 && Icinde(pk, klasor))) return true;
        }
        return false;
    }

    static int Derinlik(string klasor) { return klasor.TrimEnd('\\').Split('\\').Length; }

    static bool Icinde(string alt, string ust)
    {
        ust = ust.TrimEnd('\\');
        return alt.Equals(ust, StringComparison.OrdinalIgnoreCase)
            || alt.StartsWith(ust + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

// MSI Afterburner benzeri tek göstergelik kutu: başlık, alt yazı, değer ve renkli çubuk.
class Gosterge : Control
{
    static readonly Font BaslikYazi = new Font("Segoe UI", 8.5f, FontStyle.Bold);
    static readonly Font AltYazi = new Font("Segoe UI", 8f);
    static readonly Font DegerYazi = new Font("Segoe UI Semibold", 14f);

    string deger = "–";
    double oran;
    public string Alt;

    public Gosterge(string baslik, string alt)
    {
        Text = baslik;
        Alt = alt;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Renk.Panel;
        Dock = DockStyle.Fill;
        Margin = new Padding(4);
    }

    public void Ayarla(string d, double o)
    {
        if (double.IsNaN(o) || double.IsInfinity(o)) o = 0;
        o = Math.Max(0, Math.Min(1, o));
        if (d == deger && Math.Abs(o - oran) < 0.002) return;
        deger = d; oran = o;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int x = 12, w = Width - 24;
        int dg = TextRenderer.MeasureText(deger, DegerYazi).Width;
        var sol = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, Text, BaslikYazi, new Rectangle(x, 9, w - dg - 6, 18), Renk.Soluk, sol);
        if (!string.IsNullOrEmpty(Alt))
            TextRenderer.DrawText(g, Alt, AltYazi, new Rectangle(x, 27, w - dg - 6, 16), Renk.Soluk, sol);
        TextRenderer.DrawText(g, deger, DegerYazi, new Rectangle(x, 6, w, 34), Renk.Yazi,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var cubuk = new Rectangle(x, Height - 15, w, 6);
        var renk = oran >= 0.9 ? Renk.Kirmizi : oran >= 0.7 ? Renk.Turuncu : Renk.Yesil;
        using (var b = new SolidBrush(Renk.Cizgi)) g.FillRectangle(b, cubuk);
        using (var b = new SolidBrush(renk)) g.FillRectangle(b, new Rectangle(cubuk.X, cubuk.Y, (int)(cubuk.Width * oran), cubuk.Height));
    }
}

class KoyuListe : ListView
{
    public KoyuListe()
    {
        DoubleBuffered = true;
        OwnerDraw = true;
        View = View.Details;
        CheckBoxes = true;
        FullRowSelect = true;
        BorderStyle = BorderStyle.None;
        BackColor = Renk.Panel;
        ForeColor = Renk.Yazi;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Yerel.SetWindowTheme(Handle, "DarkMode_Explorer", null); } catch { }  // koyu kaydırma çubuğu
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using (var b = new SolidBrush(Renk.Arka)) e.Graphics.FillRectangle(b, e.Bounds);
        var hiza = e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
        var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, r, Renk.Soluk,
            hiza | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // Seçili satır elle çizilir: Windows'un açık mavi seçim zemininde soluk (kapalı / önemsiz) yazılar okunmuyordu.
    // Koyu mavi zemin; soluk yazı seçiliyken açılır, renkli (turuncu, kırmızı) yazılar korunur.
    static readonly Color SecimZemini = Color.FromArgb(40, 66, 102);
    static readonly Color SecimSoluk = Color.FromArgb(196, 203, 214);

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        if (!e.Item.Selected) { e.DrawDefault = true; return; }
        using (var b = new SolidBrush(SecimZemini)) e.Graphics.FillRectangle(b, e.Bounds);
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (!e.Item.Selected || e.Header == null) { e.DrawDefault = true; return; }
        var r = e.Bounds;
        if (e.ColumnIndex == 0) r.Width = Math.Min(r.Width, e.Header.Width);   // ilk sütunun sınırı bütün satırı kaplıyor
        using (var b = new SolidBrush(SecimZemini)) e.Graphics.FillRectangle(b, r);
        int sol = 6;
        if (e.ColumnIndex == 0 && CheckBoxes)
        {
            var boyut = CheckBoxRenderer.GetGlyphSize(e.Graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);
            CheckBoxRenderer.DrawCheckBox(e.Graphics, new Point(r.X + 4, r.Y + (r.Height - boyut.Height) / 2),
                e.Item.Checked ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);
            sol = 4 + boyut.Width + 4;
        }
        bool ogeStili = e.Item.UseItemStyleForSubItems || e.SubItem == null;
        Color renk = ogeStili ? e.Item.ForeColor : e.SubItem.ForeColor;
        if (renk.ToArgb() == Renk.Soluk.ToArgb()) renk = SecimSoluk;
        else if (renk.ToArgb() == Renk.Yazi.ToArgb()) renk = Color.White;
        Font yazi = (ogeStili ? e.Item.Font : e.SubItem.Font) ?? Font;
        var hiza = e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right
                 : e.Header.TextAlign == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
        var metinAlani = new Rectangle(r.X + sol, r.Y, Math.Max(0, r.Width - sol - 6), r.Height);
        TextRenderer.DrawText(e.Graphics, e.SubItem != null ? e.SubItem.Text : e.Item.Text, yazi, metinAlani, renk,
            hiza | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}

class Siralayici : IComparer
{
    public int Sutun = 3;
    public bool Azalan = true;

    public int Compare(object x, object y)
    {
        var ox = (ListViewItem)x; var oy = (ListViewItem)y;
        var a = (Grup)ox.Tag; var b = (Grup)oy.Tag;
        int s;
        switch (Sutun)
        {
            case 0: s = string.Compare(ox.Text, oy.Text, StringComparison.CurrentCultureIgnoreCase); break;
            case 1: s = a.Pencereli.CompareTo(b.Pencereli); break;
            case 2: s = a.Cpu.CompareTo(b.Cpu); break;
            case 4: s = a.Gpu.CompareTo(b.Gpu); break;
            case 5: s = a.Vram.CompareTo(b.Vram); break;
            default: s = a.Bellek.CompareTo(b.Bellek); break;
        }
        if (s == 0) s = string.Compare(a.Ad, b.Ad, StringComparison.OrdinalIgnoreCase);
        return Azalan ? -s : s;
    }
}

class Dugum
{
    public string Ad, Yol;
    public long Boyut, DosyaBoyutu;   // DosyaBoyutu: yalnızca bu klasörün kendi dosyaları
    public bool Klasor = true;
    public Dugum Ust;
    public List<Dugum> Alt = new List<Dugum>();
}

class DiskSekmesi : Panel
{
    public const string DerinUyari =
        "Derin temizlik daha çok yer açar ama bedeli var. Açmadan önce oku:\n\n" +
        "•  Oyunlar ilk açılışta takılır ya da uzun yüklenir: ekran kartı ve Steam shader önbellekleri silinir, " +
        "oyunlar onları yeniden derler (1–2 oyun oturumu sürer).\n" +
        "•  Eski Windows'a dönülemez: Windows.old, güncelleme yedekleri ve eski sürücü paketleri silinir; " +
        "güncelleme ya da sürücü geri alma seçeneği kaybolur.\n" +
        "•  Yeni geçici dosyalar da silinir: o sırada bir kurulum ya da güncelleme sürüyorsa yarıda kalabilir. " +
        "Kurulum yaparken çalıştırma.\n" +
        "•  Çökme kayıtları silinir: bir oyunun ya da mavi ekranın sebebini sonradan araştırmak zorlaşır.\n" +
        "•  \"Hazırda bekletme dosyası\"nı işaretlersen hazırda bekletme ve Hızlı Başlatma kapanır; bilgisayar biraz geç açılır.\n" +
        "•  Silinenler geri dönüşüm kutusuna gitmez, geri getirilemez.\n\n" +
        "Silinmeyenler: belgeler, resimler, İndirilenler, oyunlar ve oyun kayıtları, şifreler, tarayıcı geçmişi ve çerezler.\n\n" +
        "Derin mod açılsın mı?";

    readonly TableLayoutPanel suruculer = new TableLayoutPanel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(8, 8, 8, 0), RowCount = 1 };
    readonly KoyuListe temizlikListesi = new KoyuListe();
    readonly KoyuListe analizListesi = new KoyuListe();
    readonly CheckBox chkDerin = new CheckBox();
    readonly Button btnTemizle, btnTara, btnAnaliz, btnUst, btnDosyalar, btnAlgilayici;
    readonly Label lblYol = new Label(), lblDurum = new Label();
    readonly System.Windows.Forms.Timer ilerleme = new System.Windows.Forms.Timer { Interval = 400 };
    readonly Dictionary<string, bool> secimler = new Dictionary<string, bool>();
    List<Temizlik> temizlikler = new List<Temizlik>();
    bool ilkGosterim = true, calisiyor, dolduruluyor, derinDegisiyor, dosyaGorunumu;
    Dugum kok, simdiki;
    List<Dugum> buyukDosyalar = new List<Dugum>();
    int taranan;
    string analizSurucu = SanalBellek.Surucu;   // "C:"; üstteki disk göstergesine tıklayınca değişir

    public DiskSekmesi()
    {
        Dock = DockStyle.Fill;
        BackColor = Renk.Arka;

        var temizBaslik = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(14, 8, 12, 2) };
        var baslik1 = Baslik("GEREKSİZ DOSYALAR", "İşaretlileri siler. Turuncular derin moddan gelir. Açık programın önbelleği atlanır.");
        chkDerin.Text = "Derin temizlik modu";
        chkDerin.AutoSize = true;
        chkDerin.Dock = DockStyle.Right;
        chkDerin.ForeColor = Renk.Turuncu;
        chkDerin.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        chkDerin.CheckedChanged += DerinDegisti;
        temizBaslik.Controls.Add(baslik1);
        temizBaslik.Controls.Add(chkDerin);

        temizlikListesi.Columns.Add("Temizlenecek", 280);
        temizlikListesi.Columns.Add("Açıklama", 300);
        temizlikListesi.Columns.Add("Boyut", 90, HorizontalAlignment.Right);
        temizlikListesi.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        temizlikListesi.Dock = DockStyle.Fill;
        temizlikListesi.Resize += delegate { SutunDoldur(temizlikListesi, 1); };
        temizlikListesi.ItemCheck += delegate(object s, ItemCheckEventArgs e)
        {
            if (dolduruluyor) return;
            var t = (Temizlik)temizlikListesi.Items[e.Index].Tag;
            if (t.AcikSurec != null && e.NewValue == CheckState.Checked)
            {
                e.NewValue = CheckState.Unchecked;
                lblDurum.Text = string.Format("Önce {0} programını kapat, sonra \"Yeniden tara\"ya bas.", t.AcikSurec);
            }
        };
        temizlikListesi.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
        {
            if (dolduruluyor) return;
            secimler[((Temizlik)e.Item.Tag).Anahtar] = e.Item.Checked;
            ToplamiYaz();
        };
        var temizKutu = new Panel { Dock = DockStyle.Top, Height = 290, Padding = new Padding(12, 0, 12, 0) };
        temizKutu.Controls.Add(temizlikListesi);

        var temizSerit = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(12, 8, 12, 4) };
        btnTemizle = Dugme("Seçilenleri temizle", 0, true);
        btnTemizle.Dock = DockStyle.Fill;
        btnTemizle.Click += Temizle_Click;
        btnTara = Dugme("Yeniden tara", 150, false);
        btnTara.Click += async delegate { await TaraAsync(); };
        temizSerit.Controls.Add(btnTemizle);
        temizSerit.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        temizSerit.Controls.Add(btnTara);

        var analizSerit = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(14, 14, 12, 6) };
        btnAnaliz = Dugme(analizSurucu + " diskini tara", 140, false);
        btnAnaliz.Click += Analiz_Click;
        btnUst = Dugme("↑ Üst klasör", 110, false);
        btnUst.Enabled = false;
        btnUst.Click += delegate { if (simdiki != null && simdiki.Ust != null) { simdiki = simdiki.Ust; AnaliziGoster(); } };
        btnDosyalar = Dugme("En büyük dosyalar", 150, false);
        btnDosyalar.Enabled = false;
        btnDosyalar.Click += delegate { dosyaGorunumu = !dosyaGorunumu; AnaliziGoster(); };
        btnAlgilayici = Dugme("Depolama Algılayıcısı", 170, false);
        btnAlgilayici.Click += delegate { try { Process.Start("ms-settings:storagesense"); } catch { } };
        analizSerit.Controls.Add(Baslik("DEPOLAMA ANALİZİ", "Hiçbir şey silmez; yer kaplayanı gösterir."));
        foreach (var b in new[] { btnAnaliz, btnUst, btnDosyalar, btnAlgilayici })
        {
            analizSerit.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6 });
            analizSerit.Controls.Add(b);
        }

        lblYol.Dock = DockStyle.Top;
        lblYol.Height = 24;
        lblYol.Padding = new Padding(14, 4, 12, 0);
        lblYol.ForeColor = Renk.Soluk;
        lblYol.AutoEllipsis = true;
        lblYol.Text = BosYolMetni();

        analizListesi.CheckBoxes = false;
        analizListesi.Columns.Add("Ad", 400);
        analizListesi.Columns.Add("Boyut", 100, HorizontalAlignment.Right);
        analizListesi.Columns.Add("Oran", 70, HorizontalAlignment.Right);
        analizListesi.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        analizListesi.Dock = DockStyle.Fill;
        analizListesi.Resize += delegate { SutunDoldur(analizListesi, 0); };
        analizListesi.DoubleClick += delegate { AnalizOgesiniAc(); };
        var menu = new ContextMenuStrip();
        var mGoster = new ToolStripMenuItem("Gezgin'de göster");
        mGoster.Click += delegate { GezgindeGoster(); };
        menu.Items.Add(mGoster);
        menu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = analizListesi.SelectedItems.Count == 0 || analizListesi.SelectedItems[0].Tag == null;
        };
        analizListesi.ContextMenuStrip = menu;
        var analizKutu = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        analizKutu.Controls.Add(analizListesi);

        lblDurum.Dock = DockStyle.Bottom;
        lblDurum.Height = 34;
        lblDurum.Padding = new Padding(14, 0, 12, 0);
        lblDurum.TextAlign = ContentAlignment.MiddleLeft;
        lblDurum.ForeColor = Renk.Soluk;

        ilerleme.Tick += delegate { lblDurum.Text = string.Format("{0} diski taranıyor... {1:N0} dosya", analizSurucu, taranan); };

        Controls.Add(analizKutu);
        Controls.Add(lblYol);
        Controls.Add(analizSerit);
        Controls.Add(temizSerit);
        Controls.Add(temizKutu);
        Controls.Add(temizBaslik);
        Controls.Add(suruculer);
        Controls.Add(lblDurum);

        // Küçük pencerede temizlik listesi kısalır; depolama analizine her zaman en az ~180 px kalsın.
        Resize += delegate
        {
            int sabit = suruculer.Height + temizBaslik.Height + temizSerit.Height + analizSerit.Height + lblYol.Height + lblDurum.Height;
            temizKutu.Height = Math.Max(130, Math.Min(290, ClientSize.Height - sabit - 180));
        };
    }

    static Label Baslik(string baslik, string alt)
    {
        return new Label
        {
            Dock = DockStyle.Fill, ForeColor = Renk.Soluk, AutoEllipsis = true,
            Text = baslik + "\n" + alt, Font = new Font("Segoe UI", 8.5f)
        };
    }

    static Button Dugme(string metin, int genislik, bool vurgu)
    {
        var b = new Button
        {
            Text = metin, Dock = DockStyle.Right, Width = genislik, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = vurgu ? Renk.Vurgu : Renk.Panel, ForeColor = vurgu ? Color.White : Renk.Yazi,
            Font = new Font("Segoe UI", vurgu ? 11.5f : 9.5f, FontStyle.Bold)
        };
        b.FlatAppearance.BorderSize = vurgu ? 0 : 1;
        b.FlatAppearance.BorderColor = Renk.Cizgi;
        return b;
    }

    // Yerleşim bitince (kaydırma çubukları yerine oturunca) bir kez daha sığdırılır; yoksa yatay çubuk kalabiliyor.
    static void SutunDoldur(ListView l, int sutun)
    {
        Sigdir(l, sutun, false);
        try { if (l.IsHandleCreated) l.BeginInvoke(new Action(() => Sigdir(l, sutun, true))); } catch { }
    }

    static void Sigdir(ListView l, int sutun, bool zorla)
    {
        int kalan = l.ClientSize.Width;
        for (int i = 0; i < l.Columns.Count; i++) if (i != sutun) kalan -= l.Columns[i].Width;
        if (kalan <= 120) return;
        // Yatay çubuk bir kez çıkınca genişlik tam sığsa da kalabiliyor: zorlarken bir piksel oynatılıp yeniden yazılır.
        if (zorla && l.Columns[sutun].Width == kalan) l.Columns[sutun].Width = kalan - 1;
        if (l.Columns[sutun].Width != kalan) l.Columns[sutun].Width = kalan;
    }

    public async void Gosterildi()
    {
        if (!ilkGosterim) return;
        ilkGosterim = false;
        SuruculeriGoster();
        await TaraAsync();
    }

    void SuruculeriGoster()
    {
        var diskler = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Take(4).ToList();
        // Seçili disk artık yoksa (çıkarılmış) sistem diskine dön.
        if (!diskler.Any(d => d.Name.TrimEnd('\\').Equals(analizSurucu, StringComparison.OrdinalIgnoreCase))) analizSurucu = SanalBellek.Surucu;
        suruculer.SuspendLayout();
        suruculer.Controls.Clear();
        suruculer.ColumnStyles.Clear();
        suruculer.ColumnCount = Math.Max(1, diskler.Count);
        for (int i = 0; i < diskler.Count; i++)
        {
            var d = diskler[i];
            suruculer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / diskler.Count));
            const double GB = 1073741824.0;
            double top = d.TotalSize / GB, bos = d.AvailableFreeSpace / GB;
            string ad = string.IsNullOrEmpty(d.VolumeLabel) ? "Yerel disk" : d.VolumeLabel;
            string harf = d.Name.TrimEnd('\\');
            var g = new Gosterge(harf + " DİSKİ", string.Format("{0} · boş {1:0.0} GB", ad, bos));
            g.Ayarla(string.Format("{0:0} / {1:0} GB", top - bos, top), (top - bos) / top);
            if (diskler.Count > 1)
            {
                // Birden çok disk varsa göstergeye tıklayınca depolama analizi o diski tarar.
                g.Tag = new[] { harf, g.Alt };
                g.Cursor = Cursors.Hand;
                g.Click += delegate { if (!calisiyor) { analizSurucu = harf; AnalizSurucusunuYaz(); } };
            }
            suruculer.Controls.Add(g, i, 0);
        }
        suruculer.ResumeLayout();
        AnalizSurucusunuYaz();
    }

    string BosYolMetni()
    {
        return "\"" + analizSurucu + " diskini tara\"ya bas: en çok yer kaplayan klasörler burada listelenir." +
               (suruculer.Controls.Count > 1 ? " Başka bir disk için üstteki göstergesine tıkla." : "");
    }

    // Düğme, açıklama ve disk göstergelerindeki "analiz için seçili" işareti seçili diske göre yazılır.
    void AnalizSurucusunuYaz()
    {
        btnAnaliz.Text = analizSurucu + " diskini tara";
        if (kok == null) lblYol.Text = BosYolMetni();
        foreach (var g in suruculer.Controls.OfType<Gosterge>())
        {
            var t = g.Tag as string[];
            if (t == null) continue;
            g.Alt = t[1] + (t[0].Equals(analizSurucu, StringComparison.OrdinalIgnoreCase) ? " · analiz için seçili" : "");
            g.Invalidate();
        }
    }

    // Başka yerden (optimize düğmeleri) temizlik yapıldıktan sonra; sekme hiç açılmadıysa bir şey yapmaz.
    public async void Yenile()
    {
        if (ilkGosterim || calisiyor) return;
        SuruculeriGoster();
        await TaraAsync();
    }

    // Temizlik / tarama sürüyor mu (optimize düğmeleri aynı klasörlere aynı anda dokunmasın).
    public bool Calisiyor { get { return calisiyor; } }

    public static long BosAlan()
    {
        long t = 0;
        foreach (var d in DriveInfo.GetDrives())
            try { if (d.DriveType == DriveType.Fixed && d.IsReady) t += d.AvailableFreeSpace; } catch { }
        return t;
    }

    void Mesgul(bool mesgul, string mesaj)
    {
        calisiyor = mesgul;
        foreach (var b in new Control[] { btnTemizle, btnTara, btnAnaliz, chkDerin }) b.Enabled = !mesgul;
        btnUst.Enabled = !mesgul && !dosyaGorunumu && simdiki != null && simdiki.Ust != null;
        btnDosyalar.Enabled = !mesgul && kok != null;
        if (mesaj != null) lblDurum.Text = mesaj;
    }

    async Task TaraAsync()
    {
        if (calisiyor) return;
        Mesgul(true, "Gereksiz dosyalar hesaplanıyor...");
        bool derin = chkDerin.Checked;
        temizlikler = await Task.Run(() =>
        {
            var l = DiskTemizlik.Liste(derin);
            foreach (var t in l) DiskTemizlik.Hesapla(t);
            return l;
        });
        ListeyiDoldur();
        Mesgul(false, null);
        ToplamiYaz();
    }

    void ListeyiDoldur()
    {
        dolduruluyor = true;
        temizlikListesi.BeginUpdate();
        temizlikListesi.Items.Clear();
        foreach (var t in temizlikler)
        {
            var oge = new ListViewItem(t.Ad + (t.Yonetici ? "  (yönetici)" : "")) { Tag = t };
            oge.SubItems.Add(t.AcikSurec != null ? string.Format("{0} açık; kapatıp \"Yeniden tara\"ya bas", t.AcikSurec) : t.Aciklama);
            oge.SubItems.Add(t.Boyut < 0 ? "?" : AnaForm.Boyut(t.Boyut));
            bool sec;
            if (!secimler.TryGetValue(t.Anahtar, out sec)) sec = t.Varsayilan;
            oge.Checked = sec && t.AcikSurec == null;
            oge.ForeColor = t.AcikSurec != null ? Renk.Soluk : t.Derin ? Renk.Turuncu : Renk.Yazi;
            temizlikListesi.Items.Add(oge);
        }
        temizlikListesi.EndUpdate();
        dolduruluyor = false;
        SutunDoldur(temizlikListesi, 1);
    }

    List<Temizlik> Secililer()
    {
        return temizlikListesi.Items.Cast<ListViewItem>().Where(o => o.Checked).Select(o => (Temizlik)o.Tag)
            .Where(t => t.AcikSurec == null).ToList();
    }

    void ToplamiYaz()
    {
        if (calisiyor) return;
        var secili = Secililer();
        long toplam = secili.Where(t => t.Boyut > 0).Sum(t => t.Boyut);
        string ek = secili.Any(t => t.Boyut < 0) ? " (+ boyutu yönetici izniyle görünenler)" : "";
        lblDurum.Text = string.Format("Seçilenlerle yaklaşık {0} boşalır{1}.", AnaForm.Boyut(toplam), ek);
    }

    void DerinDegisti(object sender, EventArgs e)
    {
        if (derinDegisiyor) return;
        if (chkDerin.Checked && MessageBox.Show(FindForm(), DerinUyari, "Derin temizlik modu",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            derinDegisiyor = true;
            chkDerin.Checked = false;
            derinDegisiyor = false;
            return;
        }
        var bekleme = TaraAsync();
    }

    async void Temizle_Click(object sender, EventArgs e)
    {
        var secili = Secililer();
        if (secili.Count == 0) { lblDurum.Text = "Hiçbir şey seçilmedi."; return; }

        long toplam = secili.Where(t => t.Boyut > 0).Sum(t => t.Boyut);
        var metin = new StringBuilder("Şunlar kalıcı olarak silinecek (geri dönüşüm kutusuna gitmez):\n\n");
        foreach (var t in secili)
            metin.AppendFormat("•  {0}{1}\n", t.Ad, t.Boyut > 0 ? "  —  " + AnaForm.Boyut(t.Boyut) : "");
        metin.AppendFormat("\nYaklaşık {0} boşalacak.", AnaForm.Boyut(toplam));
        if (secili.Any(t => t.Yonetici)) metin.Append(" Windows kalıntıları için Windows bir kez yönetici izni isteyecek.");
        if (secili.Any(t => t.Ozel == "windows")) metin.Append("\n\nWindows'un kendi temizlik aracı da çalışacak; 10–30 dakika sürebilir, bu sırada kapatma.");
        if (secili.Any(t => t.Derin)) metin.Append("\n\nDerin mod açık: oyunlar ilk açılışta takılabilir, eski Windows'a dönülemez.");
        metin.Append("\n\nDevam edilsin mi?");
        if (MessageBox.Show(FindForm(), metin.ToString(), "Disk temizliği", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        Mesgul(true, "Temizleniyor...");
        long once = BosAlan();
        await Task.Run(() => { foreach (var t in secili.Where(x => !x.Yonetici)) { try { DiskTemizlik.Temizle(t); } catch { } } });

        string not = "";
        var yonetici = secili.Where(t => t.Yonetici).Select(t => t.Anahtar).ToList();
        if (yonetici.Count > 0)
        {
            lblDurum.Text = secili.Any(t => t.Ozel == "windows")
                ? "Windows kalıntıları temizleniyor; Windows'un aracı uzun sürebilir..."
                : "Windows kalıntıları temizleniyor...";
            try
            {
                var bilgi = new ProcessStartInfo(Application.ExecutablePath,
                    "--disk-temizle " + (chkDerin.Checked ? "derin " : "normal ") + string.Join(" ", yonetici))
                {
                    UseShellExecute = true, Verb = "runas"
                };
                var p = Process.Start(bilgi);
                await Task.Run(() => { p.WaitForExit(); p.Dispose(); });
            }
            catch (System.ComponentModel.Win32Exception) { not = " Yönetici izni verilmediği için Windows kalıntıları atlandı."; }
        }

        long bosalan = Math.Max(0, BosAlan() - once);
        Mesgul(false, null);
        SuruculeriGoster();
        await TaraAsync();
        lblDurum.ForeColor = Renk.Yesil;
        lblDurum.Text = string.Format("Temizlik bitti: {0} yer açıldı. Kullanımdaki dosyalar atlandı.{1}", AnaForm.Boyut(bosalan), not);
        var bekle = new System.Windows.Forms.Timer { Interval = 10000 };
        bekle.Tick += delegate { bekle.Dispose(); lblDurum.ForeColor = Renk.Soluk; };
        bekle.Start();
    }

    async void Analiz_Click(object sender, EventArgs e)
    {
        Mesgul(true, analizSurucu + " diski taranıyor...");
        taranan = 0;
        ilerleme.Start();
        string kokYol = analizSurucu + "\\";
        var dosyalar = new List<Dugum>();
        var sonuc = await Task.Run(() => KlasorTara(new DirectoryInfo(kokYol), null, dosyalar));
        ilerleme.Stop();
        kok = simdiki = sonuc;
        buyukDosyalar = dosyalar.OrderByDescending(f => f.Boyut).Take(50).ToList();
        dosyaGorunumu = false;
        Mesgul(false, string.Format("{0:N0} dosya tarandı. Klasöre çift tıkla: içine gir. Sağ tık: Gezgin'de göster. " +
                                    "Yönetici izni olmadan bazı sistem klasörleri okunamaz.", taranan));
        AnaliziGoster();
    }

    // Bağlantı noktalarına girmez (aksi hâlde aynı klasörler iki kez sayılır ya da döngüye girer).
    Dugum KlasorTara(DirectoryInfo d, Dugum ust, List<Dugum> dosyalar)
    {
        var n = new Dugum { Ad = ust == null ? d.FullName : d.Name, Yol = d.FullName, Ust = ust };
        try
        {
            foreach (var o in d.EnumerateFileSystemInfos())
            {
                if ((o.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var alt = o as DirectoryInfo;
                if (alt != null)
                {
                    var c = KlasorTara(alt, n, dosyalar);
                    n.Alt.Add(c);
                    n.Boyut += c.Boyut;
                    continue;
                }
                long b = ((FileInfo)o).Length;
                n.DosyaBoyutu += b;
                n.Boyut += b;
                Interlocked.Increment(ref taranan);
                if (b >= 100L * 1048576) dosyalar.Add(new Dugum { Ad = o.Name, Yol = o.FullName, Boyut = b, Klasor = false, Ust = n });
            }
        }
        catch { }   // okunamayan klasör: o ana kadar sayılanla devam
        return n;
    }

    void AnaliziGoster()
    {
        if (kok == null) return;
        analizListesi.BeginUpdate();
        analizListesi.Items.Clear();
        long tam;
        if (dosyaGorunumu)
        {
            lblYol.Text = "En büyük dosyalar (100 MB üstü), " + kok.Yol + " içinde";
            tam = kok.Boyut;
            foreach (var f in buyukDosyalar) AnalizSatiri(f.Yol, f.Boyut, tam, f);
        }
        else
        {
            lblYol.Text = simdiki.Yol + "   ·   " + AnaForm.Boyut(simdiki.Boyut);
            tam = simdiki.Boyut;
            var satirlar = simdiki.Alt.Select(a => new KeyValuePair<long, Dugum>(a.Boyut, a)).ToList();
            satirlar.Add(new KeyValuePair<long, Dugum>(simdiki.DosyaBoyutu, null));
            foreach (var s in satirlar.OrderByDescending(x => x.Key))
                if (s.Key > 0) AnalizSatiri(s.Value == null ? "(bu klasördeki dosyalar)" : s.Value.Ad, s.Key, tam, s.Value);
        }
        analizListesi.EndUpdate();
        SutunDoldur(analizListesi, 0);
        btnDosyalar.Text = dosyaGorunumu ? "Klasör görünümü" : "En büyük dosyalar";
        btnUst.Enabled = !calisiyor && !dosyaGorunumu && simdiki.Ust != null;
    }

    void AnalizSatiri(string ad, long boyut, long tam, Dugum d)
    {
        var oge = new ListViewItem(ad) { Tag = d };
        oge.SubItems.Add(AnaForm.Boyut(boyut));
        oge.SubItems.Add(tam > 0 ? string.Format("%{0:0}", 100.0 * boyut / tam) : "");
        if (d != null && !d.Klasor) oge.ForeColor = Renk.Soluk;
        analizListesi.Items.Add(oge);
    }

    void AnalizOgesiniAc()
    {
        if (analizListesi.SelectedItems.Count == 0) return;
        var d = analizListesi.SelectedItems[0].Tag as Dugum;
        if (d == null) return;
        if (d.Klasor && !dosyaGorunumu) { simdiki = d; AnaliziGoster(); }
        else GezgindeGoster();
    }

    void GezgindeGoster()
    {
        if (analizListesi.SelectedItems.Count == 0) return;
        var d = analizListesi.SelectedItems[0].Tag as Dugum;
        if (d == null) return;
        try { Process.Start("explorer.exe", d.Klasor ? "\"" + d.Yol + "\"" : "/select,\"" + d.Yol + "\""); } catch { }
    }
}

// Full optimize seçenekleri: her işin altında ne yaptığı ve bedeli yazar.
class OptimizeFormu : Form
{
    public readonly CheckBox Programlar, Ram, Disk, Derin, GeriDonusum, Sanal, Hazirda;
    readonly FlowLayoutPanel akis = new FlowLayoutPanel
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Padding = new Padding(16, 4, 16, 0)
    };

    public OptimizeFormu(int programSayisi, string sanalAciklama, bool sanalGerekli, long hazirdaBoyut)
    {
        Text = "Full optimize";
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Renk.Arka;
        ForeColor = Renk.Yazi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 680);
        Icon = Logo.Simge();

        var ust = new Label
        {
            Dock = DockStyle.Top, Height = 56, Padding = new Padding(16, 14, 16, 0), ForeColor = Renk.Soluk,
            Text = "Yapılacakları seç ve \"Hepsini yap\"a bas; hepsi sırayla, tek seferde yapılır.\n" +
                   "Yönetici gerektirenler için Windows yalnızca bir kez izin ister."
        };

        Programlar = Secenek("Arka plan programlarını kapat",
            string.Format("Sistem sekmesinde işaretli {0} program kapanır. Açık pencereler, oyunlar ve sesli sohbet işaretsiz gelir.", programSayisi),
            programSayisi > 0, programSayisi > 0, Renk.Yazi);
        Ram = Secenek("RAM'i boşalt", "Programların o an kullanmadığı bellek geri alınır; çalışan oyuna dokunulmaz.", true, true, Renk.Yazi);
        Disk = Secenek("Gereksiz dosyaları temizle",
            "Geçici dosyalar, tarayıcı / Discord / Steam / Epic önbellekleri, Windows Update kalıntıları. Kişisel dosyalar, şifreler ve geçmiş silinmez.",
            true, true, Renk.Yazi);
        Derin = Secenek("Derin temizlik",
            "Shader önbellekleri, Windows.old, günlükler ve çökme kayıtları da silinir. Oyunlar ilk açılışta takılır, eski Windows'a dönülemez.",
            false, true, Renk.Turuncu);
        GeriDonusum = Secenek("Geri dönüşüm kutusunu boşalt", "Çöpe atılmış dosyalar kalıcı olarak silinir.", false, true, Renk.Yazi);
        Sanal = Secenek("Sanal belleği büyüt", sanalAciklama, sanalGerekli, sanalGerekli, Renk.Yazi);
        Hazirda = Secenek("Hazırda bekletmeyi kapat",
            hazirdaBoyut > 0
                ? string.Format("{0} yer açar; hazırda bekletme ve Hızlı Başlatma kapanır, bilgisayar biraz geç açılır.", AnaForm.Boyut(hazirdaBoyut))
                : "Bu bilgisayarda zaten kapalı.",
            false, hazirdaBoyut > 0, Renk.Turuncu);

        Derin.CheckedChanged += delegate
        {
            if (!Derin.Checked) return;
            if (MessageBox.Show(this, DiskSekmesi.DerinUyari, "Derin temizlik", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) Derin.Checked = false;
            else Disk.Checked = true;
        };
        Disk.CheckedChanged += delegate { if (!Disk.Checked) Derin.Checked = false; };

        var alt = new Panel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(16, 10, 16, 14) };
        var btnYap = new Button
        {
            Text = "Hepsini yap", Dock = DockStyle.Fill, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat,
            BackColor = Renk.Vurgu, ForeColor = Color.White, Font = new Font("Segoe UI", 11.5f, FontStyle.Bold), Cursor = Cursors.Hand
        };
        btnYap.FlatAppearance.BorderSize = 0;
        var btnVazgec = new Button
        {
            Text = "Vazgeç", Dock = DockStyle.Right, Width = 120, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat,
            BackColor = Renk.Panel, ForeColor = Renk.Yazi, Cursor = Cursors.Hand
        };
        btnVazgec.FlatAppearance.BorderColor = Renk.Cizgi;
        alt.Controls.Add(btnYap);
        alt.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        alt.Controls.Add(btnVazgec);
        AcceptButton = btnYap;
        CancelButton = btnVazgec;

        Controls.Add(akis);
        Controls.Add(ust);
        Controls.Add(alt);
    }

    CheckBox Secenek(string baslik, string aciklama, bool isaretli, bool etkin, Color renk)
    {
        var p = new Panel { Width = 550, Height = 72, Margin = new Padding(0, 0, 0, 4) };
        var c = new CheckBox
        {
            Text = baslik, AutoSize = true, Location = new Point(0, 4), Checked = isaretli, Enabled = etkin,
            ForeColor = renk, Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        var l = new Label
        {
            Text = aciklama, Location = new Point(20, 28), Size = new Size(530, 40), ForeColor = Renk.Soluk,
            Font = new Font("Segoe UI", 8.75f)
        };
        p.Controls.Add(c);
        p.Controls.Add(l);
        akis.Controls.Add(p);
        return c;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int koyu = 1; Yerel.DwmSetWindowAttribute(Handle, 20, ref koyu, 4); } catch { }
    }
}

// "Sanal bellek ayarı" seçim penceresi. Sonuç: Secim = "boyut" (IlkMB / EnCokMB), "otomatik" (Windows yönetsin)
// ya da "geri" (programdan önceki ayar). Boyutlar sistem sürücüsündeki sayfa dosyası içindir.
class SanalBellekFormu : Form
{
    public string Secim;
    public int IlkMB, EnCokMB;

    readonly FlowLayoutPanel akis = new FlowLayoutPanel
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Padding = new Padding(16, 4, 16, 0)
    };
    readonly List<RadioButton> secenekler = new List<RadioButton>();
    readonly Dictionary<RadioButton, int[]> boyutlar = new Dictionary<RadioButton, int[]>();
    RadioButton rbElle, rbOtomatik, rbGeri;
    NumericUpDown sayIlk, sayEnCok;
    readonly long ramMB, suankiMB, bosMB;

    public SanalBellekFormu(string[] satirlar, long ramMB, long suankiMB, long bosMB, int hedefIlk, int hedefEnCok, string[] yedek)
    {
        this.ramMB = ramMB; this.suankiMB = suankiMB; this.bosMB = bosMB;
        Text = "Sanal bellek ayarı";
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Renk.Arka;
        ForeColor = Renk.Yazi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(580, 690);
        Icon = Logo.Simge();

        var ust = new Label
        {
            Dock = DockStyle.Top, Height = 92, Padding = new Padding(16, 12, 16, 0), ForeColor = Renk.Soluk,
            Text = string.Format("Şu anki ayar: {0}\nRAM {1:0.#} GB + sayfa dosyası {2:0.#} GB = sanal bellek sınırı {3:0.#} GB\n" +
                                 "Sınır dolunca programlar \"bellek yetersiz\" deyip kapanır. Bir boyut seç; yeniden başlatınca geçerli olur.",
                                 SanalBellek.Anlat(satirlar), ramMB / 1024.0, suankiMB / 1024.0, (ramMB + suankiMB) / 1024.0)
        };

        // Önerilen en üstte; hazır boyutlardan onunla aynı olan tekrar eklenmez.
        var hazir = new List<int[]> { new[] { hedefIlk / 1024, hedefEnCok / 1024 } };
        foreach (var b in new[] { new[] { 8, 16 }, new[] { 16, 32 }, new[] { 24, 48 }, new[] { 32, 64 } })
            if (b[0] != hazir[0][0]) hazir.Add(b);
        int elleIlk = SanalBellek.ElleIlkMB(satirlar);
        for (int i = 0; i < hazir.Count; i++)
        {
            int ilk = hazir[i][0] * 1024;
            int enCok = (int)Math.Max(ilk, Math.Min(hazir[i][1] * 1024L, (bosMB - 5120) / 1024 * 1024));
            bool yerVar = ilk <= bosMB - 10240;
            string aciklama;
            Color renk = Renk.Yazi;
            if (!yerVar) aciklama = string.Format("{0} diskinde yer yok (en az {1} GB boş yer gerekir).", SanalBellek.Surucu, ilk / 1024 + 10);
            else
            {
                aciklama = string.Format("Sınır {0:0.#} GB olur, gerekirse {1:0.#} GB'a çıkar. Diskte {2} GB yer kaplar.",
                    (ramMB + ilk) / 1024.0, (ramMB + enCok) / 1024.0, ilk / 1024);
                if (ilk < suankiMB - 512) { aciklama += " Şu ankinden küçük: sınır düşer."; renk = Renk.Turuncu; }
                if (ilk == elleIlk) aciklama += " (Şu anki ayar bu.)";
            }
            var rb = Secenek(string.Format("{0} GB, gerekirse {1} GB{2}", ilk / 1024, enCok / 1024, i == 0 ? "  (önerilen)" : ""),
                aciklama, yerVar, renk);
            boyutlar[rb] = new[] { ilk, enCok };
        }

        rbElle = Secenek("Elle gir", "", true, Renk.Yazi);
        var elle = rbElle.Parent;
        sayIlk = Sayi(hedefIlk / 1024, new Point(20, 30));
        sayEnCok = Sayi(hedefEnCok / 1024, new Point(196, 30));
        elle.Controls.Add(sayIlk);
        elle.Controls.Add(new Label { Text = "GB'tan başlasın,", AutoSize = true, Location = new Point(84, 33), ForeColor = Renk.Soluk });
        elle.Controls.Add(sayEnCok);
        elle.Controls.Add(new Label { Text = "GB'a kadar büyüsün", AutoSize = true, Location = new Point(260, 33), ForeColor = Renk.Soluk });
        foreach (var l in elle.Controls.OfType<Label>().Where(x => x.Text == "").ToList()) elle.Controls.Remove(l);
        sayIlk.ValueChanged += delegate { rbElle.Checked = true; if (sayEnCok.Value < sayIlk.Value) sayEnCok.Value = sayIlk.Value; };
        sayEnCok.ValueChanged += delegate { rbElle.Checked = true; };

        bool otomatikZaten = satirlar.Length > 0 && satirlar.All(s => s.Trim().StartsWith("?:"));
        rbOtomatik = Secenek("Windows otomatik yönetsin",
            otomatikZaten ? "Şu an zaten böyle."
                          : "Boyutu Windows ayarlar ve gerektikçe büyütür; büyürken anlık \"bellek yetersiz\" hatası görülebilir.",
            !otomatikZaten, Renk.Yazi);

        if (yedek != null)
            rbGeri = Secenek("Programdan önceki ayara dön", "Önceki ayar: " + SanalBellek.Anlat(yedek), true, Renk.Yazi);

        // Sanal belleği küçültmeyen ilk seçenek seçili gelir.
        var ilkSecilecek = secenekler.FirstOrDefault(r => r.Enabled && (!boyutlar.ContainsKey(r) || boyutlar[r][0] >= suankiMB - 512))
                        ?? secenekler.FirstOrDefault(r => r.Enabled);
        if (ilkSecilecek != null) ilkSecilecek.Checked = true;

        var not = new Label
        {
            Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(16, 6, 16, 0), ForeColor = Renk.Soluk,
            Font = new Font("Segoe UI", 8.75f), Text = "Windows yönetici izni isteyecek. Ayar bilgisayar yeniden başlayınca geçerli olur."
        };
        var alt = new Panel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(16, 10, 16, 14) };
        var btnUygula = new Button
        {
            Text = "Uygula", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            BackColor = Renk.Vurgu, ForeColor = Color.White, Font = new Font("Segoe UI", 11.5f, FontStyle.Bold), Cursor = Cursors.Hand
        };
        btnUygula.FlatAppearance.BorderSize = 0;
        btnUygula.Click += Uygula_Click;
        var btnVazgec = new Button
        {
            Text = "Vazgeç", Dock = DockStyle.Right, Width = 120, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat,
            BackColor = Renk.Panel, ForeColor = Renk.Yazi, Cursor = Cursors.Hand
        };
        btnVazgec.FlatAppearance.BorderColor = Renk.Cizgi;
        alt.Controls.Add(btnUygula);
        alt.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        alt.Controls.Add(btnVazgec);
        AcceptButton = btnUygula;
        CancelButton = btnVazgec;

        Controls.Add(akis);
        Controls.Add(ust);
        Controls.Add(not);
        Controls.Add(alt);
    }

    void Uygula_Click(object sender, EventArgs e)
    {
        var rb = secenekler.FirstOrDefault(r => r.Checked);
        if (rb == null) return;
        if (rb == rbOtomatik) Secim = "otomatik";
        else if (rb == rbGeri) Secim = "geri";
        else
        {
            Secim = "boyut";
            if (rb == rbElle)
            {
                IlkMB = (int)sayIlk.Value * 1024;
                EnCokMB = (int)Math.Max(sayIlk.Value, sayEnCok.Value) * 1024;
                if (IlkMB > bosMB - 10240 || EnCokMB > bosMB - 5120)
                {
                    MessageBox.Show(this, string.Format("{0} diskinde bu kadar yer yok. Kullanılabilir yer yaklaşık {1:0} GB; " +
                        "başlangıç boyutu bundan en az 10 GB, en büyük boyut en az 5 GB küçük olmalı.", SanalBellek.Surucu, bosMB / 1024.0),
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else { IlkMB = boyutlar[rb][0]; EnCokMB = boyutlar[rb][1]; }
            if (IlkMB < suankiMB - 512 && MessageBox.Show(this, string.Format(
                    "Bu ayar sanal bellek sınırını {0:0.#} GB'tan {1:0.#} GB'a düşürür. Oyunlarda \"bellek yetersiz\" hatası artabilir.\n\nYine de uygulansın mı?",
                    (ramMB + suankiMB) / 1024.0, (ramMB + IlkMB) / 1024.0), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        }
        DialogResult = DialogResult.OK;
    }

    RadioButton Secenek(string baslik, string aciklama, bool etkin, Color renk)
    {
        var p = new Panel { Width = 530, Height = 62, Margin = new Padding(0, 0, 0, 4) };
        var r = new RadioButton
        {
            Text = baslik, AutoSize = true, Location = new Point(0, 4), Enabled = etkin, AutoCheck = false,
            ForeColor = Renk.Yazi, Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        // Her seçenek ayrı panelde olduğu için gruplamayı burada yapıyoruz.
        r.Click += delegate { r.Checked = true; };
        r.CheckedChanged += delegate { if (r.Checked) foreach (var x in secenekler) if (x != r) x.Checked = false; };
        var l = new Label
        {
            Text = aciklama, Location = new Point(20, 28), Size = new Size(510, 32), ForeColor = etkin ? (renk == Renk.Yazi ? Renk.Soluk : renk) : Renk.Soluk,
            Font = new Font("Segoe UI", 8.75f)
        };
        p.Controls.Add(r);
        p.Controls.Add(l);
        akis.Controls.Add(p);
        secenekler.Add(r);
        return r;
    }

    NumericUpDown Sayi(int deger, Point yer)
    {
        return new NumericUpDown
        {
            Minimum = 1, Maximum = 256, Value = Math.Max(1, Math.Min(256, deger)), Location = yer, Width = 58,
            BackColor = Renk.Panel, ForeColor = Renk.Yazi, BorderStyle = BorderStyle.FixedSingle
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int koyu = 1; Yerel.DwmSetWindowAttribute(Handle, 20, ref koyu, 4); } catch { }
    }
}

class NvidiaBilgi
{
    public string KartAdi, KuruluSurum, KuruluTarih;            // KuruluSurum NVIDIA biçiminde: 617.42
    public string SonSurum, SonTarih, Boyut, IndirmeAdresi, DetayAdresi;
    public string Uygulama;                                     // NVIDIA App / GeForce Experience yolu; varsa güncellemeyi o yapar
    public string Hata;

    public bool YeniVar
    {
        get
        {
            double k, s;
            return Hata == null && SonSurum != null
                && double.TryParse(KuruluSurum, NumberStyles.Float, CultureInfo.InvariantCulture, out k)
                && double.TryParse(SonSurum, NumberStyles.Float, CultureInfo.InvariantCulture, out s) && s > k;
        }
    }
}

// NVIDIA ekran kartı sürücüsünü NVIDIA'nın kendi sitesindeki sürücü aramasıyla karşılaştırır. Bu adresler
// NVIDIA'nın resmi olarak belgelediği bir API değil (sitenin kendi arama sayfası kullanıyor); değişirse
// yalnızca bu bölüm "öğrenilemedi" der.
static class NvidiaSurucu
{
    const string UrunListesi = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    const string Arama = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                         "?func=DriverManualLookup&psid={0}&pfid={1}&osID={2}&languageCode=1033&isWHQL=1&dch=1&upCRD=0&sort1=0&numberOfResults=1";

    // null: bu bilgisayarda NVIDIA kart yok.
    // Windows derleme numarası kayıt defterinden: manifestsiz .NET Framework exe'de Environment.OSVersion
    // Windows 11'de de 6.2.9200 der, ona güvenilmez.
    static int WindowsDerlemesi()
    {
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                int d;
                if (k != null && int.TryParse(Convert.ToString(k.GetValue("CurrentBuildNumber")), out d)) return d;
            }
        }
        catch { }
        return Environment.OSVersion.Version.Build;
    }

    public static NvidiaBilgi Kontrol()
    {
        string ad, surum, tarih;
        if (!KartBul(out ad, out surum, out tarih)) return null;
        var b = new NvidiaBilgi { KartAdi = ad, KuruluSurum = SurumCevir(surum), KuruluTarih = tarih, Uygulama = UygulamaBul() };
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2 (.NET 4.0 hedefinde ad yok)
            string hedef = Sade(ad), psid = null, pfid = null;
            foreach (Match m in Regex.Matches(Getir(UrunListesi), "<LookupValue ParentID=\"(\\d+)\">\\s*<Name>([^<]*)</Name>\\s*<Value>(\\d+)</Value>"))
                if (Sade(WebUtility.HtmlDecode(m.Groups[2].Value)) == hedef) { psid = m.Groups[1].Value; pfid = m.Groups[3].Value; break; }
            if (pfid == null) { b.Hata = "Bu kart NVIDIA'nın sürücü listesinde bulunamadı."; return b; }

            // Windows 11 / Windows 10 64 bit / Windows 10 32 bit (Windows 11'in 32 bit sürümü yok).
            int isletim = !Environment.Is64BitOperatingSystem ? 56 : WindowsDerlemesi() >= 22000 ? 135 : 57;
            string json = Getir(string.Format(Arama, psid, pfid, isletim));
            b.SonSurum = Alan(json, "Version");
            b.SonTarih = Alan(json, "ReleaseDateTime");
            b.Boyut = Alan(json, "DownloadURLFileSize");
            b.IndirmeAdresi = Alan(json, "DownloadURL");
            b.DetayAdresi = Alan(json, "DetailsURL");
            if (b.SonSurum == null || b.IndirmeAdresi == null) b.Hata = "NVIDIA'dan sürüm bilgisi alınamadı.";
        }
        catch (Exception e) { b.Hata = "NVIDIA'ya bağlanılamadı (" + e.Message + ")."; }
        return b;
    }

    static string Getir(string adres)
    {
        using (var w = new WebClient())
        {
            w.Encoding = Encoding.UTF8;
            w.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 SergenPcTemizle";
            return w.DownloadString(adres);
        }
    }

    // Bu bilgisayarda takılı, sürücüsü yüklü bir NVIDIA kartı var mı (internete gitmez).
    public static bool KartVar()
    {
        string ad, surum, tarih;
        return KartBul(out ad, out surum, out tarih);
    }

    // Takılı ve sürücüsü yüklü NVIDIA kartları içinden en çok VRAM'i olan: ad, Windows biçiminde sürüm
    // (32.0.16.1742), tarih. Sökülmüş kartların kalıntı girdileri sayılmaz.
    static bool KartBul(out string ad, out string surum, out string tarih)
    {
        ad = surum = tarih = null;
        long enCok = -1;
        try
        {
            var takili = EkranKartlari.Takililar();
            using (var sinif = Registry.LocalMachine.OpenSubKey(EkranKartlari.SinifAnahtari))
            {
                if (sinif == null) return false;
                foreach (var alt in sinif.GetSubKeyNames())
                {
                    if (alt.Length != 4) continue;
                    if (takili != null && !takili.Contains(alt)) continue;
                    try
                    {
                        using (var k = sinif.OpenSubKey(alt))
                        {
                            string d = k == null ? null : k.GetValue("DriverDesc") as string;
                            if (d == null || d.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string s = k.GetValue("DriverVersion") as string;
                            long vram = EkranKartlari.Vram(k);
                            if (s == null || vram <= enCok) continue;
                            enCok = vram;
                            ad = d.Trim();
                            surum = s;
                            DateTime t;
                            string ham = k.GetValue("DriverDate") as string;
                            tarih = DateTime.TryParseExact(ham, "M-d-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)
                                ? t.ToString("dd.MM.yyyy") : ham;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        return surum != null;
    }

    // Windows sürümünün son 5 hanesi NVIDIA sürümüdür: 32.0.16.1742 → 61742 → 617.42
    static string SurumCevir(string windows)
    {
        string h = new string(windows.Where(char.IsDigit).ToArray());
        if (h.Length < 5) return windows;
        h = h.Substring(h.Length - 5);
        return h.Substring(0, 3) + "." + h.Substring(3);
    }

    static string Sade(string ad)
    {
        ad = ad.Trim();
        if (ad.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) ad = ad.Substring(7);
        return ad.ToLowerInvariant();
    }

    static string Alan(string json, string ad)
    {
        var m = Regex.Match(json, "\"" + ad + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success && m.Groups[1].Value.Length > 0 ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    public static string UygulamaBul()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var y in new[] {
            Path.Combine(pf, @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"),
            Path.Combine(pf, @"NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe"),
            Path.Combine(pf86, @"NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe") })
            if (File.Exists(y)) return y;
        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile, pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid eylem, ref WINTRUST_DATA veri);

    // Dosyanın Authenticode imzası geçerli mi ve imzalayan NVIDIA Corporation mı?
    public static bool ImzaNvidia(string yol)
    {
        var dosya = new WINTRUST_FILE_INFO { pcwszFilePath = yol };
        dosya.cbStruct = (uint)Marshal.SizeOf(dosya);
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(dosya));
        try
        {
            Marshal.StructureToPtr(dosya, p, false);
            var veri = new WINTRUST_DATA { dwUIChoice = 2 /* arayüz yok */, dwUnionChoice = 1 /* dosya */, pFile = p };
            veri.cbStruct = (uint)Marshal.SizeOf(veri);
            var eylem = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            if (WinVerifyTrust(IntPtr.Zero, ref eylem, ref veri) != 0) return false;
            var sertifika = X509Certificate.CreateFromSignedFile(yol);
            return sertifika.Subject.IndexOf("O=NVIDIA Corporation", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
        finally
        {
            Marshal.DestroyStructure(p, typeof(WINTRUST_FILE_INFO));
            Marshal.FreeHGlobal(p);
        }
    }

    // Yönetici olarak çalışır: imzayı yeniden doğrular, geri yükleme noktası ister, NVIDIA kurulumunu açar.
    // 0 = kurulum açıldı, 3 = açıldı ama geri yükleme noktası oluşmadı, 2 = imza geçersiz, 1 = açılamadı.
    public static int Kur(string yol)
    {
        if (!File.Exists(yol) || !ImzaNvidia(yol)) return 2;
        bool nokta = false;
        try
        {
            var bilgi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description 'Sergen PC Temizle: NVIDIA surucusu oncesi' " +
                "-RestorePointType DEVICE_DRIVER_INSTALL -ErrorAction Stop\"")
            {
                UseShellExecute = false, CreateNoWindow = true
            };
            using (var p = Process.Start(bilgi)) { p.WaitForExit(); nokta = p.ExitCode == 0; }
        }
        catch { }
        try { Process.Start(new ProcessStartInfo(yol) { UseShellExecute = true }); }
        catch { return 1; }
        return nokta ? 0 : 3;
    }
}

class SurucuSekmesi : Panel
{
    public event Action<bool> GuncellemeDurumu;   // sekme düğmesinde "yeni" işareti için

    readonly Label lblKart = new Label(), lblKurulu = new Label(), lblSon = new Label(), lblSonuc = new Label(), lblDurum = new Label();
    readonly Gosterge gIndirme = new Gosterge("İNDİRME", "Dosya İndirilenler klasörüne kaydediliyor");
    readonly Panel indirmeKutusu = new Panel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(8, 6, 8, 6), Visible = false };
    readonly Button btnKur, btnKontrol, btnNotlar, btnIptal;
    NvidiaBilgi bilgi;
    WebClient indirici;
    DateTime indirmeBasi;
    bool calisiyor, kontrolEdildi;

    public SurucuSekmesi()
    {
        Dock = DockStyle.Fill;
        BackColor = Renk.Arka;

        var kart = new Panel { Dock = DockStyle.Top, Height = 176, BackColor = Renk.Panel, Padding = new Padding(16, 12, 16, 10) };
        var kartDis = new Panel { Dock = DockStyle.Top, Height = 192, Padding = new Padding(12, 12, 12, 4) };
        var baslik = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Renk.Soluk, Text = "EKRAN KARTI SÜRÜCÜSÜ · NVIDIA", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
        lblKart.Font = new Font("Segoe UI Semibold", 15f);
        lblKart.Height = 36;
        lblSonuc.Font = new Font("Segoe UI", 12.5f, FontStyle.Bold);
        lblSonuc.Height = 34;
        foreach (var l in new[] { lblKurulu, lblSon }) { l.Height = 24; l.ForeColor = Renk.Yazi; }
        foreach (var l in new[] { lblSonuc, lblSon, lblKurulu, lblKart }) { l.Dock = DockStyle.Top; l.AutoEllipsis = true; kart.Controls.Add(l); }
        kart.Controls.Add(baslik);
        kartDis.Controls.Add(kart);

        var dugmeler = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(12, 8, 12, 4) };
        btnKur = DiskSekmesiDugme("İndir ve kur", true);
        btnKur.Dock = DockStyle.Fill;
        btnKur.Click += Kur_Click;
        btnIptal = DiskSekmesiDugme("İptal", false);
        btnIptal.Width = 110;
        btnIptal.Visible = false;
        btnIptal.Click += delegate { if (indirici != null) indirici.CancelAsync(); };
        btnNotlar = DiskSekmesiDugme("Sürüm notları", false);
        btnNotlar.Width = 140;
        btnNotlar.Click += delegate { if (bilgi != null && bilgi.DetayAdresi != null) try { Process.Start(bilgi.DetayAdresi); } catch { } };
        btnKontrol = DiskSekmesiDugme("Yeniden kontrol et", false);
        btnKontrol.Width = 160;
        btnKontrol.Click += delegate { Kontrol(); };
        dugmeler.Controls.Add(btnKur);
        foreach (var b in new[] { btnIptal, btnNotlar, btnKontrol })
        {
            dugmeler.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            dugmeler.Controls.Add(b);
        }

        indirmeKutusu.Controls.Add(gIndirme);

        var aciklama = new Label
        {
            Dock = DockStyle.Top, Height = 150, Padding = new Padding(16, 10, 16, 0), ForeColor = Renk.Soluk,
            Text = "Nasıl çalışır?\n" +
                   "•  Kurulu sürüm, NVIDIA'nın sitesindeki en son Game Ready sürücüsüyle karşılaştırılır.\n" +
                   "•  NVIDIA App (ya da GeForce Experience) kuruluysa güncellemeyi o yapar; bu program ayrıca indirmez, iki kez kurulmaz.\n" +
                   "•  NVIDIA App yoksa sürücü yalnızca NVIDIA'nın sitesinden indirilir; imzası NVIDIA değilse çalıştırılmaz.\n" +
                   "•  Kurmadan önce sistem geri yükleme noktası istenir; sonra NVIDIA'nın kendi kurulum ekranı açılır.\n" +
                   "•  Kurulumda ekran birkaç kez kararabilir: önce oyunları kapat. Sorun olursa Aygıt Yöneticisi'nden \"Sürücüyü geri al\"."
        };

        lblDurum.Dock = DockStyle.Bottom;
        lblDurum.Height = 34;
        lblDurum.Padding = new Padding(14, 0, 12, 0);
        lblDurum.TextAlign = ContentAlignment.MiddleLeft;
        lblDurum.ForeColor = Renk.Soluk;

        Controls.Add(aciklama);
        Controls.Add(indirmeKutusu);
        Controls.Add(dugmeler);
        Controls.Add(kartDis);
        Controls.Add(lblDurum);
        Goster();
    }

    static Button DiskSekmesiDugme(string metin, bool vurgu)
    {
        var b = new Button
        {
            Text = metin, Dock = DockStyle.Right, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = vurgu ? Renk.Vurgu : Renk.Panel, ForeColor = vurgu ? Color.White : Renk.Yazi,
            Font = new Font("Segoe UI", vurgu ? 11.5f : 9.5f, FontStyle.Bold)
        };
        b.FlatAppearance.BorderSize = vurgu ? 0 : 1;
        b.FlatAppearance.BorderColor = Renk.Cizgi;
        return b;
    }

    public void Gosterildi() { if (!kontrolEdildi) Kontrol(); }

    public async void Kontrol()
    {
        if (calisiyor || indirici != null) return;
        calisiyor = true;
        kontrolEdildi = true;
        lblDurum.Text = "NVIDIA'ya soruluyor...";
        Goster();
        bilgi = await Task.Run(() => NvidiaSurucu.Kontrol());
        calisiyor = false;
        lblDurum.Text = "";
        Goster();
        if (GuncellemeDurumu != null) GuncellemeDurumu(bilgi != null && bilgi.YeniVar);
    }

    void Goster()
    {
        btnKontrol.Enabled = !calisiyor && indirici == null;
        btnNotlar.Enabled = bilgi != null && bilgi.DetayAdresi != null;
        btnKur.Enabled = false;
        btnKur.Text = "İndir ve kur";
        btnKur.BackColor = Renk.Panel;
        btnKur.ForeColor = Renk.Soluk;
        if (calisiyor && bilgi == null) { lblKart.Text = "Kontrol ediliyor..."; lblKurulu.Text = lblSon.Text = lblSonuc.Text = ""; return; }
        if (!kontrolEdildi) { lblKart.Text = "Henüz kontrol edilmedi"; lblKurulu.Text = lblSon.Text = lblSonuc.Text = ""; return; }
        if (bilgi == null)
        {
            lblKart.Text = "NVIDIA ekran kartı bulunamadı";
            lblKurulu.Text = "Bu bölüm yalnızca NVIDIA kartlar için.";
            lblSon.Text = "Intel ya da AMD için üreticinin kendi güncelleme aracını kullan.";
            lblSonuc.Text = "";
            return;
        }
        lblKart.Text = bilgi.KartAdi;
        lblKurulu.Text = string.Format("Kurulu sürüm:   {0}   ({1})", bilgi.KuruluSurum, bilgi.KuruluTarih);
        if (bilgi.Hata != null)
        {
            lblSon.Text = "En son sürüm:   öğrenilemedi";
            lblSonuc.Text = bilgi.Hata;
            lblSonuc.ForeColor = Renk.Turuncu;
            return;
        }
        DateTime t;
        string tarih = DateTime.TryParseExact(bilgi.SonTarih, "ddd MMM dd, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)
            ? t.ToString("dd.MM.yyyy") : bilgi.SonTarih;
        lblSon.Text = string.Format("En son sürüm:   {0}   ({1} · {2})", bilgi.SonSurum, tarih, bilgi.Boyut);
        bool yeni = bilgi.YeniVar;
        lblSonuc.Text = yeni ? "Yeni sürüm var: " + bilgi.SonSurum : "Sürücün güncel.";
        lblSonuc.ForeColor = yeni ? Renk.Turuncu : Renk.Yesil;
        btnKur.Enabled = yeni && indirici == null;
        if (!yeni) btnKur.Text = "Güncelleme gerekmiyor";
        else if (bilgi.Uygulama != null) btnKur.Text = Path.GetFileNameWithoutExtension(bilgi.Uygulama) + "'te güncelle";
        btnKur.BackColor = btnKur.Enabled ? Renk.Vurgu : Renk.Panel;
        btnKur.ForeColor = btnKur.Enabled ? Color.White : Renk.Soluk;
    }

    async void Kur_Click(object sender, EventArgs e)
    {
        if (bilgi == null || !bilgi.YeniVar) return;

        // NVIDIA App / GeForce Experience varsa güncellemeyi o yapsın: aynı sürücü iki yoldan inmesin.
        if (bilgi.Uygulama != null)
        {
            try { Process.Start(new ProcessStartInfo(bilgi.Uygulama) { UseShellExecute = true }); } catch { }
            MessageBox.Show(FindForm(), string.Format("{0} açılıyor. \"Sürücüler\" bölümünde {1} sürümü için \"İndir\"e, sonra \"Hızlı kurulum\"a bas.\n\n" +
                "Bitince buraya dönüp \"Yeniden kontrol et\"e basarsan güncel olduğunu görürsün.",
                Path.GetFileNameWithoutExtension(bilgi.Uygulama), bilgi.SonSurum), "Sürücü güncelleme", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Uri adres;
        if (!Uri.TryCreate(bilgi.IndirmeAdresi, UriKind.Absolute, out adres) || adres.Scheme != Uri.UriSchemeHttps
            || !adres.Host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase))
        {
            lblDurum.Text = "İndirme adresi NVIDIA'ya ait görünmüyor; hiçbir şey indirilmedi.";
            return;
        }
        string klasor = IndirilenlerKlasoru();
        string yol = Path.Combine(klasor, Path.GetFileName(adres.LocalPath));

        if (MessageBox.Show(FindForm(), string.Format(
                "NVIDIA {0} sürücüsü indirilecek ({1}) ve kurulumu başlatılacak.\n\n" +
                "•  Dosya İndirilenler klasörüne kaydedilir.\n" +
                "•  İmzası NVIDIA'ya ait değilse çalıştırılmaz.\n" +
                "•  Kurmadan önce sistem geri yükleme noktası istenir.\n" +
                "•  Kurulumda ekran birkaç kez kararabilir; önce oyunları kapat.\n\n" +
                "Windows yönetici izni isteyecek. Devam edilsin mi?", bilgi.SonSurum, bilgi.Boyut),
                "Sürücü güncelleme", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        Directory.CreateDirectory(klasor);
        if (!File.Exists(yol) && !await Indir(adres, yol)) return;

        btnKur.Enabled = btnKontrol.Enabled = false;
        lblDurum.Text = "İmza kontrol ediliyor...";
        if (!await Task.Run(() => NvidiaSurucu.ImzaNvidia(yol)))
        {
            try { File.Delete(yol); } catch { }
            Goster();
            lblDurum.Text = "";
            MessageBox.Show(FindForm(), "İndirilen dosyanın imzası NVIDIA'ya ait değil ya da dosya bozuk; silindi. Hiçbir şey kurulmadı.",
                "Sürücü güncelleme", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Pencere tepsideyken (oyun sırasında) indirme bittiyse yönetici izni ve kurulum oyunun ortasında açılmasın.
        var f = FindForm();
        if (f == null || !f.Visible)
        {
            Goster();
            lblDurum.Text = "İndirme bitti; kurmak için \"İndir ve kur\"a yeniden bas.";
            return;
        }

        lblDurum.Text = "Geri yükleme noktası oluşturuluyor, ardından NVIDIA kurulumu açılacak...";
        int kod = -1;
        try
        {
            var bilgiP = new ProcessStartInfo(Application.ExecutablePath, "--nvidia-kur \"" + yol + "\"") { UseShellExecute = true, Verb = "runas" };
            var p = Process.Start(bilgiP);
            await Task.Run(() => { p.WaitForExit(); kod = p.ExitCode; p.Dispose(); });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            lblDurum.Text = "Yönetici izni verilmedi; kurulum başlamadı. Dosya İndirilenler klasöründe duruyor.";
            Goster();
            return;
        }
        Goster();
        lblDurum.Text = "";
        string metin;
        if (kod == 0 || kod == 3)
        {
            metin = "NVIDIA kurulum ekranı açıldı. \"NVIDIA Grafik Sürücüsü\"nü seç ve \"Hızlı\" ile devam et. " +
                    "Bitince bilgisayarı yeniden başlat, sonra buradan \"Yeniden kontrol et\"e bas.";
            if (kod == 3) metin += "\n\nGeri yükleme noktası oluşturulamadı (Sistem Geri Yükleme kapalı olabilir). " +
                                   "Sorun çıkarsa Aygıt Yöneticisi'nden \"Sürücüyü geri al\" kullanılabilir.";
        }
        else if (kod == 2) metin = "Dosyanın imzası doğrulanamadı; kurulum başlatılmadı.";
        else metin = "NVIDIA kurulumu başlatılamadı. Dosya İndirilenler klasöründe; elle açabilirsin.";
        MessageBox.Show(FindForm(), metin, "Sürücü güncelleme", MessageBoxButtons.OK, kod == 0 || kod == 3 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(ref Guid kimlik, uint bayrak, IntPtr belirtec, out IntPtr yol);

    const string IndirilenlerKimligi = "{374DE290-123F-4565-9164-39C4925E467B}";   // FOLDERID_Downloads

    // Kullanıcının gerçek İndirilenler klasörü: taşınmış (D:\İndirilenler) ya da OneDrive / ilkeyle yönlendirilmiş
    // olabilir. Önce Known Folder API, sonra User Shell Folders kaydı, en son profil\Downloads.
    static string IndirilenlerKlasoru()
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            var kimlik = new Guid(IndirilenlerKimligi);
            if (SHGetKnownFolderPath(ref kimlik, 0, IntPtr.Zero, out p) == 0)
            {
                string s = Marshal.PtrToStringUni(p);
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { }
        finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
            {
                string s = k == null ? null : k.GetValue(IndirilenlerKimligi, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (!string.IsNullOrEmpty(s)) return Environment.ExpandEnvironmentVariables(s);
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // .part dosyasına indirir, bitince asıl adına taşır; yarım dosya asla "hazır" sanılmaz.
    Task<bool> Indir(Uri adres, string yol)
    {
        var sonuc = new TaskCompletionSource<bool>();
        string gecici = yol + ".part";
        indirici = new WebClient();
        indirici.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 SergenPcTemizle";
        indirmeBasi = DateTime.UtcNow;
        indirmeKutusu.Visible = btnIptal.Visible = true;
        gIndirme.Ayarla("Başlıyor...", 0);
        Goster();
        lblDurum.Text = "İndiriliyor...";
        indirici.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
        {
            double mb = e.BytesReceived / 1048576.0, top = e.TotalBytesToReceive / 1048576.0;
            double hiz = mb / Math.Max(0.5, (DateTime.UtcNow - indirmeBasi).TotalSeconds);
            gIndirme.Ayarla(string.Format("{0:0} / {1:0} MB · {2:0.0} MB/sn", mb, top, hiz), top > 0 ? mb / top : 0);
        };
        indirici.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            indirici.Dispose();
            indirici = null;
            indirmeKutusu.Visible = btnIptal.Visible = false;
            bool tamam = false;
            if (e.Cancelled || e.Error != null)
            {
                try { File.Delete(gecici); } catch { }
                lblDurum.Text = e.Cancelled ? "İndirme iptal edildi." : "İndirme başarısız: " + e.Error.Message;
            }
            else
            {
                try { if (File.Exists(yol)) File.Delete(yol); File.Move(gecici, yol); tamam = true; }
                catch (Exception x) { lblDurum.Text = "İndirilen dosya kaydedilemedi: " + x.Message; }
            }
            Goster();
            sonuc.SetResult(tamam);
        };
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; indirici.DownloadFileAsync(adres, gecici); }
        catch (Exception x)
        {
            indirici.Dispose(); indirici = null;
            indirmeKutusu.Visible = btnIptal.Visible = false;
            lblDurum.Text = "İndirme başlatılamadı: " + x.Message;
            Goster();
            sonuc.SetResult(false);
        }
        return sonuc.Task;
    }

    public void Kapaniyor() { if (indirici != null) indirici.CancelAsync(); }
}

class AnaForm : Form
{
    // Hiç listelenmez (Windows klasöründen çalışanlar zaten listelenmez): güvenlik yazılımları,
    // hile önleyiciler (kapatılırsa oyundan atar) ve yaygın bağlantı araçları. Adı bilinmeyen DPI / VPN araçları
    // WinDivert sürücüsünü taşımalarından tanınır (WinDivertTasiyor).
    static readonly HashSet<string> Korunan = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MsMpEng", "SecurityHealthSystray", "AvastUI", "AvastSvc", "AVGUI", "avgsvc", "avp", "avpui",
        "bdagent", "bdservicehost", "egui", "ekrn", "MBAMTray", "MBAMService", "mcshield", "McUICnt",
        "NortonUI", "NortonSecurity", "ccSvcHst", "360tray", "360sd",
        "vgtray", "vgc", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64",
        "FACEIT", "FACEITService", "faceitclient", "SGuard64", "SGuardSvc64", "ACE-Tray", "ACE-Guard",
        "xhunter1", "GameGuard",
        "goodbyedpi", "winws",
        "SergenPcTemizle", "PcTemizle"
    };

    // Adı bunlarla başlayanlar da listelenmez: ekran kartı, ses ve dokunmatik yüzey yazılımları.
    static readonly string[] KorunanOnek =
    {
        "NVIDIA", "nvcontainer", "NVDisplay", "nvsphelper", "AMD", "Radeon", "atiec", "atiesr", "Intel", "igfx", "IGCC",
        "Rtk", "Realtek", "Nahimic", "A-Volute", "Dolby", "Waves", "MaxxAudio", "SonicStudio",
        "SynTP", "Synaptics", "ETD", "Elan", "ApntEx", "Apoint"
    };

    // Bilgisayar üreticisinin yazılımları (fan, Fn tuşları, pil, klavye ışığı): yayıncıdan (CompanyName) tanınır,
    // her üretici için en az bir yayıncı adı. Exe adı önekleri yalnızca yayıncı boşsa yedek olarak kullanılır;
    // böylece aynı markanın isteğe bağlı araçları (ör. MSI Afterburner, yayıncısı "MSI Co., LTD") listelenebilir.
    public static readonly string[] UreticiYayincilari =
    {
        "Micro-Star", "ASUSTeK", "Lenovo", "Dell", "Alienware", "HP Inc", "Hewlett-Packard", "HP Development",
        "Acer", "GIGA-BYTE", "Gigabyte", "Samsung Electronics", "Huawei", "Honor Device", "LG Electronics",
        "Clevo", "Tongfang", "Uniwill", "Monster", "Casper", "Xiaomi"
    };

    public static readonly string[] UreticiOnek =
    {
        "MSI", "DCv2", "LEDKeeper", "OmAp", "Armoury", "Asus", "Lenovo", "Dell", "Alienware",
        "OmenCommandCenter", "HPSystemEvent", "Acer", "PredatorSense", "Gigabyte", "AORUS"
    };

    // Listelenir ama varsayılan olarak işaretsiz gelir: kapatınca oyun, sesli sohbet, kayıt,
    // fare/klavye ayarı ya da uzaktan yardım kopar. Komut satırından / terminalden başlatılmış süreçler de
    // (kaydedilmemiş işleri olabilir) adlarından bağımsız olarak işaretsiz gelir (Grup.Konsol).
    static readonly HashSet<string> VarsayilanIsaretsiz = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "steamservice", "gameoverlayui", "gameoverlayui64",
        "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "RiotClientServices", "RiotClientUx", "RiotClientUxRender",
        "EADesktop", "EABackgroundService", "upc", "UbisoftConnect", "GalaxyClient", "XboxPcApp",
        "Discord", "DiscordPTB", "DiscordCanary", "TeamSpeak", "ts3client_win64", "ms-teams", "Teams",
        "Skype", "WhatsApp", "Telegram", "Zoom",
        "obs64", "obs32", "RTSS", "RTSSHooksLoader64", "EncoderServer", "Medal", "Overwolf", "ShareX", "bdcam",
        "lghub", "lghub_agent", "lghub_system_tray", "Razer Synapse 3", "RazerCentral", "Razer Synapse Service Process",
        "iCUE", "SteelSeriesGG", "SteelSeriesEngine", "SteelSeriesSonar",
        "TeamViewer", "TeamViewer_Service", "AnyDesk", "RustDesk", "parsecd", "OneDrive", "GoogleDriveFS", "Dropbox",
        "msedgewebview2"
    };

    // Adı bunlarla başlayan fare/klavye/kulaklık yazılımlarının tüm parçaları da işaretsiz gelir.
    static readonly string[] VarsayilanIsaretsizOnek = { "SteelSeries", "Razer", "lghub", "Corsair", "iCUE", "HyperX", "Wooting" };

    static readonly string[] Basliklar = { "Program", "Durum", "İşlemci", "Bellek", "GPU", "VRAM" };

    static readonly string AyarDosyasi = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SergenPcTemizle", "secimler.txt");

    const string BilgiMetni = "İşaretli programlar \"Temizle\" ile kapanır. Açık pencereler ve oyunların parçaları işaretsiz gelir.\n" +
                              "Sağ tık → tek programı hemen kapat. Sütun başlığına tıkla → sırala.";

    readonly Gosterge gIslemci = new Gosterge("İŞLEMCİ", "");
    readonly Gosterge gGpu = new Gosterge("EKRAN KARTI", "");
    readonly Gosterge gRam = new Gosterge("RAM", "Fiziksel bellek");
    readonly Gosterge gVram = new Gosterge("VRAM", "Ekran kartı belleği");
    readonly Gosterge gSanal = new Gosterge("SANAL BELLEK", "RAM + sayfa dosyası · dolarsa programlar çöker");
    readonly Gosterge gIsi = new Gosterge("ISI", "");
    readonly Label lblBilgi = new Label();
    readonly KoyuListe liste = new KoyuListe();
    readonly Siralayici siralayici = new Siralayici();
    readonly Button btnTemizle = new Button();
    readonly Label lblDurum = new Label();
    readonly Label lblImza = new Label();
    readonly Button btnSanal = new Button();
    readonly Button btnOto = new Button();
    readonly Button btnFull = new Button();
    readonly Label lblIs = new Label();
    readonly DiskSekmesi diskSekmesi = new DiskSekmesi { Visible = false };
    readonly SurucuSekmesi surucuSekmesi = new SurucuSekmesi { Visible = false };
    readonly BaslangicSekmesi baslangicSekmesi = new BaslangicSekmesi { Visible = false };
    readonly WindowsSekmesi windowsSekmesi = new WindowsSekmesi { Visible = false };
    readonly TurboSekmesi turboSekmesi = new TurboSekmesi { Visible = false };
    readonly SaglikSekmesi saglikSekmesi;
    readonly System.Windows.Forms.Timer zamanlayici = new System.Windows.Forms.Timer();
    readonly BellekBekcisi bekci;

    // true: pencere ilk açılışta gösterilmez (Program.Main "--arkaplan"); yalnızca tepsi simgesi çalışır.
    // Load olayı (ilk ölçüm, sürücü kontrolü) pencere ilk kez gösterilene kadar ertelenir.
    public bool GizliBasla;

    Olcer olcer;
    readonly object olcerKilit = new object();
    bool olcuyor, temizleniyor, dolduruluyor, yuklendi;
    HashSet<int> oyunPidleri = new HashSet<int>();
    readonly Dictionary<string, ListViewItem> ogeler = new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> hatirlanan = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public static bool Korunuyor(string ad)
    {
        if (Korunan.Contains(ad)) return true;
        foreach (var onek in KorunanOnek)
            if (ad.StartsWith(onek, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static readonly Dictionary<string, bool> yolKorumasi = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    // Ada ek olarak dosyaya bakan kurallar: üretici yazılımı (yayıncıdan) ve WinDivert taşıyan bağlantı araçları.
    // Sonuç yol başına saklanır (sürüm bilgisi her ölçümde yeniden okunmasın). Birden çok iş parçacığından çağrılır.
    public static bool Korunuyor(string ad, string yol)
    {
        if (Korunuyor(ad)) return true;
        if (string.IsNullOrEmpty(yol)) return UreticiYazilimi(ad, null);
        bool sonuc;
        lock (yolKorumasi) if (yolKorumasi.TryGetValue(yol, out sonuc)) return sonuc;
        string yayinci = null;
        try { yayinci = FileVersionInfo.GetVersionInfo(yol).CompanyName; } catch { }
        sonuc = UreticiYazilimi(ad, yayinci) || WinDivertTasiyor(yol);
        lock (yolKorumasi) yolKorumasi[yol] = sonuc;
        return sonuc;
    }

    // Yayıncı biliniyorsa yalnızca ona bakılır; boşsa exe adı öneki yedektir.
    public static bool UreticiYazilimi(string ad, string yayinci)
    {
        if (!string.IsNullOrWhiteSpace(yayinci))
            return UreticiYayincilari.Any(y => yayinci.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0);
        return !string.IsNullOrEmpty(ad) && UreticiOnek.Any(o => ad.StartsWith(o, StringComparison.OrdinalIgnoreCase));
    }

    // Exe ile aynı klasörde WinDivert sürücüsü var mı: DPI aşma / VPN / paket yönlendirme aracıdır (goodbyedpi,
    // zapret ve adı bilinmeyen benzerleri); kapatılırsa bağlantı kopar.
    public static bool WinDivertTasiyor(string yol)
    {
        try
        {
            if (string.IsNullOrEmpty(yol)) return false;
            string k = Path.GetDirectoryName(yol);
            if (string.IsNullOrEmpty(k)) return false;
            foreach (var d in new[] { "WinDivert.dll", "WinDivert64.sys", "WinDivert32.sys" })
                if (File.Exists(Path.Combine(k, d))) return true;
        }
        catch { }
        return false;
    }

    public AnaForm()
    {
        Text = "Sergen PC Temizle v" + Surum.Metin + "  ·  by Sergen";
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Renk.Arka;
        ForeColor = Renk.Yazi;
        ClientSize = new Size(1000, 830);
        StartPosition = FormStartPosition.CenterScreen;
        EkranaSigdir(Screen.FromPoint(Cursor.Position));
        Icon = Logo.Simge();

        // Bekçi: tepsi simgesi, bellek uyarıları, sızıntı izleme. Pencere kapalıyken de çalışır.
        bekci = new BellekBekcisi(this, delegate { OtomatikOptimize_Click(this, EventArgs.Empty); }, KisayolOptimize);
        // Turbo sekmesinin oyun profili: oyun sırasında kapanmış bir önceki oturumun güç modu önce geri yazılır.
        OyunProfili.AcilistaGeriYukle();
        bekci.Profil = OyunProfili.Ornek = new OyunProfili(OndeGpuKullaniyor, delegate { return KisayolIsi(true); });
        var guncelleyici = new Guncelleyici(this, bekci);
        FormClosed += delegate { guncelleyici.Dispose(); };
        saglikSekmesi = new SaglikSekmesi(bekci) { Visible = false };

        var ust = new TableLayoutPanel { Dock = DockStyle.Top, Height = 214, ColumnCount = 2, RowCount = 3, Padding = new Padding(8, 8, 8, 0) };
        ust.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        ust.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 3; i++) ust.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));
        ust.Controls.Add(gIslemci, 0, 0); ust.Controls.Add(gGpu, 1, 0);
        ust.Controls.Add(gRam, 0, 1);     ust.Controls.Add(gVram, 1, 1);
        ust.Controls.Add(gSanal, 0, 2);   ust.Controls.Add(gIsi, 1, 2);

        lblBilgi.Dock = DockStyle.Top;
        lblBilgi.Height = 48;
        lblBilgi.Padding = new Padding(14, 6, 12, 0);
        lblBilgi.ForeColor = Renk.Soluk;
        lblBilgi.Text = BilgiMetni;

        foreach (var b in Basliklar) liste.Columns.Add(b);
        liste.Columns[1].Width = 128;
        for (int i = 2; i < 6; i++) { liste.Columns[i].TextAlign = HorizontalAlignment.Right; liste.Columns[i].Width = 78; }
        liste.Columns[3].Width = 92;
        liste.Dock = DockStyle.Fill;
        liste.ListViewItemSorter = siralayici;
        liste.Resize += delegate { SutunlariAyarla(); };
        liste.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
        {
            if (!dolduruluyor) hatirlanan[((Grup)e.Item.Tag).Ad] = e.Item.Checked;
        };
        liste.ColumnClick += delegate(object s, ColumnClickEventArgs e)
        {
            if (siralayici.Sutun == e.Column) siralayici.Azalan = !siralayici.Azalan;
            else { siralayici.Sutun = e.Column; siralayici.Azalan = e.Column > 1; }
            BasliklariYaz();
            liste.Sort();
        };
        BasliklariYaz();

        var menu = new ContextMenuStrip();
        var mKapat = new ToolStripMenuItem("Bu programı hemen kapat");
        mKapat.Click += async delegate
        {
            var secili = liste.SelectedItems.Cast<ListViewItem>().Select(o => (Grup)o.Tag).ToList();
            if (secili.Count > 0) await Kapat(secili, false);
        };
        menu.Items.Add(mKapat);
        menu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = liste.SelectedItems.Count == 0 || temizleniyor;
        };
        liste.ContextMenuStrip = menu;

        var orta = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        orta.Controls.Add(liste);

        var alt = new Panel { Dock = DockStyle.Bottom, Height = 100, Padding = new Padding(12, 8, 12, 10) };
        btnTemizle.Text = "Temizle";
        btnTemizle.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
        btnTemizle.BackColor = Renk.Vurgu;
        btnTemizle.ForeColor = Color.White;
        btnTemizle.FlatStyle = FlatStyle.Flat;
        btnTemizle.FlatAppearance.BorderSize = 0;
        btnTemizle.Dock = DockStyle.Fill;
        btnTemizle.Cursor = Cursors.Hand;
        btnTemizle.Click += Temizle_Click;
        lblDurum.Dock = DockStyle.Fill;
        lblDurum.TextAlign = ContentAlignment.MiddleLeft;
        lblDurum.ForeColor = Renk.Soluk;
        lblDurum.Text = "Ölçülüyor...";
        lblImza.Font = new Font("Segoe UI", 9f, FontStyle.Italic);   // yerleşimi sekme şeridindeki imza bloğunda
        lblImza.ForeColor = Color.FromArgb(239, 83, 80);
        lblImza.Text = "Sergen tarafından yapıldı · çaylar senden";
        var satir = new Panel { Dock = DockStyle.Bottom, Height = 32 };
        satir.Controls.Add(lblDurum);
        btnSanal.Text = "Sanal bellek ayarı";
        btnSanal.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        btnSanal.BackColor = Renk.Panel;
        btnSanal.ForeColor = Renk.Yazi;
        btnSanal.FlatStyle = FlatStyle.Flat;
        btnSanal.FlatAppearance.BorderColor = Renk.Cizgi;
        btnSanal.Dock = DockStyle.Right;
        btnSanal.Width = 200;
        btnSanal.Cursor = Cursors.Hand;
        btnSanal.Click += SanalBellek_Click;
        alt.Controls.Add(btnTemizle);
        alt.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        alt.Controls.Add(btnSanal);
        alt.Controls.Add(satir);

        var sistem = new Panel { Dock = DockStyle.Fill };
        sistem.Controls.Add(orta);
        sistem.Controls.Add(lblBilgi);
        sistem.Controls.Add(ust);
        sistem.Controls.Add(alt);
        // Sekme şeridi: koyu temada TabControl çirkin durduğu için düz düğmeler; yanında tek tuşluk optimize düğmeleri.
        var serit = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(12, 8, 12, 2) };
        var sSistem = new Button { Text = "Sistem" };
        var sDisk = new Button { Text = "Disk" };
        var sSurucu = new Button { Text = "Sürücü" };
        var sSaglik = new Button { Text = "Sağlık" };
        var sBaslangic = new Button { Text = "Başlangıç" };
        var sWindows = new Button { Text = "Windows" };
        var sTurbo = new Button { Text = "Turbo" };
        var sekmeler = new[] { sSistem, sDisk, sSurucu, sSaglik, sBaslangic, sWindows, sTurbo };
        var paneller = new Control[] { sistem, diskSekmesi, surucuSekmesi, saglikSekmesi, baslangicSekmesi, windowsSekmesi, turboSekmesi };
        bool surucuYeni = false;
        int acik = 0;
        Action boya = delegate
        {
            for (int i = 0; i < sekmeler.Length; i++)
            {
                sekmeler[i].BackColor = i == acik ? Renk.Panel : Renk.Arka;
                sekmeler[i].ForeColor = i == acik ? Renk.Yazi : Renk.Soluk;
            }
            sSurucu.Text = surucuYeni ? "Sürücü •" : "Sürücü";
            if (surucuYeni) sSurucu.ForeColor = Renk.Turuncu;
        };
        Action<int> sec = delegate(int n)
        {
            acik = n;
            for (int i = 0; i < paneller.Length; i++) paneller[i].Visible = i == n;
            boya();
            if (n == 1) diskSekmesi.Gosterildi();
            if (n == 2) surucuSekmesi.Gosterildi();
            if (n == 3) saglikSekmesi.Gosterildi();
            if (n == 4) baslangicSekmesi.Gosterildi();
            if (n == 5) windowsSekmesi.Gosterildi();
            if (n == 6) turboSekmesi.Gosterildi();
        };
        surucuSekmesi.GuncellemeDurumu += delegate(bool yeni) { surucuYeni = yeni; boya(); };
        btnOto.Text = "Otomatik optimize";
        btnOto.BackColor = Color.FromArgb(46, 125, 50);
        btnOto.Click += OtomatikOptimize_Click;
        btnFull.Text = "Full optimize";
        btnFull.BackColor = Renk.Vurgu;
        btnFull.Click += FullOptimize_Click;
        lblIs.Dock = DockStyle.Fill;
        lblIs.TextAlign = ContentAlignment.MiddleLeft;
        lblIs.Padding = new Padding(10, 0, 0, 0);
        lblIs.ForeColor = Renk.Turuncu;
        lblIs.AutoEllipsis = true;
        serit.Controls.Add(lblIs);
        var araliklar = new List<Panel>();
        foreach (var b in new[] { btnFull, null, btnOto, null, sTurbo, sWindows, sBaslangic, sSaglik, sSurucu, sDisk, sSistem })
        {
            if (b == null) { var ara = new Panel { Dock = DockStyle.Left, Width = 8 }; araliklar.Add(ara); serit.Controls.Add(ara); continue; }
            b.Dock = DockStyle.Left;
            bool optimize = b == btnOto || b == btnFull;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Font = new Font("Segoe UI", optimize ? 9.5f : 10f, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
            if (b == btnOto || b == btnFull) b.ForeColor = Color.White;
            serit.Controls.Add(b);
        }
        for (int i = 0; i < sekmeler.Length; i++)
        {
            int n = i;
            sekmeler[i].Click += delegate { sec(n); };
        }
        // Logo şeridin en solunda (Dock=Left'te en son eklenen en solda durur).
        var logoKutusu = new PictureBox
        {
            Dock = DockStyle.Left, Width = 44, Padding = new Padding(0, 1, 10, 1),
            SizeMode = PictureBoxSizeMode.Zoom, Image = Logo.Resim()
        };
        new ToolTip().SetToolTip(logoKutusu, "Hakkında");
        logoKutusu.Cursor = Cursors.Hand;
        logoKutusu.Click += delegate { Hakkinda.Goster(); };
        serit.Controls.Add(logoKutusu);
        // Sağ uçta iki satır: imza ve altında tıklanabilir "v… · Hakkında" (güncelleme denetimi de orada). Blok imzadan
        // geniş değildir, her pencere boyunda görünür. Bir iş sürerken ikisi birden iş yazısına (lblIs) yer bırakır.
        var imzaBlok = new Panel { Dock = DockStyle.Right };
        lblImza.Dock = DockStyle.Top;
        lblImza.AutoSize = false;
        lblImza.Height = 19;
        lblImza.Padding = Padding.Empty;
        lblImza.TextAlign = ContentAlignment.BottomRight;
        var lblHakkinda = new Label
        {
            Dock = DockStyle.Top, Height = 17, TextAlign = ContentAlignment.TopRight, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 8.25f), ForeColor = Renk.Soluk, Text = "v" + Surum.Metin + " · Hakkında"
        };
        new ToolTip().SetToolTip(lblHakkinda, "Sürüm, özellikler ve güncellemeleri denetle");
        lblHakkinda.MouseEnter += delegate { lblHakkinda.ForeColor = Renk.Yazi; };
        lblHakkinda.MouseLeave += delegate { lblHakkinda.ForeColor = Renk.Soluk; };
        lblHakkinda.Click += delegate { Hakkinda.Goster(); };
        imzaBlok.Controls.Add(lblHakkinda);
        imzaBlok.Controls.Add(lblImza);   // Dock=Top: son eklenen en üstte
        serit.Controls.Add(imzaBlok);
        // Şerit genişliği: sekmeler ve optimize düğmeleri yazılarına göre; dar pencerede önce boşluklar, sonra
        // sekmeler daralır. İmza hep sağda kalır; yalnızca bir iş sürerken yerini iş yazısına (lblIs) bırakır.
        Action seridiYerlestir = delegate
        {
            // Sürücü sekmesi "•" işaretinin yerini hep ayırır; yeni sürüm çıkınca genişlik zıplamaz.
            var yazi = sekmeler.Select(b => TextRenderer.MeasureText(b == sSurucu ? "Sürücü •" : b.Text, b.Font).Width).ToArray();
            Func<int, int, int> genislik = (y, p) => Math.Max(y + p, p >= 24 ? 72 : 0);
            int otoYazi = TextRenderer.MeasureText(btnOto.Text, btnOto.Font).Width;
            int fullYazi = TextRenderer.MeasureText(btnFull.Text, btnFull.Font).Width;
            int blok = Math.Max(TextRenderer.MeasureText(lblImza.Text, lblImza.Font).Width,
                                TextRenderer.MeasureText(lblHakkinda.Text, lblHakkinda.Font).Width) + 6;
            imzaBlok.Width = blok;
            int imza = imzaBlok.Visible ? blok + 4 : 0;
            int yer = serit.ClientSize.Width - serit.Padding.Horizontal - logoKutusu.Width - imza;   // sekmeler + optimize + iki boşluk
            // Dar pencerede sırayla: boşluklar (en az 2), sekme payları (en az 8), optimize düğmelerinin payları (en az 10) daralır.
            // İmza hiç kırpılmaz (kullanıcı kararı); 7 sekme 980 px'lik en dar pencereye de sığar.
            int ara = 8, pay = 24, optPay = 28;
            Func<int> gereken = () => yazi.Sum(y => genislik(y, pay)) + 2 * ara + otoYazi + fullYazi + 2 * optPay;
            while (gereken() > yer)
            {
                if (ara > 4) ara = 4;
                else if (ara > 2) ara = 2;
                else if (pay > 8) pay -= 2;
                else if (optPay > 10) optPay -= 2;
                else break;
            }
            for (int i = 0; i < sekmeler.Length; i++) sekmeler[i].Width = genislik(yazi[i], pay);
            btnOto.Width = otoYazi + optPay;
            btnFull.Width = fullYazi + optPay;
            foreach (var a in araliklar) a.Width = ara;
        };
        serit.Resize += delegate { seridiYerlestir(); };
        // İlk gösterimde yazı ölçüleri / pencere boyu kesinleşince bir kez daha (yoksa imza ilk açılışta kırpılabiliyor).
        Shown += delegate { seridiYerlestir(); };
        lblIs.TextChanged += delegate
        {
            imzaBlok.Visible = lblIs.Text.Length == 0;
            seridiYerlestir();
        };
        sec(0);

        Controls.Add(sistem);
        Controls.Add(diskSekmesi);
        Controls.Add(surucuSekmesi);
        Controls.Add(saglikSekmesi);
        Controls.Add(baslangicSekmesi);
        Controls.Add(windowsSekmesi);
        Controls.Add(turboSekmesi);
        Controls.Add(serit);
        seridiYerlestir();

        zamanlayici.Interval = 2000;
        zamanlayici.Tick += delegate { Olc(); };

        SecimleriYukle();
        // Sürücü kontrolü açılışta bir kez arka planda yapılır; yeni sürüm varsa sekme "Sürücü •" olur.
        // "--arkaplan" ile gizli açılışta Load, pencere ilk kez gösterilince çalışır.
        Load += delegate { yuklendi = true; Olc(); zamanlayici.Start(); surucuSekmesi.Kontrol(); };
        // Pencere gizliyken (tepside) 2 sn'lik ölçüm boşuna çalışmasın; bekçinin kendi zamanlayıcıları sürer.
        VisibleChanged += delegate
        {
            if (!yuklendi) return;
            if (Visible) { Olc(); zamanlayici.Start(); }
            else zamanlayici.Stop();
        };
        FormClosing += delegate(object s, FormClosingEventArgs e)
        {
            // Arka planda çalışma açıksa pencere yalnızca gizlenir (tepsiye iner); bekçi çalışmaya devam eder.
            if (bekci != null && bekci.KapanisiYakala(e)) { SecimleriKaydet(); return; }
            SecimleriKaydet();
        };
        // Durdurma işleri FormClosed'da: Windows kapanışında FormClosing ilk aşamada gelir, kapanış iptal edilirse
        // (ör. "Kaydet?" sorusunda İptal) program ayakta kalır; zamanlayıcı, indirme ve bekçi çalışmaya devam etmeli.
        // Bekçi kendini FormClosed'da kapatır (BellekBekcisi kurucusu).
        FormClosed += delegate { zamanlayici.Stop(); surucuSekmesi.Kapaniyor(); };
    }

    // Pencereyi ekranın çalışma alanına sığdırır (1366x768 dizüstünde görev çubuğunun altına taşmasın).
    void EkranaSigdir(Screen ekran)
    {
        var alan = ekran.WorkingArea;
        // En dar hâlde de 6 sekme + 2 optimize düğmesi + imza şeride sığar (~960); en kısa hâlde Sağlık'ta liste görünür.
        MinimumSize = new Size(Math.Min(980, alan.Width), Math.Min(680, alan.Height));
        Size = new Size(Math.Min(Width, alan.Width), Math.Min(Height, alan.Height));
    }

    // Tek örnek: ikinci kez açılan exe sinyal gönderir (Program.Main); bu, iş parçacığı havuzundan çağrılır.
    public void GosterIstegi()
    {
        try
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { if (bekci != null) bekci.Goster(); else Show(); }));
        }
        catch { }
    }

    // "--arkaplan": ilk görünür olma isteği yutulur (pencere tutamacı yine oluşur ki BeginInvoke çalışsın).
    // Sonraki Show/Goster normal çalışır; Load da o zaman tetiklenir.
    protected override void SetVisibleCore(bool value)
    {
        if (GizliBasla)
        {
            GizliBasla = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnLoad(EventArgs e)
    {
        // Pencere hangi ekranda açılıyorsa ona göre yeniden sığdır, sonra (StartPosition) ortala.
        EkranaSigdir(Screen.FromPoint(Cursor.Position));
        base.OnLoad(e);
        var alan = Screen.FromControl(this).WorkingArea;
        if (!alan.Contains(Bounds))
            Location = new Point(alan.Left + Math.Max(0, (alan.Width - Width) / 2), alan.Top + Math.Max(0, (alan.Height - Height) / 2));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int koyu = 1; Yerel.DwmSetWindowAttribute(Handle, 20 /* koyu başlık çubuğu */, ref koyu, 4); } catch { }
    }

    async void Olc()
    {
        if (olcuyor) return;
        olcuyor = true;
        try
        {
            var o = await Task.Run(() =>
            {
                lock (olcerKilit)
                {
                    if (olcer == null) olcer = new Olcer();
                    return olcer.Topla();
                }
            });
            Goster(o);
            if (!temizleniyor) ListeyiGuncelle(o.Gruplar);
        }
        catch { }
        finally { olcuyor = false; }
    }

    void Goster(Olcum o)
    {
        const double GB = 1073741824.0;
        var m = o.Bellek;

        if (string.IsNullOrEmpty(gIslemci.Alt) && olcer != null)
        {
            gIslemci.Alt = olcer.CpuAdi;
            gGpu.Alt = olcer.GpuAdi;
        }

        if (o.CpuYuzde >= 0) gIslemci.Ayarla(string.Format("%{0:0}", o.CpuYuzde), o.CpuYuzde / 100);
        if (o.GpuYuzde >= 0) gGpu.Ayarla(string.Format("%{0:0}", o.GpuYuzde), o.GpuYuzde / 100);

        double ramTop = m.ullTotalPhys / GB, ramKul = (m.ullTotalPhys - m.ullAvailPhys) / GB;
        gRam.Ayarla(string.Format("{0:0.0} / {1:0.0} GB", ramKul, ramTop), ramKul / ramTop);

        double sTop = m.ullTotalPageFile / GB, sKul = (m.ullTotalPageFile - m.ullAvailPageFile) / GB;
        gSanal.Ayarla(string.Format("{0:0.0} / {1:0.0} GB", sKul, sTop), sKul / sTop);

        if (o.VramKullanilan >= 0 && o.VramToplam > 0)
            gVram.Ayarla(string.Format("{0:0.0} / {1:0.0} GB", o.VramKullanilan / GB, o.VramToplam / GB), (double)o.VramKullanilan / o.VramToplam);
        else if (o.VramKullanilan >= 0)
            gVram.Ayarla(string.Format("{0:0.0} GB", o.VramKullanilan / GB), 0);

        var parcalar = new List<string>();
        if (o.CpuIsi >= 0) parcalar.Add(string.Format("İşlemci ~{0:0}°", o.CpuIsi));
        if (o.GpuIsi >= 0) parcalar.Add(string.Format("GPU {0:0}°", o.GpuIsi));
        gIsi.Alt = o.CpuIsi >= 0 ? "İşlemci değeri yaklaşıktır (ACPI)" : (o.GpuIsi >= 0 ? "İşlemci ısısı bu bilgisayarda okunamıyor" : "Bu bilgisayarda okunamıyor");
        gIsi.Ayarla(parcalar.Count > 0 ? string.Join("  ", parcalar) : "–", Math.Max(o.CpuIsi, o.GpuIsi) / 100);

        // Ekran kartını yoğun kullananlar (oyunlar) Temizle'de bellekten atılmaz; takılma yapar.
        var oyun = new HashSet<int>();
        foreach (var g in o.Gruplar) if (g.Gpu >= 10) foreach (var pid in g.Pidler) oyun.Add(pid);
        oyunPidleri = oyun;

        // Uyarılar genel; oyuna özel ipucu yalnızca şu an ekran kartını yoğun kullanan bir program varsa.
        if (sKul / sTop >= 0.9)
        {
            lblBilgi.ForeColor = Renk.Kirmizi;
            lblBilgi.Text = "Sanal bellek dolmak üzere! Programlar (oyunlar dahil) \"bellek yetersiz\" diye kapanabilir." +
                            (oyun.Count > 0 ? " Oyundaysan ilerlemeni kaydet." : "") + "\n" +
                            "Şimdilik Temizle'ye bas; sık oluyorsa bir kere \"Sanal belleği büyüt\"e bas.";
        }
        else if (ramKul / ramTop >= 0.9)
        {
            lblBilgi.ForeColor = Renk.Turuncu;
            lblBilgi.Text = "RAM neredeyse dolu; bilgisayar" + (oyun.Count > 0 ? " ve açık oyun" : "") +
                            " takılabilir. Gereksiz programları işaretleyip Temizle'ye bas.\n" +
                            "Sağ tık → tek programı hemen kapat. Sütun başlığına tıkla → sırala.";
        }
        else
        {
            lblBilgi.ForeColor = Renk.Soluk;
            lblBilgi.Text = BilgiMetni;
        }
    }

    void ListeyiGuncelle(List<Grup> gruplar)
    {
        dolduruluyor = true;
        liste.BeginUpdate();
        try
        {
            var gorulen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in gruplar)
            {
                gorulen.Add(g.Ad);
                ListViewItem oge;
                if (!ogeler.TryGetValue(g.Ad, out oge))
                {
                    string ad = g.Gorunen.Equals(g.Ad, StringComparison.OrdinalIgnoreCase) ? g.Ad : g.Gorunen + "  (" + g.Ad + ")";
                    oge = new ListViewItem(ad) { Tag = g };
                    for (int i = 1; i < Basliklar.Length; i++) oge.SubItems.Add("");
                    bool isaretli;
                    if (!hatirlanan.TryGetValue(g.Ad, out isaretli))
                        isaretli = !g.Pencereli && !g.Bagli && !g.Konsol && !VarsayilanIsaretsiz.Contains(g.Ad)
                                   && !VarsayilanIsaretsizOnek.Any(o => g.Ad.StartsWith(o, StringComparison.OrdinalIgnoreCase));
                    oge.Checked = isaretli;
                    Yaz(oge, g);
                    liste.Items.Add(oge);
                    ogeler[g.Ad] = oge;
                }
                else
                {
                    oge.Tag = g;
                    Yaz(oge, g);
                }
            }
            foreach (var ad in ogeler.Keys.Where(k => !gorulen.Contains(k)).ToList())
            {
                liste.Items.Remove(ogeler[ad]);
                ogeler.Remove(ad);
            }
            liste.Sort();
        }
        finally
        {
            liste.EndUpdate();
            dolduruluyor = false;
        }
        SutunlariAyarla();
        if (!temizleniyor && lblDurum.Tag == null)
            lblDurum.Text = string.Format("{0} program listelendi, {1} tanesi işaretli.",
                liste.Items.Count, liste.Items.Cast<ListViewItem>().Count(i => i.Checked));
    }

    static void Yaz(ListViewItem oge, Grup g)
    {
        Ayarla(oge.SubItems[1], g.Pencereli ? "Açık pencere" : g.Bagli ? "Uygulama parçası" : g.Konsol ? "Konsol işlemi" : "Arka plan");
        Ayarla(oge.SubItems[2], g.Cpu >= 0.05 ? string.Format("%{0:0.0}", g.Cpu) : "–");
        Ayarla(oge.SubItems[3], Boyut(g.Bellek));
        Ayarla(oge.SubItems[4], g.Gpu >= 0.5 ? string.Format("%{0:0}", g.Gpu) : "–");
        Ayarla(oge.SubItems[5], g.Vram >= 1048576 ? Boyut(g.Vram) : "–");
    }

    static void Ayarla(ListViewItem.ListViewSubItem s, string metin) { if (s.Text != metin) s.Text = metin; }

    public static string Boyut(long bayt)
    {
        return bayt >= 1073741824 ? string.Format("{0:0.0} GB", bayt / 1073741824.0) : string.Format("{0:N0} MB", bayt / 1048576);
    }

    void BasliklariYaz()
    {
        for (int i = 0; i < Basliklar.Length; i++)
            liste.Columns[i].Text = Basliklar[i] + (i == siralayici.Sutun ? (siralayici.Azalan ? " ▼" : " ▲") : "");
        liste.Invalidate();
    }

    // Program sütunu kalan genişliği doldurur (dikey kaydırma çubuğu hesaba katılır).
    void SutunlariAyarla()
    {
        int kalan = liste.ClientSize.Width;
        for (int i = 1; i < liste.Columns.Count; i++) kalan -= liste.Columns[i].Width;
        if (kalan > 140 && liste.Columns[0].Width != kalan) liste.Columns[0].Width = kalan;
    }

    // Sanal bellek = RAM + sayfa dosyası. Sayfa dosyası küçük ya da kapalıysa programlar "bellek yetersiz" der;
    // sistem sürücüsünde (SanalBellek.Surucu) RAM'e göre boyutlanan (bkz. SanalBellek.Hedef) bir sayfa dosyası ayarlanır.
    void SanalBellek_Click(object sender, EventArgs e)
    {
        SanalBellekAyarla();
    }

    // Kendini yönetici olarak argümanlarla çalıştırır; başarılıysa true, değilse kullanıcıya söyleyip false.
    bool YoneticiOlarakCalistir(string argumanlar, string baslik)
    {
        int sonuc;
        try
        {
            var bilgi = new ProcessStartInfo(Application.ExecutablePath, argumanlar) { UseShellExecute = true, Verb = "runas" };
            using (var p = Process.Start(bilgi)) { p.WaitForExit(); sonuc = p.ExitCode; }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "Yönetici izni verilmedi, hiçbir şey değişmedi.", baslik, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (sonuc != 0)
        {
            MessageBox.Show(this, "Ayar yazılamadı, hiçbir şey değişmedi.", baslik, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        return true;
    }

    void YenidenBaslatSor(string mesaj, string baslik)
    {
        if (MessageBox.Show(this, mesaj + "\n\nŞimdi yeniden başlatılsın mı? (Önce açık işlerini kaydet.)", baslik,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo("shutdown", "/r /t 10 /c \"Sergen PC Temizle: sanal bellek ayari icin yeniden baslatiliyor\"")
            {
                UseShellExecute = false, CreateNoWindow = true
            });
        }
        catch { }
    }

    // Sanal bellek büyütülmeli mi; büyütülecekse boyutlar (MB). Gerek yoksa ya da diskte yer yoksa false ve sebebi.
    static bool SanalHedef(out long ilk, out long enCok, out string neden)
    {
        ilk = enCok = 0;
        neden = null;
        var satirlar = SanalBellek.Oku();
        int hedefIlk, hedefEnCok;
        var m = Yerel.Bellek();
        SanalBellek.Hedef(m.ullTotalPhys, out hedefIlk, out hedefEnCok);
        // Windows yönetiyorsa kayıt defterinde boyut yok; şu anki gerçek boyut (sınır − RAM) hedeften büyükse
        // yeni ayar sanal belleği küçültür, dokunma.
        long suankiMB = m.ullTotalPageFile > m.ullTotalPhys ? (long)((m.ullTotalPageFile - m.ullTotalPhys) / 1048576) : 0;
        if (SanalBellek.YedekVar || SanalBellek.ElleIlkMB(satirlar) >= hedefIlk || suankiMB >= hedefIlk)
        {
            neden = "Sayfa dosyası zaten yeterince büyük, değiştirmeye gerek yok.";
            return false;
        }
        long bosMB;
        try { bosMB = new DriveInfo(SanalBellek.Surucu).AvailableFreeSpace / 1048576; } catch { bosMB = 0; }
        ilk = Math.Min(hedefIlk, bosMB - 10240) / 1024 * 1024;   // diskte en az 10 GB boş kalsın
        if (ilk < 4096)
        {
            neden = string.Format("{0} diskinde yeterli boş yer yok ({1:0.0} GB). Önce en az 15 GB yer aç.", SanalBellek.Surucu, bosMB / 1024.0);
            return false;
        }
        enCok = Math.Max(ilk, Math.Min(hedefEnCok, bosMB - 5120) / 1024 * 1024);
        return true;
    }

    // Kısa bilgi: iş yazısı yerinde 8 sn görünür (bir iş sürüyorsa yazılmaz; iş yazısını ezmesin).
    public void BilgiYaz(string metin)
    {
        if (temizleniyor || IsDisposed) return;
        lblIs.Text = metin;
        var t = new System.Windows.Forms.Timer { Interval = 8000 };
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            if (!temizleniyor && !IsDisposed && lblIs.Text == metin) lblIs.Text = "";
        };
        t.Start();
    }

    void OptimizeDurumu(bool calisiyor, string metin)
    {
        temizleniyor = calisiyor;
        btnOto.Enabled = btnFull.Enabled = btnTemizle.Enabled = btnSanal.Enabled = !calisiyor;
        lblIs.Text = metin ?? "";
    }

    // Soru sormadan, yönetici izni istemeden yalnızca risksiz işler: RAM boşaltma, geçici dosyalar
    // ve kapalı programların önbellekleri. Program kapatmaz, ayar değiştirmez.
    // Tepsi menüsünden de çağrılır (bekçi); pencere gizliyken sonuç balonla söylenir.
    async void OtomatikOptimize_Click(object sender, EventArgs e)
    {
        // Pencere gizliyse ya da önde tam ekran oyun varsa ileti kutusu açılmaz (oyunun önüne geçip onu küçültür).
        bool sessiz = !Visible || (bekci != null && bekci.OyunSuruyor);
        if (temizleniyor || !btnOto.Enabled || diskSekmesi.Calisiyor)
        {
            if (sessiz && bekci != null) bekci.Bildir("Otomatik optimize", "Şu an başka bir iş sürüyor; bitince yeniden dene.", ToolTipIcon.Info);
            return;
        }
        OptimizeDurumu(true, "Otomatik optimize çalışıyor...");
        var once = Yerel.Bellek();
        long diskOnce = DiskSekmesi.BosAlan();
        var atla = oyunPidleri;
        bool olcumEski = !zamanlayici.Enabled;
        await Task.Run(() =>
        {
            if (olcumEski) { var taze = TazeOyunPidleri(); if (taze != null) atla = taze; }
            CalismaKumeleriniBosalt(atla);
            foreach (var t in DiskTemizlik.Liste(false))
            {
                if (t.Yonetici || !t.Varsayilan || t.Ozel != null) continue;
                DiskTemizlik.Hesapla(t);
                if (t.AcikSurec == null) { try { DiskTemizlik.Temizle(t); } catch { } }
            }
            Thread.Sleep(800);
        });
        long ram = Math.Max(0, (long)Yerel.Bellek().ullAvailPhys - (long)once.ullAvailPhys);
        long disk = Math.Max(0, DiskSekmesi.BosAlan() - diskOnce);
        OptimizeDurumu(false, null);
        diskSekmesi.Yenile();
        Olc();

        string metin = string.Format("Bitti.\n\n•  RAM'de {0} boşaldı.\n•  Diskte {1} yer açıldı (geçici dosyalar ve kapalı programların önbellekleri).\n\n" +
                                     "Hiçbir program kapatılmadı, hiçbir ayara dokunulmadı.", Boyut(ram), Boyut(disk));
        long ilk, enCok; string neden;
        bool sanalKucuk = SanalHedef(out ilk, out enCok, out neden);
        bool oyunda = bekci != null && bekci.OyunSuruyor;   // iş sürerken oyuna geçilmiş olabilir
        if (bekci != null && (sessiz || oyunda || !Visible))
        {
            if (oyunda) Ses.Cal(SesTuru.Tamam);
            bekci.Bildir("Otomatik optimize bitti",
                string.Format("RAM'de {0} boşaldı, diskte {1} yer açıldı. Hiçbir program kapatılmadı.{2}", Boyut(ram), Boyut(disk),
                    sanalKucuk ? " Sanal bellek küçük: programı açıp \"Sanal belleği büyüt\"e bas." : ""),
                ToolTipIcon.Info);
            return;
        }
        if (sanalKucuk)
            metin += "\n\nİpucu: sanal bellek bu bilgisayar için küçük. \"Full optimize\" ya da \"Sanal belleği büyüt\" ile kalıcı olarak düzeltebilirsin.";
        MessageBox.Show(this, metin, "Otomatik optimize", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Seçenekleri tek pencerede sunar, seçilenleri sırayla yapar; yönetici gerekenler tek izinle.
    async void FullOptimize_Click(object sender, EventArgs e)
    {
        // Temizle ya da Disk sekmesindeki temizlik sürerken iki iş aynı programlara / klasörlere dokunmasın.
        if (temizleniyor || diskSekmesi.Calisiyor) return;
        var programlar = liste.Items.Cast<ListViewItem>().Where(o => o.Checked).Select(o => (Grup)o.Tag).ToList();
        long ilk, enCok; string neden;
        bool sanalGerekli = SanalHedef(out ilk, out enCok, out neden);
        string sanalAciklama = sanalGerekli
            ? string.Format("{0} sürücüsünde {1} GB sayfa dosyası (gerekirse {2} GB'a kadar); programların (oyunlar dahil) \"bellek yetersiz\" diye kapanmasını önler. Yeniden başlatınca geçerli olur.",
                SanalBellek.Surucu, ilk / 1024, enCok / 1024)
            : neden;
        long hazirdaBoyut = DiskTemizlik.HazirdaBoyutu();

        bool prog, ram, disk, derin, geri, sanal, hazirda;
        using (var f = new OptimizeFormu(programlar.Count, sanalAciklama, sanalGerekli, hazirdaBoyut))
        {
            if (f.ShowDialog(this) != DialogResult.OK) return;
            if (temizleniyor) return;   // pencere açıkken tepsiden Otomatik optimize başlamış olabilir
            prog = f.Programlar.Checked; ram = f.Ram.Checked; disk = f.Disk.Checked; derin = f.Derin.Checked;
            geri = f.GeriDonusum.Checked; sanal = f.Sanal.Checked; hazirda = f.Hazirda.Checked;
        }

        OptimizeDurumu(true, "Full optimize çalışıyor...");
        var rapor = new List<string>();
        var once = Yerel.Bellek();
        long diskOnce = DiskSekmesi.BosAlan();

        if (prog && programlar.Count > 0)
        {
            lblIs.Text = "Arka plan programları kapatılıyor...";
            int kapanan = 0, kalan = 0;
            await Task.Run(() => { foreach (var g in programlar) { if (GrubuKapat(g) == 1) kapanan++; else kalan++; } });
            rapor.Add(string.Format("{0} arka plan programı kapatıldı{1}.", kapanan, kalan > 0 ? string.Format(", {0} tanesi kapanmadı", kalan) : ""));
        }
        if (ram)
        {
            lblIs.Text = "RAM boşaltılıyor...";
            var atla = oyunPidleri;
            await Task.Run(() => { CalismaKumeleriniBosalt(atla); Thread.Sleep(800); });
        }

        var yoneticiIsleri = new List<string>();
        if (disk || geri)
        {
            lblIs.Text = "Gereksiz dosyalar temizleniyor...";
            var secilen = await Task.Run(() =>
            {
                var l = new List<Temizlik>();
                foreach (var t in DiskTemizlik.Liste(derin))
                {
                    bool istendi = t.Ozel == "geridonusum" ? geri : disk && t.Varsayilan;
                    if (!istendi) continue;
                    DiskTemizlik.Hesapla(t);
                    if (t.AcikSurec != null) continue;
                    l.Add(t);
                    if (!t.Yonetici) { try { DiskTemizlik.Temizle(t); } catch { } }
                }
                return l;
            });
            yoneticiIsleri.AddRange(secilen.Where(t => t.Yonetici).Select(t => t.Anahtar));
        }
        if (hazirda) yoneticiIsleri.Add("hazirda");
        if (sanal) yoneticiIsleri.Add(string.Format("sanal={0},{1}", ilk, enCok));

        bool yenidenBaslat = false;
        if (yoneticiIsleri.Count > 0)
        {
            lblIs.Text = derin ? "Windows kalıntıları temizleniyor; Windows'un aracı uzun sürebilir..." : "Windows kalıntıları temizleniyor...";
            var eskiSanal = SanalBellek.Oku();
            int kod = -1;
            try
            {
                var bilgi = new ProcessStartInfo(Application.ExecutablePath,
                    "--disk-temizle " + (derin ? "derin " : "normal ") + string.Join(" ", yoneticiIsleri))
                {
                    UseShellExecute = true, Verb = "runas"
                };
                var p = Process.Start(bilgi);
                await Task.Run(() => { p.WaitForExit(); kod = p.ExitCode; p.Dispose(); });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                rapor.Add("Yönetici izni verilmedi; Windows kalıntıları" + (sanal ? ", sanal bellek" : "") + (hazirda ? ", hazırda bekletme" : "") + " atlandı.");
            }
            if (kod == 0)
            {
                if (sanal)
                {
                    SanalBellek.Yedekle(eskiSanal);
                    rapor.Add(string.Format("Sanal bellek {0} GB'a büyütüldü (yeniden başlatınca geçerli; geri almak için \"Sanal bellek ayarı\" → önceki ayara dön).", ilk / 1024));
                    yenidenBaslat = true;
                }
                if (hazirda) rapor.Add("Hazırda bekletme kapatıldı (geri açmak: yönetici CMD'de powercfg /h on).");
            }
            else if (kod > 0) rapor.Add("Sanal bellek ayarı yazılamadı, değişmedi.");
        }

        long ramB = Math.Max(0, (long)Yerel.Bellek().ullAvailPhys - (long)once.ullAvailPhys);
        long diskB = Math.Max(0, DiskSekmesi.BosAlan() - diskOnce);
        if (prog || ram) rapor.Add(string.Format("RAM'de {0} boşaldı.", Boyut(ramB)));
        if (disk || geri || hazirda) rapor.Add(string.Format("Diskte {0} yer açıldı.", Boyut(diskB)));
        OptimizeDurumu(false, null);
        diskSekmesi.Yenile();
        Olc();

        string metin = "Full optimize bitti.\n\n" + string.Join("\n", rapor.Select(r => "•  " + r));
        // Pencere tepsideyken (oyun sırasında) görünmeyen pencereye bağlı soru açılmasın; balonla söylenir.
        if (!Visible && bekci != null)
        {
            bekci.Bildir("Full optimize bitti", (yenidenBaslat ? "Sanal bellek ayarlandı; yeniden başlatınca geçerli olur. " : "") +
                "Ayrıntı için programı aç.", ToolTipIcon.Info);
            return;
        }
        if (yenidenBaslat) YenidenBaslatSor(metin, "Full optimize");
        else MessageBox.Show(this, metin, "Full optimize", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Seçim penceresini açar: önerilen / hazır / elle boyut, Windows'a bırakma, varsa programdan önceki ayara dönüş.
    void SanalBellekAyarla()
    {
        const string Baslik = "Sanal bellek";
        var satirlar = SanalBellek.Oku();
        var m = Yerel.Bellek();
        int hedefIlk, hedefEnCok;
        SanalBellek.Hedef(m.ullTotalPhys, out hedefIlk, out hedefEnCok);
        long ramMB = (long)(m.ullTotalPhys / 1048576);
        long suankiMB = m.ullTotalPageFile > m.ullTotalPhys ? (long)((m.ullTotalPageFile - m.ullTotalPhys) / 1048576) : 0;
        // Eski sayfa dosyası yenisine yer açar; boş yere onun boyutu da eklenir.
        long bosMB = 0;
        try { bosMB = new DriveInfo(SanalBellek.Surucu).AvailableFreeSpace / 1048576; } catch { }
        try { bosMB += new FileInfo(SanalBellek.Surucu + "\\pagefile.sys").Length / 1048576; } catch { }
        string[] yedek = SanalBellek.YedekVar ? SanalBellek.YedekOku() : null;

        string secim;
        int ilk, enCok;
        using (var f = new SanalBellekFormu(satirlar, ramMB, suankiMB, bosMB, hedefIlk, hedefEnCok, yedek))
        {
            if (f.ShowDialog(this) != DialogResult.OK) return;
            secim = f.Secim; ilk = f.IlkMB; enCok = f.EnCokMB;
        }

        if (secim == "geri")
        {
            string argumanlar = "--sanal-bellek-geri" + string.Concat(yedek.Select(s => " \"" + s.Trim() + "\""));
            if (!YoneticiOlarakCalistir(argumanlar, Baslik)) return;
            SanalBellek.YedekSil();
            YenidenBaslatSor("Programdan önceki ayara dönüldü; bilgisayar yeniden başlayınca geçerli olacak.", Baslik);
            return;
        }
        string arg = secim == "otomatik" ? "--sanal-bellek-geri \"?:\\pagefile.sys\"" : string.Format("--sanal-bellek {0} {1}", ilk, enCok);
        if (!YoneticiOlarakCalistir(arg, Baslik)) return;
        SanalBellek.Yedekle(satirlar);
        YenidenBaslatSor("Sanal bellek ayarlandı; bilgisayar yeniden başlayınca geçerli olacak.\n" +
            "Vazgeçersen aynı düğmeden \"Programdan önceki ayara dön\"ü seçebilirsin.", Baslik);
    }

    async void Temizle_Click(object sender, EventArgs e)
    {
        var secili = liste.Items.Cast<ListViewItem>().Where(o => o.Checked).Select(o => (Grup)o.Tag).ToList();
        if (secili.Count > 0)
        {
            var adlar = string.Join("\n", secili.Take(12).Select(g => "•  " + g.Gorunen));
            if (secili.Count > 12) adlar += string.Format("\n•  ... ve {0} program daha", secili.Count - 12);
            var metin = string.Format("{0} program kapatılacak:\n\n{1}\n\nKaydedilmemiş işi olan pencereler kapanmaz, seni bekler. Devam edilsin mi?",
                secili.Count, adlar);
            if (MessageBox.Show(this, metin, "Sergen PC Temizle", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        }
        if (temizleniyor) return;   // soru açıkken tepsiden Otomatik optimize başlamış olabilir
        await Kapat(secili, true);
    }

    async Task Kapat(List<Grup> gruplar, bool bosalt)
    {
        OptimizeDurumu(true, null);   // optimize düğmeleri de kapanır: iki iş aynı programları kapatmasın
        lblDurum.Tag = "mesaj";
        lblDurum.ForeColor = Renk.Soluk;
        lblDurum.Text = bosalt ? "Temizleniyor..." : "Kapatılıyor...";
        SecimleriKaydet();

        var once = Yerel.Bellek();
        var atla = oyunPidleri;
        int kapanan = 0, acikKalan = 0, hata = 0;

        await Task.Run(() =>
        {
            foreach (var g in gruplar)
            {
                int sonuc = GrubuKapat(g);
                if (sonuc == 1) kapanan++; else if (sonuc == 0) acikKalan++; else hata++;
            }
            if (bosalt) CalismaKumeleriniBosalt(atla);
            Thread.Sleep(800);
        });

        var sonra = Yerel.Bellek();
        long ram = Math.Max(0, (long)sonra.ullAvailPhys - (long)once.ullAvailPhys) / 1048576;
        long sanal = Math.Max(0, (long)sonra.ullAvailPageFile - (long)once.ullAvailPageFile) / 1048576;

        var mesaj = string.Format("{0} program kapatıldı · RAM'de {1:N0} MB, sanal bellekte {2:N0} MB yer açıldı.", kapanan, ram, sanal);
        if (acikKalan > 0) mesaj += string.Format(" {0} program kaydetme bekliyor olabilir.", acikKalan);
        if (hata > 0) mesaj += string.Format(" {0} program kapatılamadı.", hata);
        lblDurum.ForeColor = Renk.Yesil;
        lblDurum.Text = mesaj;

        OptimizeDurumu(false, null);
        Olc();
        // Sonuç mesajı birkaç ölçüm boyunca ekranda kalsın.
        var bekle = new System.Windows.Forms.Timer { Interval = 8000 };
        bekle.Tick += delegate { bekle.Dispose(); lblDurum.Tag = null; lblDurum.ForeColor = Renk.Soluk; };
        bekle.Start();
    }

    // 1 = kapandı, 0 = penceresi açık kaldı (ör. "Kaydet?" sorusu), -1 = yetki/hata
    static int GrubuKapat(Grup g)
    {
        int oturum;
        using (var ben = Process.GetCurrentProcess()) oturum = ben.SessionId;
        var surecler = new List<Process>();
        try
        {
            foreach (var p in Process.GetProcessesByName(g.Ad))
            {
                if (p.SessionId == oturum) surecler.Add(p); else p.Dispose();
            }

            // Pencereli programa önce düzgünce "kapan" de; kaydedilmemiş iş varsa zorlamayız.
            var pencereliler = surecler.Where(p => !p.HasExited && p.MainWindowHandle != IntPtr.Zero).ToList();
            foreach (var p in pencereliler) p.CloseMainWindow();
            foreach (var p in pencereliler)
            {
                if (p.WaitForExit(2500)) continue;
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero) return 0; // hâlâ görünür pencere var: kullanıcıya bırak
            }

            // Kalanlar (arka plan / tepsiye küçülenler) zorla kapatılır.
            bool hepsi = true;
            foreach (var p in surecler)
            {
                try { if (!p.HasExited) p.Kill(); }
                catch { hepsi = false; }
            }
            return hepsi ? 1 : -1;
        }
        catch { return -1; }
        finally { foreach (var p in surecler) p.Dispose(); }
    }

    bool kisayolCalisiyor;

    // Genel kısayol ve tepsideki "RAM boşalt": oyundayken de güvenli. Yalnızca RAM boşaltılır; disk temizliği yok
    // (oyunda takılma yapmasın). Ekran kartını yoğun kullananlar ve öndeki program atlanır. Hiçbir pencere / ileti
    // kutusu açmaz: sonuç kısa bir sesle ve balonla söylenir. RAM boşaltmak sanal belleği (commit) düşürmez; sanal
    // bellek doluysa uyarı sesi çalar ve en çok kullanan oyun dışı programın adı söylenir. Hiçbir şeyi kapatmaz.
    async void KisayolOptimize() { await KisayolIsi(false); }

    // sessiz: Turbo sekmesinin oyun profili "oyun açılınca RAM boşalt" için; aynı iş ama ses, balon ve "meşgul" bildirimi yok
    // (başka iş sürüyorsa sessizce atlanır).
    async Task KisayolIsi(bool sessiz)
    {
        if (kisayolCalisiyor) return;
        if (temizleniyor || diskSekmesi.Calisiyor)
        {
            if (sessiz) return;
            Ses.Cal(SesTuru.Uyari);
            if (bekci != null) bekci.Bildir("RAM boşaltılamadı", "Şu an başka bir iş sürüyor; bitince yeniden dene.", ToolTipIcon.Info);
            return;
        }
        kisayolCalisiyor = true;
        OptimizeDurumu(true, sessiz ? "" : "RAM boşaltılıyor...");
        try
        {
            var once = Yerel.Bellek();
            int onde = TamEkran.OndekiSurec();
            var atla = new HashSet<int>(oyunPidleri);
            bool olcumEski = !zamanlayici.Enabled;
            EnCokKullanan suclu = null;
            double commit = 0;
            await Task.Run(() =>
            {
                if (olcumEski) { var taze = TazeOyunPidleri(); if (taze != null) atla.UnionWith(taze); }
                OndekiProgramiEkle(atla, onde);
                CalismaKumeleriniBosalt(atla);
                Thread.Sleep(500);
                var m = Yerel.Bellek();
                if (m.ullTotalPageFile > 0) commit = (double)(m.ullTotalPageFile - m.ullAvailPageFile) / m.ullTotalPageFile;
                if (commit >= 0.90) suclu = EnCokSanalBellek(atla);
#if DENEME
                if (suclu == null) suclu = EnCokSanalBellek(atla);   // sanal bellek dolu olmasa da sınansın
#endif
            });
            long ram = Math.Max(0, (long)Yerel.Bellek().ullAvailPhys - (long)once.ullAvailPhys);
            int yuzde = (int)Math.Floor(Math.Min(1, commit) * 100);
#if DENEME
            Deneme.Yaz(string.Format("kısayol işi: önde={0} tamEkran={1} oyunda={2} sanal=%{3} suçlu={4}", onde, TamEkran.Var(),
                bekci != null && bekci.OyunSuruyor, yuzde, suclu != null ? suclu.Ad + " " + Boyut(suclu.Bellek) : "-"));
#endif
            if (sessiz) { }
            else if (commit >= 0.90)
            {
                Ses.Cal(SesTuru.Uyari);
                string kim = suclu != null
                    ? string.Format(" En çok kullanan: {0} ({1}); gerekmiyorsa kapat.", Kisalt(suclu.Ad, 40), Boyut(suclu.Bellek))
                    : " Gereksiz programları kapat.";
                if (bekci != null)
                    bekci.Bildir(string.Format("Sanal bellek %{0} dolu!", yuzde),
                        string.Format("RAM'de {0} boşaldı ama RAM boşaltmak sanal belleği düşürmez.{1} Sık oluyorsa \"Sanal belleği büyüt\"e bas.",
                            Boyut(ram), kim),
                        ToolTipIcon.Warning);
            }
            else
            {
                Ses.Cal(SesTuru.Tamam);
                if (bekci != null)
                    bekci.Bildir("RAM boşaltıldı",
                        string.Format("RAM'de {0} boşaldı · sanal bellek %{1}. Oyun ve öndeki program atlandı, hiçbir şey kapatılmadı.",
                            Boyut(ram), yuzde),
                        ToolTipIcon.Info);
            }
        }
        catch { }
        finally
        {
            OptimizeDurumu(false, null);
            kisayolCalisiyor = false;
            if (Visible) Olc();
        }
    }

    // Oyun profili: öndeki programın (aynı adlı bütün parçalarıyla) ekran kartı kullanımı %10 ve üstü mü? Arayüz
    // iş parçacığında çağrılır, beklemez. Pencere açıksa 2 sn'lik ölçümün sonucu kullanılır; gizliyken (tepside)
    // ölçüm durduğu için bir ölçüm yapılır (iki çağrı arasındaki fark; ilk çağrı genelde 0 döner, profil 2 kontrol ister).
    Task<bool> OndeGpuKullaniyor(int pid)
    {
        if (zamanlayici.Enabled) return Task.FromResult(oyunPidleri.Contains(pid));
        return Task.Run(() =>
        {
            try
            {
                Olcum o;
                lock (olcerKilit) { if (olcer == null) olcer = new Olcer(); o = olcer.Topla(); }
                foreach (var g in o.Gruplar) if (g.Gpu >= 10 && g.Pidler.Contains(pid)) return true;
            }
            catch { }
            return false;
        });
    }

    static string Kisalt(string s, int n) { return s == null || s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

    // Pencere gizliyken 2 sn'lik ölçüm durduğu için oyun listesi eski olabilir: iki kısa ölçümle yenilenir
    // (ekran kartı yüzdesi iki örnek arasındaki farktan çıkar). Oyunun belleği boşaltılırsa oyun takılır.
    // Arka plan iş parçacığında çağrılır; ölçülemezse null.
    HashSet<int> TazeOyunPidleri()
    {
        try
        {
            lock (olcerKilit) { if (olcer == null) olcer = new Olcer(); olcer.Topla(); }
            Thread.Sleep(1000);
            Olcum o;
            lock (olcerKilit) o = olcer.Topla();
            var oyun = new HashSet<int>();
            foreach (var g in o.Gruplar) if (g.Gpu >= 10) foreach (var pid in g.Pidler) oyun.Add(pid);
            return oyun;
        }
        catch { return null; }
    }

    // Öndeki program (oyun menüdeyken ekran kartını az kullanabilir) ve aynı adlı bütün parçaları atlanır.
    static void OndekiProgramiEkle(HashSet<int> atla, int onde)
    {
        if (onde <= 0) return;
        atla.Add(onde);
        try
        {
            string ad;
            using (var p = Process.GetProcessById(onde)) ad = p.ProcessName;
            foreach (var p in Process.GetProcessesByName(ad)) using (p) atla.Add(p.Id);
        }
        catch { }
    }

    class EnCokKullanan { public string Ad; public long Bellek; }

    // Sanal belleği (özel / commit bellek) en çok kullanan, kapatılabilir oyun dışı program; Sistem sekmesinin
    // koruma kuralları geçerli (Windows klasörü, güvenlik / hile önleyici / sürücü / üretici yazılımı sayılmaz).
    static EnCokKullanan EnCokSanalBellek(HashSet<int> atla)
    {
        string winKlasoru = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        int benim, oturum;
        using (var b = Process.GetCurrentProcess()) { benim = b.Id; oturum = b.SessionId; }
        var toplam = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var yollar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == benim || p.Id == 0 || p.SessionId != oturum || atla.Contains(p.Id)) continue;
                    string ad = p.ProcessName;
                    if (string.IsNullOrEmpty(ad) || Korunuyor(ad)) continue;
                    string yol;
                    if (!yollar.TryGetValue(ad, out yol))
                    {
                        yol = SurecYolu(p.Id);
                        yollar[ad] = yol;
                    }
                    if (string.IsNullOrEmpty(yol) || yol.StartsWith(winKlasoru, StringComparison.OrdinalIgnoreCase) || Korunuyor(ad, yol)) continue;
                    long eski;
                    toplam.TryGetValue(ad, out eski);
                    toplam[ad] = eski + p.PrivateMemorySize64;
                }
                catch { }
            }
        }
        if (toplam.Count == 0) return null;
        var en = toplam.OrderByDescending(k => k.Value).First();
        string gorunen = en.Key;
        try
        {
            string d = FileVersionInfo.GetVersionInfo(yollar[en.Key]).FileDescription;
            if (!string.IsNullOrWhiteSpace(d)) gorunen = d.Trim();
        }
        catch { }
        return new EnCokKullanan { Ad = gorunen, Bellek = en.Value };
    }

    static string SurecYolu(int pid)
    {
        IntPtr h = Yerel.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var tampon = new StringBuilder(1024);
            int boyut = tampon.Capacity;
            return Yerel.QueryFullProcessImageName(h, 0, tampon, ref boyut) ? tampon.ToString() : null;
        }
        finally { Yerel.CloseHandle(h); }
    }

    static void CalismaKumeleriniBosalt(HashSet<int> atla)
    {
#if DENEME
        // Deneme derlemesi (araclar\test\deneme-derle.cmd): gerçek bilgisayarda hiçbir programın RAM'ine dokunmaz.
        Deneme.Yaz("RAM boşaltma atlandı; atlanan süreç sayısı " + atla.Count);
        if (atla.Count >= 0) return;
#endif
        const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_SET_QUOTA = 0x0100;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (atla.Contains(p.Id)) continue;
                IntPtr h = Yerel.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, p.Id);
                if (h != IntPtr.Zero) { Yerel.EmptyWorkingSet(h); Yerel.CloseHandle(h); }
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    void SecimleriYukle()
    {
        try
        {
            if (!File.Exists(AyarDosyasi)) return;
            foreach (var satir in File.ReadAllLines(AyarDosyasi))
                if (satir.Length > 1 && (satir[0] == '+' || satir[0] == '-'))
                    hatirlanan[satir.Substring(1)] = satir[0] == '+';
        }
        catch { }
    }

    void SecimleriKaydet()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AyarDosyasi));
            File.WriteAllLines(AyarDosyasi, hatirlanan.Select(k => (k.Value ? "+" : "-") + k.Key));
        }
        catch { }
    }
}
