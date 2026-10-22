// Uygulama içi güncelleyici: GitHub Release'lerinde yeni sürüm var mı bakar, sorar, indirir, imzasını doğrular,
// exe'yi değiştirir ve programı yeniden başlatır. Tarayıcı açılmaz.
//  - Güncellemeler yazarın GİZLİ anahtarıyla imzalanır (araclar\imzala.ps1); program yalnızca burada gömülü AÇIK anahtarla
//    doğrulanan dosyayı kurar. Gizli anahtar depoda ASLA yer almaz.
//  - Kullanıcıya sorulmadan kurulmaz. Önde tam ekran oyun varken sorulmaz (oyun bitince sorulur).
// Dosyalar: GuncellemeMotoru (ağ + doğrulama + değiştirme), Guncelleyici (ne zaman bakılır / sorulur),
// GuncellemeFormu (koyu soru ve ilerleme penceresi).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class GuncellemeBilgisi
{
    public Version Surum;          // etiketten (v1.2.3)
    public string Etiket, Notlar, ExeUrl, ImzaUrl;
    public long ExeBoyut;
}

enum DenetimSonucu { Guncel, Yeni, Eksik, Hata }

class DenetimYaniti
{
    public DenetimSonucu Sonuc;
    public GuncellemeBilgisi Bilgi;   // Yeni ve Eksik'te dolu
    public string Hata;               // Hata'da kısa neden
}

// İndirme sırasında form ile motor arasında: iptal isteği ve "artık geri dönüş yok" işareti.
class KurmaDurumu
{
    public bool Iptal, Kritik;
}

static class GuncellemeMotoru
{
    public const string Depo = "sergenmico/SergenPcTemizle";
    public const string ApiAdresi = "https://api.github.com/repos/sergenmico/SergenPcTemizle/releases/latest";
    public const string ExeAdi = "SergenPcTemizle.exe";
    public const string ImzaAdi = "SergenPcTemizle.exe.imza";
    const long EnCokExeBoyutu = 50L * 1024 * 1024;
    const int EnCokImzaBoyutu = 16 * 1024;

    // RSA-3072 açık anahtar (.NET XML biçimi). Karşılığı olan gizli anahtar yalnızca yazarın bilgisayarındadır.
    const string AcikAnahtar =
        "<RSAKeyValue><Modulus>38DdNomK9GkU8XufaHwwvWHg3FrcZs1upBYXzfbHOIP5Fylho44sMytZQcb2ERFINxFUBHKpYoidyLXRo2FF3Z4CMz7Ptie8G8cqsnbzlo0aZ5rRHI7l9ugCpjHuWT2kKQZ4uxzXFIKCqwNk/KBGYJuRfr/9yPhm1jlG2UnYxbNekaOcNi9MzwBGb/uTVOy78/GFs0eadQq2yKvTdXbWmaApV8STIuhClJqB5sLlPGYKm7ZLptVBulELixHHaRasmFJdKQxg5dfxF8okNH/5qZF7KjP54Ytp3oQJbD825egTUXWD1BS/y3pCVmH1PoZtNygRR13CNAYIkFgAVCFkO8cfJU9DTs4OlFA/jgUhXC8FH83ESXGaDFMJZEdFIau72NJBEGCVN0wEFo7TzSb71idzelHO5oH9xjBnDN1aHEwcN5hj7mAdLpomcU5KqcbG4cHFMzJseWS23ekk2KA5+Z+oVC+DhkJ3tIg1GT0E8gAbYMOJ3sDmTRL5359RZl35</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    static HttpWebRequest Istek(string adres, int zamanAsimiMs)
    {
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        var istek = (HttpWebRequest)WebRequest.Create(adres);
        istek.UserAgent = "SergenPcTemizle/" + Surum.Metin;
        istek.Timeout = zamanAsimiMs;
        istek.ReadWriteTimeout = zamanAsimiMs;
        return istek;
    }

    // Arka plan iş parçacığında çağrılır. Hiçbir zaman istisna fırlatmaz.
    public static DenetimYaniti Denetle()
    {
        try
        {
            var istek = Istek(ApiAdresi, 15000);
            istek.Accept = "application/vnd.github+json";
            string json;
            using (var yanit = (HttpWebResponse)istek.GetResponse())
            using (var oku = new StreamReader(yanit.GetResponseStream(), Encoding.UTF8))
                json = oku.ReadToEnd();

            var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            if (d == null) return Hata("beklenmeyen yanıt");
            object etiketNesnesi;
            string etiket = d.TryGetValue("tag_name", out etiketNesnesi) ? etiketNesnesi as string : null;
            Version surum = Surum.Coz(etiket);
            if (surum == null) return Hata("sürüm etiketi anlaşılamadı");
            if (surum <= Surum.Simdiki) return new DenetimYaniti { Sonuc = DenetimSonucu.Guncel };

            var bilgi = new GuncellemeBilgisi { Surum = surum, Etiket = etiket.Trim() };
            object notNesnesi;
            bilgi.Notlar = d.TryGetValue("body", out notNesnesi) ? (notNesnesi as string ?? "") : "";
            object varliklar;
            if (d.TryGetValue("assets", out varliklar) && varliklar is IEnumerable)
            {
                foreach (var o in (IEnumerable)varliklar)
                {
                    var a = o as Dictionary<string, object>;
                    if (a == null) continue;
                    string ad = a.ContainsKey("name") ? a["name"] as string : null;
                    string adres = a.ContainsKey("browser_download_url") ? a["browser_download_url"] as string : null;
                    if (ad == null || adres == null) continue;
                    if (string.Equals(ad, ExeAdi, StringComparison.OrdinalIgnoreCase))
                    {
                        bilgi.ExeUrl = adres;
                        try { bilgi.ExeBoyut = a.ContainsKey("size") ? Convert.ToInt64(a["size"], CultureInfo.InvariantCulture) : 0; } catch { }
                    }
                    else if (string.Equals(ad, ImzaAdi, StringComparison.OrdinalIgnoreCase)) bilgi.ImzaUrl = adres;
                }
            }
            bool tamam = !string.IsNullOrEmpty(bilgi.ExeUrl) && !string.IsNullOrEmpty(bilgi.ImzaUrl);
            return new DenetimYaniti { Sonuc = tamam ? DenetimSonucu.Yeni : DenetimSonucu.Eksik, Bilgi = bilgi };
        }
        catch (WebException w)
        {
            var r = w.Response as HttpWebResponse;
            if (r != null && r.StatusCode == HttpStatusCode.NotFound)
                return new DenetimYaniti { Sonuc = DenetimSonucu.Guncel };   // henüz yayın yok
            return Hata(KisaNeden(w));
        }
        catch (Exception e) { return Hata(KisaNeden(e)); }
    }

    static DenetimYaniti Hata(string neden)
    {
        return new DenetimYaniti { Sonuc = DenetimSonucu.Hata, Hata = neden };
    }

    public static string KisaNeden(Exception e)
    {
        var w = e as WebException;
        if (w != null)
        {
            var r = w.Response as HttpWebResponse;
            if (r != null) return "sunucu yanıtı " + (int)r.StatusCode;
            switch (w.Status)
            {
                case WebExceptionStatus.Timeout: return "zaman aşımı";
                case WebExceptionStatus.NameResolutionFailure:
                case WebExceptionStatus.ConnectFailure: return "internet bağlantısı yok";
                case WebExceptionStatus.SecureChannelFailure:
                case WebExceptionStatus.TrustFailure: return "güvenli bağlantı kurulamadı";
            }
        }
        string m = e.Message ?? "bilinmeyen hata";
        m = m.Replace("\r", " ").Replace("\n", " ").Trim();
        return m.Length > 80 ? m.Substring(0, 79) + "…" : m;
    }

    // ---- İndirme ve kurma (arka plan iş parçacığı) ----

    // Başarıda null, aksi halde kullanıcıya yazılacak kısa hata. Yarım dosyalar her durumda silinir.
    // "Kritik" işareti konunca (değiştirme başladı) form kapatılamaz; başarıdan sonra da işaret kalır.
    public static string Kur(GuncellemeBilgisi b, KurmaDurumu durum, Action<int, string> ilerleme)
    {
        string exe = Application.ExecutablePath;
        string klasor = Path.GetDirectoryName(exe);
        string yeni = exe + ".yeni", parca = exe + ".yeni.part", eski = exe + ".eski";
        try
        {
            // 1) Klasöre yazılabiliyor mu?
            string deneme = Path.Combine(klasor, "SergenPcTemizle.yazma-denemesi.tmp");
            try { File.WriteAllText(deneme, "x"); File.Delete(deneme); }
            catch
            {
                return "Program yazılamayan bir klasörde (ör. Program Files). Güncellemek için programı Masaüstü ya da Belgeler gibi bir klasöre taşı.";
            }
            if (b.ExeBoyut <= 0 || b.ExeBoyut > EnCokExeBoyutu) return "Güncelleme dosyasının boyutu beklenmedik; kurulmadı.";
            if (!Guvenli(b.ExeUrl) || !Guvenli(b.ImzaUrl)) return "Güncelleme adresi güvenli değil; kurulmadı.";

            // 2) İndir: önce küçük imza dosyası, sonra exe (.part → .yeni).
            ilerleme(0, "İndiriliyor...");
            byte[] imzaBaytlari;
            using (var bellek = new MemoryStream())
            {
                Indir(b.ImzaUrl, EnCokImzaBoyutu, bellek, durum, null);
                imzaBaytlari = bellek.ToArray();
            }
            using (var dosya = new FileStream(parca, FileMode.Create, FileAccess.Write, FileShare.None))
                Indir(b.ExeUrl, b.ExeBoyut, dosya, durum, y => ilerleme(y, "İndiriliyor... %" + y));
            if (new FileInfo(parca).Length != b.ExeBoyut) return "İndirilen dosya eksik geldi; kurulmadı. Biraz sonra yeniden dene.";
            if (File.Exists(yeni)) File.Delete(yeni);
            File.Move(parca, yeni);

            // 3) İmza doğrula.
            ilerleme(100, "Doğrulanıyor...");
            if (!ImzaDogru(File.ReadAllBytes(yeni), imzaBaytlari))
                return "İmza doğrulanamadı; güncelleme kurulmadı (dosya bozuk ya da değiştirilmiş).";

            // 4) Sürüm doğrula: etiketteki sürüm olmalı ve şimdikinden büyük olmalı (eski sürüme düşürme saldırısı olmasın).
            Version dosyaSurumu = Surum.Coz(FileVersionInfo.GetVersionInfo(yeni).FileVersion);
            if (dosyaSurumu == null || dosyaSurumu <= Surum.Simdiki
                || dosyaSurumu.Major != b.Surum.Major || dosyaSurumu.Minor != b.Surum.Minor || dosyaSurumu.Build != b.Surum.Build)
                return "İndirilen dosyanın sürümü beklenenle uyuşmuyor; güncelleme kurulmadı.";

            // 5) Değiştir. Çalışan exe'nin adı değiştirilebilir; yeni dosya eski adın yerine konur.
            lock (durum)
            {
                if (durum.Iptal) return "İptal edildi.";
                durum.Kritik = true;
            }
            ilerleme(100, "Kuruluyor...");
            if (File.Exists(eski)) File.Delete(eski);
            File.Move(exe, eski);
            try { File.Move(yeni, exe); }
            catch (Exception e)
            {
                try { File.Move(eski, exe); } catch { }
                lock (durum) durum.Kritik = false;
                return "Yeni dosya yerine konamadı: " + GuncellemeMotoru.KisaNeden(e);
            }
            return null;
        }
        catch (OperationCanceledException) { return "İptal edildi."; }
        catch (Exception e)
        {
            lock (durum) durum.Kritik = false;
            return "Güncelleme başarısız: " + KisaNeden(e);
        }
        finally
        {
            try { if (File.Exists(parca)) File.Delete(parca); } catch { }
            try { if (File.Exists(yeni)) File.Delete(yeni); } catch { }
        }
    }

    static bool Guvenli(string adres)
    {
        Uri u;
        return Uri.TryCreate(adres, UriKind.Absolute, out u) && u.Scheme == Uri.UriSchemeHttps;
    }

    static void Indir(string adres, long enCok, Stream hedef, KurmaDurumu durum, Action<int> yuzde)
    {
        var istek = Istek(adres, 30000);
        istek.Accept = "application/octet-stream";
        using (var yanit = (HttpWebResponse)istek.GetResponse())
        {
            long uzunluk = yanit.ContentLength;
            if (uzunluk > enCok) throw new InvalidDataException("dosya beklenenden büyük");
            using (var akis = yanit.GetResponseStream())
            {
                var tampon = new byte[32768];
                long toplam = 0;
                int sonYuzde = -1;
                int n;
                while ((n = akis.Read(tampon, 0, tampon.Length)) > 0)
                {
                    if (durum.Iptal) throw new OperationCanceledException();
                    toplam += n;
                    if (toplam > enCok) throw new InvalidDataException("dosya beklenenden büyük");
                    hedef.Write(tampon, 0, n);
                    if (yuzde != null && uzunluk > 0)
                    {
                        int y = (int)(toplam * 100 / uzunluk);
                        if (y != sonYuzde) { sonYuzde = y; yuzde(Math.Min(100, y)); }
                    }
                }
            }
        }
    }

    // İmza dosyası Base64 metin (boşluklar ve olası BOM yok sayılır).
    static bool ImzaDogru(byte[] exe, byte[] imzaDosyasi)
    {
        try
        {
            string metin = new string(Encoding.UTF8.GetString(imzaDosyasi).Where(c => !char.IsWhiteSpace(c) && c != '﻿').ToArray());
            byte[] imza = Convert.FromBase64String(metin);
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(AcikAnahtar);
                return rsa.VerifyData(exe, "SHA256", imza);
            }
        }
        catch { return false; }
    }

    // Kurmadan sonra: yeni exe'yi başlatır (eski pid kapanınca açılır). Hata varsa kısa metin, yoksa null.
    public static string YenidenBaslat(bool gizli)
    {
        try
        {
            int pid;
            using (var p = Process.GetCurrentProcess()) pid = p.Id;
            string arg = "--guncellendi " + pid + (gizli ? " --arkaplan" : "");
            Process.Start(new ProcessStartInfo(Application.ExecutablePath, arg)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath)
            });
            return null;
        }
        catch (Exception e) { return "Güncelleme kuruldu ama program yeniden başlatılamadı (" + KisaNeden(e) + "). Programı kapatıp yeniden aç."; }
    }

    // ---- Açılışta ----

    // Önceki güncellemeden kalan yan dosyalar. .eski, eski sürüm kapanana kadar silinemez: birkaç deneme.
    public static void ArtiklariSil()
    {
        string exe = Application.ExecutablePath;
        ThreadPool.QueueUserWorkItem(delegate
        {
            foreach (var son in new[] { ".yeni", ".yeni.part" })
                try { if (File.Exists(exe + son)) File.Delete(exe + son); } catch { }
            for (int i = 0; i < 8; i++)
            {
                try { if (!File.Exists(exe + ".eski")) return; File.Delete(exe + ".eski"); return; }
                catch { Thread.Sleep(1500); }
            }
        });
    }

    // "--guncellendi <pid>": eski örneğin kapanmasını en çok 30 sn bekler (tek örnek kontrolünden önce).
    public static void EskiOrnegiBekle(int pid)
    {
        try
        {
            using (var p = Process.GetProcessById(pid))
                p.WaitForExit(30000);
        }
        catch { }
    }
}

// Ne zaman bakılır, ne zaman sorulur. Arayüz iş parçacığında çalışır.
class Guncelleyici : IDisposable
{
    public const string AyarDenetle = "guncelleme_denetle";
    const string AyarSon = "guncelleme_son";
    static readonly TimeSpan EnAzAralik = TimeSpan.FromHours(20);
    static readonly DateTime UnixBasi = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Program.Main "--guncellendi" görürse kurar.
    public static bool Guncellendi;
    public static Guncelleyici Ornek;

    readonly AnaForm ana;
    readonly BellekBekcisi bekci;
    readonly System.Windows.Forms.Timer zamanlayici = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer bilgiZamanlayici = new System.Windows.Forms.Timer();
    GuncellemeBilgisi bekleyen;     // bulundu, henüz sorulmadı / yanıtlanmadı
    Version sonraSurum, balonSurumu;
    bool denetliyor, soruAcik, kapandi;

    public Guncelleyici(AnaForm ana, BellekBekcisi bekci)
    {
        this.ana = ana;
        this.bekci = bekci;
        Ornek = this;

        // İlk bakış ~60 sn sonra; yeni güncellenmişse ilk bakış atlanır. Sonra 6 saatte bir (oyun varsa 5 dakikada bir).
        zamanlayici.Interval = Guncellendi ? (int)TimeSpan.FromHours(6).TotalMilliseconds : 60000;
        zamanlayici.Tick += delegate { Tik(); };
        zamanlayici.Start();

        ana.VisibleChanged += delegate
        {
            if (ana.Visible && bekleyen != null && ana.IsHandleCreated)
                ana.BeginInvoke(new Action(delegate { Sor(); }));
        };

        if (Guncellendi)
        {
            bilgiZamanlayici.Interval = 3000;
            bilgiZamanlayici.Tick += delegate { bilgiZamanlayici.Stop(); GuncellendiBildir(); };
            bilgiZamanlayici.Start();
        }
    }

    void GuncellendiBildir()
    {
        if (kapandi) return;
        string metin = "v" + Surum.Metin + " sürümüne güncellendi.";
        if (ana.Visible) ana.BilgiYaz(metin);
        else bekci.Bildir("Sergen PC Temizle", metin, ToolTipIcon.Info);
    }

    public static bool Acik { get { return Ayarlar.Oku(AyarDenetle, true); } }

    // Son başarılı denetim (yerel saat); hiç yoksa null.
    public static DateTime? SonDenetim
    {
        get
        {
            long s;
            if (!long.TryParse(Ayarlar.OkuMetin(AyarSon, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out s) || s <= 0) return null;
            try { return UnixBasi.AddSeconds(s).ToLocalTime(); } catch { return null; }
        }
    }

    static void SonuYaz()
    {
        long s = (long)(DateTime.UtcNow - UnixBasi).TotalSeconds;
        Ayarlar.YazMetin(AyarSon, s.ToString(CultureInfo.InvariantCulture));
    }

    void Tik()
    {
        if (kapandi) return;
        int sonraki = (int)TimeSpan.FromHours(6).TotalMilliseconds;
        try
        {
            if (!Acik) { zamanlayici.Interval = sonraki; return; }
            if (bekleyen != null)
            {
                if (Sor()) sonraki = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;   // oyun sürüyor
            }
            else if (!denetliyor)
            {
                var son = SonDenetim;
                if (son == null || DateTime.Now - son.Value >= EnAzAralik || son.Value > DateTime.Now)
                    DenetleVeSor();
            }
        }
        catch { }
        zamanlayici.Interval = sonraki;
    }

    async void DenetleVeSor()
    {
        denetliyor = true;
        try
        {
            var y = await Task.Run(() => GuncellemeMotoru.Denetle());
            if (kapandi) return;
            if (y.Sonuc != DenetimSonucu.Hata) SonuYaz();
            if (y.Sonuc == DenetimSonucu.Yeni)
            {
                bekleyen = y.Bilgi;
                sonraSurum = null;   // yeni denetimde "Sonra" unutulur: yeniden sorulur
                if (Sor()) zamanlayici.Interval = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;
            }
        }
        catch { }
        finally { denetliyor = false; }
    }

    // "Şimdi denetle" (Hakkında penceresi): sonucu hemen döndürür; sormayı çağıran yapar.
    public async Task<DenetimYaniti> SimdiDenetle()
    {
        var y = await Task.Run(() => GuncellemeMotoru.Denetle());
        if (y.Sonuc != DenetimSonucu.Hata) SonuYaz();
        if (y.Sonuc == DenetimSonucu.Yeni) { bekleyen = y.Bilgi; sonraSurum = null; }
        return y;
    }

    // Tepsi menüsündeki "Güncellemeleri denetle": güncelse ya da denetlenemezse balonla söyler, yeni sürüm varsa sorar.
    bool tepsidenDenetliyor;
    public async void TepsidenDenetle()
    {
        if (tepsidenDenetliyor || kapandi) return;
        tepsidenDenetliyor = true;
        try
        {
            var y = await SimdiDenetle();
            if (kapandi) return;
            if (y.Sonuc == DenetimSonucu.Yeni) FormuAc(y.Bilgi, null);
            else if (y.Sonuc == DenetimSonucu.Guncel) bekci.Bildir("Sergen PC Temizle", "Güncelsin (v" + Surum.Metin + ").", ToolTipIcon.Info);
            else if (y.Sonuc == DenetimSonucu.Eksik) bekci.Bildir("Sergen PC Temizle", "Yeni sürüm var ama dosyaları eksik; sonra yeniden dene.", ToolTipIcon.Info);
            else bekci.Bildir("Güncelleme denetlenemedi", y.Hata ?? "bilinmeyen hata", ToolTipIcon.Warning);
        }
        catch { }
        finally { tepsidenDenetliyor = false; }
    }

    // true: oyun sürüyor, sorma ertelendi (5 dakika sonra yeniden bakılacak).
    bool Sor()
    {
        var b = bekleyen;
        if (b == null || soruAcik || kapandi || !Acik) return false;
        if (sonraSurum != null && sonraSurum == b.Surum) return false;
        if (bekci.OyunSuruyor || TamEkran.Var()) return true;
        if (ana.Visible)
        {
            FormuAc(b, null);
            return false;
        }
        if (balonSurumu != b.Surum)
        {
            balonSurumu = b.Surum;
            bekci.Bildir("Yeni sürüm var (v" + SurumMetni(b.Surum) + ")", "Güncellemek için tıkla.", ToolTipIcon.Info,
                         delegate { FormuAc(b, null); });
        }
        return false;
    }

    public static string SurumMetni(Version v)
    {
        return v.Build > 0 || v.Revision > 0 ? v.Major + "." + v.Minor + "." + v.Build : v.Major + "." + v.Minor + ".0";
    }

    // Güncelleme sorusunu açar (aynı anda tek pencere). Kurulursa program kapanır, yenisi açılır.
    public void FormuAc(GuncellemeBilgisi b, IWin32Window sahip)
    {
        if (b == null || soruAcik || kapandi) return;
        soruAcik = true;
        bool yeniden = false;
        try
        {
            if (!ana.Visible) bekci.Goster();
            using (var f = new GuncellemeFormu(b, ana.Visible))
            {
                f.ShowDialog(sahip ?? (ana.Visible ? ana : null));
                yeniden = f.YenidenBaslat;
                if (!yeniden) sonraSurum = b.Surum;
            }
        }
        catch { }
        finally { soruAcik = false; }
        if (yeniden) bekci.Cik();
    }

    public void Dispose()
    {
        kapandi = true;
        try { zamanlayici.Stop(); zamanlayici.Dispose(); bilgiZamanlayici.Dispose(); } catch { }
    }
}

class GuncellemeFormu : Form
{
    readonly GuncellemeBilgisi bilgi;
    readonly bool gizliAcilis;     // ana pencere gizliyken (tepsi) başlatıldıysa yeni program da gizli açılır
    readonly KurmaDurumu durum = new KurmaDurumu();
    readonly Label lblDurum = new Label();
    readonly Panel cubukDis = new Panel(), cubukIc = new Panel();
    readonly Button btnGuncelle, btnSonra;
    bool calisiyor;

    public bool YenidenBaslat;

    public GuncellemeFormu(GuncellemeBilgisi bilgi, bool anaGorunur)
    {
        this.bilgi = bilgi;
        gizliAcilis = !anaGorunur;
        Text = "Güncelleme";
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Renk.Arka;
        ForeColor = Renk.Yazi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 400);
        Icon = Logo.Simge();

        var baslik = new Label
        {
            Text = "Yeni sürüm: v" + Guncelleyici.SurumMetni(bilgi.Surum) + " (şu an v" + Surum.Metin + ")",
            Left = 20, Top = 16, Width = 480, Height = 28, Font = new Font("Segoe UI", 12f, FontStyle.Bold)
        };
        var notEtiketi = new Label { Text = "Sürüm notları", Left = 20, Top = 52, Width = 480, Height = 20, ForeColor = Renk.Soluk };
        var notlar = new TextBox
        {
            Left = 20, Top = 74, Width = 480, Height = 190, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = Renk.Panel, ForeColor = Renk.Yazi, BorderStyle = BorderStyle.FixedSingle,
            Text = string.IsNullOrWhiteSpace(bilgi.Notlar) ? "(Sürüm notu yok.)" : DuzMetin(bilgi.Notlar)
        };
        notlar.Select(0, 0);

        cubukDis.SetBounds(20, 284, 480, 10);
        cubukDis.BackColor = Renk.Cizgi;
        cubukDis.Visible = false;
        cubukIc.SetBounds(0, 0, 0, 10);
        cubukIc.BackColor = Renk.Yesil;
        cubukDis.Controls.Add(cubukIc);
        lblDurum.SetBounds(20, 300, 480, 44);
        lblDurum.ForeColor = Renk.Yazi;
        lblDurum.Visible = false;

        btnGuncelle = Dugme("Güncelle", true);
        btnSonra = Dugme("Sonra", false);
        btnGuncelle.SetBounds(290, 352, 100, 32);
        btnSonra.SetBounds(400, 352, 100, 32);
        btnGuncelle.Click += delegate { Guncelle(); };
        btnSonra.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        CancelButton = btnSonra;
        AcceptButton = btnGuncelle;

        Controls.AddRange(new Control[] { baslik, notEtiketi, notlar, cubukDis, lblDurum, btnGuncelle, btnSonra });

        FormClosing += delegate(object s, FormClosingEventArgs e)
        {
            lock (durum)
            {
                // Dosyalar değiştirilirken kapatılmasın; başarılı kurulumdan sonra (YenidenBaslat) kapanmalı, yoksa
                // ShowDialog dönmez, eski program kapanmaz ve yeni sürüm onu beklerken takılır.
                if (durum.Kritik && !YenidenBaslat) { e.Cancel = true; return; }
                if (calisiyor) durum.Iptal = true;
            }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int koyu = 1; Yerel.DwmSetWindowAttribute(Handle, 20, ref koyu, 4); } catch { }
    }

    // GitHub sürüm notları Markdown: metin kutusunda işaretler (**kalın**, `kod`, # başlık, - madde) okunur hâle getirilir.
    static string DuzMetin(string md)
    {
        var satirlar = md.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < satirlar.Length; i++)
        {
            string s = satirlar[i].Replace("**", "").Replace("`", "");
            string kirpik = s.TrimStart();
            if (kirpik.StartsWith("#")) s = kirpik.TrimStart('#').TrimStart();
            else if (kirpik.StartsWith("- ") || kirpik.StartsWith("* ")) s = s.Substring(0, s.Length - kirpik.Length) + "• " + kirpik.Substring(2);
            satirlar[i] = s;
        }
        return string.Join("\r\n", satirlar);
    }

    static Button Dugme(string metin, bool vurgu)
    {
        var b = new Button
        {
            Text = metin, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = vurgu ? Renk.Yesil : Renk.Panel, ForeColor = vurgu ? Color.White : Renk.Yazi,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = vurgu ? Renk.Yesil : Renk.Cizgi;
        return b;
    }

    void Ilerleme(int yuzde, string metin)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(delegate
            {
                if (IsDisposed) return;
                cubukIc.Width = cubukDis.Width * Math.Max(0, Math.Min(100, yuzde)) / 100;
                lblDurum.Text = metin;
            }));
        }
        catch { }
    }

    async void Guncelle()
    {
        if (calisiyor) return;
        calisiyor = true;
        btnGuncelle.Visible = false;
        btnSonra.Visible = false;
        cubukDis.Visible = true;
        lblDurum.Visible = true;
        lblDurum.ForeColor = Renk.Yazi;
        lblDurum.Text = "Başlıyor...";
        AcceptButton = null;
        string hata;
        try { hata = await Task.Run(() => GuncellemeMotoru.Kur(bilgi, durum, Ilerleme)); }
        catch (Exception e) { hata = "Güncelleme başarısız: " + GuncellemeMotoru.KisaNeden(e); }
        calisiyor = false;
        if (IsDisposed) return;

        if (hata == null)
        {
            lblDurum.Text = "Yeniden başlatılıyor...";
            hata = GuncellemeMotoru.YenidenBaslat(gizliAcilis);
            if (hata == null)
            {
                YenidenBaslat = true;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            lock (durum) durum.Kritik = false;
        }
        cubukDis.Visible = false;
        lblDurum.ForeColor = Renk.Turuncu;
        lblDurum.Text = hata;
        btnSonra.Text = "Kapat";
        btnSonra.Visible = true;
        CancelButton = btnSonra;
    }
}
