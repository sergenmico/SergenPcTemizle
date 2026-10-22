// Cokme.cs — Windows olay günlüğünden çökme geçmişi. Yalnızca OKUR (Application ve System günlükleri; normal
// kullanıcı izniyle okunabilir). Program çökmesi / donması, "sanal bellek doldu" uyarısı, ekran kartı sürücüsü
// çökmesi, beklenmedik kapanma, mavi ekran ve donanım hatası kayıtlarını sade Türkçe olaylara çevirir.
// Her sorgu ayrı try/catch içinde; günlük okunamazsa o bölüm boş kalır, asla exception fırlatılmaz.

using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

class CokmeOlayi
{
    public DateTime Zaman;
    public string Tur, Uygulama, Ayrinti;
    public int OlayKimligi;
    public bool BellekDoluyken, Ciddi;

    // Sözleşmeye ek alanlar (arayüz ve destek raporu için):
    public string Kategori;     // "cokme", "donma", "bellek", "ekran", "maviekran", "kapanma", "donanim"
    public string Kaynak;       // olay sağlayıcısı (ör. "Application Error")
    public string HataKodu;     // "0xC0000005" (program çökmesi) ya da mavi ekran kodu; yoksa null
    public string Yol;          // çöken programın tam yolu (biliniyorsa)
    public bool Onemsiz;        // düşük önemli kayıt (0x80000003 kesme noktası): listede soluk, özet sayılarına girmez
}

static class CokmeGecmisi
{
    const int EnFazlaToplam = 500;
    const int BellekPenceresiDk = 15;   // çökme ile "sanal bellek doldu" uyarısı arası en çok bu kadar dakika

    static int sonGun = 7;              // Ozet() metni için son Oku() çağrısının gün sayısı
    static List<CokmeOlayi> sonListe;   // son Oku() sonucu; Ozet() bu listeyle çağrılırsa kesilen türleri bilir
    static HashSet<string> kesilenler = new HashSet<string>();   // sınıra takılan (eksik okunan) kategoriler

    // Program çökmesi hata kodlarının sade anlamı. bellek = bu kod tek başına "bellek yetmedi" demek.
    static readonly Dictionary<string, string> KodAnlami = new Dictionary<string, string>
    {
        { "c0000005", "bellek erişim hatası (program hatası; bellek dolu demek değil)" },
        { "c0000409", "güvenlik kontrolü / ani kapanma" },
        { "c00000fd", "yığın taşması" },
        { "e0434352", ".NET hatası" },
        { "c000012d", "SANAL BELLEK DOLDU" },
        { "c0000017", "SANAL BELLEK DOLDU" },
        { "c00001ad", "SANAL BELLEK DOLDU, bellek ayrılamadı" },
        { "e0000008", "bellek yetmedi, Chrome tabanlı program" },
        { "80000003", "kesme noktası; çoğu zaman zararsız, özete sayılmaz" },
        { "c0000374", "program belleği bozuldu (öbek)" },
        { "c0000142", "başlatılamadı" },
        { "e06d7363", "program içi hata, C++" },
        { "c000041d", "işlenmemiş hata" },
    };
    static readonly HashSet<string> BellekKodlari = new HashSet<string> { "c000012d", "c0000017", "c00001ad", "e0000008" };
    // 0x80000003 (kesme noktası): program adından bağımsız olarak düşük önemli. Chromium/CEF tabanlı yardımcılar ve
    // hata ayıklama denetimleri bunu sık yazar, çoğu zaman kullanıcıya etkisi yoktur. Listede kalır, özete sayılmaz.
    const string KesmeNoktasi = "80000003";

    // Önemsiz kayıt mı? Alan dışarıdan doldurulmamış olaylar için hata koduna da bakılır.
    public static bool OnemsizMi(CokmeOlayi o)
    {
        return o != null && (o.Onemsiz || (o.Kategori == "cokme" && string.Equals(o.HataKodu, "0x" + KesmeNoktasi, StringComparison.OrdinalIgnoreCase)));
    }

    // Sık görülen mavi ekran kodları (bugcheck) için kısa ipucu.
    static readonly Dictionary<uint, string> MaviEkranAnlami = new Dictionary<uint, string>
    {
        { 0x0A, "sürücü hatası" },
        { 0x19, "bellek bozulması" },
        { 0x1A, "bellek yönetimi hatası (RAM sorunu olabilir)" },
        { 0x3B, "sistem hizmeti hatası (çoğu zaman sürücü)" },
        { 0x50, "bellek hatası (RAM ya da sürücü)" },
        { 0x7E, "sürücü hatası" },
        { 0x9F, "uyku / güç geçişinde sürücü hatası" },
        { 0xD1, "sürücü hatası" },
        { 0xEF, "kritik Windows işlemi kapandı" },
        { 0x101, "işlemci yanıt vermedi" },
        { 0x116, "ekran kartı sürücüsü kurtarılamadı" },
        { 0x117, "ekran kartı sürücüsü yanıt vermedi" },
        { 0x124, "donanım hatası (işlemci, RAM, ısı ya da güç)" },
        { 0x133, "sürücü uzun süre yanıt vermedi" },
        { 0x139, "çekirdek güvenlik kontrolü (çoğu zaman sürücü)" },
        { 0x1000007E, "sürücü hatası" },
        { 0x1000008E, "sürücü ya da bellek hatası" },
    };

    // ---------------------------------------------------------------------------------------------------------
    // Son "gun" günün olayları, yeniden eskiye. Hata olursa boş (ya da eksik) liste döner.
    public static List<CokmeOlayi> Oku(int gun)
    {
        var sonuc = new List<CokmeOlayi>();
        try
        {
            if (gun < 1) gun = 1;
            sonGun = gun;
            DateTime sinir = DateTime.Now.AddDays(-gun);
            long ms = (long)gun * 86400000L;
            long msPay = ms + BellekPenceresiDk * 60000L;   // pencerenin hemen dışındaki uyarı da eşleşsin

            // "Sanal bellek doldu" uyarıları önce: hem listeye girer hem çökmeleri işaretlemekte kullanılır.
            var bellekUyarilari = Sorgula("System",
                "*[System[Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and EventID=2004 and " + Zaman(msPay) + "]]",
                100, BellekDoldu);

            var programlar = Sorgula("Application",
                "*[System[Provider[@Name='Application Error'] and EventID=1000 and " + Zaman(ms) + "]]", 300, ProgramCokmesi);
            programlar.AddRange(Sorgula("Application",
                "*[System[Provider[@Name='Application Hang'] and EventID=1002 and " + Zaman(ms) + "]]", 100, ProgramDonmasi));

            var ciddiler = new List<CokmeOlayi>();
            ciddiler.AddRange(Sorgula("System",
                "*[System[Provider[@Name='Display'] and EventID=4101 and " + Zaman(ms) + "]]", 50, EkranSurucusu));
            var maviEkranlar = Sorgula("System",
                "*[System[(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='BugCheck']) and EventID=1001 and " + Zaman(ms) + "]]",
                50, MaviEkran);
            ciddiler.AddRange(maviEkranlar);
            // Kernel-Power 41: aynı yeniden başlatma için ayrıca mavi ekran kaydı varsa iki kez sayma.
            foreach (var k in Sorgula("System",
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and " + Zaman(ms) + "]]", 50, BeklenmedikKapanma))
                if (!maviEkranlar.Any(m => Math.Abs((m.Zaman - k.Zaman).TotalMinutes) <= 10)) ciddiler.Add(k);
            // WHEA 18/20 = ölümcül donanım hatası, 1 = hata düzeyinde donanım kaydı. 19/17 (düzeltilmiş hata) gürültü, alınmaz.
            ciddiler.AddRange(Sorgula("System",
                "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=18 or EventID=20 or (EventID=1 and (Level=1 or Level=2))) and " + Zaman(ms) + "]]",
                50, DonanimHatasi));

            // Bir çökme / donma "sanal bellek doldu" uyarısının ±15 dk içindeyse bellek doluyken olmuştur.
            foreach (var o in programlar)
            {
                if (o.BellekDoluyken) continue;
                foreach (var b in bellekUyarilari)
                    if (Math.Abs((o.Zaman - b.Zaman).TotalMinutes) <= BellekPenceresiDk) { o.BellekDoluyken = true; break; }
            }
            foreach (var o in ciddiler)   // ekran kartı sürücüsü çökmesi de bellek dolunca sık görülür
            {
                if (o.Kategori != "ekran") continue;
                foreach (var b in bellekUyarilari)
                    if (Math.Abs((o.Zaman - b.Zaman).TotalMinutes) <= BellekPenceresiDk) { o.BellekDoluyken = true; break; }
            }

            ciddiler.AddRange(bellekUyarilari.Where(b => b.Zaman >= sinir));
            // Toplam sınır: önce ciddi olayların hepsi, kalan yer en yeni program olaylarına.
            ciddiler = ciddiler.OrderByDescending(o => o.Zaman).Take(EnFazlaToplam).ToList();
            sonuc.AddRange(ciddiler);
            sonuc.AddRange(programlar.OrderByDescending(o => o.Zaman).Take(Math.Max(0, EnFazlaToplam - ciddiler.Count)));
            sonuc = sonuc.OrderByDescending(o => o.Zaman).ToList();

            // Bir sorgu sınırına ya da toplam sınıra takıldıysa o türün sayısı "en az" olarak yazılır.
            var kesik = new HashSet<string>();
            if (programlar.Count(o => o.Kategori == "cokme") >= 300) kesik.Add("cokme");
            if (programlar.Count(o => o.Kategori == "donma") >= 100) kesik.Add("donma");
            if (bellekUyarilari.Count >= 100) kesik.Add("bellek");
            foreach (var g in ciddiler.GroupBy(o => o.Kategori)) if (g.Count() >= 50) kesik.Add(g.Key);
            if (sonuc.Count >= EnFazlaToplam) { kesik.Add("cokme"); kesik.Add("donma"); }
            kesilenler = kesik;
            sonListe = sonuc;
        }
        catch { }
        return sonuc;
    }

    static string Zaman(long ms)
    {
        return "TimeCreated[timediff(@SystemTime) <= " + ms.ToString(CultureInfo.InvariantCulture) + "]";
    }

    // Bir sorguyu yeniden eskiye okur; her kaydı cevirici'ye verir (null dönerse atlar). Hata: o ana kadarkiler.
    static List<CokmeOlayi> Sorgula(string gunluk, string xpath, int enFazla, Func<EventRecord, CokmeOlayi> cevirici)
    {
        var liste = new List<CokmeOlayi>();
        try
        {
            var sorgu = new EventLogQuery(gunluk, PathType.LogName, xpath);
            sorgu.ReverseDirection = true;   // en yeniden başla
            using (var okuyucu = new EventLogReader(sorgu))
            {
                while (liste.Count < enFazla)
                {
                    EventRecord k;
                    try { k = okuyucu.ReadEvent(); }
                    catch { break; }
                    if (k == null) break;
                    using (k)
                    {
                        try
                        {
                            var o = cevirici(k);
                            if (o == null) continue;
                            if (o.Zaman == default(DateTime)) o.Zaman = k.TimeCreated.HasValue ? k.TimeCreated.Value : DateTime.Now;
                            o.OlayKimligi = k.Id;
                            if (o.Kaynak == null) o.Kaynak = k.ProviderName;
                            liste.Add(o);
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        return liste;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Olay türleri

    // Application Error 1000. Özellikler (Windows 10/11): 0 uygulama, 1 sürüm, 2 zaman damgası, 3 modül,
    // 4 modül sürümü, 5 modül zaman damgası, 6 hata kodu (onaltılı, "0x"siz), 7 uzaklık, 8 pid, 9 başlangıç,
    // 10 uygulama yolu, 11 modül yolu. Eski sürümlerde dizinler kayabilir; uymazsa mesaj metninden çıkarılır.
    static CokmeOlayi ProgramCokmesi(EventRecord k)
    {
        var o = new CokmeOlayi { Tur = "Program çöktü", Kategori = "cokme" };
        var p = k.Properties;
        string uygulama = null, modul = null, kod = null, yol = null;
        if (p.Count >= 7)
        {
            uygulama = Metin(p[0]);
            modul = Metin(p[3]);
            kod = KodDuzelt(Metin(p[6]));
            if (p.Count >= 11) yol = Metin(p[10]);
        }
        if (kod == null || string.IsNullOrEmpty(uygulama))
        {
            string m = Aciklama(k);
            if (m != null)
            {
                if (string.IsNullOrEmpty(uygulama))
                {
                    var a = Regex.Match(m, @"([^\s,:\\/]+\.exe)\b", RegexOptions.IgnoreCase);
                    if (a.Success) uygulama = a.Groups[1].Value;
                }
                if (string.IsNullOrEmpty(modul))
                {
                    var a = Regex.Match(m, @"([^\s,:\\/]+\.(?:dll|exe|sys))\b", RegexOptions.IgnoreCase);
                    var b = a.Success ? a.NextMatch() : a;   // ilki uygulamanın kendisi, ikincisi modül
                    if (b.Success) modul = b.Groups[1].Value;
                }
                if (kod == null)
                {
                    // Etiketli satır (Türkçe / İngilizce / Almanca); olmazsa üçüncü 8 haneli onaltılı sayı
                    // (uygulama zaman damgası, modül zaman damgası, hata kodu sırası).
                    var a = Regex.Match(m, @"(?:Özel durum kodu|Exception code|Ausnahmecode)\s*:\s*(0x[0-9a-fA-F]{1,8})\b", RegexOptions.IgnoreCase);
                    if (a.Success) kod = KodDuzelt(a.Groups[1].Value);
                    else
                    {
                        var hepsi = Regex.Matches(m, @"\b0x([0-9a-fA-F]{8})\b");
                        if (hepsi.Count >= 3) kod = KodDuzelt(hepsi[2].Groups[1].Value);
                    }
                }
            }
        }

        o.Uygulama = string.IsNullOrEmpty(uygulama) ? "Bilinmeyen program" : uygulama;
        o.Yol = string.IsNullOrEmpty(yol) ? null : yol;
        var ayrinti = new StringBuilder();
        if (kod != null)
        {
            o.HataKodu = "0x" + kod.ToUpperInvariant();
            ayrinti.Append("Hata kodu ").Append(o.HataKodu);
            string anlam;
            if (KodAnlami.TryGetValue(kod, out anlam)) ayrinti.Append(" (").Append(anlam).Append(")");
            if (BellekKodlari.Contains(kod)) o.BellekDoluyken = true;
            if (kod == KesmeNoktasi) o.Onemsiz = true;
        }
        if (!string.IsNullOrEmpty(modul) && !string.Equals(modul, "unknown", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(modul, o.Uygulama, StringComparison.OrdinalIgnoreCase))
        {
            if (ayrinti.Length > 0) ayrinti.Append(", ");
            ayrinti.Append("hatalı modül: ").Append(modul);
        }
        o.Ayrinti = ayrinti.Length > 0 ? ayrinti.ToString() : "Ayrıntı yok.";
        return o;
    }

    // Application Hang 1002. Özellikler: 0 uygulama, 1 sürüm, 2 pid, 3 başlangıç, 4 süre, 5 yol.
    static CokmeOlayi ProgramDonmasi(EventRecord k)
    {
        var o = new CokmeOlayi { Tur = "Program yanıt vermedi", Kategori = "donma" };
        var p = k.Properties;
        string uygulama = p.Count >= 1 ? Metin(p[0]) : null;
        if (string.IsNullOrEmpty(uygulama))
        {
            string m = Aciklama(k);
            var a = m == null ? Match.Empty : Regex.Match(m, @"([^\s,:\\/]+\.exe)\b", RegexOptions.IgnoreCase);
            if (a.Success) uygulama = a.Groups[1].Value;
        }
        o.Uygulama = string.IsNullOrEmpty(uygulama) ? "Bilinmeyen program" : uygulama;
        if (p.Count >= 6)
        {
            string yol = Metin(p[5]);
            if (!string.IsNullOrEmpty(yol) && yol.IndexOf('\\') >= 0) o.Yol = yol;
        }
        o.Ayrinti = "Program dondu; siz ya da Windows kapattı.";
        return o;
    }

    // Resource-Exhaustion-Detector 2004 ("Windows yetersiz sanal bellek durumunu tanıladı").
    // Özellikler: 0 sanal bellek sınırı (commit limit), 1 kullanılan (commit charge), 2..6 diğer sayaçlar,
    // 7 süreç sayısı, ardından birkaç "etiket + bayt" çifti (çekirdek havuzları), sonra en çok kullanan
    // süreçler: ad (metin), pid, başlangıç zamanı (tarih), bayt, tanıtıcı sayısı, sürüm, ... (7'şerli).
    // Dizine güvenmek yerine "ad, sayı, tarih, bayt" kalıbı aranır; bulunamazsa mesaj metni kullanılır.
    static CokmeOlayi BellekDoldu(EventRecord k)
    {
        var o = new CokmeOlayi { Tur = "Sanal bellek doldu", Kategori = "bellek", Ciddi = true, BellekDoluyken = true };
        var p = k.Properties;
        var surecler = new List<KeyValuePair<string, ulong>>();
        for (int i = 0; i + 3 < p.Count; i++)
        {
            string ad = p[i].Value as string;
            if (string.IsNullOrEmpty(ad) || !(p[i + 2].Value is DateTime)) continue;
            ulong bayt;
            if (!Sayi(p[i + 3].Value, out bayt) || bayt == 0) continue;
            surecler.Add(new KeyValuePair<string, ulong>(ad, bayt));
            i += 3;
        }
        if (surecler.Count == 0)
        {
            // "oyun.exe (1234) 15886966784 bayt" / "... bytes"
            string m = Aciklama(k);
            if (m != null)
                foreach (Match a in Regex.Matches(m, @"([^\s,:\\/()]+)\s*\(\d+\)\s*(\d{4,})"))
                {
                    ulong bayt;
                    if (ulong.TryParse(a.Groups[2].Value, out bayt)) surecler.Add(new KeyValuePair<string, ulong>(a.Groups[1].Value, bayt));
                }
        }

        // Aynı programın birden çok süreci tek satırda toplanır.
        var gruplar = surecler.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Ad = g.First().Key, Bayt = g.Aggregate(0UL, (t, s) => t + s.Value), Adet = g.Count() })
            .OrderByDescending(g => g.Bayt).ToList();
        var ayrinti = new StringBuilder();
        if (gruplar.Count > 0)
        {
            o.Uygulama = gruplar[0].Ad;
            ayrinti.Append("En çok bellek kullananlar: ");
            for (int i = 0; i < gruplar.Count && i < 3; i++)
            {
                if (i > 0) ayrinti.Append(", ");
                ayrinti.Append(gruplar[i].Ad).Append(" ").Append(AnaForm.Boyut((long)gruplar[i].Bayt));
                if (gruplar[i].Adet > 1) ayrinti.Append(" (").Append(gruplar[i].Adet).Append(" kopya)");
            }
            ayrinti.Append(".");
        }
        else o.Uygulama = "Windows";
        ulong sinir, kullanilan;
        if (p.Count >= 2 && Sayi(p[0].Value, out sinir) && Sayi(p[1].Value, out kullanilan) && sinir > 0 && kullanilan <= sinir * 2)
        {
            if (ayrinti.Length > 0) ayrinti.Append(" ");
            ayrinti.Append("Sanal bellek sınırı ").Append(AnaForm.Boyut((long)sinir)).Append(", kullanılan ")
                   .Append(AnaForm.Boyut((long)kullanilan)).Append(".");
        }
        o.Ayrinti = ayrinti.Length > 0 ? ayrinti.ToString() : "Windows bellek yetmediği için uyardı.";
        return o;
    }

    // Display 4101 ("Görüntü sürücüsü nvlddmkm yanıt vermeyi durdurdu ve başarıyla kurtarıldı").
    static CokmeOlayi EkranSurucusu(EventRecord k)
    {
        var o = new CokmeOlayi { Tur = "Ekran kartı sürücüsü çöküp yeniden başladı", Kategori = "ekran", Ciddi = true };
        string surucu = k.Properties.Count >= 1 ? Metin(k.Properties[0]) : null;
        if (string.IsNullOrEmpty(surucu) || surucu.IndexOf(' ') >= 0)
        {
            surucu = null;
            string m = Aciklama(k);
            var a = m == null ? Match.Empty : Regex.Match(m, @"\b(nvlddmkm|amdkmdag|atikmdag|atikmpag|amdwddmg|igfx\w*|iastor\w*|basicdisplay)\b", RegexOptions.IgnoreCase);
            if (a.Success) surucu = a.Groups[1].Value;
        }
        o.Uygulama = string.IsNullOrEmpty(surucu) ? "Ekran kartı sürücüsü" : surucu;
        string marka = null;
        if (surucu != null)
        {
            string s = surucu.ToLowerInvariant();
            if (s.StartsWith("nvlddmkm")) marka = "NVIDIA";
            else if (s.StartsWith("amd") || s.StartsWith("ati")) marka = "AMD";
            else if (s.StartsWith("igfx")) marka = "Intel";
        }
        o.Ayrinti = (surucu != null ? "Sürücü: " + surucu + (marka != null ? " (" + marka + ")" : "") + ". " : "")
                  + "Ekran bir an kararmış; o sırada açık olan program (ör. oyun) donmuş ya da kapanmış olabilir.";
        return o;
    }

    // Kernel-Power 41. Özellik 0 = mavi ekran kodu (0 ise mavi ekran kaydı yok).
    static CokmeOlayi BeklenmedikKapanma(EventRecord k)
    {
        var o = new CokmeOlayi { Tur = "Beklenmedik kapanma (donma, elektrik ya da mavi ekran)", Kategori = "kapanma", Ciddi = true, Uygulama = "Windows" };
        ulong kod;
        if (k.Properties.Count >= 1 && Sayi(k.Properties[0].Value, out kod) && kod != 0)
        {
            o.HataKodu = "0x" + kod.ToString("X8");
            o.Ayrinti = "Mavi ekran kodu " + o.HataKodu + MaviEkranIpucu((uint)kod) + ". Bilgisayar düzgün kapanmadan yeniden başladı.";
        }
        else
            o.Ayrinti = "Bilgisayar düzgün kapanmadan yeniden başladı. Mavi ekran kaydı yok: donma sonrası güç düğmesiyle kapatma ya da elektrik kesintisi olabilir.";
        return o;
    }

    // WER-SystemErrorReporting / BugCheck 1001. Özellik 0 = "0x0000003b (0x..., ...)" ; 1 = döküm dosyası.
    static CokmeOlayi MaviEkran(EventRecord k)
    {
        var o = new CokmeOlayi { Tur = "Mavi ekran", Kategori = "maviekran", Ciddi = true, Uygulama = "Windows" };
        string kaynak = k.Properties.Count >= 1 ? Metin(k.Properties[0]) : null;
        var a = kaynak == null ? Match.Empty : Regex.Match(kaynak, @"0x([0-9a-fA-F]{1,8})\b");
        if (!a.Success)
        {
            string m = Aciklama(k);
            if (m != null) a = Regex.Match(m, @"0x([0-9a-fA-F]{1,8})\b");
        }
        uint kod;
        if (a.Success && uint.TryParse(a.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out kod))
        {
            o.HataKodu = "0x" + kod.ToString("X8");
            o.Ayrinti = "Mavi ekran kodu " + o.HataKodu + MaviEkranIpucu(kod) + ".";
        }
        else o.Ayrinti = "Windows mavi ekranla durup yeniden başladı.";
        return o;
    }

    // WHEA-Logger 18 / 20 / 1: işlemci, RAM, ekran kartı ya da PCIe'den gelen düzeltilemeyen hata.
    // Bileşen dilden bağımsız veriden çıkarılır: 18 = işlemci çekirdeği, 20 = AMD Northbridge (olay tanımı sabit),
    // özellik 1 = işlemci APIC kimliği; 1'de ham kayıt (CPER) bölüm türü. Mesaj metni yalnızca son çare.
    static CokmeOlayi DonanimHatasi(EventRecord k)
    {
        var o = new CokmeOlayi { Tur = "Donanım hatası", Kategori = "donanim", Ciddi = true, Uygulama = "Donanım" };
        var p = k.Properties;
        string bilesen = null;
        ulong apic;
        if (k.Id == 18 || k.Id == 20)
        {
            bilesen = k.Id == 18 ? "İşlemci çekirdeği" : "AMD Northbridge (işlemci içi bağlantı)";
            if (p.Count >= 2 && Sayi(p[1].Value, out apic)) bilesen += ", APIC " + apic;
        }
        else
        {
            for (int i = p.Count - 1; i >= 0 && bilesen == null; i--)
                if (p[i].Value is byte[]) bilesen = CperBileseni((byte[])p[i].Value);
        }
        if (bilesen == null)
        {
            string m = Aciklama(k);
            var a = m == null ? Match.Empty : Regex.Match(m, @"(?:Bileşen|Component|Komponente)\s*:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            if (a.Success) bilesen = a.Groups[1].Value.Trim();
        }
        if (!string.IsNullOrEmpty(bilesen)) o.Uygulama = bilesen;
        o.Ayrinti = (string.IsNullOrEmpty(bilesen) ? "" : "Bileşen: " + bilesen + ". ")
                  + "Düzeltilemeyen donanım hatası; aşırı ısınma, hız aşırtma (overclock), RAM ya da güç kaynağı kaynaklı olabilir.";
        return o;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Özet: "Son 7 günde 5 sorun: 3 program çökmesi (2'si bellek doluyken), 1 ekran kartı sürücüsü çökmesi, ..."
    public static string Ozet(List<CokmeOlayi> olaylar)
    {
        return Ozet(olaylar, sonGun);
    }

    public static string Ozet(List<CokmeOlayi> olaylar, int gun)
    {
        try
        {
            string donem = gun == 1 ? "Son 24 saatte" : "Son " + gun + " günde";
            var kesik = ReferenceEquals(olaylar, sonListe) ? kesilenler : new HashSet<string>();
            if (olaylar == null || olaylar.Count == 0) return donem + " çökme kaydı yok.";

            // Önemsiz kayıtlar (kesme noktası) hiçbir sayıya, orana ve "en sık" hesabına girmez; yalnızca adedi söylenir.
            int onemsiz = olaylar.Count(OnemsizMi);
            string onemsizNot = onemsiz > 0 ? " " + onemsiz + " önemsiz kayıt (kesme noktası, 0x80000003) sayılmadı." : "";
            olaylar = olaylar.Where(o => !OnemsizMi(o)).ToList();
            if (olaylar.Count == 0) return donem + " çökme kaydı yok." + onemsizNot;

            // Kategori sırası ve adı (sayıdan sonra gelen ad).
            string[][] turler =
            {
                new[] { "cokme", "program çökmesi" },
                new[] { "donma", "program donması" },
                new[] { "bellek", "\"sanal bellek doldu\" uyarısı" },
                new[] { "ekran", "ekran kartı sürücüsü çökmesi" },
                new[] { "maviekran", "mavi ekran" },
                new[] { "kapanma", "beklenmedik kapanma" },
                new[] { "donanim", "donanım hatası" },
            };
            var parcalar = new List<string>();
            foreach (var t in turler)
            {
                var bu = olaylar.Where(o => o.Kategori == t[0]).ToList();
                if (bu.Count == 0) continue;
                string parca = (kesik.Contains(t[0]) ? "en az " : "") + bu.Count + " " + t[1];
                if (t[0] != "bellek")
                {
                    int bellekli = bu.Count(o => o.BellekDoluyken);
                    if (bellekli > 0)
                    {
                        if (bu.Count == 1) parca += " (bellek doluyken)";
                        else if (bellekli == bu.Count) parca += " (hepsi bellek doluyken)";
                        else parca += " (" + bellekli + Iyelik(bellekli) + " bellek doluyken)";
                    }
                }
                parcalar.Add(parca);
            }
            // Kategorisi bilinmeyen (dışarıdan eklenmiş) olaylar da sayıya girsin.
            int bilinmeyen = olaylar.Count(o => o.Kategori == null || !turler.Any(t => t[0] == o.Kategori));
            if (bilinmeyen > 0) parcalar.Add(bilinmeyen + " diğer sorun");

            var sb = new StringBuilder();
            sb.Append(donem).Append(" ").Append(kesik.Count > 0 ? "en az " : "").Append(olaylar.Count).Append(" sorun: ").Append(string.Join(", ", parcalar)).Append(".");

            // Tek bir program sürekli çöküyorsa adını ver.
            var enSik = olaylar.Where(o => o.Kategori == "cokme" || o.Kategori == "donma")
                .GroupBy(o => o.Uygulama, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).FirstOrDefault();
            if (enSik != null && enSik.Count() >= 3)
                sb.Append(" En sık sorun çıkaran: ").Append(enSik.Key).Append(" (").Append(enSik.Count()).Append(" kez).");

            // Oran yalnızca çökmelerden hesaplanır: "sanal bellek doldu" uyarılarının kendisi sayılmaz.
            var cokmeler = olaylar.Where(o => o.Kategori != "bellek").ToList();
            int bellekToplam = cokmeler.Count(o => o.BellekDoluyken);
            int uyariSayisi = olaylar.Count - cokmeler.Count;
            if (bellekToplam > 0)
            {
                if (bellekToplam * 2 >= cokmeler.Count)
                    sb.Append(" Çoğu bellek doluyken olmuş; sık oluyorsa Full optimize → Sanal belleği büyüt önerilir.");
                else
                    sb.Append(" Bir kısmı bellek doluyken olmuş; sık oluyorsa Full optimize → Sanal belleği büyüt yardımcı olabilir.");
            }
            else if (uyariSayisi > 0)
                sb.Append(" Sanal bellek ").Append(uyariSayisi).Append(" kez doldu.");

            // Sanal belleği dolduran program (2004 olayında en çok kullanan): asıl işe yarar bilgi.
            var dolduran = olaylar.Where(o => o.Kategori == "bellek" && !string.IsNullOrEmpty(o.Uygulama) && o.Uygulama != "Windows")
                .GroupBy(o => o.Uygulama, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).FirstOrDefault();
            if (dolduran != null)
                sb.Append(" Bellek dolduğunda en çok ").Append(dolduran.Key).Append(" kullanıyordu; gerekmiyorsa onu kapat.");
            sb.Append(onemsizNot);
            return sb.ToString();
        }
        catch { return "Çökme geçmişi özetlenemedi."; }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Yardımcılar

    // Sayının iyelik eki: 1'i, 2'si, 3'ü, 6'sı, 9'u, 10'u, 40'ı, 100'ü ...
    static string Iyelik(int n)
    {
        n = Math.Abs(n);
        if (n == 0) return "'ı";
        int birler = n % 10, onlar = (n / 10) % 10;
        if (birler != 0) return new[] { "", "'i", "'si", "'ü", "'ü", "'i", "'sı", "'si", "'i", "'u" }[birler];
        if (onlar != 0) return new[] { "", "'u", "'si", "'u", "'ı", "'si", "'ı", "'i", "'i", "'ı" }[onlar];
        if (n % 1000 != 0) return "'ü";   // yüz
        return "'i";                      // bin
    }

    // WHEA ham kaydı (UEFI CPER): "CPER" imzası, 10. baytta bölüm sayısı, 128. bayttan itibaren 72'şer baytlık
    // bölüm tanımları (12. bayt bayraklar, bit 0 = birincil; 16. bayt bölüm türü GUID'i). Tanınmazsa null.
    static readonly Dictionary<Guid, string> CperBolumleri = new Dictionary<Guid, string>
    {
        { new Guid("9876CCAD-47B4-4BDB-B65E-16F193C4F3DB"), "İşlemci" },
        { new Guid("DC3EA0B0-A144-4797-B95B-53FA242B6E1D"), "İşlemci" },
        { new Guid("E19E3D16-BC11-11E4-9CAA-C2051D5D46B0"), "İşlemci" },
        { new Guid("A5BC1114-6F64-4EDE-B863-3E83ED7C83B1"), "Bellek (RAM)" },
        { new Guid("61EC04FC-48E6-D813-25C9-8DAA44750B12"), "Bellek (RAM)" },
        { new Guid("D995E954-BBC1-430F-AD91-B44DCB3C6F35"), "PCI Express (ekran kartı, NVMe disk ya da başka bir kart)" },
        { new Guid("C5753963-3B84-4095-BF78-EDDAD3F9C9DD"), "PCI veri yolu" },
        { new Guid("EB5E4685-CA66-4769-B6A2-26068B001326"), "PCI aygıtı" },
        { new Guid("81212A96-09ED-4996-9471-8D729C8E69ED"), "Ürün yazılımı (BIOS / UEFI)" },
    };

    static string CperBileseni(byte[] v)
    {
        try
        {
            if (v == null || v.Length < 128 + 72 || v[0] != 'C' || v[1] != 'P' || v[2] != 'E' || v[3] != 'R') return null;
            int sayi = BitConverter.ToUInt16(v, 10);
            string ilk = null;
            for (int i = 0; i < sayi && 128 + (i + 1) * 72 <= v.Length; i++)
            {
                int t = 128 + i * 72;
                var g = new byte[16];
                Array.Copy(v, t + 16, g, 0, 16);
                string ad;
                if (!CperBolumleri.TryGetValue(new Guid(g), out ad)) continue;
                if ((BitConverter.ToUInt32(v, t + 12) & 1) != 0) return ad;   // birincil bölüm
                if (ilk == null) ilk = ad;
            }
            return ilk;
        }
        catch { return null; }
    }

    static string MaviEkranIpucu(uint kod)
    {
        string anlam;
        return MaviEkranAnlami.TryGetValue(kod, out anlam) ? " (" + anlam + ")" : "";
    }

    // "c0000005", "0xC0000005", "-1073741819" → "c0000005"; geçersizse null.
    static string KodDuzelt(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        uint deger;
        if (Regex.IsMatch(s, "^[0-9a-fA-F]{1,8}$") && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out deger))
            return deger.ToString("x8");
        int isaretli;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out isaretli) && isaretli < 0)
            return unchecked((uint)isaretli).ToString("x8");
        return null;
    }

    static string Metin(EventProperty p)
    {
        if (p == null || p.Value == null) return null;
        string s = Convert.ToString(p.Value, CultureInfo.InvariantCulture);
        return s == null ? null : s.Trim();
    }

    static bool Sayi(object deger, out ulong sonuc)
    {
        sonuc = 0;
        if (deger == null) return false;
        if (deger is ulong) { sonuc = (ulong)deger; return true; }
        if (deger is uint) { sonuc = (uint)deger; return true; }
        if (deger is long) { long l = (long)deger; if (l < 0) return false; sonuc = (ulong)l; return true; }
        if (deger is int) { int i = (int)deger; if (i < 0) return false; sonuc = (ulong)i; return true; }
        if (deger is ushort) { sonuc = (ushort)deger; return true; }
        if (deger is byte) { sonuc = (byte)deger; return true; }
        return false;
    }

    // Olay mesajı (sistem dilinde). Yavaş olabildiği için yalnızca gerektiğinde çağrılır.
    static string Aciklama(EventRecord k)
    {
        try { return k.FormatDescription(); }
        catch { return null; }
    }
}
