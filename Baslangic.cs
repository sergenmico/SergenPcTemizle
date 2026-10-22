// Başlangıç programları: bilgisayar açılınca kendiliğinden başlayanlar (Görev Yöneticisi → Başlangıç ile aynı liste).
// Kaynaklar: HKCU\...\Run, HKLM\...\Run (64 bit), HKLM\SOFTWARE\WOW6432Node\...\Run (32 bit), kullanıcı ve ortak
// Başlangıç klasörü. Açık/kapalı bilgisi Görev Yöneticisi'nin kullandığı StartupApproved değerlerinden okunur;
// kapatmak Run girdisini SİLMEZ, yalnızca o değeri yazar (Görev Yöneticisi ile birebir aynı, geri alınabilir).
// StartupApproved değeri 12 bayt: ilk 4 bayt bayrak (02 00 00 00 = açık, 03 00 00 00 = kapalı; ilk bayt çift = açık,
// tek = kapalı), sonraki 8 bayt kapatıldığı an (FILETIME, UTC). Değer yoksa öğe açıktır.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

class BaslangicOgesi
{
    public string Ad;            // kayıt defteri değer adı ya da kısayol adı (Discord)
    public string Komut;         // çalıştırılan komut, argümanlarıyla
    public string Konum;         // sade konum adı: "Kullanıcı", "Tüm kullanıcılar (yönetici)", "Başlangıç klasörü"...
    public string Yol;           // çözülen exe yolu (rundll32 gibi aracı programlarda komutun kendisi)
    public string Aciklama;      // dosya açıklaması (FileDescription)
    public string Yayinci;       // şirket adı (CompanyName)
    public string OnayAnahtari;  // StartupApproved alt anahtarı: "Run" | "Run32" | "StartupFolder"
    public string OnayDegeri;    // StartupApproved değer adı (Run değer adı ya da klasördeki dosya adı)
    public bool Acik, YoneticiGerekir, Onerilmez;
    public long CalisanBellek;   // aynı adla çalışan süreçlerin özel bellek toplamı (bayt)

    // Sözleşmeye ek alanlar
    public string ExeAdi;           // uzantısız süreç adı (bellek eşleştirmesi için); aracı programlarda null
    public string OnerilmezNedeni;  // "bir ses sürücüsünün parçası; kapatırsan ... çalışmayabilir"
    public bool DosyaYok;           // komutun gösterdiği dosya artık yok (program kaldırılmış olabilir)

    public string Gorunen { get { return string.IsNullOrEmpty(Aciklama) ? Ad : Aciklama; } }
}

static class BaslangicYoneticisi
{
    public const string OnayYolu = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    const string RunYolu = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string Run32Yolu = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    static readonly string[] OnayAltAnahtarlari = { "Run", "Run32", "StartupFolder" };

    // Başka bir programı çalıştırmaya aracılık eden Windows araçları: bunların adıyla bellek toplanmaz,
    // açıklama/yayıncı gösterilmez (yanıltır), komut olduğu gibi gösterilir.
    static readonly HashSet<string> Araci = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32", "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "conhost", "explorer",
        "regsvr32", "msiexec", "schtasks", "javaw", "java", "start"
    };

    static readonly Dictionary<string, string> TurkceAdlar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "SecurityHealthSystray", "Windows Güvenliği simgesi" },
        { "OneDrive", "OneDrive (bulut yedekleme)" },
        { "msedge", "Microsoft Edge (arka planda hazır bekletme)" }
    };

    class Kural
    {
        public string[] Onekler;    // exe adı ya da girdi adı bununla başlarsa
        public string[] Yayincilar; // ya da şirket adı bunu içerirse
        public bool OnekYedek;      // önekler yalnızca yayıncı boşsa kullanılır
        public bool WinDivert;      // exe'nin klasöründe WinDivert sürücüsü varsa da eşleşir
        public string Tur, Etki;
    }

    // Sıra önemli: ilk eşleşen kural kullanılır.
    static readonly Kural[] Kurallar =
    {
        new Kural { Onekler = new[] { "SecurityHealth" }, Yayincilar = new string[0],
            Tur = "Windows Güvenliği bileşeni",
            Etki = "görev çubuğundaki Windows Güvenliği simgesi ve uyarıları görünmez (korumanın kendisi yine çalışır)" },
        new Kural { Onekler = new[] { "MsMpEng", "Avast", "AVG", "avp", "avpui", "bdagent", "bdservicehost", "egui", "ekrn",
                "MBAM", "mcshield", "McUICnt", "Norton", "ccSvcHst", "360tray", "360sd", "Kaspersky", "ESET" },
            Yayincilar = new[] { "Avast", "AVG Technologies", "Kaspersky", "Bitdefender", "ESET", "Malwarebytes", "McAfee",
                "NortonLifeLock", "Gen Digital", "Symantec", "Qihoo" },
            Tur = "antivirüs programının parçası",
            Etki = "koruma ya da uyarıları açılışta başlamayabilir" },
        new Kural { Onekler = new[] { "vgtray", "vgc", "Riot Vanguard", "EasyAntiCheat", "BEService", "BattlEye", "FACEIT",
                "SGuard", "ACE-", "xhunter", "GameGuard" },
            Yayincilar = new[] { "Easy Anti-Cheat", "BattlEye", "FACEIT" },
            Tur = "oyun hile önleyicisi",
            Etki = "onu isteyen oyunlar (Valorant, FACEIT maçları gibi) açılmayabilir ya da önce elle başlatman gerekir" },
        new Kural { Onekler = new[] { "goodbyedpi", "winws" }, Yayincilar = new string[0], WinDivert = true,
            Tur = "bağlantı aracı",
            Etki = "engelli sitelere ve bazı oyun sunucularına bağlanamayabilirsin" },
        new Kural { Onekler = new[] { "Rtk", "Realtek", "Nahimic", "A-Volute", "Dolby", "Waves", "MaxxAudio", "SonicStudio" },
            Yayincilar = new[] { "Realtek", "Dolby", "A-Volute", "Nahimic", "Waves Audio" },
            Tur = "ses sürücüsünün parçası",
            Etki = "ses ayarları, kulaklık/mikrofon algılama ya da ses efektleri çalışmayabilir" },
        new Kural { Onekler = new[] { "SynTP", "Synaptics", "ETD", "Elan", "ApntEx", "Apoint" },
            Yayincilar = new[] { "Synaptics", "ELAN Microelectronics" },
            Tur = "dokunmatik yüzey (touchpad) sürücüsünün parçası",
            Etki = "dokunmatik yüzeyde iki parmakla kaydırma gibi hareketler çalışmayabilir" },
        new Kural { Onekler = new[] { "NVIDIA", "nvcontainer", "NVDisplay", "nvsphelper", "nvtray", "AMD", "Radeon", "atiec",
                "atiesr", "Intel", "igfx", "IGCC" },
            Yayincilar = new[] { "NVIDIA", "Advanced Micro Devices", "Intel" },
            Tur = "ekran kartı ya da yonga seti yazılımı",
            Etki = "ekran kartı ayarları, kısayolları ya da sürücü bildirimleri çalışmayabilir" },
        new Kural { Onekler = AnaForm.UreticiOnek, Yayincilar = AnaForm.UreticiYayincilari, OnekYedek = true,
            Tur = "bilgisayar üreticisinin yazılımı",
            Etki = "Fn tuşları, fan, pil ya da klavye ışığı ayarları çalışmayabilir" }
    };

    // ------------------------------------------------------------------ ikili değer (saf fonksiyonlar)

    // Görev Yöneticisi'nin yazdığıyla aynı 12 bayt: açık = 02 00 00 00 + 8 sıfır; kapalı = 03 00 00 00 + FILETIME (UTC).
    public static byte[] OnayVerisi(bool acik, long dosyaZamaniUtc)
    {
        var v = new byte[12];
        v[0] = acik ? (byte)0x02 : (byte)0x03;
        if (!acik)
        {
            var zaman = BitConverter.GetBytes(dosyaZamaniUtc);   // little-endian, Windows'un FILETIME düzeni
            Array.Copy(zaman, 0, v, 4, 8);
        }
        return v;
    }

    // Değer yoksa ya da boşsa açık; ilk bayt çiftse (02, 06) açık, tekse (03, 07) kapalı.
    public static bool OnayAcikMi(byte[] veri)
    {
        if (veri == null || veri.Length == 0) return true;
        return (veri[0] & 1) == 0;
    }

    // Kapatıldığı an (yerel saat); açıksa ya da bilinmiyorsa null.
    public static DateTime? KapatilmaZamani(byte[] veri)
    {
        if (veri == null || veri.Length < 12 || OnayAcikMi(veri)) return null;
        long ft = BitConverter.ToInt64(veri, 4);
        if (ft <= 0) return null;
        try { return DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { return null; }
    }

    // Tek bir öğenin onay durumu (örneğin bekçinin kendi "--arkaplan" girdisinin gerçekten açık olup olmadığı).
    public static bool OnayDurumu(bool hklm, string alt, string degerAdi)
    {
        try
        {
            using (var kok = RegistryKey.OpenBaseKey(hklm ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var k = kok.OpenSubKey(OnayYolu + "\\" + alt))
                return k == null || OnayAcikMi(k.GetValue(degerAdi) as byte[]);
        }
        catch { return true; }
    }

    // ------------------------------------------------------------------ listeleme

    public static List<BaslangicOgesi> Listele()
    {
        var liste = new List<BaslangicOgesi>();
        Dictionary<string, long> bellek;
        try { bellek = SurecBellekleri(); } catch { bellek = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); }

        RunOku(liste, RegistryHive.CurrentUser, RunYolu, "Run", "Kullanıcı", bellek);
        RunOku(liste, RegistryHive.LocalMachine, RunYolu, "Run", "Tüm kullanıcılar (yönetici)", bellek);
        RunOku(liste, RegistryHive.LocalMachine, Run32Yolu, "Run32", "Tüm kullanıcılar, 32 bit (yönetici)", bellek);

        object kabuk = null;
        try
        {
            try { var tip = Type.GetTypeFromProgID("WScript.Shell"); if (tip != null) kabuk = Activator.CreateInstance(tip); } catch { }
            KlasorOku(liste, Environment.SpecialFolder.Startup, false, "Başlangıç klasörü", bellek, kabuk);
            KlasorOku(liste, Environment.SpecialFolder.CommonStartup, true, "Ortak başlangıç klasörü (yönetici)", bellek, kabuk);
        }
        finally { Birak(kabuk); }
        return liste;
    }

    static Dictionary<string, long> SurecBellekleri()
    {
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                long b = p.PrivateMemorySize64;
                long eski;
                d.TryGetValue(p.ProcessName, out eski);
                d[p.ProcessName] = eski + b;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return d;
    }

    static Dictionary<string, byte[]> OnaylariOku(RegistryHive kokAdi, string alt)
    {
        var d = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var kok = RegistryKey.OpenBaseKey(kokAdi, RegistryView.Registry64))
            using (var k = kok.OpenSubKey(OnayYolu + "\\" + alt))
            {
                if (k == null) return d;
                foreach (var ad in k.GetValueNames())
                {
                    try { var v = k.GetValue(ad) as byte[]; if (v != null) d[ad] = v; } catch { }
                }
            }
        }
        catch { }
        return d;
    }

    static void RunOku(List<BaslangicOgesi> liste, RegistryHive kokAdi, string yol, string alt, string konum, Dictionary<string, long> bellek)
    {
        var onay = OnaylariOku(kokAdi, alt);
        try
        {
            using (var kok = RegistryKey.OpenBaseKey(kokAdi, RegistryView.Registry64))
            using (var k = kok.OpenSubKey(yol))
            {
                if (k == null) return;
                foreach (var ad in k.GetValueNames())
                {
                    if (string.IsNullOrEmpty(ad)) continue;   // adsız (varsayılan) değer: Görev Yöneticisi de göstermez
                    string komut = null;
                    try { komut = k.GetValue(ad) as string; } catch { }
                    if (string.IsNullOrWhiteSpace(komut)) continue;
                    var o = new BaslangicOgesi
                    {
                        Ad = ad, Komut = komut.Trim(), Konum = konum, OnayAnahtari = alt, OnayDegeri = ad,
                        YoneticiGerekir = kokAdi == RegistryHive.LocalMachine
                    };
                    byte[] v;
                    o.Acik = !onay.TryGetValue(ad, out v) || OnayAcikMi(v);
                    try { Tamamla(o, bellek); } catch { }
                    liste.Add(o);
                }
            }
        }
        catch { }
    }

    static void KlasorOku(List<BaslangicOgesi> liste, Environment.SpecialFolder klasor, bool ortak, string konum,
                          Dictionary<string, long> bellek, object kabuk)
    {
        string yol;
        try { yol = Environment.GetFolderPath(klasor); } catch { return; }
        if (string.IsNullOrEmpty(yol) || !Directory.Exists(yol)) return;
        var onay = OnaylariOku(ortak ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, "StartupFolder");
        string[] dosyalar;
        try { dosyalar = Directory.GetFiles(yol); } catch { return; }
        foreach (var dosya in dosyalar)
        {
            string ad = Path.GetFileName(dosya);
            if (ad.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || ad.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) continue;
            var o = new BaslangicOgesi
            {
                Ad = Path.GetFileNameWithoutExtension(dosya), Konum = konum, OnayAnahtari = "StartupFolder", OnayDegeri = ad,
                YoneticiGerekir = ortak
            };
            string komut = "\"" + dosya + "\"";
            if (Path.GetExtension(dosya).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string hedef, arguman;
                KisayolCoz(kabuk, dosya, out hedef, out arguman);
                if (!string.IsNullOrEmpty(hedef))
                    komut = "\"" + hedef + "\"" + (string.IsNullOrEmpty(arguman) ? "" : " " + arguman);
            }
            o.Komut = komut;
            byte[] v;
            o.Acik = !onay.TryGetValue(ad, out v) || OnayAcikMi(v);
            try { Tamamla(o, bellek); } catch { }
            liste.Add(o);
        }
    }

    static void KisayolCoz(object kabuk, string lnk, out string hedef, out string arguman)
    {
        hedef = null; arguman = null;
        if (kabuk == null) return;
        object kisayol = null;
        try
        {
            kisayol = kabuk.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, kabuk, new object[] { lnk });
            if (kisayol == null) return;
            var t = kisayol.GetType();
            hedef = t.InvokeMember("TargetPath", BindingFlags.GetProperty, null, kisayol, null) as string;
            arguman = t.InvokeMember("Arguments", BindingFlags.GetProperty, null, kisayol, null) as string;
            if (hedef != null) hedef = hedef.Trim();
            if (arguman != null) arguman = arguman.Trim();
        }
        catch { }
        finally { Birak(kisayol); }
    }

    static void Birak(object com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
    }

    // Yol, açıklama, yayıncı, bellek ve "önerilmez" bilgisini doldurur.
    static void Tamamla(BaslangicOgesi o, Dictionary<string, long> bellek)
    {
        string exe = ExeBul(o.Komut);
        string exeAdi = null, uzanti = "";
        try
        {
            if (exe != null) { exeAdi = Path.GetFileNameWithoutExtension(exe); uzanti = Path.GetExtension(exe).ToLowerInvariant(); }
        }
        catch { }

        if (exeAdi != null && (Araci.Contains(exeAdi) || uzanti == ".bat" || uzanti == ".cmd" || uzanti == ".vbs" || uzanti == ".js" || uzanti == ".ps1"))
        {
            // rundll32, betik vb.: asıl işi yapanı güvenilir bilemeyiz; komutu olduğu gibi göster.
            o.Yol = o.Komut;
            o.ExeAdi = null;
        }
        else
        {
            // Discord, FACEIT, Teams gibi Squirrel kurulumları: "Update.exe --processStart Discord.exe".
            if (exeAdi != null && exeAdi.Equals("Update", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(o.Komut, "--processStart(?:=|\\s+)(?:\"([^\"]+)\"|(\\S+))", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string hedefAd = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    string bulunan = SquirrelHedefi(exe, hedefAd);
                    if (bulunan != null) exe = bulunan;
                    try { exeAdi = Path.GetFileNameWithoutExtension(hedefAd); uzanti = ".exe"; } catch { }
                }
            }
            o.Yol = exe;
            o.ExeAdi = uzanti == ".exe" || uzanti == ".com" || uzanti == "" ? exeAdi : null;

            if (exe != null && Path.IsPathRooted(exe))
            {
                if (File.Exists(exe))
                {
                    try
                    {
                        var fv = FileVersionInfo.GetVersionInfo(exe);
                        o.Aciklama = Bosalt(fv.FileDescription);
                        o.Yayinci = Bosalt(fv.CompanyName);
                        if (o.Aciklama == null) o.Aciklama = Bosalt(fv.ProductName);
                    }
                    catch { }
                }
                else o.DosyaYok = true;
            }
        }

        // Windows'un kendi öğelerinde İngilizce dosya açıklaması yerine bilinen Türkçe ad.
        string turkce;
        if (o.ExeAdi != null && TurkceAdlar.TryGetValue(o.ExeAdi, out turkce)) o.Aciklama = turkce;

        long b;
        if (o.ExeAdi != null && bellek.TryGetValue(o.ExeAdi, out b)) o.CalisanBellek = b;

        o.OnerilmezNedeni = Neden(o);
        o.Onerilmez = o.OnerilmezNedeni != null;
    }

    static string Bosalt(string s)
    {
        if (s == null) return null;
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    // Update.exe'nin klasöründe en yeni "app-x.y.z" alt klasöründeki hedef exe (yoksa doğrudan klasördeki).
    static string SquirrelHedefi(string updateExe, string hedefAd)
    {
        try
        {
            string klasor = Path.GetDirectoryName(updateExe);
            if (string.IsNullOrEmpty(klasor) || !Directory.Exists(klasor)) return null;
            var surumler = Directory.GetDirectories(klasor, "app-*")
                .Select(d => new { Yol = d, Surum = SurumOku(Path.GetFileName(d).Substring(4)) })
                .OrderByDescending(x => x.Surum).ThenByDescending(x => x.Yol, StringComparer.OrdinalIgnoreCase);
            foreach (var s in surumler)
            {
                string aday = Path.Combine(s.Yol, hedefAd);
                if (File.Exists(aday)) return aday;
            }
            string dogrudan = Path.Combine(klasor, hedefAd);
            if (File.Exists(dogrudan)) return dogrudan;
        }
        catch { }
        return null;
    }

    static Version SurumOku(string s)
    {
        Version v;
        return Version.TryParse(s, out v) ? v : new Version(0, 0);
    }

    // Komuttan çalıştırılan dosyanın yolu: tırnaklı ya da tırnaksız, ortam değişkenleri açılmış, argümanlar atılmış.
    // Bulunamazsa ilk parça (dosya artık yoksa bile yolu göstermek için) döner; komut boşsa null.
    static string ExeBul(string komut)
    {
        string k = Environment.ExpandEnvironmentVariables(komut ?? "").Trim();
        if (k.Length == 0) return null;
        if (k[0] == '"')
        {
            int son = k.IndexOf('"', 1);
            string aday = son > 1 ? k.Substring(1, son - 1) : k.Substring(1).TrimEnd('"');
            aday = aday.Trim();
            if (aday.Length == 0) return null;
            return Coz(aday) ?? aday;
        }
        // Tırnaksız: "C:\Program Files\X\y.exe -a" gibi boşluklu yolları bulmak için parçaları birleştirerek dene.
        var parcalar = k.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string birikim = null;
        for (int i = 0; i < parcalar.Length; i++)
        {
            birikim = i == 0 ? parcalar[0] : birikim + " " + parcalar[i];
            string c = Coz(birikim);
            if (c != null) return c;
            // ".exe" ile biten parçadan sonrası argümandır; dosya yoksa da burada dur.
            if (birikim.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return birikim;
        }
        return parcalar[0];
    }

    // Var olan dosyanın tam yolu; yalnızca ad verilmişse System32, Windows ve PATH'te arar.
    static string Coz(string yol)
    {
        try
        {
            yol = yol.Trim();
            if (yol.Length == 0 || yol.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            if (Path.IsPathRooted(yol))
            {
                if (File.Exists(yol)) return Path.GetFullPath(yol);
                if (!Path.HasExtension(yol) && File.Exists(yol + ".exe")) return Path.GetFullPath(yol + ".exe");
                return null;
            }
            if (yol.IndexOf('\\') >= 0 || yol.IndexOf('/') >= 0) return null;
            var klasorler = new List<string> { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            klasorler.AddRange(path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Trim('"')));
            foreach (var kl in klasorler)
            {
                if (kl.Length == 0) continue;
                try
                {
                    string aday = Path.Combine(kl, yol);
                    if (File.Exists(aday)) return aday;
                    if (!Path.HasExtension(yol) && File.Exists(aday + ".exe")) return aday + ".exe";
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    static string Neden(BaslangicOgesi o)
    {
        var adlar = new List<string>();
        if (o.ExeAdi != null) adlar.Add(o.ExeAdi);
        if (o.Ad != null) adlar.Add(o.Ad);
        foreach (var ad in adlar)
            if (ad.Equals("SergenPcTemizle", StringComparison.OrdinalIgnoreCase) || ad.Equals("PcTemizle", StringComparison.OrdinalIgnoreCase))
                return null;   // bu programın kendisi: kapatmak zararsız

        foreach (var kural in Kurallar)
        {
            bool eslesti = !(kural.OnekYedek && !string.IsNullOrWhiteSpace(o.Yayinci))
                && adlar.Any(ad => kural.Onekler.Any(on => ad.StartsWith(on, StringComparison.OrdinalIgnoreCase)));
            if (!eslesti && o.Yayinci != null)
                eslesti = kural.Yayincilar.Any(y => o.Yayinci.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!eslesti && kural.WinDivert && o.ExeAdi != null) eslesti = AnaForm.WinDivertTasiyor(o.Yol);
            if (eslesti) return "bir " + kural.Tur + "; kapatırsan " + kural.Etki;
        }
        if (o.ExeAdi != null && AnaForm.Korunuyor(o.ExeAdi))
            return "bir sürücü ya da güvenlik bileşeni; kapatırsan ilgili donanım ya da koruma düzgün çalışmayabilir";
        return null;
    }

    // ------------------------------------------------------------------ ayarlama

    // HKCU öğelerini doğrudan yazar (true). HKLM öğeleri yönetici ister: hiçbir şey yazmaz, false döner.
    // Run girdisine ASLA dokunmaz; yalnızca StartupApproved değeri yazılır.
    public static bool Ayarla(BaslangicOgesi o, bool acik)
    {
        if (o == null || o.YoneticiGerekir || string.IsNullOrEmpty(o.OnayDegeri)) return false;
        if (Array.IndexOf(OnayAltAnahtarlari, o.OnayAnahtari) < 0) return false;
        try
        {
            using (var kok = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var k = kok.CreateSubKey(OnayYolu + "\\" + o.OnayAnahtari))
            {
                if (k == null) return false;
                k.SetValue(o.OnayDegeri, OnayVerisi(acik, DateTime.UtcNow.ToFileTimeUtc()), RegistryValueKind.Binary);
            }
            o.Acik = acik;
            return true;
        }
        catch { return false; }
    }

    // Yönetici olarak: exe --baslangic <Run|Run32|StartupFolder> <değer adı> <1|0>. 0 = tamam, 1 = hata.
    // Yalnızca gerçekten var olan bir başlangıç öğesinin onay değeri yazılır; başka hiçbir şeye dokunulmaz.
    public static int YoneticiAyarla(string[] args)
    {
        try
        {
            if (args == null || args.Length != 3) return 1;
            string alt = args[0], deger = args[1];
            if (Array.IndexOf(OnayAltAnahtarlari, alt) < 0) return 1;
            if (string.IsNullOrEmpty(deger) || deger.IndexOf('\\') >= 0) return 1;
            if (args[2] != "1" && args[2] != "0") return 1;
            bool acik = args[2] == "1";

            using (var kok = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                bool mevcut = false;
                if (alt == "StartupFolder")
                {
                    string klasor = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                    mevcut = !string.IsNullOrEmpty(klasor) && File.Exists(Path.Combine(klasor, deger));
                }
                else
                {
                    using (var run = kok.OpenSubKey(alt == "Run32" ? Run32Yolu : RunYolu))
                        mevcut = run != null && run.GetValueNames().Contains(deger, StringComparer.OrdinalIgnoreCase);
                }
                if (!mevcut) return 1;

                using (var k = kok.CreateSubKey(OnayYolu + "\\" + alt))
                {
                    if (k == null) return 1;
                    k.SetValue(deger, OnayVerisi(acik, DateTime.UtcNow.ToFileTimeUtc()), RegistryValueKind.Binary);
                }
            }
            return 0;
        }
        catch { return 1; }
    }

    // CommandLineToArgvW kurallarına göre tek argümanı tırnaklar (içteki " ve sondaki \ kaçırılır).
    public static string Tirnakla(string s)
    {
        if (s == null) s = "";
        var sb = new StringBuilder("\"");
        int ters = 0;
        foreach (char c in s)
        {
            if (c == '\\') { ters++; continue; }
            if (c == '"') { sb.Append('\\', ters * 2 + 1); sb.Append('"'); }
            else { sb.Append('\\', ters); sb.Append(c); }
            ters = 0;
        }
        sb.Append('\\', ters * 2);
        sb.Append('"');
        return sb.ToString();
    }
}

class BaslangicSekmesi : Panel
{
    readonly KoyuListe liste = new KoyuListe();
    readonly Label lblDurum = new Label();
    readonly Button btnYenile;
    List<BaslangicOgesi> ogeler = new List<BaslangicOgesi>();
    bool ilkGosterim = true, yukleniyor, dolduruluyor, isleniyor;

    public BaslangicSekmesi()
    {
        Dock = DockStyle.Fill;
        BackColor = Renk.Arka;

        var baslik = new Panel { Dock = DockStyle.Top, Height = 66, Padding = new Padding(14, 8, 12, 4) };
        baslik.Controls.Add(Baslik("BAŞLANGIÇ PROGRAMLARI",
            "Bilgisayar açılınca kendiliğinden başlayanlar. İşareti kaldırırsan program silinmez, sadece açılışta başlamaz; " +
            "Görev Yöneticisi → Başlangıç ile aynı ayar, istediğin zaman geri açabilirsin."));

        liste.Columns.Add("Program", 230);
        liste.Columns.Add("Yayıncı", 150);
        liste.Columns.Add("Nereden", 215);
        liste.Columns.Add("Şu an", 80, HorizontalAlignment.Right);
        liste.Columns.Add("Durum", 140);
        liste.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        liste.ShowItemToolTips = true;
        liste.Dock = DockStyle.Fill;
        liste.Resize += delegate { SutunDoldur(liste, 0); };
        liste.ItemCheck += Liste_ItemCheck;
        var kutu = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        kutu.Controls.Add(liste);

        var alt = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(12, 6, 12, 6) };
        btnYenile = Dugme("Yenile", 110, false);
        btnYenile.Click += async delegate { await YukleAsync(); };
        lblDurum.Dock = DockStyle.Fill;
        lblDurum.Padding = new Padding(2, 0, 8, 0);
        lblDurum.TextAlign = ContentAlignment.MiddleLeft;
        lblDurum.ForeColor = Renk.Soluk;
        lblDurum.AutoEllipsis = true;
        alt.Controls.Add(lblDurum);
        alt.Controls.Add(btnYenile);

        Controls.Add(kutu);
        Controls.Add(baslik);
        Controls.Add(alt);
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
        Sigdir(l, sutun);
        try { if (l.IsHandleCreated) l.BeginInvoke(new Action(() => Sigdir(l, sutun, true))); } catch { }
    }

    static void Sigdir(ListView l, int sutun, bool zorla = false)
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
        await YukleAsync();
    }

    async Task YukleAsync()
    {
        if (yukleniyor || isleniyor) return;
        yukleniyor = true;
        btnYenile.Enabled = false;
        lblDurum.Text = "Başlangıç programları okunuyor...";
        List<BaslangicOgesi> l;
        try { l = await Task.Run(() => BaslangicYoneticisi.Listele()); }
        catch { l = new List<BaslangicOgesi>(); }
        // Önce kapatılabilecekler, sürücü/güvenlik öğeleri en altta; kendi içinde ada göre.
        ogeler = l.OrderBy(o => o.Onerilmez).ThenBy(o => o.Gorunen, StringComparer.CurrentCultureIgnoreCase).ToList();
        Doldur();
        yukleniyor = false;
        btnYenile.Enabled = true;
        DurumYaz(null);
    }

    void Doldur()
    {
        dolduruluyor = true;
        liste.BeginUpdate();
        liste.Items.Clear();
        foreach (var o in ogeler)
        {
            var oge = new ListViewItem(o.Gorunen) { Tag = o };
            for (int i = 0; i < 4; i++) oge.SubItems.Add("");
            oge.Checked = o.Acik;
            SatiriYaz(oge);
            liste.Items.Add(oge);
        }
        liste.EndUpdate();
        dolduruluyor = false;
        SutunDoldur(liste, 0);
    }

    static void SatiriYaz(ListViewItem oge)
    {
        var o = (BaslangicOgesi)oge.Tag;
        oge.SubItems[1].Text = o.Yayinci ?? "";
        oge.SubItems[2].Text = o.Konum;
        oge.SubItems[3].Text = o.CalisanBellek > 0 ? AnaForm.Boyut(o.CalisanBellek) : "–";
        string durum;
        if (!o.Acik) durum = o.Onerilmez ? "Kapalı · önerilmez" : "Kapalı";
        else if (o.DosyaYok) durum = "Açık · dosyası yok";
        else durum = o.Onerilmez ? "Kapatma önerilmez" : "Açık";
        oge.SubItems[4].Text = durum;
        oge.ForeColor = !o.Acik ? Renk.Soluk : o.Onerilmez ? Renk.Turuncu : Renk.Yazi;

        var ipucu = new StringBuilder();
        ipucu.Append(o.Komut);
        if (o.Onerilmez) ipucu.Append("\n\nKapatma önerilmez: ").Append(o.OnerilmezNedeni).Append('.');
        if (o.DosyaYok) ipucu.Append("\n\nKomutun gösterdiği dosya yok; program kaldırılmış olabilir.");
        oge.ToolTipText = ipucu.ToString();
    }

    void DurumYaz(string onEk)
    {
        if (ogeler.Count == 0)
        {
            lblDurum.Text = (onEk != null ? onEk + "  " : "") + "Açılışta kendiliğinden başlayan program bulunamadı.";
            return;
        }
        int n = ogeler.Count(o => o.Acik);
        lblDurum.Text = onEk != null
            ? string.Format("{0}  Açılışta başlayan: {1} program.", onEk, n)
            : string.Format("{0} program açılışta başlıyor; işaretini kaldırdıkların bir sonraki açılışta başlamaz.", n);
    }

    // İşaret hemen değişmez: istek ele alınır, ayar gerçekten yazılınca işaret programdan değiştirilir.
    void Liste_ItemCheck(object sender, ItemCheckEventArgs e)
    {
        if (dolduruluyor) return;
        bool istenen = e.NewValue == CheckState.Checked;
        e.NewValue = e.CurrentValue;

        // Satıra çift tıklamak ListView'de işareti de değiştirir; yalnızca kutunun kendisine tıklanınca (ya da boşlukla) say.
        if ((Control.MouseButtons & MouseButtons.Left) != 0)
        {
            var nokta = liste.PointToClient(Cursor.Position);
            var hit = liste.HitTest(nokta);
            if (hit.Item != null && hit.Location != ListViewHitTestLocations.StateImage) return;
        }

        if (yukleniyor || isleniyor)
        {
            lblDurum.Text = "Önceki işlem bitmeden değiştirilemez; biraz bekle.";
            return;
        }
        var oge = liste.Items[e.Index];
        BeginInvoke(new Action(() => Degistir(oge, istenen)));
    }

    async void Degistir(ListViewItem oge, bool acik)
    {
        var o = (BaslangicOgesi)oge.Tag;
        if (o.Acik == acik || isleniyor || yukleniyor) return;

        if (!acik && o.Onerilmez)
        {
            string metin = string.Format("{0} {1}.\n\nYine de kapatılsın mı?", o.Gorunen, o.OnerilmezNedeni);
            var cevap = MessageBox.Show(FindForm(), metin, "Kapatma önerilmez", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (cevap != DialogResult.Yes) { lblDurum.Text = o.Gorunen + " açık bırakıldı."; return; }
        }

        isleniyor = true;
        btnYenile.Enabled = false;
        bool tamam;
        string hata = "Ayar yazılamadı; hiçbir şey değişmedi.";
        if (!o.YoneticiGerekir)
        {
            tamam = BaslangicYoneticisi.Ayarla(o, acik);
        }
        else
        {
            lblDurum.Text = "Bu ayar tüm kullanıcılar için; yönetici izni isteniyor...";
            int kod = await YoneticiIleAyarla(o, acik);
            tamam = kod == 0 && BaslangicYoneticisi.OnayDurumu(true, o.OnayAnahtari, o.OnayDegeri) == acik;
            if (tamam) o.Acik = acik;
            else if (kod == -2) hata = "Yönetici izni verilmedi; hiçbir şey değişmedi.";
        }
        isleniyor = false;
        btnYenile.Enabled = true;

        if (!tamam) { lblDurum.Text = hata; return; }
        dolduruluyor = true;
        oge.Checked = acik;
        dolduruluyor = false;
        SatiriYaz(oge);
        DurumYaz(acik
            ? o.Gorunen + " açılışta yeniden başlayacak."
            : o.Gorunen + " bir sonraki açılışta başlamayacak (şu an açıksa kapanmaz).");
    }

    // -2 = izin verilmedi, -1 = başlatılamadı, diğerleri yardımcının çıkış kodu (0 = tamam).
    static Task<int> YoneticiIleAyarla(BaslangicOgesi o, bool acik)
    {
        string argumanlar = "--baslangic " + BaslangicYoneticisi.Tirnakla(o.OnayAnahtari) + " " +
                            BaslangicYoneticisi.Tirnakla(o.OnayDegeri) + " " + (acik ? "1" : "0");
        Process p;
        try
        {
            p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, argumanlar) { UseShellExecute = true, Verb = "runas" });
        }
        catch (System.ComponentModel.Win32Exception) { return Task.FromResult(-2); }
        catch { return Task.FromResult(-1); }
        if (p == null) return Task.FromResult(-1);
        return Task.Run(() =>
        {
            using (p)
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        });
    }
}
