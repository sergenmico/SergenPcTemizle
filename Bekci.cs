// Bellek bekçisi: tepsi simgesi, "sanal bellek doluyor" uyarıları ve bellek sızıntısı dedektörü.
// Pencere kapatılınca (ayar açıksa) program tepside çalışmaya devam eder; sanal bellek dolmaya
// yaklaşınca ya da bir program sürekli büyüyorsa balonla haber verir. Kendi başına hiçbir şeyi
// kapatmaz, silmez; yalnızca haber verir.
// Ayarlar: %APPDATA%\SergenPcTemizle\ayarlar.txt ("ad=1" / "ad=0" satırları).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Küçük kalıcı ayarlar. Dosya ilk kullanımda bir kez okunur, her yazışta baştan yazılır.
// Okuma/yazma hatasında sessizce varsayılana düşer; program bu yüzden asla çökmez.
static class Ayarlar
{
    static readonly object kilit = new object();
    static readonly string Dosya = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SergenPcTemizle", "ayarlar.txt");
    static Dictionary<string, string> degerler;

    public static bool Oku(string ad, bool varsayilan)
    {
        lock (kilit)
        {
            Yukle();
            string d;
            if (ad == null || !degerler.TryGetValue(ad, out d)) return varsayilan;
            if (d == "1") return true;
            if (d == "0") return false;
            return varsayilan;
        }
    }

    public static void Yaz(string ad, bool deger) { YazMetin(ad, deger ? "1" : "0"); }

    public static int OkuSayi(string ad, int varsayilan)
    {
        lock (kilit)
        {
            Yukle();
            string d;
            int sayi;
            if (ad == null || !degerler.TryGetValue(ad, out d)) return varsayilan;
            return int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out sayi) ? sayi : varsayilan;
        }
    }

    public static void YazSayi(string ad, int deger) { YazMetin(ad, deger.ToString(CultureInfo.InvariantCulture)); }

    public static string OkuMetin(string ad, string varsayilan)
    {
        lock (kilit)
        {
            Yukle();
            string d;
            return ad != null && degerler.TryGetValue(ad, out d) ? d : varsayilan;
        }
    }

    // Boş metin yazmak ayarı temizler (okuyan boş görür).
    public static void YazMetin(string ad, string yeni)
    {
        if (string.IsNullOrEmpty(ad) || ad.IndexOf('=') >= 0 || ad.IndexOf('\n') >= 0) return;
        lock (kilit)
        {
            Yukle();
            string eski;
            if (degerler.TryGetValue(ad, out eski) && eski == yeni) return;
            degerler[ad] = yeni;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Dosya));
                var satirlar = degerler.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => k.Key + "=" + k.Value).ToArray();
                // Önce geçici dosyaya yaz, sonra değiştir: yazarken elektrik giderse eski dosya bozulmaz.
                string gecici = Dosya + ".yeni";
                File.WriteAllLines(gecici, satirlar, new UTF8Encoding(false));
                if (File.Exists(Dosya)) File.Replace(gecici, Dosya, null);   // tek adımda; arada dosyasız kalınmaz
                else File.Move(gecici, Dosya);
            }
            catch { }
        }
    }

    static void Yukle()
    {
        if (degerler != null) return;
        degerler = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Asıl dosya yoksa yarıda kalmış yazımın geçici dosyası okunur.
            string oku = File.Exists(Dosya) ? Dosya : (File.Exists(Dosya + ".yeni") ? Dosya + ".yeni" : null);
            if (oku == null) return;
            foreach (var satir in File.ReadAllLines(oku, Encoding.UTF8))
            {
                int i = satir.IndexOf('=');
                if (i <= 0) continue;
                degerler[satir.Substring(0, i).Trim()] = satir.Substring(i + 1).Trim();
            }
        }
        catch { }
    }
}

class SizintiSupheli
{
    public string Ad, Gorunen;
    public long Simdi, Artis;      // bayt: şu anki özel bellek, büyümenin başladığı en düşük noktadan bu yana artış
    public TimeSpan Sure;          // büyümenin süresi (en düşük noktadan son örneğe)
    public double HizMBDakika;
}

// Süreç adı gruplarının özel (commit) belleğini zamanla izler ve "sürekli büyüyen"leri bulur.
//
// Neden bu kurallar? Amaç yanlış alarmı az tutmak:
//  - Oyunlar yüklenirken birkaç dakikada 5–8 GB'a çıkıp sonra düz kalır. Bu sızıntı değildir. Bunu
//    ayırmak için büyümenin zamana YAYILMIŞ olması istenir: en az 20 dk veri, büyümenin kendisi en az
//    30 dk; büyüme ~2,5 dakikalık dilimlere bölünür ve komşu iki dilimin artışı toplamın %60'ını
//    geçemez, dilimlerin en az %40'ında anlamlı büyüme olmalı (aşamalı yüklemeyi eler), anlamlı
//    farkların en az %70'i pozitif olmalı ve son üçte birde de büyüme sürmelidir (en az %15).
//  - Belleği geri bırakan (testere dişi: büyü–bırak) programlar sızdırmıyordur: anlamlı düşüşlerin
//    toplamı net büyümenin %30'unu geçerse şüphe yok.
//  - Yükleme sıçramasından SONRA da yavaş yavaş büyüyen bir oyun gerçekten sızdırıyor olabilir;
//    bu yüzden en büyük sıçrama elenip kalan bölüm yeniden değerlendirilir (en fazla 3 kez).
//  - Ölçüm gürültüsü (±50 MB oynamalar) "büyüme" sayılmaz; tek bir anlık sıçrama da sonucu
//    bozmasın diye "son değer" son üç örneğin ortancasıdır, dilim değerleri de dilim ortancasıdır.
//  - Küçük programlar (son değer 1 GB altı) ve küçük artışlar (1,5 GB altı) hiç uyarı vermez; artış
//    başlangıcın en az %50'si olmalı (10 GB'lık bir şeyin 1,5 GB oynaması normal sayılır).
//  - Tarayıcılar sekme açıldıkça adım adım büyür; bu da "şüpheli" sayılabilir, kabul edilebilir:
//    uyarı yalnızca "sızıntı olabilir; gerekmiyorsa kapat" der.
class SizintiIzleyici
{
    const long MB = 1048576;
    static readonly TimeSpan EnKisaAralik = TimeSpan.FromSeconds(25);
    static readonly TimeSpan EnUzunGecmis = TimeSpan.FromHours(2);
    static readonly TimeSpan KayipSuresi = TimeSpan.FromMinutes(10);
    static readonly TimeSpan EnAzVeri = TimeSpan.FromMinutes(20);
    // Büyümenin kendisi en az 30 dk sürmeli: 20 dk büyüyüp sonra belleği bırakan (testere dişi) programlar
    // uyarı vermesin. Gerçek sızıntı sanal belleği saatlerde doldurur; 10 dk geç haber vermek zarar değil.
    static readonly TimeSpan EnAzBuyume = TimeSpan.FromMinutes(30);
    static readonly TimeSpan DilimHedefi = TimeSpan.FromMinutes(2.5);
    const long EnAzArtis = 1536 * MB;      // 1,5 GB
    const long EnAzSonDeger = 1024 * MB;   // 1 GB
    const double EnAzOransalArtis = 0.5;   // başlangıcın %50'si
    const long Gurultu = 50 * MB;
    const double EnAzPozitifOran = 0.7;
    const double EnBuyukDilimPayi = 0.6;    // komşu iki dilimin toplamı
    const double EnAzBuyuyenDilim = 0.4;    // dilimlerin en az %40'ında anlamlı büyüme
    const double EnCokDususPayi = 0.3;      // anlamlı düşüşlerin toplamı, net büyümenin en fazla %30'u
    const double EnAzSonUcteBirPayi = 0.15;

    class SizintiOrnegi { public DateTime Zaman; public long Deger; }
    class SizintiKaydi { public readonly List<SizintiOrnegi> Ornekler = new List<SizintiOrnegi>(); public DateTime SonGorulme; }

    readonly object kilit = new object();
    readonly Dictionary<string, SizintiKaydi> kayitlar = new Dictionary<string, SizintiKaydi>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> gorunenler = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void Ekle(DateTime zaman, List<KeyValuePair<string, long>> grupBellek)
    {
        if (grupBellek == null) return;
        lock (kilit)
        {
            var gelen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in grupBellek)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                gelen.Add(kv.Key);
                SizintiKaydi k;
                if (!kayitlar.TryGetValue(kv.Key, out k)) { k = new SizintiKaydi(); kayitlar[kv.Key] = k; }
                k.SonGorulme = zaman;
                var o = k.Ornekler;
                // Saat geri alındıysa (yaz saati, elle ayar) geçmiş anlamsızlaşır: baştan başla.
                if (o.Count > 0 && zaman < o[o.Count - 1].Zaman) o.Clear();
                if (o.Count > 0 && zaman - o[o.Count - 1].Zaman < EnKisaAralik) continue;
                o.Add(new SizintiOrnegi { Zaman = zaman, Deger = kv.Value });
                int eski = 0;
                while (eski < o.Count && zaman - o[eski].Zaman > EnUzunGecmis) eski++;
                if (eski > 0) o.RemoveRange(0, eski);
            }
            foreach (var ad in kayitlar.Keys.ToList())
                if (!gelen.Contains(ad) && zaman - kayitlar[ad].SonGorulme > KayipSuresi) kayitlar.Remove(ad);
        }
    }

    public List<SizintiSupheli> Supheliler()
    {
        var sonuc = new List<SizintiSupheli>();
        lock (kilit)
        {
            foreach (var kv in kayitlar)
            {
                var s = Degerlendir(kv.Value.Ornekler);
                if (s == null) continue;
                s.Ad = kv.Key;
                s.Gorunen = GorunenAdKilitli(kv.Key);
                sonuc.Add(s);
            }
        }
        return sonuc.OrderByDescending(s => s.Artis).ToList();
    }

    public string GorunenAd(string ad)
    {
        if (ad == null) return "";
        lock (kilit) return GorunenAdKilitli(ad);
    }

    string GorunenAdKilitli(string ad)
    {
        string g;
        return gorunenler.TryGetValue(ad, out g) && !string.IsNullOrWhiteSpace(g) ? g : ad;
    }

    // Sözleşmeye ek: bekçi süreç adını ilk gördüğünde dosya açıklamasını buraya bildirir.
    public bool AdBiliniyor(string ad) { lock (kilit) return ad != null && gorunenler.ContainsKey(ad); }

    public void AdTanit(string ad, string gorunen)
    {
        if (string.IsNullOrEmpty(ad)) return;
        lock (kilit) gorunenler[ad] = string.IsNullOrWhiteSpace(gorunen) ? ad : gorunen.Trim();
    }

    // Sözleşmeye ek: kaç grup izleniyor (destek raporu için).
    public int GrupSayisi { get { lock (kilit) return kayitlar.Count; } }

    static SizintiSupheli Degerlendir(List<SizintiOrnegi> o)
    {
        if (o.Count < 10) return null;
        var son = o[o.Count - 1];
        if (son.Zaman - o[0].Zaman < EnAzVeri) return null;

        long sonDeger = Ortanca(o.Skip(Math.Max(0, o.Count - 3)).Select(x => x.Deger).ToList());
        if (sonDeger < EnAzSonDeger || son.Deger < EnAzSonDeger) return null;

        int bas = 0;
        for (int deneme = 0; deneme < 3; deneme++)
        {
            // Penceredeki (bas..son-1) en düşük nokta; eşitlerde en sonuncusu (büyüme oradan başlar).
            int enDusuk = -1;
            for (int i = bas; i < o.Count - 1; i++)
                if (enDusuk < 0 || o[i].Deger <= o[enDusuk].Deger) enDusuk = i;
            if (enDusuk < 0) return null;

            var baslangic = o[enDusuk];
            TimeSpan sure = son.Zaman - baslangic.Zaman;
            long artis = sonDeger - baslangic.Deger;
            if (sure < EnAzBuyume || artis < EnAzArtis || artis < baslangic.Deger * EnAzOransalArtis) return null;

            int sicramaSonu;
            if (Surekli(o, enDusuk, artis, out sicramaSonu))
            {
                return new SizintiSupheli
                {
                    Simdi = son.Deger,
                    Artis = artis,
                    Sure = sure,
                    HizMBDakika = artis / (double)MB / Math.Max(1.0, sure.TotalMinutes)
                };
            }
            if (sicramaSonu < 0 || sicramaSonu >= o.Count - 1) return null;
            bas = sicramaSonu;   // en büyük sıçramayı ele (oyun yüklemesi), sonrasına yeniden bak
        }
        return null;
    }

    // Büyüme zamana yayılmış mı? Bölüm eşit zaman dilimlerine ayrılır, her dilimin ortancası alınır.
    // Başarısızlıkta sicramaSonu = en büyük artışın olduğu dilimin bittiği örneğin sırası.
    static bool Surekli(List<SizintiOrnegi> o, int bas, long artis, out int sicramaSonu)
    {
        sicramaSonu = -1;
        DateTime t0 = o[bas].Zaman, t1 = o[o.Count - 1].Zaman;
        double toplamDk = (t1 - t0).TotalMinutes;
        int dilimSayisi = (int)Math.Round(toplamDk / DilimHedefi.TotalMinutes);
        dilimSayisi = Math.Max(6, Math.Min(24, dilimSayisi));
        double dilimDk = toplamDk / dilimSayisi;

        // Her dilim: ortanca değer ve dilimin son örneğinin sırası. İlk değer başlangıç noktasının kendisi.
        var degerler = new List<long> { o[bas].Deger };
        var sonSira = new List<int> { bas };
        int j = bas + 1;
        for (int d = 0; d < dilimSayisi; d++)
        {
            DateTime bitis = d == dilimSayisi - 1 ? DateTime.MaxValue : t0.AddMinutes(dilimDk * (d + 1));
            var dilim = new List<long>();
            int sonJ = -1;
            while (j < o.Count && o[j].Zaman < bitis) { dilim.Add(o[j].Deger); sonJ = j; j++; }
            if (dilim.Count == 0) continue;   // boşluk (uyku vb.): dilimi atla
            degerler.Add(Ortanca(dilim));
            sonSira.Add(sonJ);
        }
        if (degerler.Count < 5) return false;

        int farkSayisi = degerler.Count - 1, pozitif = 0, anlamli = 0, enBuyukSira = -1;
        long enBuyuk = 0, dusus = 0;
        for (int i = 1; i < degerler.Count; i++)
        {
            long fark = degerler[i] - degerler[i - 1];
            // Sıçrama iki dilime bölünmüş olabilir: komşu iki dilimin toplamına bakılır.
            long cift = fark + (i > 1 ? Math.Max(0, degerler[i - 1] - degerler[i - 2]) : 0);
            if (cift > enBuyuk) { enBuyuk = cift; enBuyukSira = i; }
            if (Math.Abs(fark) < Gurultu) continue;
            anlamli++;
            if (fark > 0) pozitif++;
            else dusus -= fark;
        }
        if (enBuyukSira > 0) sicramaSonu = sonSira[enBuyukSira];

        long toplam = degerler[degerler.Count - 1] - degerler[0];
        if (toplam < artis / 2) return false;                          // dilim ortancaları büyümeyi doğrulamıyor
        if (anlamli < 3 || pozitif < anlamli * EnAzPozitifOran) return false;
        if (pozitif < farkSayisi * EnAzBuyuyenDilim) return false;     // birkaç sıçrama, arası düz: aşamalı yükleme
        if (enBuyuk > toplam * EnBuyukDilimPayi) return false;         // büyümenin çoğu tek seferde: yükleme
        if (dusus > toplam * EnCokDususPayi) return false;             // belleği geri bırakıyor: sızıntı değil

        int ucteIki = (degerler.Count - 1) * 2 / 3;
        long sonUcteBir = degerler[degerler.Count - 1] - degerler[ucteIki];
        if (sonUcteBir < toplam * EnAzSonUcteBirPayi) return false;    // büyüme durmuş

        return true;
    }

    static long Ortanca(List<long> l)
    {
        if (l.Count == 0) return 0;
        var s = l.OrderBy(x => x).ToList();
        int m = s.Count / 2;
        return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2;
    }
}

// Tepsi menüsü için koyu renkler.
class BekciMenuRenkleri : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground { get { return Renk.Panel; } }
    public override Color MenuBorder { get { return Renk.Cizgi; } }
    public override Color MenuItemBorder { get { return Renk.Cizgi; } }
    public override Color MenuItemSelected { get { return Renk.Cizgi; } }
    public override Color MenuItemSelectedGradientBegin { get { return Renk.Cizgi; } }
    public override Color MenuItemSelectedGradientEnd { get { return Renk.Cizgi; } }
    public override Color MenuItemPressedGradientBegin { get { return Renk.Cizgi; } }
    public override Color MenuItemPressedGradientEnd { get { return Renk.Cizgi; } }
    public override Color ImageMarginGradientBegin { get { return Renk.Panel; } }
    public override Color ImageMarginGradientMiddle { get { return Renk.Panel; } }
    public override Color ImageMarginGradientEnd { get { return Renk.Panel; } }
    public override Color SeparatorDark { get { return Renk.Cizgi; } }
    public override Color SeparatorLight { get { return Renk.Panel; } }
}

class BellekBekcisi : IDisposable
{
    const string Baslik = "Sergen PC Temizle · bellek bekçisi";
    const string RunAnahtari = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunDegeri = "SergenPcTemizle";
    const string AyarArkaPlan = "arka_planda_calis";
    const string AyarWindowsIle = "windows_ile_baslat";
    const string AyarIpucu = "bekci_ipucu_gosterildi";
    const string AyarOyunModu = "oyun_modu";

    static readonly string WinKlasoru = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    readonly Form anaForm;
    readonly Action otomatikOptimize, kisayolIsi;
    readonly SizintiIzleyici izleyici = new SizintiIzleyici();
    readonly NotifyIcon simge;
    readonly ContextMenuStrip menu;
    readonly ToolStripMenuItem mRam;
    readonly GenelKisayol kisayol;
    readonly System.Windows.Forms.Timer bellekZamanlayici = new System.Windows.Forms.Timer();
    readonly System.Threading.Timer ornekZamanlayici;
    readonly int benim, oturum;

    // Yalnızca örnekleme iş parçacığı kullanır: Windows klasöründen çalışan adlar (dwm, explorer, svchost...).
    readonly HashSet<string> sistemAdlari = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Örnekleme iş parçacığı yazar, arayüz zamanlayıcısı okur.
    readonly object supheliKilit = new object();
    List<SizintiSupheli> sonSupheliler = new List<SizintiSupheli>();

    // Yalnızca arayüz iş parçacığı.
    readonly Dictionary<string, DateTime> sizintiBildirildi = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    DateTime sonCommitUyari = DateTime.MinValue, sonKritikUyari = DateTime.MinValue, sonRamUyari = DateTime.MinValue;
    DateTime sonBalon = DateTime.MinValue;
    bool cikisIstendi;
    Action balonEylemi;   // son balona tıklanınca yapılacak iş (yoksa yalnızca pencere açılır)
    volatile bool kapandi;   // örnekleme iş parçacığı da okur
    int ornekleniyor;

    // kisayolIsi: genel kısayola (ya da tepsideki "RAM boşalt"a) basılınca; oyundayken de güvenli, sessiz iş.
    public BellekBekcisi(Form anaForm, Action otomatikOptimize, Action kisayolIsi)
    {
        this.anaForm = anaForm;
        this.otomatikOptimize = otomatikOptimize;
        this.kisayolIsi = kisayolIsi;
        using (var p = Process.GetCurrentProcess()) { benim = p.Id; oturum = p.SessionId; }

        menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new BekciMenuRenkleri()),
            BackColor = Renk.Panel,
            ForeColor = Renk.Yazi,
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9.5f)
        };
        var mAc = new ToolStripMenuItem("Aç") { ForeColor = Renk.Yazi, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        var mHakkinda = new ToolStripMenuItem("Hakkında") { ForeColor = Renk.Yazi };
        var mGuncelle = new ToolStripMenuItem("Güncellemeleri denetle") { ForeColor = Renk.Yazi };
        var mOto = new ToolStripMenuItem("Otomatik optimize") { ForeColor = Renk.Yazi };
        mRam = new ToolStripMenuItem("RAM boşalt (oyunu atlar)") { ForeColor = Renk.Yazi };
        var mCikis = new ToolStripMenuItem("Çıkış") { ForeColor = Renk.Yazi };
        mAc.Click += delegate { Goster(); };
        mHakkinda.Click += delegate { Hakkinda.Goster(); };
        mGuncelle.Click += delegate { if (Guncelleyici.Ornek != null) Guncelleyici.Ornek.TepsidenDenetle(); };
        mOto.Click += delegate { OtomatikOptimizeCalistir(); };
        mRam.Click += delegate { KisayolIsiniCalistir(); };
        mCikis.Click += delegate { Cik(); };
        menu.Items.Add(mAc);
        menu.Items.Add(mHakkinda);
        menu.Items.Add(mGuncelle);
        menu.Items.Add(mOto);
        menu.Items.Add(mRam);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(mCikis);

        simge = new NotifyIcon
        {
            Icon = Logo.Simge(SystemInformation.SmallIconSize),
            Text = Baslik,
            ContextMenuStrip = menu
        };
        simge.DoubleClick += delegate { Goster(); };
        simge.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Goster(); };
        simge.BalloonTipClicked += delegate
        {
            Goster();
            // Balonla birlikte bir eylem verildiyse (ör. "Güncellemek için tıkla") tıklanınca o da yapılır.
            var eylem = balonEylemi;
            balonEylemi = null;
            if (eylem != null) try { eylem(); } catch { }
        };
        simge.Visible = true;

        bellekZamanlayici.Interval = 5000;
        bellekZamanlayici.Tick += delegate { BellegeBak(); };
        bellekZamanlayici.Start();

        ornekZamanlayici = new System.Threading.Timer(Ornekle, null, 5000, 30000);

        // Genel kısayol: tuş başka programdaysa sessizce alınamaz; Sağlık sekmesi bunu gösterir.
        try
        {
            kisayol = new GenelKisayol();
            kisayol.Basildi += KisayolIsiniCalistir;
            kisayol.Ayarla(KisayolAyari.Oku());
        }
        catch { kisayol = null; }
        MenuKisayolunuYaz();

        if (anaForm != null) anaForm.FormClosed += delegate { Dispose(); };

        // "Windows açılınca" açık ama exe taşınmışsa Run değeri bu exe'ye çekilir (kapatılmışsa getter false döner,
        // dokunulmaz). Zipin içinden / geçici klasörden çalışan kopya Run değerini kendine çekmez.
        try
        {
            if (BaslangicYoluEski && WindowsIleBaslat
                && !Application.ExecutablePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                WindowsIleBaslat = true;
        }
        catch { }
    }

    // Turbo sekmesinin oyun profili: 5 sn'lik zamanlayıcıda (arayüz iş parçacığı) Tik() çağrılır. Ana form kurar.
    public OyunProfili Profil;

    public SizintiIzleyici Izleyici { get { return izleyici; } }
    public bool CikisIstendi { get { return cikisIstendi; } }

    // Oyun modu (varsayılan açık): önde tam ekran bir program varken pencere / ileti kutusu açılmaz,
    // bellek uyarıları balona ek olarak sesle de verilir (Windows tam ekranda balonları çoğu zaman gizler).
    public bool OyunModu
    {
        get { return Ayarlar.Oku(AyarOyunModu, true); }
        set { Ayarlar.Yaz(AyarOyunModu, value); }
    }

    public bool OyunSuruyor { get { return OyunModu && TamEkran.Var(); } }

    // ---- Genel kısayol ----

    public Keys Kisayol { get { return kisayol != null ? kisayol.Tus : KisayolAyari.Oku(); } }

    // Tuş alınamadıysa (başka program kullanıyor) false; kısayol kapalıysa true.
    public bool KisayolCalisiyor { get { return kisayol != null && (kisayol.Tus == Keys.None || kisayol.Calisiyor); } }

    // Yeni tuşu kaydeder ve alır. false: tuş başka bir programda; ayar yine de kaydedilmez, eski tuş geri alınır.
    public bool KisayolAyarla(Keys k)
    {
        if (kisayol == null) return false;
        if (k != Keys.None && !KisayolAyari.Gecerli(k)) return false;
        Keys eski = KisayolAyari.Oku();
        bool oldu = kisayol.Ayarla(k);
        if (oldu) KisayolAyari.Yaz(k);
        else kisayol.Ayarla(eski);
        MenuKisayolunuYaz();
        return oldu;
    }

    // Sağlık sekmesinde yeni tuş beklenirken eski kısayol bırakılır (yakalarken optimize başlamasın).
    public void KisayoluBirak() { if (kisayol != null) kisayol.Birak(); }
    public void KisayoluGeriAl() { if (kisayol != null) kisayol.Ayarla(KisayolAyari.Oku()); MenuKisayolunuYaz(); }

    void MenuKisayolunuYaz()
    {
        try
        {
            Keys k = kisayol != null && kisayol.Calisiyor ? kisayol.Tus : Keys.None;
            mRam.ShortcutKeyDisplayString = k == Keys.None ? "" : KisayolAyari.Metin(k);
            mRam.ShowShortcutKeys = k != Keys.None;
        }
        catch { }
    }

    void KisayolIsiniCalistir()
    {
        if (kapandi || kisayolIsi == null) return;
        try { kisayolIsi(); } catch { }
    }

    public bool ArkaPlandaCalis
    {
        get { return Ayarlar.Oku(AyarArkaPlan, true); }
        set { Ayarlar.Yaz(AyarArkaPlan, value); }
    }

    // Kayıt defterindeki gerçek durum: değer varsa ve Görev Yöneticisi / Başlangıç sekmesinden kapatılmamışsa
    // (StartupApproved) true; yol eski olsa bile (kurucu set ile günceller).
    public bool WindowsIleBaslat
    {
        get { return RunKomutu() != null && BaslangicYoneticisi.OnayDurumu(false, "Run", RunDegeri); }
        set
        {
            Ayarlar.Yaz(AyarWindowsIle, value);
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunAnahtari))
                {
                    if (k == null) return;
                    if (value) k.SetValue(RunDegeri, BeklenenKomut(), RegistryValueKind.String);
                    else k.DeleteValue(RunDegeri, false);
                }
                // Daha önce Görev Yöneticisi'nden "devre dışı" bırakıldıysa Run girdisi tek başına yetmez: onayı da aç.
                if (value && !BaslangicYoneticisi.OnayDurumu(false, "Run", RunDegeri))
                    using (var o = Registry.CurrentUser.CreateSubKey(BaslangicYoneticisi.OnayYolu + "\\Run"))
                        if (o != null) o.SetValue(RunDegeri, BaslangicYoneticisi.OnayVerisi(true, 0), RegistryValueKind.Binary);
            }
            catch { }
        }
    }

    // Sözleşmeye ek: "Windows ile başlat" açık ama başka bir exe yolunu gösteriyor (program taşınmış).
    // Düzeltmek için WindowsIleBaslat = true yeniden atanır.
    public bool BaslangicYoluEski
    {
        get
        {
            string k = RunKomutu();
            return k != null && !string.Equals(k.Trim(), BeklenenKomut(), StringComparison.OrdinalIgnoreCase);
        }
    }

    static string BeklenenKomut() { return "\"" + Application.ExecutablePath + "\" --arkaplan"; }

    static string RunKomutu()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunAnahtari))
            {
                if (k == null) return null;
                string d = k.GetValue(RunDegeri) as string;
                return string.IsNullOrWhiteSpace(d) ? null : d;
            }
        }
        catch { return null; }
    }

    public bool KapanisiYakala(FormClosingEventArgs e)
    {
        if (e == null || kapandi || cikisIstendi || e.CloseReason != CloseReason.UserClosing || !ArkaPlandaCalis) return false;
        e.Cancel = true;
        anaForm.Hide();
        if (!Ayarlar.Oku(AyarIpucu, false))
        {
            Ayarlar.Yaz(AyarIpucu, true);
            Bildir("Arka planda çalışıyorum",
                   "Bellek dolarsa haber veririm. Tamamen kapatmak için simgeye sağ tıkla → Çıkış.", ToolTipIcon.Info);
        }
        return true;
    }

    public void Goster()
    {
        if (kapandi || anaForm == null || anaForm.IsDisposed) return;
        try
        {
            anaForm.Show();
            // Simge durumuna Windows'tan (görev çubuğu, Win+D) küçültülmüşse WindowState bunu her zaman bilmez: Win32'ye sor.
            if (anaForm.IsHandleCreated && Yerel.IsIconic(anaForm.Handle)) Yerel.ShowWindow(anaForm.Handle, 9 /* SW_RESTORE */);
            else if (anaForm.WindowState == FormWindowState.Minimized) anaForm.WindowState = FormWindowState.Normal;
            anaForm.Activate();
            anaForm.BringToFront();
            if (anaForm.IsHandleCreated) Yerel.SetForegroundWindow(anaForm.Handle);
        }
        catch { }
    }

    public void Bildir(string baslik, string metin, ToolTipIcon simgeTuru, Action tiklaninca)
    {
        Bildir(baslik, metin, simgeTuru);
        balonEylemi = tiklaninca;
    }

    public void Bildir(string baslik, string metin, ToolTipIcon simgeTuru)
    {
        balonEylemi = null;
        if (kapandi) return;
        if (string.IsNullOrEmpty(baslik)) baslik = "Sergen PC Temizle";
        if (string.IsNullOrEmpty(metin)) metin = baslik;
        if (baslik.Length > 63) baslik = baslik.Substring(0, 62) + "…";
        if (metin.Length > 255) metin = metin.Substring(0, 254) + "…";
        try
        {
            simge.ShowBalloonTip(8000, baslik, metin, simgeTuru);
            sonBalon = DateTime.UtcNow;
        }
        catch { }
    }

    // Programı tamamen kapatır (tepsiye inmez): tepsi menüsündeki Çıkış ve güncelleme sonrası yeniden başlatma.
    public void Cik()
    {
        cikisIstendi = true;
        try { if (anaForm != null && !anaForm.IsDisposed) anaForm.Close(); } catch { }
    }

    void OtomatikOptimizeCalistir()
    {
        if (otomatikOptimize == null) return;
        try { otomatikOptimize(); } catch { }
    }

    // Arayüz iş parçacığında, 5 saniyede bir: sanal bellek / RAM uyarıları ve bekleyen sızıntı uyarıları.
    void BellegeBak()
    {
        if (kapandi) return;
        var profil = Profil;
        if (profil != null) profil.Tik();
        var m = Yerel.Bellek();
        if (m.ullTotalPageFile == 0 || m.ullTotalPhys == 0) return;
        double commit = (double)(m.ullTotalPageFile - m.ullAvailPageFile) / m.ullTotalPageFile;
        double ram = (double)(m.ullTotalPhys - m.ullAvailPhys) / m.ullTotalPhys;
        DateTime simdi = DateTime.UtcNow;

        IpucunuYaz(commit, ram);

        // Histerezis: %85 altına inince bekleme sıfırlanır, tekrar dolarsa hemen uyarılır.
        if (commit < 0.85) { sonCommitUyari = DateTime.MinValue; sonKritikUyari = DateTime.MinValue; }
        if (ram < 0.85) sonRamUyari = DateTime.MinValue;

        // Pencere zaten öndeyse balon gereksiz (pencerede kırmızı uyarı var); "gördü" sayılır.
        bool onde = anaForm != null && !anaForm.IsDisposed && anaForm.Visible
                    && anaForm.WindowState != FormWindowState.Minimized && Form.ActiveForm == anaForm;

        string baslik = null, metin = null;
        var tur = ToolTipIcon.Warning;
        if (commit >= 0.97)
        {
            if (simdi - sonKritikUyari >= TimeSpan.FromMinutes(3))
            {
                sonKritikUyari = simdi; sonCommitUyari = simdi;
                baslik = string.Format("Sanal bellek %{0} dolu!", Yuzde(commit));
                metin = "Programlar (oyunlar dahil) her an \"bellek yetersiz\" diye kapanabilir. Tıkla, programı aç; gereksiz programları kapatıp Temizle'ye bas (sık oluyorsa Sanal belleği büyüt).";
                tur = ToolTipIcon.Error;
            }
        }
        else if (commit >= 0.90)
        {
            if (simdi - sonCommitUyari >= TimeSpan.FromMinutes(10))
            {
                sonCommitUyari = simdi;
                baslik = string.Format("Sanal bellek %{0} dolu", Yuzde(commit));
                metin = "Programlar (oyunlar dahil) \"bellek yetersiz\" diye kapanabilir. Tıkla, programı aç; gereksiz programları kapatıp Temizle'ye bas (sık oluyorsa Sanal belleği büyüt).";
            }
        }
        else if (ram >= 0.95)
        {
            if (simdi - sonRamUyari >= TimeSpan.FromMinutes(15))
            {
                sonRamUyari = simdi;
                baslik = string.Format("RAM %{0} dolu", Yuzde(ram));
                metin = "Bilgisayar takılabilir. Tıkla, programı aç; gereksiz programları kapatıp Temizle'ye bas.";
                tur = ToolTipIcon.Info;
            }
        }
        if (baslik != null)
        {
            if (!onde)
            {
                Bildir(baslik, metin, tur);
                // Tam ekran oyunda balon görünmeyebilir: sanal bellek uyarısı sesle de verilir (RAM uyarısı sessiz).
                if (tur != ToolTipIcon.Info && OyunSuruyor) Ses.Cal(SesTuru.Uyari);
            }
            return;   // bu turda sızıntı balonu ile üst üste binmesin
        }

        SizintiyiBildir(simdi, onde);
    }

    void SizintiyiBildir(DateTime simdi, bool onde)
    {
        if (simdi - sonBalon < TimeSpan.FromSeconds(20)) return;   // önceki balon okunsun
        List<SizintiSupheli> liste;
        lock (supheliKilit) liste = sonSupheliler;
        if (liste == null || liste.Count == 0) return;

        foreach (var s in liste)
        {
            DateTime once;
            if (sizintiBildirildi.TryGetValue(s.Ad, out once) && simdi - once < TimeSpan.FromMinutes(60)) continue;
            sizintiBildirildi[s.Ad] = simdi;
            if (onde) return;   // pencere öndeyse sessizce "gördü" say (Sağlık sekmesi listeler)
            string ad = s.Gorunen ?? s.Ad;
            if (ad.Length > 40) ad = ad.Substring(0, 39) + "…";
            Bildir(ad + " sürekli büyüyor",
                   string.Format("{0} +{1} (şu an {2}). Bellek sızıntısı olabilir; gerekmiyorsa kapat.",
                                 SureMetni(s.Sure), AnaForm.Boyut(s.Artis), AnaForm.Boyut(s.Simdi)),
                   ToolTipIcon.Warning);
            return;   // her turda en fazla bir sızıntı balonu
        }
        // Artık şüpheli olmayanların bekleme kaydını tut (60 dk kuralı), çok eskileri temizle.
        foreach (var ad in sizintiBildirildi.Where(k => simdi - k.Value > TimeSpan.FromHours(3)).Select(k => k.Key).ToList())
            sizintiBildirildi.Remove(ad);
    }

    static string SureMetni(TimeSpan t)
    {
        int dk = (int)Math.Round(t.TotalMinutes);
        if (dk < 60) return dk + " dakikada";
        int sa = dk / 60; dk %= 60;
        return dk == 0 ? sa + " saatte" : sa + " saat " + dk + " dakikada";
    }

    static int Yuzde(double oran) { return (int)Math.Floor(Math.Min(1, Math.Max(0, oran)) * 100); }

    string sonIpucu;
    void IpucunuYaz(double commit, double ram)
    {
        string t = Baslik + "\nSanal bellek %" + Yuzde(commit) + " · RAM %" + Yuzde(ram);
        if (t.Length > 63) t = Baslik;   // .NET Framework 63 karakterden uzununu kabul etmez
        if (t == sonIpucu) return;
        sonIpucu = t;
        try { simge.Text = t; } catch { }
    }

    // Arka plan iş parçacığında, 30 saniyede bir. Üst üste binmez (ornekleniyor bayrağı).
    void Ornekle(object durum)
    {
        if (kapandi || Interlocked.Exchange(ref ornekleniyor, 1) == 1) return;
        try
        {
            var toplam = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Process[] surecler;
            try { surecler = Process.GetProcesses(); } catch { return; }
            foreach (var p in surecler)
            {
                using (p)
                {
                    try
                    {
                        if (kapandi) continue;
                        if (p.Id == benim || p.Id == 0 || p.SessionId != oturum) continue;
                        string ad = p.ProcessName;
                        if (string.IsNullOrEmpty(ad) || AnaForm.Korunuyor(ad)) continue;
                        if (!izleyici.AdBiliniyor(ad)) Tanit(p, ad);
                        if (sistemAdlari.Contains(ad)) continue;
                        long b = p.PrivateMemorySize64;
                        long eski;
                        toplam.TryGetValue(ad, out eski);
                        toplam[ad] = eski + b;
                    }
                    catch { }
                }
            }
            if (kapandi) return;
            izleyici.Ekle(DateTime.UtcNow, toplam.ToList());
            var yeni = izleyici.Supheliler();
            lock (supheliKilit) sonSupheliler = yeni;
        }
        catch { }
        finally { Interlocked.Exchange(ref ornekleniyor, 0); }
    }

    // İlk görülen süreç adı: dosya yolundan açıklama (Google Chrome) ve Windows klasöründen mi çalışıyor.
    void Tanit(Process p, string ad)
    {
        string yol = null;
        try
        {
            IntPtr h = Yerel.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var tampon = new StringBuilder(1024);
                    int boyut = tampon.Capacity;
                    if (Yerel.QueryFullProcessImageName(h, 0, tampon, ref boyut)) yol = tampon.ToString();
                }
                finally { Yerel.CloseHandle(h); }
            }
        }
        catch { }
        if (string.IsNullOrEmpty(yol))
        {
            try { yol = p.MainModule.FileName; } catch { }
        }

        string gorunen = ad;
        // Yolu okunamayan (dwm, csrss, winlogon gibi başka hesapla çalışan) süreçler izlenmez:
        // bunlar sistem parçasıdır, kullanıcı kapatamaz; uyarı vermenin anlamı yok.
        if (string.IsNullOrEmpty(yol)) sistemAdlari.Add(ad);
        else
        {
            // Sistem sekmesinde listelenmeyen (üretici yazılımı, bağlantı aracı) süreçler sızıntı için de izlenmez.
            if (yol.StartsWith(WinKlasoru, StringComparison.OrdinalIgnoreCase) || AnaForm.Korunuyor(ad, yol)) sistemAdlari.Add(ad);
            try
            {
                string d = FileVersionInfo.GetVersionInfo(yol).FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) gorunen = d.Trim();
            }
            catch { }
        }
        izleyici.AdTanit(ad, gorunen);
    }

    public void Dispose()
    {
        if (kapandi) return;
        kapandi = true;
        try { bellekZamanlayici.Stop(); bellekZamanlayici.Dispose(); } catch { }
        try { if (Profil != null) Profil.Kapat(); } catch { }   // oyun sırasında kapanırsa güç modu eski hâline döner
        try { ornekZamanlayici.Dispose(); } catch { }
        try { if (kisayol != null) kisayol.Dispose(); } catch { }
        try { simge.Visible = false; simge.Dispose(); } catch { }
        try { menu.Dispose(); } catch { }
    }
}
