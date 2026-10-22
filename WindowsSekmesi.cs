// Windows sekmesi: Win11Debloat tarzı kategoriler (solda), her kategorinin ayarları (sağda, işaretli = uygulanmış).
// Kullanıcı işaretleri değiştirir, "Uygula" yalnızca farkları yapar: yeni işaretlenen uygulanır, işareti kaldırılan
// eski hâline döner (WindowsAyarlari). Son kategori "Uygulamaları kaldır": bu bilgisayarda kurulu hazır uygulamalar.
// Kendiliğinden hiçbir şeyi değiştirmez; her işlem öncesinde ne olacağı sorulur.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

class WindowsSekmesi : Panel
{
    const string UygulamaKategorisi = "uygulamalar", AgKategorisi = "ag", GucKategorisi = "guc";
    static readonly Color Yesil = Color.FromArgb(102, 187, 106);

    readonly Panel solPanel = new Panel(), satirlar = new Panel();
    readonly Label lblKategori = new Label(), lblKategoriAciklama = new Label(), lblDurum = new Label();
    readonly Button btnOneri, btnSifirla, btnUygula, btnGezgin;
    readonly CheckBox chkNokta = new CheckBox();
    readonly ToolTip ipucu = new ToolTip { AutoPopDelay = 15000 };
    readonly Dictionary<string, Button> kategoriDugmeleri = new Dictionary<string, Button>();
    readonly Dictionary<string, bool> istenen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AyarDurumu> durumlar = new Dictionary<string, AyarDurumu>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<HazirUygulama> kaldirilacak = new HashSet<HazirUygulama>();
    readonly Font kalin = new Font("Segoe UI", 9.5f, FontStyle.Bold), kucuk = new Font("Segoe UI", 8.25f);
    Dictionary<HazirUygulama, List<string>> kurulu = new Dictionary<HazirUygulama, List<string>>();
    string secili = "gizlilik";
    bool yuklendi, isleniyor;

    public WindowsSekmesi()
    {
        Dock = DockStyle.Fill;
        BackColor = Renk.Arka;

        var baslik = new Panel { Dock = DockStyle.Top, Height = 66, Padding = new Padding(14, 8, 12, 4) };
        baslik.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, ForeColor = Renk.Soluk, AutoEllipsis = true, Font = new Font("Segoe UI", 8.5f),
            Text = "WINDOWS AYARLARI · Win11Debloat'tan seçilmiş, geri alınabilir ayarlar\n" +
                   "İşaretli olanlar şu an uygulanmış demektir. İşaretle → Uygula; işareti kaldırıp Uygula'ya basarsan eski hâline döner. " +
                   "Edge ve OneDrive'a dokunulmaz."
        });

        // Solda kategoriler
        solPanel.Dock = DockStyle.Left;
        solPanel.Width = 214;
        solPanel.Padding = new Padding(12, 0, 6, 8);
        solPanel.AutoScroll = true;   // en kısa pencerede son kategoriler de ulaşılabilsin
        // Koyu kaydırma çubuğu (KoyuListe ile aynı).
        foreach (var kaydirilan in new Control[] { solPanel, satirlar })
            kaydirilan.HandleCreated += delegate(object o, EventArgs e) { try { Yerel.SetWindowTheme(((Control)o).Handle, "DarkMode_Explorer", null); } catch { } };
        var kategoriler = WindowsAyarlari.Kategoriler.Where(k => WindowsAyarlari.BuSurumdekiler.Any(a => a.Kategori == k.Kimlik))
            .Select(k => new KeyValuePair<string, string>(k.Kimlik, k.Ad)).ToList();
        kategoriler.Add(new KeyValuePair<string, string>(AgKategorisi, "Ağ"));
        kategoriler.Add(new KeyValuePair<string, string>(GucKategorisi, "Güç"));
        kategoriler.Add(new KeyValuePair<string, string>(UygulamaKategorisi, "Uygulamaları kaldır"));
        foreach (var k in Enumerable.Reverse(kategoriler))   // Dock=Top: son eklenen en üstte
        {
            var b = new Button
            {
                Dock = DockStyle.Top, Height = 31, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 4, 0), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9.5f), Tag = k.Key, Text = k.Value
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Renk.Cizgi;
            string kimlik = k.Key;
            b.Click += delegate { KategoriSec(kimlik); };
            kategoriDugmeleri[k.Key] = b;
            solPanel.Controls.Add(b);
            if (k.Key == UygulamaKategorisi || k.Key == AgKategorisi) solPanel.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10 });
        }

        // Sağda seçili kategorinin başlığı ve ayarları
        var sag = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 12, 0) };
        lblKategori.Dock = DockStyle.Top;
        lblKategori.Height = 24;
        lblKategori.ForeColor = Renk.Yazi;
        lblKategori.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        lblKategoriAciklama.Dock = DockStyle.Top;
        lblKategoriAciklama.Height = 24;
        lblKategoriAciklama.ForeColor = Renk.Soluk;
        lblKategoriAciklama.AutoEllipsis = true;
        satirlar.Dock = DockStyle.Fill;
        satirlar.AutoScroll = true;
        satirlar.BackColor = Renk.Panel;
        sag.Controls.Add(satirlar);
        sag.Controls.Add(lblKategoriAciklama);
        sag.Controls.Add(lblKategori);

        // Altta: önerilenler, sıfırla, durum, geri yükleme noktası, uygula
        var alt = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(12, 8, 12, 6) };
        btnOneri = Dugme("Önerilenleri seç", false);
        btnSifirla = Dugme("Seçimi sıfırla", false);
        btnUygula = Dugme("Uygula", true);
        btnGezgin = Dugme("Gezgini yeniden başlat", false);
        btnOneri.Dock = btnSifirla.Dock = DockStyle.Left;
        btnOneri.Width = 150;
        btnSifirla.Width = 120;
        btnUygula.Width = 190;
        btnGezgin.Width = 180;
        btnGezgin.Visible = false;
        btnOneri.Click += delegate { OnerilenleriSec(); };
        btnSifirla.Click += delegate { Sifirla(); };
        btnUygula.Click += async delegate { await UygulaAsync(); };
        btnGezgin.Click += async delegate { await GezginAsync(); };
        chkNokta.Text = "Önce geri yükleme noktası";
        chkNokta.Checked = true;
        chkNokta.ForeColor = Renk.Soluk;
        chkNokta.Dock = DockStyle.Right;
        chkNokta.Width = 190;
        chkNokta.Padding = new Padding(8, 0, 0, 0);
        ipucu.SetToolTip(chkNokta, "Yönetici izni isteyen ayarlardan önce Windows'un Sistem Geri Yükleme noktası oluşturulur (Windows günde en fazla bir tane izin verir).\n" +
                                   "Program ayrıca her ayarın eski değerini kendisi de yedekler; işareti kaldırıp Uygula'ya basmak onu geri yazar.");
        // Durum yazısı düğmelerin üstünde kendi satırında: uzun sonuç yazıları düğmelerin arasına sıkışmasın.
        var durumSatiri = new Panel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(14, 4, 12, 0) };
        lblDurum.Dock = DockStyle.Fill;
        lblDurum.TextAlign = ContentAlignment.MiddleLeft;
        lblDurum.ForeColor = Renk.Soluk;
        lblDurum.AutoEllipsis = true;
        durumSatiri.Controls.Add(lblDurum);
        alt.Controls.Add(new Panel { Dock = DockStyle.Fill });
        alt.Controls.Add(chkNokta);
        alt.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        alt.Controls.Add(btnGezgin);
        alt.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        alt.Controls.Add(btnUygula);
        alt.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 });
        alt.Controls.Add(btnSifirla);
        alt.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 });
        alt.Controls.Add(btnOneri);
        // Dar pencerede durum yazısının yeri kalmazsa geri yükleme kutusu yazısını kısaltır.
        alt.Resize += delegate { chkNokta.Text = alt.Width < 1080 ? "Geri yükleme noktası" : "Önce geri yükleme noktası"; chkNokta.Width = alt.Width < 1080 ? 160 : 190; };

        Controls.Add(sag);
        Controls.Add(solPanel);
        Controls.Add(baslik);
        Controls.Add(durumSatiri);
        Controls.Add(alt);
    }

    static Button Dugme(string metin, bool vurgu)
    {
        var b = new Button
        {
            Text = metin, Dock = DockStyle.Right, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = vurgu ? Renk.Vurgu : Renk.Panel, ForeColor = vurgu ? Color.White : Renk.Yazi,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        b.FlatAppearance.BorderSize = vurgu ? 0 : 1;
        b.FlatAppearance.BorderColor = Renk.Cizgi;
        return b;
    }

    public void Gosterildi()
    {
        if (isleniyor) return;
        if (!yuklendi) { yuklendi = true; DurumlariOku(true); }
        else DurumlariOku(false);   // başka bir yerden (Ayarlar uygulaması) değişmiş olabilir; bekleyen seçimler korunur
        KategoriSec(secili);
    }

    // Kayıt defterinden güncel durum. sifirla: işaretler de duruma eşitlenir (bekleyen seçimler silinir).
    void DurumlariOku(bool sifirla)
    {
        foreach (var a in WindowsAyarlari.BuSurumdekiler)
        {
            var d = WindowsAyarlari.Durum(a);
            bool eskiUygulandi = durumlar.ContainsKey(a.Kimlik) && durumlar[a.Kimlik] == AyarDurumu.Uygulandi;
            durumlar[a.Kimlik] = d;
            // Bekleyen değişiklik yoksa işaret yeni duruma uyar.
            if (sifirla || !istenen.ContainsKey(a.Kimlik) || istenen[a.Kimlik] == eskiUygulandi)
                istenen[a.Kimlik] = d == AyarDurumu.Uygulandi;
        }
        kurulu = HazirUygulamalar.Kurulu();
        kaldirilacak.RemoveWhere(u => !kurulu.ContainsKey(u));
        KategoriYazilariniYaz();
        UygulaDugmesiniYaz();
    }

    void KategoriYazilariniYaz()
    {
        foreach (var kv in kategoriDugmeleri)
        {
            var b = kv.Value;
            bool sec = kv.Key == secili;
            b.BackColor = sec ? Renk.Panel : Renk.Arka;
            b.ForeColor = sec ? Renk.Yazi : Renk.Soluk;
            if (kv.Key == AgKategorisi) { b.Text = "Ağ"; continue; }
            if (kv.Key == GucKategorisi) { b.Text = "Güç"; continue; }
            string ad = kv.Key == UygulamaKategorisi ? "Uygulamaları kaldır"
                      : WindowsAyarlari.Kategoriler.First(k => k.Kimlik == kv.Key).Ad;
            if (kv.Key == UygulamaKategorisi) b.Text = ad + "  (" + kurulu.Count + ")";
            else
            {
                var l = WindowsAyarlari.BuSurumdekiler.Where(a => a.Kategori == kv.Key).ToList();
                int uyg = l.Count(a => durumlar.ContainsKey(a.Kimlik) && durumlar[a.Kimlik] == AyarDurumu.Uygulandi);
                int bekleyen = l.Count(Bekliyor);
                b.Text = ad + "  " + uyg + "/" + l.Count + (bekleyen > 0 ? "  •" : "");
            }
        }
    }

    bool Bekliyor(WinAyar a)
    {
        AyarDurumu d;
        bool ist;
        if (!durumlar.TryGetValue(a.Kimlik, out d) || !istenen.TryGetValue(a.Kimlik, out ist)) return false;
        return ist ? d != AyarDurumu.Uygulandi : d == AyarDurumu.Uygulandi;
    }

    void KategoriSec(string kimlik)
    {
        secili = kimlik;
        KategoriYazilariniYaz();
        satirlar.SuspendLayout();
        try
        {
            foreach (Control c in satirlar.Controls.Cast<Control>().ToList()) c.Dispose();
            satirlar.Controls.Clear();
            satirlar.AutoScrollPosition = new Point(0, 0);
            if (kimlik == UygulamaKategorisi) UygulamalariDoldur();
            else if (kimlik == AgKategorisi) AgDoldur();
            else if (kimlik == GucKategorisi) GucDoldur();
            else AyarlariDoldur(kimlik);
        }
        finally { satirlar.ResumeLayout(true); }
        UygulaDugmesiniYaz();
    }

    void AyarlariDoldur(string kategori)
    {
        var k = WindowsAyarlari.Kategoriler.First(x => x.Kimlik == kategori);
        lblKategori.Text = k.Ad;
        lblKategoriAciklama.Text = k.Aciklama;
        btnOneri.Text = "Önerilenleri seç";
        var liste = WindowsAyarlari.BuSurumdekiler.Where(a => a.Kategori == kategori).ToList();
        foreach (var a in Enumerable.Reverse(liste)) satirlar.Controls.Add(AyarSatiri(a));
    }

    Control AyarSatiri(WinAyar a)
    {
        var satir = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(10, 6, 10, 4) };
        var chk = new CheckBox
        {
            AutoSize = true, Location = new Point(10, 7), Font = kalin, ForeColor = Renk.Yazi, Text = a.Baslik,
            Checked = istenen.ContainsKey(a.Kimlik) && istenen[a.Kimlik], Cursor = Cursors.Hand
        };
        var etiket = new Label { AutoSize = true, Font = kucuk, Location = new Point(chk.PreferredSize.Width + 16, 10) };
        var aciklama = new Label
        {
            Location = new Point(28, 31), Height = 20, ForeColor = Renk.Soluk, AutoEllipsis = true, Text = a.Aciklama,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
        };
        Action etiketYaz = delegate
        {
            AyarDurumu d;
            durumlar.TryGetValue(a.Kimlik, out d);
            var parca = new List<string>();
            Color renk = Renk.Soluk;
            if (Bekliyor(a)) { parca.Add(chk.Checked ? "uygulanacak" : "eski hâline dönecek"); renk = Renk.Turuncu; }
            else if (d == AyarDurumu.Uygulandi) { parca.Add("uygulandı"); renk = Yesil; }
            else if (d == AyarDurumu.Kismen) { parca.Add("kısmen uygulanmış"); renk = Renk.Turuncu; }
            if (a.Yonetici) parca.Add("yönetici izni");
            if (a.Etki == Etki.Gezgin) parca.Add("Gezgin yeniden başlayınca");
            if (a.Etki == Etki.Oturum) parca.Add("oturum kapatıp açınca");
            if (a.Oneri) parca.Add("önerilen");
            etiket.Text = parca.Count > 0 ? "· " + string.Join(" · ", parca) : "";
            etiket.ForeColor = renk;
        };
        etiketYaz();
        chk.CheckedChanged += delegate
        {
            istenen[a.Kimlik] = chk.Checked;
            etiketYaz();
            KategoriYazilariniYaz();
            UygulaDugmesiniYaz();
        };
        satir.Resize += delegate { aciklama.Width = Math.Max(100, satir.ClientSize.Width - 40); };
        ipucu.SetToolTip(chk, a.Aciklama);
        ipucu.SetToolTip(aciklama, a.Aciklama);
        satir.Controls.Add(chk);
        satir.Controls.Add(etiket);
        satir.Controls.Add(aciklama);
        satir.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Renk.Cizgi });
        return satir;
    }

    void UygulamalariDoldur()
    {
        lblKategori.Text = "Uygulamaları kaldır";
        lblKategoriAciklama.Text = "Windows'la ya da üreticiyle kendiliğinden gelen uygulamalardan bu bilgisayarda kurulu olanlar. " +
                                   "Yalnızca senin hesabından kaldırılır; Microsoft Store'dan yeniden kurulabilir.";
        btnOneri.Text = "Önerilenleri seç";
        if (kurulu.Count == 0)
        {
            satirlar.Controls.Add(new Label
            {
                Dock = DockStyle.Top, Height = 60, Padding = new Padding(12, 12, 12, 0), ForeColor = Renk.Soluk,
                Text = "Listedeki hazır uygulamalardan hiçbiri bu bilgisayarda kurulu değil. Kaldırılacak bir şey yok."
            });
            return;
        }
        // Dock=Top: son eklenen en üstte; görünüş sırası önce önerilenler, sonra ada göre.
        foreach (var u in kurulu.Keys.OrderBy(x => x.Oneri).ThenByDescending(x => x.Ad, StringComparer.CurrentCulture))
        {
            var uyg = u;
            var satir = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 4, 10, 2) };
            var chk = new CheckBox
            {
                AutoSize = true, Location = new Point(10, 9), Font = kalin, ForeColor = Renk.Yazi, Text = u.Ad,
                Checked = kaldirilacak.Contains(u), Cursor = Cursors.Hand
            };
            var aciklama = new Label
            {
                AutoSize = true, Location = new Point(chk.PreferredSize.Width + 16, 11), ForeColor = Renk.Soluk,
                Text = u.Aciklama + (u.Oneri ? "  · önerilen" : "")
            };
            chk.CheckedChanged += delegate
            {
                if (chk.Checked) kaldirilacak.Add(uyg); else kaldirilacak.Remove(uyg);
                UygulaDugmesiniYaz();
            };
            ipucu.SetToolTip(chk, string.Join(", ", kurulu[u]));
            satir.Controls.Add(chk);
            satir.Controls.Add(aciklama);
            satir.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Renk.Cizgi });
            satirlar.Controls.Add(satir);
        }
    }

    // ---- Ağ ve Güç: her bölüm bir başlık, kısa açıklama ve kendi düğmeleri ----

    // Dock=Top satırlar ters sırayla eklendiği için bölümler toplanıp en sonda ters çevrilerek eklenir.
    readonly List<Control> bolumler = new List<Control>();

    FlowLayoutPanel Bolum(string baslik, string aciklama, int aciklamaSatiri)
    {
        var satir = new Panel { Dock = DockStyle.Top, Padding = new Padding(10, 8, 10, 8) };
        var b = new Label { Dock = DockStyle.Top, Height = 22, Font = kalin, ForeColor = Renk.Yazi, Text = baslik };
        var a = new Label { Dock = DockStyle.Top, Height = 6 + 17 * aciklamaSatiri, ForeColor = Renk.Soluk, Text = aciklama };
        var icerik = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, Padding = new Padding(0, 2, 0, 0) };
        satir.Controls.Add(icerik);
        satir.Controls.Add(a);
        satir.Controls.Add(b);
        satir.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Renk.Cizgi });
        satir.Height = 16 + b.Height + a.Height + icerik.Height + 1;
        bolumler.Add(satir);
        return icerik;
    }

    void BolumleriEkle()
    {
        for (int i = bolumler.Count - 1; i >= 0; i--) satirlar.Controls.Add(bolumler[i]);
        bolumler.Clear();
    }

    ComboBox Secim(int genislik)
    {
        return new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = genislik, FlatStyle = FlatStyle.Flat,
            BackColor = Renk.Arka, ForeColor = Renk.Yazi, Margin = new Padding(0, 3, 8, 0)
        };
    }

    Button KucukDugme(string metin)
    {
        var b = Dugme(metin, false);
        b.Dock = DockStyle.None;
        b.AutoSize = true;
        b.MinimumSize = new Size(90, 28);
        b.Height = 28;
        b.Margin = new Padding(0, 1, 8, 0);
        return b;
    }

    Label SatirYazisi(string metin)
    {
        return new Label { AutoSize = true, ForeColor = Renk.Soluk, Text = metin, Margin = new Padding(0, 7, 6, 0) };
    }

    void Bildir(string metin, bool sorun)
    {
        lblDurum.ForeColor = sorun ? Renk.Turuncu : Renk.Soluk;
        lblDurum.Text = metin;
        ipucu.SetToolTip(lblDurum, metin);
    }

    void AgDoldur()
    {
        lblKategori.Text = "Ağ";
        lblKategoriAciklama.Text = "DNS, Wi-Fi güç tasarrufu ve bağlantı testi. Hıza göre \"hızlandırma\" ayarları bilerek yok: Windows bunları kendisi ayarlar.";
        var bagdastiricilar = AgAyarlari.Bagdastiricilar();

        // 1) DNS
        var dns = Bolum("DNS sunucusu",
            "Alan adlarını (google.com) IP adresine çeviren sunucu. Cloudflare / Google çoğu zaman internet sağlayıcısınınkinden hızlı ve kararlıdır.\n" +
            "DPI aşma, VPN ya da reklam engelleme aracı kullanıyorsan dokunma; o araçlar kendi DNS'ini kullanır.\n" + AgAyarlari.DurumMetni(bagdastiricilar), 4);
        var cmbDns = Secim(300);
        foreach (var s in AgAyarlari.Saglayicilar) cmbDns.Items.Add(s);
        if (AgAyarlari.YedekVar) cmbDns.Items.Add("Önceki ayarına geri dön (yedek)");
        // Şu an kullanılan seçili gelir; bilinmeyen (elle girilmiş) DNS'te hiçbir şey seçili gelmez ve seçilene kadar
        // Uygula kapalı kalır: bakmadan basılınca elle girilmiş DNS kaybolmasın.
        cmbDns.SelectedIndex = AgAyarlari.SuankiSaglayici(bagdastiricilar);
        var btnDns = KucukDugme("Uygula · yönetici");
        btnDns.Enabled = cmbDns.SelectedIndex >= 0;
        cmbDns.SelectedIndexChanged += delegate { btnDns.Enabled = cmbDns.SelectedIndex >= 0; };
        btnDns.Click += async delegate
        {
            var sec = cmbDns.SelectedItem as AgAyarlari.DnsSaglayici;   // null = önceki ayar
            string ne = sec != null ? sec.Ad : "önceki ayarın";
            if (MessageBox.Show(FindForm(), "Bütün kablolu ve kablosuz bağlantıların DNS'i \"" + ne + "\" yapılacak (VPN / sanal bağdaştırıcılara dokunulmaz).\n\n" +
                                (sec != null && !AgAyarlari.YedekVar ? "Şu anki ayarın yedeklenir; buradan \"Önceki ayarına geri dön\" ile geri alabilirsin.\n\n" : "") +
                                "Windows bir kez izin isteyecek.", "DNS", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Mesgul(true, "DNS değiştiriliyor...");
            int kod = await Task.Run(() => AgAyarlari.DnsUygula(sec, YoneticiCalistir));
            Mesgul(false, null);
            KategoriSec(AgKategorisi);
            Bildir(kod == 0 ? "Tamam: DNS \"" + ne + "\" yapıldı, DNS önbelleği temizlendi." :
                   kod == -2 ? "DNS değiştirilmedi: yönetici izni verilmedi." : "DNS değiştirilemedi (kod " + kod + ").", kod != 0);
        };
        dns.Controls.Add(cmbDns);
        dns.Controls.Add(btnDns);

        // 2) Wi-Fi güç tasarrufu
        bool wifiVar = bagdastiricilar.Any(x => x.Kablosuz);
        var wifi = Bolum("Wi-Fi güç tasarrufu",
            "Wi-Fi kartı enerji tasarrufu yaparken oyunda ping sıçrayabilir. Prizdeyken \"En yüksek performans\" önerilir; pilde tasarruf pili uzatır." +
            (wifiVar ? "" : "\nBu bilgisayarda Wi-Fi kartı bulunamadı."), wifiVar ? 2 : 3);
        var cmbPriz = Secim(170);
        var cmbPil = Secim(170);
        foreach (var a in GucAyarlari.WifiAdlari) { cmbPriz.Items.Add(a); cmbPil.Items.Add(a); }
        int wp = GucAyarlari.Wifi(true), wl = GucAyarlari.Wifi(false);
        cmbPriz.SelectedIndex = Math.Max(0, wp);
        cmbPil.SelectedIndex = Math.Max(0, wl);
        var btnWifi = KucukDugme("Uygula");
        btnWifi.Click += delegate
        {
            bool oldu = GucAyarlari.WifiYaz(cmbPriz.SelectedIndex, GucAyarlari.PilVar ? cmbPil.SelectedIndex : -1);
            Bildir(oldu ? "Tamam: Wi-Fi güç tasarrufu ayarlandı (etkin güç planında)." : "Wi-Fi güç ayarı yazılamadı.", !oldu);
        };
        wifi.Controls.Add(SatirYazisi("Prizde:"));
        wifi.Controls.Add(cmbPriz);
        if (GucAyarlari.PilVar) { wifi.Controls.Add(SatirYazisi("Pilde:")); wifi.Controls.Add(cmbPil); }
        wifi.Controls.Add(btnWifi);
        wifi.Enabled = wifiVar && wp >= 0;

        // 3) Ağ testi
        var test = Bolum("Bağlantı testi",
            "Modeme ve internete 5 saniye ping atar: gecikme, dalgalanma (jitter) ve paket kaybı. Sorunun evdeki Wi-Fi / modemde mi, internet sağlayıcısında mı olduğunu söyler. Hiçbir ayarı değiştirmez.", 2);
        var btnTest = KucukDugme("Testi başlat");
        var lblSonuc = new Label { AutoSize = true, ForeColor = Renk.Yazi, Margin = new Padding(4, 7, 0, 0), Text = "" };
        var sonucKutu = new Label { Dock = DockStyle.Top, Height = 0, ForeColor = Renk.Yazi, Padding = new Padding(0, 4, 0, 0) };
        test.Parent.Controls.Add(sonucKutu);
        sonucKutu.BringToFront();
        btnTest.Click += async delegate
        {
            btnTest.Enabled = false;
            lblSonuc.Text = "Ölçülüyor (yaklaşık 5 saniye)...";
            List<AgAyarlari.TestSonucu> l = null;
            int sinyal = -1;
            try
            {
                await Task.Run(() => { l = AgAyarlari.Test(20); sinyal = AgAyarlari.WifiSinyali(); });
            }
            catch { }
            btnTest.Enabled = true;
            if (l == null) { lblSonuc.Text = "Test yapılamadı."; return; }
            lblSonuc.Text = AgAyarlari.Yorum(l, sinyal);
            sonucKutu.Text = string.Join("\n", l.Select(x => x.KayipYuzde >= 100
                ? string.Format("{0} ({1}): cevap yok", x.Ad, x.Adres)
                : string.Format("{0} ({1}): ortalama {2:0} ms · en çok {3:0} ms · dalgalanma {4:0} ms · kayıp %{5:0}",
                                x.Ad, x.Adres, x.Ortalama, x.EnCok, x.Dalgalanma, x.KayipYuzde)));
            if (sinyal >= 0) sonucKutu.Text += "\nWi-Fi sinyali: %" + sinyal;
            int ek = 20 * (l.Count + (sinyal >= 0 ? 1 : 0)) + 6;
            sonucKutu.Parent.Height += ek - sonucKutu.Height;
            sonucKutu.Height = ek;
        };
        test.Controls.Add(btnTest);
        test.Controls.Add(lblSonuc);

        // 4) İnternet bozuldu
        var bozuk = Bolum("İnternet bozuldu mu?",
            "Siteler açılmıyor ya da \"DNS hatası\" çıkıyorsa önce DNS önbelleğini temizle (zararsız, anında). Düzelmezse ağı sıfırla: Winsock ve TCP/IP " +
            "varsayılana döner, bilgisayarı yeniden başlatmak gerekir; VPN / DPI araçlarını yeniden kurman gerekebilir.", 2);
        var btnOnbellek = KucukDugme("DNS önbelleğini temizle");
        btnOnbellek.Click += delegate
        {
            bool oldu = AgAyarlari.DnsOnbellegiTemizle();
            Bildir(oldu ? "Tamam: DNS önbelleği temizlendi." : "DNS önbelleği temizlenemedi.", !oldu);
        };
        var btnSifirla = KucukDugme("Ağı sıfırla… · yönetici");
        btnSifirla.Click += async delegate
        {
            if (MessageBox.Show(FindForm(), "Ağ ayarları (Winsock ve TCP/IP) Windows varsayılanına döndürülecek.\n\n" +
                                "• Bitince bilgisayarı yeniden başlatman gerekir.\n• VPN, DPI aşma ya da proxy araçlarını yeniden kurman / ayarlaman gerekebilir.\n" +
                                "• Wi-Fi şifren ve elle verdiğin IP / DNS ayarları silinmez.\n\nDevam edilsin mi?", "Ağı sıfırla",
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            Mesgul(true, "Ağ sıfırlanıyor...");
            int kod = await Task.Run(() => YoneticiCalistir("--ag-sifirla"));
            Mesgul(false, null);
            Bildir(kod == 0 ? "Tamam: ağ sıfırlandı. Bilgisayarı yeniden başlat." :
                   kod == -2 ? "Ağ sıfırlanmadı: yönetici izni verilmedi." : "Ağ sıfırlanamadı (kod " + kod + ").", kod != 0);
        };
        bozuk.Controls.Add(btnOnbellek);
        bozuk.Controls.Add(btnSifirla);
        BolumleriEkle();
    }

    void GucDoldur()
    {
        lblKategori.Text = "Güç";
        lblKategoriAciklama.Text = "Güç planı ve Windows 11 güç modu. Yönetici izni gerekmez; istediğin zaman buradan geri değiştirebilirsin.";
        Guid etkin = GucAyarlari.EtkinPlan();
        bool dengeli = etkin == GucAyarlari.DengeliPlan;

        // 1) Güç planı
        var plan = Bolum("Güç planı",
            "Önerilen: Dengeli. Windows 11'in güç modu yalnızca Dengeli planda çalışır; \"Yüksek performans\" gibi eski planlar dizüstünü ısıtır ve pili hızlı bitirir, " +
            "oyunda Dengeli + \"En iyi performans\" modu aynı hızı verir.", 2);
        var cmbPlan = Secim(260);
        var planlar = GucAyarlari.Planlar();
        foreach (var p in planlar) cmbPlan.Items.Add(p);
        int sira = planlar.FindIndex(p => p.Kimlik == etkin);
        if (sira >= 0) cmbPlan.SelectedIndex = sira;
        var btnPlan = KucukDugme("Uygula");
        btnPlan.Click += delegate
        {
            var p = cmbPlan.SelectedItem as GucAyarlari.Plan;
            if (p == null) return;
            bool oldu = GucAyarlari.PlanSec(p.Kimlik);
            KategoriSec(GucKategorisi);
            Bildir(oldu ? "Tamam: güç planı \"" + p.Ad + "\" yapıldı." : "Güç planı değiştirilemedi.", !oldu);
        };
        plan.Controls.Add(cmbPlan);
        plan.Controls.Add(btnPlan);

        // 2) Güç modu
        bool prizde = GucAyarlari.Prizde;
        int modPriz = GucAyarlari.Mod(true), modPil = GucAyarlari.Mod(false);
        string simdi = "Şu an prizde: " + (modPriz >= 0 ? GucAyarlari.ModAdlari[modPriz] : "bilinmiyor") +
                       (GucAyarlari.PilVar ? " · pilde: " + (modPil >= 0 ? GucAyarlari.ModAdlari[modPil] : "bilinmiyor") : "") + ".";
        var mod = Bolum("Güç modu",
            (dengeli ? "Oyunda \"En iyi performans\", pilde \"En iyi pil verimliliği\" önerilir. Seçtiğin mod şu anki güç kaynağına (" +
                       (prizde ? "prizde" : "pilde") + ") uygulanır; diğeri için fişi takıp / çekip yeniden seç (Windows Ayarlar'daki gibi). "
                     : "Güç modu yalnızca Dengeli planda çalışır; önce güç planını Dengeli yap. ") + simdi, 2);
        var cmbMod = Secim(220);
        foreach (var a in GucAyarlari.ModAdlari) cmbMod.Items.Add(a);
        int simdiki = prizde ? modPriz : modPil;
        cmbMod.SelectedIndex = simdiki >= 0 ? simdiki : 1;
        var btnMod = KucukDugme(prizde ? "Prizdeyken uygula" : "Pildeyken uygula");
        btnMod.Click += delegate
        {
            bool oldu = GucAyarlari.ModSec(cmbMod.SelectedIndex);
            KategoriSec(GucKategorisi);
            Bildir(oldu ? "Tamam: güç modu \"" + GucAyarlari.ModAdlari[cmbMod.SelectedIndex] + "\" yapıldı." : "Güç modu değiştirilemedi.", !oldu);
        };
        mod.Controls.Add(cmbMod);
        mod.Controls.Add(btnMod);
        mod.Enabled = dengeli;
        BolumleriEkle();
    }

    List<WinAyar> Uygulanacaklar()
    {
        return WindowsAyarlari.BuSurumdekiler.Where(a => istenen.ContainsKey(a.Kimlik) && istenen[a.Kimlik]
                                                         && durumlar[a.Kimlik] != AyarDurumu.Uygulandi).ToList();
    }

    List<WinAyar> GeriAlinacaklar()
    {
        return WindowsAyarlari.BuSurumdekiler.Where(a => istenen.ContainsKey(a.Kimlik) && !istenen[a.Kimlik]
                                                         && durumlar[a.Kimlik] == AyarDurumu.Uygulandi).ToList();
    }

    void UygulaDugmesiniYaz()
    {
        // Ağ ve Güç bölümlerinde her satırın kendi düğmesi var; alttaki seçim düğmeleri gizlenir.
        bool ozel = secili == AgKategorisi || secili == GucKategorisi;
        btnUygula.Visible = btnOneri.Visible = btnSifirla.Visible = !ozel;
        if (ozel) { chkNokta.Visible = false; return; }
        if (secili == UygulamaKategorisi)
        {
            btnUygula.Text = kaldirilacak.Count > 0 ? "Seçilenleri kaldır (" + kaldirilacak.Count + ")" : "Seçilenleri kaldır";
            btnUygula.Enabled = !isleniyor && kaldirilacak.Count > 0;
        }
        else
        {
            int n = Uygulanacaklar().Count + GeriAlinacaklar().Count;
            btnUygula.Text = n > 0 ? "Uygula (" + n + " değişiklik)" : "Uygula";
            btnUygula.Enabled = !isleniyor && n > 0;
        }
        btnOneri.Enabled = btnSifirla.Enabled = !isleniyor;
        chkNokta.Visible = secili != UygulamaKategorisi;
    }

    void OnerilenleriSec()
    {
        if (secili == UygulamaKategorisi)
        {
            foreach (var u in kurulu.Keys) if (u.Oneri) kaldirilacak.Add(u);
        }
        else
        {
            // Bütün kategorilerdeki önerilenler işaretlenir; işaretli olanların işareti kaldırılmaz.
            foreach (var a in WindowsAyarlari.BuSurumdekiler) if (a.Oneri) istenen[a.Kimlik] = true;
        }
        KategoriSec(secili);
        lblDurum.ForeColor = Renk.Soluk;
        lblDurum.Text = secili == UygulamaKategorisi
            ? "Önerilen uygulamalar işaretlendi (oyunlar, reklam uygulamaları, Microsoft'un bıraktıkları). Kaldırmadan önce listeye bak."
            : "Bütün kategorilerdeki önerilen ayarlar işaretlendi. Kategorilere göz at, sonra Uygula'ya bas.";
    }

    void Sifirla()
    {
        foreach (var a in WindowsAyarlari.BuSurumdekiler) istenen[a.Kimlik] = durumlar[a.Kimlik] == AyarDurumu.Uygulandi;
        kaldirilacak.Clear();
        KategoriSec(secili);
        lblDurum.ForeColor = Renk.Soluk;
        lblDurum.Text = "Seçimler bilgisayarın şu anki hâline döndü; hiçbir şey değişmedi.";
    }

    void Mesgul(bool m, string metin)
    {
        isleniyor = m;
        foreach (var b in kategoriDugmeleri.Values) b.Enabled = !m;
        satirlar.Enabled = !m;
        btnGezgin.Enabled = !m;
        UygulaDugmesiniYaz();
        if (metin != null) { lblDurum.ForeColor = Renk.Turuncu; lblDurum.Text = metin; }
    }

    async Task UygulaAsync()
    {
        if (isleniyor) return;
        if (secili == UygulamaKategorisi) { await KaldirAsync(); return; }
        var uygula = Uygulanacaklar();
        var geri = GeriAlinacaklar();
        if (uygula.Count + geri.Count == 0) return;
        bool yonetici = uygula.Concat(geri).Any(a => a.Yonetici);
        var soru = new List<string>();
        if (uygula.Count > 0) soru.Add(uygula.Count + " ayar uygulanacak:\n" + string.Join("\n", uygula.Take(12).Select(a => "  • " + a.Baslik)) + (uygula.Count > 12 ? "\n  • …" : ""));
        if (geri.Count > 0) soru.Add(geri.Count + " ayar eski hâline dönecek:\n" + string.Join("\n", geri.Take(12).Select(a => "  • " + a.Baslik)) + (geri.Count > 12 ? "\n  • …" : ""));
        if (yonetici) soru.Add("Bazıları yönetici izni ister; Windows bir kez \"Evet\" diye soracak." +
                               (chkNokta.Checked ? " Önce geri yükleme noktası oluşturulur (biraz sürebilir)." : ""));
        soru.Add("Her ayarın eski değeri yedeklenir; işaretini kaldırıp Uygula'ya basarak geri alabilirsin.");
        if (MessageBox.Show(FindForm(), string.Join("\n\n", soru), "Windows ayarları", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        Mesgul(true, yonetici && chkNokta.Checked ? "Uygulanıyor (geri yükleme noktası oluşturuluyor, bir dakika sürebilir)..." : "Uygulanıyor...");
        bool nokta = chkNokta.Checked;
        WindowsAyarlari.Sonuc s = null;
        try { s = await Task.Run(() => WindowsAyarlari.Uygula(uygula, geri, nokta, YoneticiCalistir)); }
        catch (Exception e) { s = new WindowsAyarlari.Sonuc { Hata = e.Message }; }
        Mesgul(false, null);
        DurumlariOku(true);
        KategoriSec(secili);

        int yapilanUyg = s.Yapilan.Count(uygula.Contains), yapilanGeri = s.Yapilan.Count(geri.Contains);
        var parca = new List<string>();
        if (yapilanUyg > 0) parca.Add(yapilanUyg + " ayar uygulandı");
        if (yapilanGeri > 0) parca.Add(yapilanGeri + " ayar eski hâline döndü");
        if (s.Yapilamayan.Count > 0) parca.Add(s.Yapilamayan.Count + " ayar yapılamadı" + (s.IzinVerilmedi ? " (yönetici izni verilmedi)" : s.Hata != null ? " (" + s.Hata + ")" : ""));
        if (s.GezginGerekli) parca.Add("görev çubuğu / Gezgin için \"Gezgini yeniden başlat\"a bas");
        if (s.OturumGerekli) parca.Add("bazıları oturumu kapatıp açınca tam geçerli olur");
        lblDurum.ForeColor = s.Yapilamayan.Count > 0 ? Renk.Turuncu : Renk.Soluk;
        lblDurum.Text = parca.Count > 0 ? "Tamam: " + string.Join("; ", parca) + "." : "Değişiklik yapılmadı.";
        ipucu.SetToolTip(lblDurum, lblDurum.Text);
        if (s.GezginGerekli) btnGezgin.Visible = true;
    }

    async Task KaldirAsync()
    {
        var secilen = kaldirilacak.Where(kurulu.ContainsKey).ToList();
        if (secilen.Count == 0) return;
        string metin = secilen.Count + " uygulama senin hesabından kaldırılacak:\n" +
                       string.Join("\n", secilen.Take(15).Select(u => "  • " + u.Ad)) + (secilen.Count > 15 ? "\n  • …" : "") +
                       "\n\nGeri almak için Microsoft Store'dan yeniden kurman gerekir. Devam edilsin mi?";
        if (MessageBox.Show(FindForm(), metin, "Uygulamaları kaldır", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        Mesgul(true, "Uygulamalar kaldırılıyor...");
        var paketler = secilen.SelectMany(u => kurulu[u]).ToList();
        List<string> kalan;
        try { kalan = await Task.Run(() => HazirUygulamalar.Kaldir(paketler)); }
        catch { kalan = paketler; }
        Mesgul(false, null);
        kaldirilacak.Clear();
        DurumlariOku(false);
        KategoriSec(secili);
        int kaldirilan = secilen.Count(u => !kurulu.ContainsKey(u));
        lblDurum.ForeColor = kalan.Count > 0 ? Renk.Turuncu : Renk.Soluk;
        lblDurum.Text = kaldirilan + " uygulama kaldırıldı." +
                        (kalan.Count > 0 ? " " + secilen.Count(kurulu.ContainsKey) + " tanesi kaldırılamadı (Windows izin vermiyor ya da kullanımda)." : "");
    }

    async Task GezginAsync()
    {
        if (isleniyor) return;
        if (MessageBox.Show(FindForm(), "Dosya Gezgini yeniden başlatılacak: görev çubuğu ve masaüstü birkaç saniye kaybolup geri gelir, " +
                                        "açık Dosya Gezgini pencereleri kapanır. Devam edilsin mi?", "Gezgini yeniden başlat",
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        Mesgul(true, "Gezgin yeniden başlatılıyor...");
        await Task.Run(() => WindowsAyarlari.GezginiYenidenBaslat());
        Mesgul(false, null);
        btnGezgin.Visible = false;
        lblDurum.ForeColor = Renk.Soluk;
        lblDurum.Text = "Gezgin yeniden başladı; görev çubuğu ve Gezgin ayarları geçerli.";
    }

    // Yardımcıyı yönetici olarak açar, bitmesini bekler. -2 = izin verilmedi, -1 = başlatılamadı.
    internal static int YoneticiCalistir(string argumanlar)
    {
        try
        {
            using (var p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, argumanlar) { UseShellExecute = true, Verb = "runas" }))
            {
                if (p == null) return -1;
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        catch (System.ComponentModel.Win32Exception) { return -2; }
        catch { return -1; }
    }
}
