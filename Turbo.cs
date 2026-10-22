// Turbo sekmesinin motoru: oyun için işe yarayan, geri alınabilir ayarlar (kayıt defteri, güç planı, DirectX tercihi,
// DISM işleri), donanım/durum okuması ve oyun profili (oyun açılınca güç modu).
//
// Kayıt defteri ayarları `WinAyar` olarak `TurboAyarlari.Tum` tablosunda (kimlikler `turbo-` önekli); uygulama, geri alma ve
// yedek Windows sekmesinin motorunu kullanır (aynı windows-yedek.txt, kimlikler farklı olduğu için çakışmaz). Windows sekmesi
// bu tabloyu görmez. Kayıt defteri olmayan ayarların (güç planı değerleri, DirectX dizgesi, Nihai plan, DISM işleri) eski
// değerleri %APPDATA%\SergenPcTemizle\turbo-yedek.txt dosyasına ("anahtar=değer" satırları) yazılır.
//
// Yönetici gerektiren her şey (HKLM satırları + DISM/fsutil işleri + geri yükleme noktası) tek runas çağrısında,
// `--turbo <dosya> [nokta]` yardımcısında yapılır. Dosyadan asla rastgele komut/argüman alınmaz: kayıt satırları
// WindowsAyarlari.Izinli ile doğrulanır, özel işler yalnızca aşağıdaki sabit beyaz listeden gelir. Yardımcı HKCU'ya
// hiç dokunmaz; HKCU işleri, güç planı ve DirectX işleri bu süreçte yapılır.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

// Bir Turbo ayarı: sekmede bir satır. Kayıt defteri kısmı Kayit'ta; kayıt dışı işler Ozel/OzelGeri delegelerinde
// (null = başarılı, metin = hata), yönetici gerektiren özel işler YonOzel/YonOzelGeri adlarıyla (beyaz liste).
class TurboAyar
{
    public readonly string Kimlik, Bolum, Baslik, Aciklama;
    public readonly bool Oneri;
    public bool Sorun, Yeniden;
    public string Etiket;                       // başlığın yanında turuncu ek etiket (null olabilir)
    public WinAyar Kayit;                       // kayıt defteri kısmı (null olabilir)
    public bool GeriVarsayilan;                 // geri alırken yedek yerine Kayit.Varsayilan yazılır
    public string YonOzel, YonOzelGeri;         // yardımcının beyaz listesindeki işler
    public Func<AyarDurumu> DurumOku;           // null: Kayit'tan
    public Func<string> UygunlukOku;            // null = seçilebilir; metin = neden seçilemediği
    public Func<string> UyariOku, EtiketOku;    // uygulamadan önceki ek uyarı / okunan duruma bağlı ek etiket
    public Func<bool> Gizle;                    // true = satır hiç gösterilmez
    public Func<bool> YedekVarOku;              // kayıt dışı ayarlarda: Turbo bunu değiştirdi mi (yedeği var mı)
    public Func<string> Ozel, OzelGeri;         // bu süreçte yapılan kayıt dışı işler
    public Func<bool, string> YonOnce;          // yönetici işinden önce (geri: bool); null dönerse devam
    public Action<bool, bool> YonSonra;         // yönetici işinden sonra (geri, başarılı)

    public TurboAyar(string kimlik, string bolum, string baslik, string aciklama, bool oneri)
    {
        Kimlik = kimlik; Bolum = bolum; Baslik = baslik; Aciklama = aciklama; Oneri = oneri;
    }

    public bool Yonetici { get { return (Kayit != null && Kayit.Yonetici) || YonOzel != null || YonOzelGeri != null; } }
}

class DurumSatiri
{
    public string Metin, Bolum, Ayar;   // Bolum/Ayar: "Düzelt" düğmesi o bölüme gider (Ayar varsa kutusunu da işaretler)
    public bool Uyari, TrimDuzelt;
}

// Bir okumanın sonucu (arka planda üretilir, arayüz yalnızca okur).
class TurboOkuma
{
    public readonly Dictionary<string, AyarDurumu> Durum = new Dictionary<string, AyarDurumu>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> Uygunluk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> EkUyari = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> EkEtiket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Gizli = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Turbo'nun kendisi değiştirdiği (yedeği olan) ayarlar. Yedeği olmayan "uygulanmış" ayar Windows'un ya da kullanıcının
    // kendi ayarıdır: "Hepsini geri al" ona dokunmaz, elle geri alınırsa Windows varsayılanı yazılır.
    public readonly HashSet<string> TurboYapti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public List<DurumSatiri> Satirlar = new List<DurumSatiri>();
}

// turbo-yedek.txt: "anahtar=değer" satırları. Yazma .yeni + File.Replace ile; dosya var ama okunamadıysa üzerine yazılmaz.
static class TurboYedek
{
    static readonly string Dosya = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SergenPcTemizle", "turbo-yedek.txt");
    static readonly object kilit = new object();
    static Dictionary<string, string> d;
    static bool okunamadi;

    static void Yukle()
    {
        if (d != null) return;
        d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(Dosya)) return;
            foreach (var satir in File.ReadAllLines(Dosya, Encoding.UTF8))
            {
                int i = satir.IndexOf('=');
                if (i <= 0) continue;
                d[satir.Substring(0, i)] = satir.Substring(i + 1);
            }
        }
        catch { okunamadi = true; }
    }

    public static string Al(string anahtar)
    {
        lock (kilit) { Yukle(); string v; return d.TryGetValue(anahtar, out v) ? v : null; }
    }

    public static bool Var(string anahtar) { return Al(anahtar) != null; }

    public static void Yaz(string anahtar, string deger)
    {
        if (anahtar.IndexOf('=') >= 0 || deger.IndexOf('\n') >= 0 || deger.IndexOf('\r') >= 0)
            throw new ArgumentException("yedek değeri yazılamaz");
        lock (kilit)
        {
            Yukle();
            if (okunamadi) throw new IOException("turbo-yedek.txt okunamadı");
            d[anahtar] = deger;
            Kaydet();
        }
    }

    // Eski hâline dönenin yedeği silinir; silinemezse kalan yedek zararsızdır (bir sonraki uygulamada üzerine yazılır).
    public static void Sil(string anahtar)
    {
        try
        {
            lock (kilit)
            {
                Yukle();
                if (okunamadi) return;
                if (d.Remove(anahtar)) Kaydet();
            }
        }
        catch { }
    }

    static void Kaydet()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Dosya));
        string gecici = Dosya + ".yeni";
        File.WriteAllLines(gecici, d.Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(false));
        if (File.Exists(Dosya)) File.Replace(gecici, Dosya, null);
        else File.Move(gecici, Dosya);
    }
}

// HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings: "Anahtar=Değer;" çiftleri. Yalnızca
// istenen çifte dokunulur, diğer çiftler aynen kalır; bu anahtardaki başka değerlere (uygulama başına ayarlar) hiç dokunulmaz.
static class DxTercihi
{
    const string Yol = @"Software\Microsoft\DirectX\UserGpuPreferences", Ad = "DirectXUserGlobalSettings";

    // Dizge ya da yoksa null; dizge olmayan bir türse hata dolar.
    static string Mevcut(out string hata)
    {
        hata = null;
        object o = WindowsAyarlari.DegerOku(Kok.KU, Yol, Ad);
        if (o == null) return null;
        var s = o as string;
        if (s == null) hata = "DirectX tercihi beklenmeyen türde; dokunulmadı";
        return s;
    }

    public static string Cift(string dizge, string anahtar)
    {
        if (dizge == null) return null;
        foreach (var p in dizge.Split(';'))
        {
            int i = p.IndexOf('=');
            if (i > 0 && string.Equals(p.Substring(0, i).Trim(), anahtar, StringComparison.OrdinalIgnoreCase)) return p.Substring(i + 1).Trim();
        }
        return null;
    }

    // deger null: çifti kaldırır. Diğer çiftler olduğu gibi kalır.
    static string Ayarla(string dizge, string anahtar, string deger)
    {
        var parcalar = new List<string>();
        bool yazildi = false;
        if (dizge != null)
            foreach (var p in dizge.Split(';'))
            {
                if (p.Trim().Length == 0) continue;
                int i = p.IndexOf('=');
                if (i > 0 && string.Equals(p.Substring(0, i).Trim(), anahtar, StringComparison.OrdinalIgnoreCase))
                {
                    if (deger != null && !yazildi) { parcalar.Add(anahtar + "=" + deger); yazildi = true; }
                    continue;
                }
                parcalar.Add(p);
            }
        if (deger != null && !yazildi) parcalar.Add(anahtar + "=" + deger);
        return parcalar.Count == 0 ? "" : string.Join(";", parcalar) + ";";
    }

    static void Yaz(string dizge)
    {
        if (dizge.Length == 0)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Yol, true)) if (k != null) k.DeleteValue(Ad, false);
            return;
        }
        using (var k = Registry.CurrentUser.CreateSubKey(Yol))
        {
            if (k == null) throw new IOException("anahtar açılamadı: " + Yol);
            k.SetValue(Ad, dizge, RegistryValueKind.String);
        }
    }

    public static bool Tutuyor(string anahtar, string deger)
    {
        string hata;
        return Cift(Mevcut(out hata), anahtar) == deger;
    }

    public static string Uygula(string anahtar, string deger)
    {
        string hata;
        string mevcut = Mevcut(out hata);
        if (hata != null) return hata;
        string yedek = "dx." + anahtar;
        if (!TurboYedek.Var(yedek)) TurboYedek.Yaz(yedek, Cift(mevcut, anahtar) ?? "yoktu");
        Yaz(Ayarla(mevcut, anahtar, deger));
        return null;
    }

    public static string GeriAl(string anahtar)
    {
        string hata;
        string mevcut = Mevcut(out hata);
        if (hata != null) return hata;
        string yedek = "dx." + anahtar, eski = TurboYedek.Al(yedek);
        Yaz(Ayarla(mevcut, anahtar, eski == null || eski == "yoktu" ? null : eski));
        TurboYedek.Sil(yedek);
        return null;
    }
}

class DonanimAnlik
{
    public int Vmp = -1;            // Win32_OptionalFeature.InstallState: 1 açık, 2 kapalı, 3 yok; -1 okunamadı
    public int Hvci = -1;           // 1 çalışıyor, 0 çalışmıyor, -1 bilinmiyor
    public int DiskTur;             // MSFT_PhysicalDisk.MediaType: 3 HDD, 4 SSD, 0 bilinmiyor
    public int Trim = -1;           // DisableDeleteNotify (0 = TRIM açık); -1 okunamadı
    public long SistemDiskBoyut;
    public List<Dictionary<string, object>> Moduller;   // Win32_PhysicalMemory; null = okunamadı
}

// WMI / fsutil okumaları (birkaç saniye sürebilir: arka planda çağrılır, yönetici gerektirmez).
static class TurboDonanim
{
    public static volatile DonanimAnlik Anlik = new DonanimAnlik();

    public static void Tazele()
    {
        var a = new DonanimAnlik();
        try { a.SistemDiskBoyut = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)).TotalSize; } catch { }
        Task.WaitAll(new[]
        {
            Task.Run(() => { try { a.Vmp = VmpDurumu(); } catch { } }),
            Task.Run(() => { try { a.Hvci = HvciCalisiyor(); } catch { } }),
            Task.Run(() => { try { a.Moduller = Wmi(@"\\.\root\cimv2", "SELECT FormFactor, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory", "FormFactor", "Speed", "ConfiguredClockSpeed"); } catch { } }),
            Task.Run(() =>
            {
                try
                {
                    a.DiskTur = SistemDiskTuru();
                    if (a.DiskTur == 4) a.Trim = TrimDurumu();
                }
                catch { }
            })
        });
        Anlik = a;
    }

    // Alanlar nesne olarak alınır (WMI nesneleri bu yöntemde kapatılır). Sorgu başarısızsa null.
    static List<Dictionary<string, object>> Wmi(string kapsam, string sorgu, params string[] alanlar)
    {
        try
        {
            var sonuc = new List<Dictionary<string, object>>();
            using (var arama = new ManagementObjectSearcher(new ManagementScope(kapsam), new ObjectQuery(sorgu)))
            using (var koleksiyon = arama.Get())
                foreach (ManagementBaseObject m in koleksiyon)
                    using (m)
                    {
                        var d = new Dictionary<string, object>();
                        foreach (var ad in alanlar) { try { d[ad] = m[ad]; } catch { } }
                        sonuc.Add(d);
                    }
            return sonuc;
        }
        catch { return null; }
    }

    static int Say(object o, int varsayilan)
    {
        try { return o == null ? varsayilan : Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch { return varsayilan; }
    }

    static int VmpDurumu()
    {
        var l = Wmi(@"\\.\root\cimv2", "SELECT InstallState FROM Win32_OptionalFeature WHERE Name='VirtualMachinePlatform'", "InstallState");
        if (l == null) return -1;
        return l.Count == 0 ? 3 : Say(l[0]["InstallState"], -1);
    }

    // Win32_DeviceGuard.SecurityServicesRunning: 2 = HVCI çalışıyor. WMI okunamazsa kayıt defterine düşer.
    static int HvciCalisiyor()
    {
        var l = Wmi(@"\\.\root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard", "SecurityServicesRunning");
        if (l != null && l.Count > 0)
        {
            var dizi = l[0].ContainsKey("SecurityServicesRunning") ? l[0]["SecurityServicesRunning"] as Array : null;
            if (dizi != null)
            {
                foreach (var x in dizi) if (Say(x, -1) == 2) return 1;
                return 0;
            }
        }
        object e = WindowsAyarlari.DegerOku(Kok.LM, TurboAyarlari.HvciYolu, "Enabled");
        return e is int ? ((int)e == 1 ? 1 : 0) : -1;
    }

    // Sistem sürücüsünün bölümü → disk numarası → fiziksel disk türü.
    static int SistemDiskTuru()
    {
        char harf = char.ToUpperInvariant(Path.GetPathRoot(Environment.SystemDirectory)[0]);
        var bolumler = Wmi(@"\\.\root\Microsoft\Windows\Storage", "SELECT DriveLetter, DiskNumber FROM MSFT_Partition", "DriveLetter", "DiskNumber");
        if (bolumler == null) return 0;
        int no = -1;
        foreach (var b in bolumler)
        {
            object h = b.ContainsKey("DriveLetter") ? b["DriveLetter"] : null;
            char c = '\0';
            if (h is char) c = (char)h;
            else if (h is ushort) c = (char)(ushort)h;
            else if (h is string && ((string)h).Length > 0) c = ((string)h)[0];
            if (char.ToUpperInvariant(c) == harf) { no = Say(b.ContainsKey("DiskNumber") ? b["DiskNumber"] : null, -1); break; }
        }
        if (no < 0) return 0;
        var diskler = Wmi(@"\\.\root\Microsoft\Windows\Storage", "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk", "DeviceId", "MediaType");
        if (diskler == null) return 0;
        foreach (var dk in diskler)
            if (Convert.ToString(dk.ContainsKey("DeviceId") ? dk["DeviceId"] : null, CultureInfo.InvariantCulture) == no.ToString(CultureInfo.InvariantCulture))
            {
                int t = Say(dk.ContainsKey("MediaType") ? dk["MediaType"] : null, 0);
                return t == 3 || t == 4 ? t : 0;
            }
        return 0;
    }

    // fsutil çıktısı dile bağlı; yalnızca "NTFS DisableDeleteNotify = 0/1" kalıbına bakılır.
    static int TrimDurumu()
    {
        string cikti;
        if (TurboAyarlari.Calistir(TurboAyarlari.Sistem32("fsutil.exe"), "behavior query DisableDeleteNotify", 15000, out cikti) < 0) return -1;
        var m = Regex.Match(cikti, @"NTFS\s+DisableDeleteNotify\s*=\s*(\d)", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
    }
}

static class TurboAyarlari
{
    public const string BolumDurum = "durum", BolumOyun = "oyun", BolumGuc = "guc", BolumEkran = "ekran", BolumGuvenlik = "guvenlik", BolumBellek = "bellek";
    public static readonly string[] Bolumler = { BolumDurum, BolumOyun, BolumGuc, BolumEkran, BolumGuvenlik, BolumBellek };
    public static readonly string[] BolumAdlari = { "Durum", "Oyun profili", "İşlemci ve güç", "Ekran kartı ve görüntü", "Güvenlik ve hız", "Bellek ve disk" };

    public const string HvciYolu = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    const string HvciIlkeYolu = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";

    static readonly Guid UsbAlt = new Guid("2a737441-1930-4402-8d77-b2bebba308a3"), UsbAyar = new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
    static readonly Guid PcieAlt = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20"), PcieAyar = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");
    static readonly Guid NihaiPlan = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");

    // Yardımcının yapabildiği kayıt dışı işler (sabit beyaz liste): ad → {exe, argümanlar}. Dosyadan başka bir şey alınmaz.
    static readonly Dictionary<string, string[]> OzelIsler = new Dictionary<string, string[]>
    {
        { "vmp-kapat", new[] { "dism.exe", "/Online /Disable-Feature /FeatureName:VirtualMachinePlatform /NoRestart" } },
        { "vmp-ac", new[] { "dism.exe", "/Online /Enable-Feature /FeatureName:VirtualMachinePlatform /All /NoRestart" } },
        { "ayrilmis-kapat", new[] { "dism.exe", "/Online /Set-ReservedStorageState /State:Disabled" } },
        { "ayrilmis-ac", new[] { "dism.exe", "/Online /Set-ReservedStorageState /State:Enabled" } },
        { "trim-ac", new[] { "fsutil.exe", "behavior set DisableDeleteNotify 0" } }
    };

    [DllImport("kernel32.dll")] static extern bool GetPhysicallyInstalledSystemMemory(out long kb);

    static TurboAyar[] tum;

    // Tablo ilk kullanımda kurulur (donanıma bağlı önerilen/değer hesapları var); yardımcı da aynı değerleri hesaplar.
    public static TurboAyar[] Tum { get { return tum ?? (tum = Kur()); } }

    public static IEnumerable<WinAyar> KayitAyarlari { get { return Tum.Where(a => a.Kayit != null).Select(a => a.Kayit); } }

    public static TurboAyar Bul(string kimlik)
    {
        return Tum.FirstOrDefault(a => a.Kimlik.Equals(kimlik, StringComparison.OrdinalIgnoreCase));
    }

    // Takılı RAM (KB); okunamazsa Windows'un gördüğü toplam.
    static long RamKB()
    {
        long kb;
        try { if (GetPhysicallyInstalledSystemMemory(out kb) && kb > 0) return kb; } catch { }
        return (long)(Yerel.Bellek().ullTotalPhys / 1024);
    }

    static WinAyar W(string kimlik, string baslik, int enAz, RegIslem[] uygula, RegIslem[] varsayilan)
    {
        return new WinAyar(kimlik, "turbo", baslik, "", false, enAz, 0, Etki.Yok, uygula, varsayilan);
    }

    static RegIslem[] A(params RegIslem[] i) { return i; }

    static TurboAyar[] Kur()
    {
        bool pil = GucAyarlari.PilVar;
        long ramKB = RamKB();
        bool azRam = ramKB <= 8L * 1024 * 1024;
        const string Surum = @"SYSTEM\CurrentControlSet\Control";
        var l = new List<TurboAyar>();

        // ---- Oyun profili bölümü ----
        l.Add(new TurboAyar("turbo-oyun-modu", BolumOyun, "Windows Oyun Modu",
            "Oyun açıkken Windows arka plan işlerini (güncelleme, sürücü kurulumu) ertler. Değer yoksa Windows varsayılanı: açık.", true)
        {
            Kayit = W("turbo-oyun-modu", "Windows Oyun Modu", 0,
                A(R.Dw(Kok.KU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1)),
                A(R.Dw(Kok.KU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 0))),
            GeriVarsayilan = true,   // değer yoksa zaten açık sayılır: işareti kaldırınca açıkça kapatılır
            DurumOku = delegate
            {
                object o = WindowsAyarlari.DegerOku(Kok.KU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
                if (o == null) return AyarDurumu.Uygulandi;
                return o is int && (int)o == 1 ? AyarDurumu.Uygulandi : AyarDurumu.Uygulanmadi;
            }
        });

        // ---- İşlemci ve güç ----
        l.Add(new TurboAyar("turbo-guc-kisitlama", BolumGuc, "Güç kısıtlamasını kapat",
            "Windows'un arka plan programlarını \"verimlilik modunda\" yavaşlatmasını kapatır." +
            (pil ? " Dizüstünde ısı ve pil kullanımı artar." : ""), !pil)
        {
            Yeniden = true,
            Kayit = W("turbo-guc-kisitlama", "Güç kısıtlamasını kapat", 0,
                A(R.Dw(Kok.LM, Surum + @"\Power\PowerThrottling", "PowerThrottlingOff", 1)),
                A(R.DegerSil(Kok.LM, Surum + @"\Power\PowerThrottling", "PowerThrottlingOff")))
        });

        l.Add(GucAc("turbo-usb", "usb", "USB askıya almayı prizdeyken kapat",
            "Fare, klavye ve kulaklığın anlık kopmasını ve takılmayı önler; pilde tasarruf sürer.", UsbAlt, UsbAyar));
        l.Add(GucAc("turbo-pcie", "pcie", "PCI Express güç tasarrufunu prizdeyken kapat",
            "Ekran kartı ve NVMe diskin uyanma gecikmesini kaldırır.", PcieAlt, PcieAyar));

        l.Add(new TurboAyar("turbo-nihai", BolumGuc, "Nihai Performans planı",
            (pil ? "Isınıp kendini yavaşlatır, pil hızla biter. " : "İşlemci hep hazır bekler; boşta daha çok güç harcar. ") +
            "Bu plan seçiliyken Windows'un güç modu ve Oyun profilinin güç adımı çalışmaz.", false)
        {
            Etiket = pil ? "dizüstü: önerilmez" : null,
            DurumOku = delegate
            {
                string[] y = NihaiYedek();
                return y != null && GucAyarlari.EtkinPlan() == new Guid(y[1]) ? AyarDurumu.Uygulandi : AyarDurumu.Uygulanmadi;
            },
            Ozel = NihaiAc,
            OzelGeri = NihaiKapat,
            YedekVarOku = delegate { return NihaiYedek() != null; }
        });

        // ---- Ekran kartı ve görüntü ----
        l.Add(new TurboAyar("turbo-hags", BolumEkran, "Donanım hızlandırmalı GPU zamanlaması (HAGS)",
            "DLSS kare üretimi için gerekli; kart desteklemiyorsa Windows yok sayar.", true)
        {
            Yeniden = true,
            Kayit = W("turbo-hags", "HAGS", 0,
                A(R.Dw(Kok.LM, Surum + @"\GraphicsDrivers", "HwSchMode", 2)),
                A(R.DegerSil(Kok.LM, Surum + @"\GraphicsDrivers", "HwSchMode")))
        });

        l.Add(DxAyari("turbo-pencereli", "Pencereli oyunlar için iyileştirme",
            "Kenarlıksız/pencereli oyunlarda gecikmeyi tam ekran seviyesine indirir (Windows Ayarlar → Ekran → Grafik'teki resmi seçenek).",
            true, "SwapEffectUpgradeEnable"));
        l.Add(DxAyari("turbo-vrr", "Pencereli oyunlarda değişken yenileme hızı",
            "G-Sync/FreeSync ekranlarda pencereli oyunlarda da değişken yenileme; ekran desteklemiyorsa etkisiz.",
            false, "VRROptimizeEnable"));

        l.Add(new TurboAyar("turbo-zamanlayici", BolumEkran, "Zamanlayıcı düzeltmesi (mikro takılma)",
            "Windows 11 oyun penceresi tam görünmediğinde zamanlayıcıyı 15,6 ms'ye düşürebiliyor, bu düzensiz takılma yapar; bunu engeller. Pil kullanımı biraz artar.", true)
        {
            Yeniden = true,
            Kayit = W("turbo-zamanlayici", "Zamanlayıcı düzeltmesi", 22000,
                A(R.Dw(Kok.LM, Surum + @"\Session Manager\kernel", "GlobalTimerResolutionRequests", 1)),
                A(R.DegerSil(Kok.LM, Surum + @"\Session Manager\kernel", "GlobalTimerResolutionRequests")))
        });

        l.Add(new TurboAyar("turbo-mpo", BolumEkran, "MPO'yu kapat (sorun giderici)",
            "Ekran titremesi, siyah ekran ya da masaüstünde takılma varsa; sorun yoksa dokunma. Bazı yeni Windows sürümlerinde etkisiz olabilir.", false)
        {
            Sorun = true, Yeniden = true,
            Kayit = W("turbo-mpo", "MPO'yu kapat", 0,
                A(R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5)),
                A(R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode")))
        });

        // ---- Güvenlik ve hız ----
        l.Add(new TurboAyar("turbo-hvci", BolumGuvenlik, "Bellek bütünlüğünü (HVCI) kapat",
            "Bazı oyunlarda FPS'i, özellikle en düşük FPS'i artırır (Microsoft'un önerisi).", false)
        {
            Yeniden = true,
            Kayit = W("turbo-hvci", "Bellek bütünlüğü", 0,
                A(R.Dw(Kok.LM, HvciYolu, "Enabled", 0)),
                A(R.Dw(Kok.LM, HvciYolu, "Enabled", 1))),
            UyariOku = delegate
            {
                return "Güvenlik azalır: kötü amaçlı sürücülere karşı koruma kapanır. Microsoft oyun oynarken kapatıp sonra açmayı öneriyor.";
            },
            DurumOku = delegate
            {
                // Değer yoksa ve çalışmıyorsa kapalı sayılır.
                if (WindowsAyarlari.DegerOku(Kok.LM, HvciYolu, "Enabled") == null)
                    return TurboDonanim.Anlik.Hvci == 0 ? AyarDurumu.Uygulandi : AyarDurumu.Uygulanmadi;
                return WindowsAyarlari.Durum(Bul("turbo-hvci").Kayit);
            },
            UygunlukOku = delegate
            {
                const string kilitli = "Kuruluş ilkesi ya da UEFI kilidi yönetiyor; buradan değiştirilemez";
                if (WindowsAyarlari.DegerOku(Kok.LM, HvciIlkeYolu, "HypervisorEnforcedCodeIntegrity") != null) return kilitli;
                object k = WindowsAyarlari.DegerOku(Kok.LM, HvciYolu, "Locked");
                return k is int && (int)k == 1 ? kilitli : null;
            },
            EtiketOku = delegate
            {
                object e = WindowsAyarlari.DegerOku(Kok.LM, HvciYolu, "Enabled");
                return e is int && (int)e == 0 && TurboDonanim.Anlik.Hvci == 1 ? "yeniden başlatınca kapanacak" : null;
            }
        });

        bool vmpYeni = false;   // bu uygulamada vmp yedeğini biz yazdık (başarısız olursa geri sileriz)
        l.Add(new TurboAyar("turbo-vmp", BolumGuvenlik, "Sanal Makine Platformunu kapat",
            "Windows'un sanallaştırma katmanını kapatır; oyunlarda ek yükü azaltır, açılış biraz hızlanabilir.", false)
        {
            Yeniden = true, YonOzel = "vmp-kapat", YonOzelGeri = "vmp-ac",
            Gizle = delegate { int v = TurboDonanim.Anlik.Vmp; return v != 1 && v != 2; },
            // Kapatıldıktan sonra yeniden başlatılana kadar Windows özelliği hâlâ "açık" gösterir: yedek varsa kapatılmış sayılır.
            DurumOku = delegate { return TurboDonanim.Anlik.Vmp != 1 || TurboYedek.Al("vmp") == "acikti" ? AyarDurumu.Uygulandi : AyarDurumu.Uygulanmadi; },
            EtiketOku = delegate { return TurboDonanim.Anlik.Vmp == 1 && TurboYedek.Al("vmp") == "acikti" ? "yeniden başlatınca kapanacak" : null; },
            YedekVarOku = delegate { return TurboYedek.Var("vmp"); },
            UygunlukOku = delegate { return TurboDonanim.Anlik.Vmp == 2 && TurboYedek.Al("vmp") != "acikti" ? "zaten kapalı" : null; },
            UyariOku = delegate
            {
                string u = "WSL, Docker Desktop ve Hyper-V kullanan Android emülatörleri çalışmaz hâle gelir.";
                try { if (WslVar()) u += " Bu bilgisayarda WSL dağıtımı kurulu."; } catch { }
                return u;
            },
            YonOnce = delegate(bool geri)
            {
                vmpYeni = false;
                if (!geri && TurboDonanim.Anlik.Vmp == 1 && !TurboYedek.Var("vmp"))
                {
                    try { TurboYedek.Yaz("vmp", "acikti"); vmpYeni = true; }
                    catch (Exception e) { return "Yedek alınamadı: " + e.Message; }
                }
                return null;
            },
            YonSonra = delegate(bool geri, bool basarili)
            {
                if (!geri && !basarili && vmpYeni) TurboYedek.Sil("vmp");
                if (geri && basarili) TurboYedek.Sil("vmp");
                vmpYeni = false;
            }
        });

        // ---- Bellek ve disk ----
        l.Add(new TurboAyar("turbo-arkaplan", BolumBellek, "Mağaza uygulamaları arka planda çalışmasın",
            "Mağaza'dan kurulu uygulamaların (WhatsApp, Telefon Bağlantısı gibi) bildirimleri gecikebilir.", true)
        {
            Kayit = W("turbo-arkaplan", "Mağaza uygulamaları arka planda", 0,
                A(R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1)),
                A(R.DegerSil(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled")))
        });

        l.Add(new TurboAyar("turbo-svchost", BolumBellek, "Windows hizmetlerini daha az süreçte topla",
            "Birkaç yüz MB RAM kazandırır; bir hizmet çökerse aynı süreçtekileri de düşürebilir." + (azRam ? "" : " Bu bilgisayarda gerek yok."), azRam)
        {
            Yeniden = true,
            Kayit = W("turbo-svchost", "Hizmetleri topla", 0,
                A(R.Dw(Kok.LM, Surum, "SvcHostSplitThresholdInKB", ramKB + 1)),
                A(R.Dw(Kok.LM, Surum, "SvcHostSplitThresholdInKB", 3670016)))
        });

        l.Add(new TurboAyar("turbo-ayrilmis", BolumBellek, "Ayrılmış depolamayı kapat",
            "7 GB'a kadar yer açar; büyük Windows güncellemesinden önce geri aç.", false)
        {
            YonOzel = "ayrilmis-kapat", YonOzelGeri = "ayrilmis-ac",
            YedekVarOku = delegate { return TurboYedek.Var("ayrilmis"); },
            Gizle = delegate { return TurboDonanim.Anlik.SistemDiskBoyut > 256L * 1024 * 1024 * 1024 && !TurboYedek.Var("ayrilmis"); },
            DurumOku = delegate { return TurboYedek.Al("ayrilmis") == "kapatildi" ? AyarDurumu.Uygulandi : AyarDurumu.Uygulanmadi; },
            YonSonra = delegate(bool geri, bool basarili)
            {
                if (!basarili) return;
                if (geri) TurboYedek.Sil("ayrilmis");
                else { try { TurboYedek.Yaz("ayrilmis", "kapatildi"); } catch { } }
            }
        });

        return l.ToArray();
    }

    static bool WslVar()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
            return k != null && k.GetSubKeyNames().Length > 0;
    }

    // ---- Güç planı değerleri (prizde) ----

    static int AcOku(Guid alt, Guid ayar)
    {
        uint d;
        return GucAyarlari.ACOku(GucAyarlari.EtkinPlan(), alt, ayar, out d) == 0 ? (int)d : -1;
    }

    // Etkin plandaki prizde değeri 0 yapan ayar. Eski değer plan kimliğiyle yedeklenir ("usb.<plan>=1").
    static TurboAyar GucAc(string kimlik, string yedekOn, string baslik, string aciklama, Guid alt, Guid ayar)
    {
        return new TurboAyar(kimlik, BolumGuc, baslik, aciklama, true)
        {
            DurumOku = delegate { return AcOku(alt, ayar) == 0 ? AyarDurumu.Uygulandi : AyarDurumu.Uygulanmadi; },
            UygunlukOku = delegate { return AcOku(alt, ayar) < 0 ? "Bu güç planında bu ayar yok" : null; },
            YedekVarOku = delegate { return TurboYedek.Var(yedekOn + "." + GucAyarlari.EtkinPlan().ToString("D")); },
            Ozel = delegate
            {
                Guid plan = GucAyarlari.EtkinPlan();
                int eski = AcOku(alt, ayar);
                if (plan == Guid.Empty || eski < 0) return "Güç ayarı okunamadı";
                string y = yedekOn + "." + plan.ToString("D");
                if (!TurboYedek.Var(y)) TurboYedek.Yaz(y, eski.ToString(CultureInfo.InvariantCulture));
                return GucAyarlari.ACYaz(plan, alt, ayar, 0) ? null : "Güç ayarı yazılamadı";
            },
            OzelGeri = delegate
            {
                Guid plan = GucAyarlari.EtkinPlan();
                if (plan == Guid.Empty) return "Güç planı okunamadı";
                string y = yedekOn + "." + plan.ToString("D");
                int v;
                if (!int.TryParse(TurboYedek.Al(y), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 0) v = 1;
                if (!GucAyarlari.ACYaz(plan, alt, ayar, (uint)v)) return "Güç ayarı yazılamadı";
                TurboYedek.Sil(y);
                return null;
            }
        };
    }

    // ---- Nihai Performans planı ----
    // Yedek "nihai" = önceki plan : oluşturulan plan : (1 = plan bizim oluşturduğumuz, geri alınca silinir).

    static string[] NihaiYedek()
    {
        string y = TurboYedek.Al("nihai");
        if (y == null) return null;
        var p = y.Split(':');
        Guid g1, g2;
        return p.Length == 3 && Guid.TryParse(p[0], out g1) && Guid.TryParse(p[1], out g2) ? p : null;
    }

    static string NihaiAc()
    {
        Guid onceki = GucAyarlari.EtkinPlan();
        var planlar = GucAyarlari.Planlar();
        string[] y = NihaiYedek();
        Guid hedef = Guid.Empty;
        bool olusturuldu = false;
        if (y != null && planlar.Any(p => p.Kimlik == new Guid(y[1])))
        {
            hedef = new Guid(y[1]);                 // daha önce oluşturduğumuz plan hâlâ duruyor
            olusturuldu = y[2] == "1";
        }
        else if (planlar.Any(p => p.Kimlik == NihaiPlan)) hedef = NihaiPlan;   // Windows'un kendi Nihai planı zaten listede
        else
        {
            uint kod = GucAyarlari.PlanKopyala(NihaiPlan, out hedef);
            if (kod != 0) return "Bu bilgisayar Nihai Performans planına izin vermedi (kod " + kod + ")";
            olusturuldu = true;
        }
        if (onceki == hedef) onceki = GucAyarlari.DengeliPlan;
        try { TurboYedek.Yaz("nihai", onceki.ToString("D") + ":" + hedef.ToString("D") + ":" + (olusturuldu ? "1" : "0")); }
        catch (Exception e)
        {
            if (olusturuldu) GucAyarlari.PlanSil(hedef);
            return "Yedek alınamadı: " + e.Message;
        }
        if (!GucAyarlari.PlanSec(hedef))
        {
            if (olusturuldu) GucAyarlari.PlanSil(hedef);
            TurboYedek.Sil("nihai");
            return "Güç planı seçilemedi";
        }
        return null;
    }

    static string NihaiKapat()
    {
        string[] y = NihaiYedek();
        if (y == null) return "Önceki güç planı bilinmiyor";
        Guid onceki = new Guid(y[0]), olusan = new Guid(y[1]);
        // Önceki plan listede aranmaz: Modern Standby'lı bilgisayarlarda Windows etkin olmayan eski planları (Yüksek performans)
        // PowerEnumerate'te gizler ama plan durur ve seçilebilir. Seçilemezse (silinmişse) Dengeli'ye dönülür.
        if (GucAyarlari.EtkinPlan() == olusan && !GucAyarlari.PlanSec(onceki) && !GucAyarlari.PlanSec(GucAyarlari.DengeliPlan))
            return "Önceki güç planı seçilemedi";
        if (y[2] == "1") GucAyarlari.PlanSil(olusan);
        TurboYedek.Sil("nihai");
        return null;
    }

    // ---- DirectX tercihi ----

    static TurboAyar DxAyari(string kimlik, string baslik, string aciklama, bool oneri, string anahtar)
    {
        return new TurboAyar(kimlik, BolumEkran, baslik, aciklama, oneri)
        {
            DurumOku = delegate { return DxTercihi.Tutuyor(anahtar, "1") ? AyarDurumu.Uygulandi : AyarDurumu.Uygulanmadi; },
            Ozel = delegate { return DxTercihi.Uygula(anahtar, "1"); },
            OzelGeri = delegate { return DxTercihi.GeriAl(anahtar); },
            YedekVarOku = delegate { return TurboYedek.Var("dx." + anahtar); }
        };
    }

    // ---- Okuma ----

    public static bool Gorunur(TurboAyar a)
    {
        if (a.Kayit != null && !a.Kayit.BuSurumdeVar(WindowsAyarlari.Yapi)) return false;
        return a.Gizle == null || !a.Gizle();
    }

    // Arka planda: bütün ayarların durumu + durum bölümünün satırları.
    public static TurboOkuma Oku()
    {
        var o = new TurboOkuma();
        TurboDonanim.Tazele();
        foreach (var a in Tum)
        {
            if (!Gorunur(a)) { o.Gizli.Add(a.Kimlik); continue; }
            AyarDurumu d = AyarDurumu.Uygulanmadi;
            try { d = a.DurumOku != null ? a.DurumOku() : WindowsAyarlari.Durum(a.Kayit); } catch { }
            o.Durum[a.Kimlik] = d;
            try { string u = a.UygunlukOku != null ? a.UygunlukOku() : null; if (u != null) o.Uygunluk[a.Kimlik] = u; } catch { }
            try { string u = a.UyariOku != null ? a.UyariOku() : null; if (u != null) o.EkUyari[a.Kimlik] = u; } catch { }
            try { string u = a.EtiketOku != null ? a.EtiketOku() : null; if (u != null) o.EkEtiket[a.Kimlik] = u; } catch { }
            try { if (a.Kayit != null ? WindowsAyarlari.YedekVar(a.Kayit) : a.YedekVarOku != null && a.YedekVarOku()) o.TurboYapti.Add(a.Kimlik); } catch { }
        }
        try { o.Satirlar = SatirlariKur(o); } catch { }
        return o;
    }

    static void Ekle(List<DurumSatiri> l, bool uyari, string metin, string bolum, string ayar)
    {
        l.Add(new DurumSatiri { Uyari = uyari, Metin = metin, Bolum = bolum, Ayar = ayar });
    }

    // Ayara bağlı satır: uygulanmışsa yeşil, değilse turuncu + "Düzelt" (ayar seçilebiliyorsa).
    static void AyarSatiri(List<DurumSatiri> l, TurboOkuma o, string kimlik, string tamam, string eksik)
    {
        if (o.Gizli.Contains(kimlik) || !o.Durum.ContainsKey(kimlik)) return;
        if (o.Durum[kimlik] == AyarDurumu.Uygulandi) { Ekle(l, false, tamam, null, null); return; }
        var a = Bul(kimlik);
        bool secilebilir = !o.Uygunluk.ContainsKey(kimlik);
        Ekle(l, true, eksik, secilebilir ? a.Bolum : null, secilebilir ? kimlik : null);
    }

    static List<DurumSatiri> SatirlariKur(TurboOkuma o)
    {
        var l = new List<DurumSatiri>();
        var an = TurboDonanim.Anlik;

        // Güç
        Guid plan = GucAyarlari.EtkinPlan();
        if (plan == GucAyarlari.DengeliPlan)
        {
            int m = GucAyarlari.Mod(true);
            string ad = m >= 0 ? GucAyarlari.ModAdlari[m] : "bilinmiyor";
            if (m == 2) Ekle(l, false, "Güç modu (prizde): En iyi performans", null, null);
            else if (OyunProfili.Acik) Ekle(l, false, "Güç modu (prizde): " + ad + " · oyun profili açık, oyunda En iyi performansa geçer", null, null);
            else Ekle(l, true, "Güç modu (prizde): " + ad + " · oyunda yavaş kalabilir; Oyun profilini aç", TurboAyarlari.BolumOyun, null);
        }
        else Ekle(l, false, "Güç planı: " + GucAyarlari.PlanAdi(plan), null, null);

        AyarSatiri(l, o, "turbo-oyun-modu", "Windows Oyun Modu açık", "Windows Oyun Modu kapalı");
        if (OyunProfili.Acik) Ekle(l, false, "Oyun profili açık", null, null);
        else Ekle(l, true, "Oyun profili kapalı", BolumOyun, null);

        // HAGS: değer 2 açık, 1 kapalı, yoksa Windows varsayılanı
        if (!o.Gizli.Contains("turbo-hags"))
        {
            object h = WindowsAyarlari.DegerOku(Kok.LM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            if (h == null) Ekle(l, false, "HAGS: Windows varsayılanı", null, null);
            else AyarSatiri(l, o, "turbo-hags", "HAGS (donanım hızlandırmalı GPU zamanlaması) açık", "HAGS kapalı");
        }
        AyarSatiri(l, o, "turbo-zamanlayici", "Zamanlayıcı düzeltmesi açık", "Zamanlayıcı düzeltmesi kapalı: oyunda mikro takılma yapabilir");
        AyarSatiri(l, o, "turbo-pencereli", "Pencereli oyun iyileştirmesi açık", "Pencereli oyun iyileştirmesi kapalı");
        AyarSatiri(l, o, "turbo-usb", "USB askıya alma prizdeyken kapalı", "USB askıya alma prizdeyken açık: fare/klavye anlık kopabilir");
        AyarSatiri(l, o, "turbo-pcie", "PCI Express güç tasarrufu prizdeyken kapalı", "PCI Express güç tasarrufu prizdeyken açık: ekran kartı geç uyanabilir");

        // Bellek bütünlüğü
        if (an.Hvci == 1)
        {
            if (o.Durum.ContainsKey("turbo-hvci") && o.Durum["turbo-hvci"] == AyarDurumu.Uygulandi)
                Ekle(l, false, "Bellek bütünlüğü yeniden başlatınca kapanacak", null, null);
            else
            {
                bool sec = o.Durum.ContainsKey("turbo-hvci") && !o.Uygunluk.ContainsKey("turbo-hvci");
                Ekle(l, true, "Bellek bütünlüğü açık: bazı oyunlarda FPS düşürebilir", sec ? BolumGuvenlik : null, sec ? "turbo-hvci" : null);
            }
        }
        else if (an.Hvci == 0) Ekle(l, false, "Bellek bütünlüğü kapalı", null, null);

        // İşlemci önceliği
        object ps = WindowsAyarlari.DegerOku(Kok.LM, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation");
        if (ps is int && ((int)ps & 3) == 0)
            Ekle(l, true, "Arka plan hizmetlerine öncelik veriliyor: Sistem Özellikleri → Gelişmiş → Performans → Gelişmiş → Programlar", null, null);
        else Ekle(l, false, "Programlara öncelik (doğru)", null, null);

        // Donanım
        if (an.Moduller != null && an.Moduller.Count > 0)
        {
            int dimm = an.Moduller.Count(m => { int f = Sayi(m, "FormFactor"); return f == 8 || f == 12; });
            if (an.Moduller.Count == 1 && dimm == 1)
                Ekle(l, true, "Tek bellek modülü: büyük ihtimalle tek kanal çalışıyor; aynı ikinci modülü takmak oyunlarda belirgin hız kazandırır", null, null);
            else if (dimm >= 2) Ekle(l, false, an.Moduller.Count + " bellek modülü", null, null);

            // Dizüstü işlemcisi RAM hızını sınırlar: yanlış alarm olmasın diye yalnızca pilsiz bilgisayarda.
            if (!GucAyarlari.PilVar)
            {
                var hizli = an.Moduller.Where(m => Sayi(m, "Speed") > 0 && Sayi(m, "ConfiguredClockSpeed") > 0)
                                       .OrderBy(m => Sayi(m, "ConfiguredClockSpeed")).FirstOrDefault();
                if (hizli != null)
                {
                    int x = Sayi(hizli, "ConfiguredClockSpeed"), y = Sayi(hizli, "Speed");
                    if (x < y * 0.95)
                        Ekle(l, true, "RAM " + x + " MT/s'de çalışıyor, modül " + y + " destekliyor: BIOS'ta XMP/EXPO açılabilir (işlemci desteklemiyorsa normaldir)", null, null);
                    else Ekle(l, false, "RAM " + x + " MT/s", null, null);
                }
            }
        }
        if (GucAyarlari.PilVar)
        {
            if (!GucAyarlari.Prizde) Ekle(l, true, "Dizüstü pilde: oyunlarda performans çok düşer, şarja tak", null, null);
            else Ekle(l, false, "Dizüstü prizde", null, null);
        }
        if (an.DiskTur == 3) Ekle(l, true, "Windows sabit diskte (HDD): en büyük yavaşlık bu; SSD'ye geçmek her ayardan çok fark eder", null, null);
        else if (an.DiskTur == 4)
        {
            Ekle(l, false, "Windows SSD'de", null, null);
            if (an.Trim == 1) l.Add(new DurumSatiri { Uyari = true, Metin = "SSD'ye TRIM gönderilmiyor: zamanla yavaşlar", TrimDuzelt = true });
            else if (an.Trim == 0) Ekle(l, false, "SSD için TRIM açık", null, null);
        }

        if (TurboYedek.Al("ayrilmis") == "kapatildi")
            Ekle(l, true, "Büyük Windows güncellemesinden önce Ayrılmış depolamayı geri aç", BolumBellek, null);
        return l;
    }

    static int Sayi(Dictionary<string, object> d, string ad)
    {
        try { return d.ContainsKey(ad) && d[ad] != null ? Convert.ToInt32(d[ad], CultureInfo.InvariantCulture) : 0; } catch { return 0; }
    }

    // ---- Uygulama ----

    public class Sonuc
    {
        public List<TurboAyar> Yapilan = new List<TurboAyar>(), Yapilamayan = new List<TurboAyar>();
        public Dictionary<string, string> Neden = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool IzinVerilmedi;
        public string Hata;
    }

    // Bu ayar için yönetici satırları: kayıt defteri (HKLM) işlemleri + varsa özel iş.
    static List<string> YoneticiSatirlari(TurboAyar a, bool geri)
    {
        var l = new List<string>();
        if (a.Kayit != null)
        {
            var ops = geri ? (a.GeriVarsayilan ? a.Kayit.Varsayilan.ToList() : WindowsAyarlari.GeriAlmaIslemleri(a.Kayit)) : a.Kayit.Uygula.ToList();
            foreach (var i in ops) if (i.Kok != Kok.KU) l.Add(WindowsAyarlari.IslemYaz(i));
        }
        string ozel = geri ? a.YonOzelGeri : a.YonOzel;
        if (ozel != null) l.Add("OZEL\t" + ozel);
        return l;
    }

    // uygulanacak: işaretlenip henüz uygulanmamışlar; geriAlinacak: işareti kaldırılan uygulanmışlar.
    // yoneticiCalistir: yardımcıyı (argümanlarla) yönetici olarak açıp çıkış kodunu döner; -2 = izin verilmedi.
    public static Sonuc Uygula(List<TurboAyar> uygulanacak, List<TurboAyar> geriAlinacak, bool geriYuklemeNoktasi,
                               Func<string, int> yoneticiCalistir)
    {
        var s = new Sonuc();
        var hepsi = uygulanacak.Concat(geriAlinacak).ToList();

        // 1) Kayıt defteri ayarlarının eski değerleri yedeklenir (ilk özgün hâl korunur).
        try
        {
            foreach (var a in uygulanacak) if (a.Kayit != null) WindowsAyarlari.Yedekle(a.Kayit);
            WindowsAyarlari.YedekleriKaydet();
        }
        catch (Exception e)
        {
            s.Hata = "Yedek alınamadı: " + e.Message;
            foreach (var a in hepsi) { s.Yapilamayan.Add(a); s.Neden[a.Kimlik] = s.Hata; }
            return s;
        }

        // 2) Yönetici işleri tek çağrıda; her ayarın satır aralığı bellekte tutulur.
        var satirlar = new List<string>();
        var aralik = new Dictionary<TurboAyar, int[]>();
        var basarisiz = new HashSet<TurboAyar>();
        foreach (var a in hepsi)
        {
            bool geri = geriAlinacak.Contains(a);
            var sat = YoneticiSatirlari(a, geri);
            if (sat.Count == 0) continue;
            if (a.YonOnce != null)
            {
                string hata = null;
                try { hata = a.YonOnce(geri); } catch (Exception e) { hata = e.Message; }
                if (hata != null) { basarisiz.Add(a); s.Neden[a.Kimlik] = hata; continue; }
            }
            aralik[a] = new[] { satirlar.Count, sat.Count };
            satirlar.AddRange(sat);
        }
        if (satirlar.Count > 0)
        {
            string dosya = Path.Combine(Path.GetTempPath(), "SergenPcTemizle-turbo-" + Guid.NewGuid().ToString("N") + ".txt");
            string[] sonuc = null;
            int kod = -1;
            try
            {
                File.WriteAllLines(dosya, satirlar, new UTF8Encoding(false));
                kod = yoneticiCalistir("--turbo " + WindowsAyarlari.Tirnak(dosya) + (geriYuklemeNoktasi ? " nokta" : ""));
                if (File.Exists(dosya + ".sonuc")) sonuc = File.ReadAllLines(dosya + ".sonuc");
            }
            catch (Exception e) { s.Hata = e.Message; }
            finally
            {
                try { File.Delete(dosya); } catch { }
                try { File.Delete(dosya + ".sonuc"); } catch { }
            }
            if (kod == -2) s.IzinVerilmedi = true;
            else if (kod != 0 && sonuc == null && s.Hata == null) s.Hata = "Yönetici işleri yapılamadı (kod " + kod + ").";

            foreach (var kv in aralik)
            {
                bool ok = kod != -2 && sonuc != null && sonuc.Length >= kv.Value[0] + kv.Value[1];
                for (int i = 0; ok && i < kv.Value[1]; i++) if (sonuc[kv.Value[0] + i].Trim() != "0") ok = false;
                bool geri = geriAlinacak.Contains(kv.Key);
                if (kv.Key.YonSonra != null) { try { kv.Key.YonSonra(geri, ok); } catch { } }
                if (ok) continue;
                basarisiz.Add(kv.Key);
                s.Neden[kv.Key.Kimlik] = kod == -2 ? "yönetici izni verilmedi" : "yönetici işi tamamlanamadı";
            }
        }

        // 3) Bu süreçte yapılanlar: HKCU kayıt işleri, güç planı, DirectX tercihi.
        foreach (var a in hepsi)
        {
            if (basarisiz.Contains(a)) { s.Yapilamayan.Add(a); continue; }
            bool geri = geriAlinacak.Contains(a);
            try
            {
                if (a.Kayit != null)
                {
                    var ops = geri ? (a.GeriVarsayilan ? a.Kayit.Varsayilan.ToList() : WindowsAyarlari.GeriAlmaIslemleri(a.Kayit)) : a.Kayit.Uygula.ToList();
                    foreach (var i in ops) if (i.Kok == Kok.KU) WindowsAyarlari.Yaz(i);
                }
                var ozel = geri ? a.OzelGeri : a.Ozel;
                string hata = ozel != null ? ozel() : null;
                if (hata != null) { s.Yapilamayan.Add(a); s.Neden[a.Kimlik] = hata; continue; }
                s.Yapilan.Add(a);
            }
            catch (Exception e) { s.Yapilamayan.Add(a); s.Neden[a.Kimlik] = e.Message; }
        }

        // 4) Geri alınan ve gerçekten eski hâline dönenlerin yedeği silinir (turbo-yedek ayarların kendi kodunda).
        WindowsAyarlari.YedekKaldir(geriAlinacak.Where(a => s.Yapilan.Contains(a) && a.Kayit != null).Select(a => a.Kayit.Kimlik));
        return s;
    }

    // Tek bir özel işi (ör. "trim-ac") yönetici olarak çalıştırır. Çıkış kodu: 0 tamam, 1 olmadı, -2 izin verilmedi.
    public static int TekOzel(string ad, Func<string, int> yoneticiCalistir)
    {
        string dosya = Path.Combine(Path.GetTempPath(), "SergenPcTemizle-turbo-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllLines(dosya, new[] { "OZEL\t" + ad }, new UTF8Encoding(false));
            return yoneticiCalistir("--turbo " + WindowsAyarlari.Tirnak(dosya));
        }
        catch { return -1; }
        finally
        {
            try { File.Delete(dosya); } catch { }
            try { File.Delete(dosya + ".sonuc"); } catch { }
        }
    }

    // ---- Yönetici yardımcısı ----

    // exe --turbo <dosya> [nokta]. Dosya satırları: kayıt işlemleri (WindowsAyarlari satır biçimi, Izinli ile doğrulanır) ya da
    // "OZEL\t<ad>" (yalnızca OzelIsler beyaz listesi). İzin verilmeyen satır varsa hiçbiri yapılmaz (2). Her satırın sonucu
    // <dosya>.sonuc'a satır sırasıyla 0 (yapıldı) / 1 (yapılamadı) yazılır. Çıkış: 0 hepsi tamam, 1 en az biri olmadı, 2 izin yok.
    public static int YoneticiYaz(string[] args)
    {
        try
        {
            if (args.Length < 1 || args.Length > 2 || !File.Exists(args[0])) return 1;
            bool nokta = args.Length == 2 && args[1] == "nokta";
            var isler = new List<object>();   // RegIslem ya da özel iş adı
            foreach (var satir in File.ReadAllLines(args[0], Encoding.UTF8))
            {
                if (satir.Length == 0) continue;
                if (satir.StartsWith("OZEL\t", StringComparison.Ordinal))
                {
                    string ad = satir.Substring(5);
                    if (!OzelIsler.ContainsKey(ad)) return 2;
                    isler.Add(ad);
                }
                else
                {
                    var i = WindowsAyarlari.IslemCoz(satir);
                    if (i == null || !WindowsAyarlari.Izinli(i)) return 2;
                    isler.Add(i);
                }
            }
            if (isler.Count == 0) return 1;
            if (nokta) WindowsAyarlari.GeriYuklemeNoktasi("Turbo oncesi");
            var sonuclar = new List<string>();
            foreach (var is1 in isler)
            {
                bool ok;
                try
                {
                    var reg = is1 as RegIslem;
                    if (reg != null) { WindowsAyarlari.Yaz(reg); ok = true; }
                    else ok = OzelCalistir((string)is1);
                }
                catch { ok = false; }
                sonuclar.Add(ok ? "0" : "1");
            }
            try { File.WriteAllLines(args[0] + ".sonuc", sonuclar); } catch { }
            return sonuclar.All(x => x == "0") ? 0 : 1;
        }
        catch { return 1; }
    }

    // DISM 0 ya da 3010 (yeniden başlatma gerekir) başarıdır.
    static bool OzelCalistir(string ad)
    {
        var is1 = OzelIsler[ad];
        string cikti;
        int kod = Calistir(Sistem32(is1[0]), is1[1], 30 * 60 * 1000, out cikti);
        return kod == 0 || (is1[0] == "dism.exe" && kod == 3010);
    }

    internal static string Sistem32(string ad)
    {
        string dizin = Environment.SystemDirectory;
        // 32 bit süreç 64 bit Windows'ta System32'yi SysWOW64 görür; gerçek araçlar Sysnative'dedir.
        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            dizin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative");
        return Path.Combine(dizin, ad);
    }

    // Pencere açmadan çalıştırır; çıkış kodunu döner (başlamazsa ya da süre dolarsa -1).
    internal static int Calistir(string exe, string argumanlar, int bekleMs, out string cikti)
    {
        cikti = "";
        try
        {
            var bilgi = new ProcessStartInfo(exe, argumanlar)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            var sb = new StringBuilder();
            using (var p = new Process { StartInfo = bilgi })
            {
                p.OutputDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += delegate { };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(bekleMs)) { try { p.Kill(); } catch { } return -1; }
                p.WaitForExit();   // asenkron çıktının sonu gelsin
                lock (sb) cikti = sb.ToString();
                return p.ExitCode;
            }
        }
        catch { return -1; }
    }
}

// Oyun profili: tam ekran bir oyun açılınca güç modunu geçici olarak "En iyi performans" yapar, oyun bitince eski hâline
// döndürür; istenirse oyun başlarken bir kez sessizce RAM boşaltır. Bekçinin 5 sn'lik zamanlayıcısında (arayüz iş
// parçacığı) Tik() çağrılır. Asla pencere, ileti kutusu, ses ya da balon açmaz. Koşul: tam ekran + öndeki programın ekran
// kartı kullanımı (video/sunum oyun sayılmasın) arka arkaya iki kontrolde (≈10 sn). Eski mod ayarda saklanır
// (oyun_profili_eski_mod = "ac:1" / "dc:0"): program oyun sırasında kapanırsa bir sonraki açılışta geri yazılır.
class OyunProfili
{
    public static OyunProfili Ornek;
    public const string AyarAcik = "oyun_profili", AyarRam = "oyun_profili_ram", AyarEski = "oyun_profili_eski_mod";

    public static bool Acik { get { return Ayarlar.Oku(AyarAcik, false); } set { Ayarlar.Yaz(AyarAcik, value); } }
    public static bool RamBosalt { get { return Ayarlar.Oku(AyarRam, false); } set { Ayarlar.Yaz(AyarRam, value); } }
    public static string EtkinAd { get; private set; }   // profil şu an etkinse oyunun adı

    readonly Func<int, Task<bool>> gpuKontrol;   // öndeki program ekran kartını kullanıyor mu
    readonly Func<Task> ramBosalt;               // sessiz RAM boşaltma
    readonly int benim;
    int aday, adayTekrar, aktifPid;
    string aktifAd;
    bool mesgul, kapandi;

    public OyunProfili(Func<int, Task<bool>> gpuKontrol, Func<Task> ramBosalt)
    {
        this.gpuKontrol = gpuKontrol;
        this.ramBosalt = ramBosalt;
        using (var p = Process.GetCurrentProcess()) benim = p.Id;
    }

    public async void Tik()
    {
        if (mesgul || kapandi) return;
        mesgul = true;
        try
        {
            if (aktifPid != 0)
            {
                if (!Acik || !SurecVar(aktifPid, aktifAd)) Bitir();
                return;
            }
            if (!Acik || !TamEkran.Var()) { aday = adayTekrar = 0; return; }
            int onde = TamEkran.OndekiSurec();
            if (onde <= 0 || onde == benim) { aday = adayTekrar = 0; return; }
            bool gpu = await gpuKontrol(onde);
            if (kapandi || !Acik) return;
            if (!gpu) { aday = adayTekrar = 0; return; }
            if (aday != onde) { aday = onde; adayTekrar = 0; }
            if (++adayTekrar < 2) return;
            aday = adayTekrar = 0;
            Basla(onde);
        }
        catch { }
        finally { mesgul = false; }
    }

    static bool SurecVar(int pid, string ad)
    {
        try
        {
            using (var p = Process.GetProcessById(pid))
                return !p.HasExited && string.Equals(p.ProcessName, ad, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    void Basla(int pid)
    {
        string ad;
        try { using (var p = Process.GetProcessById(pid)) { if (p.HasExited) return; ad = p.ProcessName; } }
        catch { return; }
        aktifPid = pid; aktifAd = ad; EtkinAd = ad;
        try
        {
            // Güç modu yalnızca Dengeli planda çalışır; zaten En iyi performanssa dokunulmaz.
            if (GucAyarlari.EtkinPlan() == GucAyarlari.DengeliPlan)
            {
                bool priz = GucAyarlari.Prizde;
                int mod = GucAyarlari.Mod(priz);
                if (mod >= 0 && mod != 2)
                {
                    Ayarlar.YazMetin(AyarEski, (priz ? "ac:" : "dc:") + mod);
                    if (!GucAyarlari.ModSec(2)) Ayarlar.YazMetin(AyarEski, "");
                }
            }
        }
        catch { }
        if (RamBosalt) { try { ramBosalt(); } catch { } }
    }

    void Bitir()
    {
        GeriYaz();
        aktifPid = 0; aktifAd = null; EtkinAd = null;
    }

    // Kaynak (priz/pil) aynıysa eski mod geri yazılır; ayar her durumda temizlenir.
    static void GeriYaz()
    {
        string e = Ayarlar.OkuMetin(AyarEski, "");
        if (string.IsNullOrEmpty(e)) return;
        try
        {
            var p = e.Split(':');
            int mod;
            if (p.Length == 2 && int.TryParse(p[1], out mod) && GucAyarlari.Prizde == (p[0] == "ac")) GucAyarlari.ModSec(mod);
        }
        catch { }
        Ayarlar.YazMetin(AyarEski, "");
    }

    public static void AcilistaGeriYukle() { GeriYaz(); }

    // Kutu kapatılırsa etkin profil hemen biter.
    public void AyarDegisti() { if (aktifPid != 0 && !Acik) Bitir(); }

    public void Kapat()
    {
        kapandi = true;
        if (aktifPid != 0) Bitir();
    }
}
