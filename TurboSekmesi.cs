// Turbo sekmesi: oyun için işe yarayan ayarlar (solda bölümler, sağda satırlar, işaretli = uygulanmış) ve bilgisayarın
// oyuna hazır olup olmadığını gösteren Durum bölümü. Windows sekmesiyle aynı akış: işaretle → Uygula; işareti kaldırıp
// Uygula'ya basmak eski hâline döndürür. Kendiliğinden hiçbir şeyi değiştirmez; yalnızca "Oyun profili" kutuları hemen
// kaydedilir (bekçi çalıştırır, bkz. OyunProfili). Motor: Turbo.cs.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

class TurboSekmesi : Panel
{
    static readonly Color Yesil = Color.FromArgb(102, 187, 106);
    static readonly Color Silik = Color.FromArgb(100, 105, 114);

    readonly Panel solPanel = new Panel(), satirlar = new Panel();
    readonly Label lblBolum = new Label(), lblBolumAciklama = new Label(), lblDurum = new Label();
    readonly Button btnOneri, btnSifirla, btnGeriAl, btnUygula;
    readonly CheckBox chkNokta = new CheckBox();
    readonly ToolTip ipucu = new ToolTip { AutoPopDelay = 15000 };
    readonly Dictionary<string, Button> bolumDugmeleri = new Dictionary<string, Button>();
    readonly Dictionary<string, bool> istenen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    readonly Font kalin = new Font("Segoe UI", 9.5f, FontStyle.Bold), kucuk = new Font("Segoe UI", 8.25f);
    readonly System.Windows.Forms.Timer profilZamanlayici = new System.Windows.Forms.Timer { Interval = 2000 };
    TurboOkuma okuma;                   // null: henüz okunmadı
    Label profilNotu;                   // Oyun profili bölümündeki durum yazısı
    string secili = TurboAyarlari.BolumDurum;
    bool yuklendi, okunuyor, isleniyor, yenidenGerekli;

    public TurboSekmesi()
    {
        Dock = DockStyle.Fill;
        BackColor = Renk.Arka;

        var baslik = new Panel { Dock = DockStyle.Top, Height = 66, Padding = new Padding(14, 8, 12, 4) };
        baslik.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, ForeColor = Renk.Soluk, AutoEllipsis = true, Font = new Font("Segoe UI", 8.5f),
            Text = "TURBO · oyunlarda işe yarayan, geri alınabilir ayarlar ve bilgisayarının oyuna hazır olup olmadığı\n" +
                   "İşaretli olanlar şu an uygulanmış demektir. İşaretle → Uygula; işareti kaldırıp Uygula'ya basarsan eski hâline döner. " +
                   "Hiçbir şey kendiliğinden değişmez."
        });

        // Solda bölümler
        solPanel.Dock = DockStyle.Left;
        solPanel.Width = 214;
        solPanel.Padding = new Padding(12, 0, 6, 8);
        solPanel.AutoScroll = true;
        foreach (var kaydirilan in new Control[] { solPanel, satirlar })
            kaydirilan.HandleCreated += delegate(object o, EventArgs e) { try { Yerel.SetWindowTheme(((Control)o).Handle, "DarkMode_Explorer", null); } catch { } };
        for (int i = TurboAyarlari.Bolumler.Length - 1; i >= 0; i--)   // Dock=Top: son eklenen en üstte
        {
            string kimlik = TurboAyarlari.Bolumler[i];
            var b = new Button
            {
                Dock = DockStyle.Top, Height = 31, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 4, 0), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9.5f), Tag = kimlik,
                Text = TurboAyarlari.BolumAdlari[i]
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Renk.Cizgi;
            b.Click += delegate { if (!isleniyor) BolumSec(kimlik); };
            bolumDugmeleri[kimlik] = b;
            solPanel.Controls.Add(b);
        }

        // Sağda seçili bölümün başlığı ve satırları
        var sag = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 12, 0) };
        lblBolum.Dock = DockStyle.Top;
        lblBolum.Height = 24;
        lblBolum.ForeColor = Renk.Yazi;
        lblBolum.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        lblBolumAciklama.Dock = DockStyle.Top;
        lblBolumAciklama.Height = 24;
        lblBolumAciklama.ForeColor = Renk.Soluk;
        lblBolumAciklama.AutoEllipsis = true;
        satirlar.Dock = DockStyle.Fill;
        satirlar.AutoScroll = true;
        satirlar.BackColor = Renk.Panel;
        sag.Controls.Add(satirlar);
        sag.Controls.Add(lblBolumAciklama);
        sag.Controls.Add(lblBolum);

        // Altta: önerilenler, sıfırla, geri al, durum, geri yükleme noktası, uygula
        var alt = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(12, 8, 12, 6) };
        btnOneri = Dugme("Önerilenleri seç");
        btnSifirla = Dugme("Seçimi sıfırla");
        btnGeriAl = Dugme("Hepsini geri al");
        btnUygula = Dugme("Uygula");
        btnUygula.BackColor = Renk.Vurgu;
        btnUygula.ForeColor = Color.White;
        btnUygula.FlatAppearance.BorderSize = 0;
        btnOneri.Dock = btnSifirla.Dock = btnGeriAl.Dock = DockStyle.Left;
        btnOneri.Width = 150;
        btnSifirla.Width = 120;
        btnGeriAl.Width = 130;
        btnUygula.Width = 190;
        btnOneri.Click += delegate { OnerilenleriSec(); };
        btnSifirla.Click += delegate { Sifirla(); };
        btnGeriAl.Click += delegate { HepsiniGeriAl(); };
        btnUygula.Click += async delegate { await UygulaAsync(); };
        chkNokta.Text = "Önce geri yükleme noktası";
        chkNokta.Checked = true;
        chkNokta.ForeColor = Renk.Soluk;
        chkNokta.Dock = DockStyle.Right;
        chkNokta.Width = 190;
        chkNokta.Padding = new Padding(8, 0, 0, 0);
        ipucu.SetToolTip(chkNokta, "Yalnızca yönetici izni isteyen ayarlar seçiliyken kullanılır: Windows'un Sistem Geri Yükleme noktası oluşturulur (Windows günde en fazla bir tane izin verir).\n" +
                                   "Program ayrıca her ayarın eski değerini kendisi de yedekler; işareti kaldırıp Uygula'ya basmak onu geri yazar.");
        var durumSatiri = new Panel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(14, 4, 12, 0) };
        lblDurum.Dock = DockStyle.Fill;
        lblDurum.TextAlign = ContentAlignment.MiddleLeft;
        lblDurum.ForeColor = Renk.Soluk;
        lblDurum.AutoEllipsis = true;
        durumSatiri.Controls.Add(lblDurum);
        alt.Controls.Add(new Panel { Dock = DockStyle.Fill });
        alt.Controls.Add(chkNokta);
        alt.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        alt.Controls.Add(btnUygula);
        alt.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 });
        alt.Controls.Add(btnGeriAl);
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

        // Oyun profili bölümündeki "Şu an etkin" yazısı kendiliğinden tazelensin (yalnızca görünürken).
        profilZamanlayici.Tick += delegate { if (Visible && secili == TurboAyarlari.BolumOyun) ProfilNotunuYaz(); };
        profilZamanlayici.Start();
    }

    static Button Dugme(string metin)
    {
        var b = new Button
        {
            Text = metin, Dock = DockStyle.Right, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = Renk.Panel, ForeColor = Renk.Yazi, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Renk.Cizgi;
        return b;
    }

    Button KucukDugme(string metin)
    {
        var b = Dugme(metin);
        b.Dock = DockStyle.None;
        b.AutoSize = true;
        b.MinimumSize = new Size(90, 28);
        b.Height = 28;
        return b;
    }

    void Bildir(string metin, bool sorun)
    {
        lblDurum.ForeColor = sorun ? Renk.Turuncu : Renk.Soluk;
        lblDurum.Text = metin;
        ipucu.SetToolTip(lblDurum, metin);
    }

    // ---- Okuma ----

    public void Gosterildi()
    {
        if (isleniyor) return;
        var gorev = OkuAsync(!yuklendi);
        if (okuma == null) BolumSec(secili);   // "Okunuyor..."
    }

    // Durumlar arka planda okunur (WMI birkaç saniye sürebilir). sifirla: işaretler de duruma eşitlenir.
    async Task OkuAsync(bool sifirla)
    {
        // Uygula'dan sonraki okuma, o sırada süren (sekme gösterilince başlamış) bir okuma yüzünden atlanmasın.
        if (okunuyor && !sifirla) return;
        while (okunuyor) await Task.Delay(100);
        okunuyor = true;
        TurboOkuma yeni = null;
        try { yeni = await Task.Run(() => TurboAyarlari.Oku()); }
        catch { }
        okunuyor = false;
        if (yeni == null || IsDisposed) return;
        foreach (var a in TurboAyarlari.Tum)
        {
            if (yeni.Gizli.Contains(a.Kimlik) || !yeni.Durum.ContainsKey(a.Kimlik)) continue;
            bool uygulandi = yeni.Durum[a.Kimlik] == AyarDurumu.Uygulandi;
            bool eskiUygulandi = okuma != null && okuma.Durum.ContainsKey(a.Kimlik) && okuma.Durum[a.Kimlik] == AyarDurumu.Uygulandi;
            // Bekleyen değişiklik yoksa (ya da satır seçilemiyorsa) işaret yeni duruma uyar.
            if (sifirla || yeni.Uygunluk.ContainsKey(a.Kimlik) || !istenen.ContainsKey(a.Kimlik) || istenen[a.Kimlik] == eskiUygulandi)
                istenen[a.Kimlik] = uygulandi;
        }
        okuma = yeni;
        yuklendi = true;
        BolumSec(secili);
    }

    bool Etkin(TurboAyar a)
    {
        return okuma != null && okuma.Durum.ContainsKey(a.Kimlik) && !okuma.Gizli.Contains(a.Kimlik) && !okuma.Uygunluk.ContainsKey(a.Kimlik);
    }

    bool Uygulandi(TurboAyar a)
    {
        return okuma != null && okuma.Durum.ContainsKey(a.Kimlik) && okuma.Durum[a.Kimlik] == AyarDurumu.Uygulandi;
    }

    bool TurboYapti(TurboAyar a) { return okuma != null && okuma.TurboYapti.Contains(a.Kimlik); }

    bool Bekliyor(TurboAyar a)
    {
        bool ist;
        if (!Etkin(a) || !istenen.TryGetValue(a.Kimlik, out ist)) return false;
        return ist != Uygulandi(a);
    }

    List<TurboAyar> Gorunenler(string bolum)
    {
        return okuma == null ? new List<TurboAyar>() : TurboAyarlari.Tum.Where(a => a.Bolum == bolum && !okuma.Gizli.Contains(a.Kimlik)).ToList();
    }

    List<TurboAyar> Uygulanacaklar()
    {
        return TurboAyarlari.Tum.Where(a => Etkin(a) && istenen.ContainsKey(a.Kimlik) && istenen[a.Kimlik] && !Uygulandi(a)).ToList();
    }

    List<TurboAyar> GeriAlinacaklar()
    {
        return TurboAyarlari.Tum.Where(a => Etkin(a) && istenen.ContainsKey(a.Kimlik) && !istenen[a.Kimlik] && Uygulandi(a)).ToList();
    }

    // ---- Bölümler ----

    void BolumDugmeleriniYaz()
    {
        foreach (var kv in bolumDugmeleri)
        {
            var b = kv.Value;
            bool sec = kv.Key == secili;
            b.BackColor = sec ? Renk.Panel : Renk.Arka;
            b.ForeColor = sec ? Renk.Yazi : Renk.Soluk;
            string ad = TurboAyarlari.BolumAdlari[Array.IndexOf(TurboAyarlari.Bolumler, kv.Key)];
            var l = Gorunenler(kv.Key);
            if (kv.Key == TurboAyarlari.BolumDurum || l.Count == 0) { b.Text = ad; continue; }
            int uyg = l.Count(Uygulandi);
            b.Text = ad + "  " + uyg + "/" + l.Count + (l.Any(Bekliyor) ? "  •" : "");
        }
    }

    void BolumSec(string kimlik)
    {
        secili = kimlik;
        BolumDugmeleriniYaz();
        profilNotu = null;
        satirlar.SuspendLayout();
        try
        {
            foreach (Control c in satirlar.Controls.Cast<Control>().ToList()) c.Dispose();
            satirlar.Controls.Clear();
            satirlar.AutoScrollPosition = new Point(0, 0);
            int sira = Array.IndexOf(TurboAyarlari.Bolumler, kimlik);
            lblBolum.Text = TurboAyarlari.BolumAdlari[sira];
            if (okuma == null)
            {
                lblBolumAciklama.Text = "";
                satirlar.Controls.Add(new Label { Dock = DockStyle.Top, Height = 60, Padding = new Padding(12, 14, 12, 0), ForeColor = Renk.Soluk, Text = "Okunuyor..." });
            }
            else if (kimlik == TurboAyarlari.BolumDurum) DurumDoldur();
            else if (kimlik == TurboAyarlari.BolumOyun) OyunDoldur();
            else AyarlariDoldur(kimlik);
        }
        finally { satirlar.ResumeLayout(true); }
        UygulaDugmesiniYaz();
    }

    static readonly Dictionary<string, string> BolumAciklamalari = new Dictionary<string, string>
    {
        { TurboAyarlari.BolumGuc, "Güç kısıtlaması, USB ve PCI Express tasarrufu, güç planı. Çoğu yeniden başlatma gerektirmez." },
        { TurboAyarlari.BolumEkran, "Ekran kartı zamanlaması, pencereli oyun iyileştirmesi, mikro takılma ve ekran sorunları." },
        { TurboAyarlari.BolumGuvenlik, "FPS'i artıran ama güvenliği ya da özellikleri azaltan ayarlar. Her birinden önce uyarılırsın." },
        { TurboAyarlari.BolumBellek, "Bellek ve disk kullanımını azaltan ayarlar." }
    };

    void AyarlariDoldur(string bolum)
    {
        string a;
        lblBolumAciklama.Text = BolumAciklamalari.TryGetValue(bolum, out a) ? a : "";
        var liste = Gorunenler(bolum);
        foreach (var x in Enumerable.Reverse(liste)) satirlar.Controls.Add(AyarSatiri(x));   // Dock=Top: son eklenen en üstte
    }

    // Satır içi yazı yüksekliği için ortak kalıp: küçük renkli etiketler yan yana.
    Control EtiketPaneli()
    {
        return new FlowLayoutPanel { WrapContents = false, AutoSize = false, Height = 20, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
    }

    void EtiketEkle(Control panel, string metin, Color renk)
    {
        var l = new Label { AutoSize = true, Font = kucuk, ForeColor = renk, Text = metin, Margin = new Padding(0, 2, 6, 0), BackColor = Color.Transparent };
        panel.Controls.Add(l);
    }

    Control AyarSatiri(TurboAyar a)
    {
        var satir = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(10, 6, 10, 4) };
        string uygunsuz;
        okuma.Uygunluk.TryGetValue(a.Kimlik, out uygunsuz);
        var chk = new CheckBox
        {
            AutoSize = true, Location = new Point(10, 7), Font = kalin, ForeColor = uygunsuz != null ? Renk.Soluk : Renk.Yazi, Text = a.Baslik,
            Checked = istenen.ContainsKey(a.Kimlik) && istenen[a.Kimlik], Cursor = uygunsuz != null ? Cursors.Default : Cursors.Hand,
            Enabled = uygunsuz == null
        };
        var etiket = (FlowLayoutPanel)EtiketPaneli();
        etiket.Location = new Point(chk.PreferredSize.Width + 16, 8);
        var aciklama = new Label
        {
            Location = new Point(28, 31), Height = 20, ForeColor = uygunsuz != null ? Silik : Renk.Soluk, AutoEllipsis = true, Text = a.Aciklama,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
        };
        Action etiketYaz = delegate
        {
            foreach (Control c in etiket.Controls.Cast<Control>().ToList()) c.Dispose();
            etiket.Controls.Clear();
            if (uygunsuz != null) { EtiketEkle(etiket, "· " + uygunsuz, Renk.Soluk); return; }
            if (Bekliyor(a)) EtiketEkle(etiket, chk.Checked ? "· uygulanacak" : TurboYapti(a) ? "· eski hâline dönecek" : "· Windows varsayılanına dönecek", Renk.Turuncu);
            else if (Uygulandi(a)) EtiketEkle(etiket, "· uygulandı", Yesil);
            string ek;
            if (okuma.EkEtiket.TryGetValue(a.Kimlik, out ek)) EtiketEkle(etiket, "· " + ek, Renk.Turuncu);
            if (a.Etiket != null) EtiketEkle(etiket, "· " + a.Etiket, Renk.Turuncu);
            if (a.Yonetici) EtiketEkle(etiket, "· yönetici izni", Renk.Soluk);
            if (a.Yeniden) EtiketEkle(etiket, "· yeniden başlatma gerekir", Renk.Soluk);
            if (a.Oneri) EtiketEkle(etiket, "· önerilen", Renk.Soluk);
            if (a.Sorun) EtiketEkle(etiket, "· sorun giderici", Renk.Soluk);
        };
        etiketYaz();
        chk.CheckedChanged += delegate
        {
            istenen[a.Kimlik] = chk.Checked;
            etiketYaz();
            BolumDugmeleriniYaz();
            UygulaDugmesiniYaz();
        };
        satir.Resize += delegate
        {
            aciklama.Width = Math.Max(100, satir.ClientSize.Width - 40);
            etiket.Width = Math.Max(60, satir.ClientSize.Width - etiket.Left - 10);
        };
        string tam = a.Aciklama + (uygunsuz != null ? "\n(" + uygunsuz + ")" : "");
        ipucu.SetToolTip(chk, tam);
        ipucu.SetToolTip(aciklama, tam);
        ipucu.SetToolTip(etiket, tam);
        satir.Controls.Add(chk);
        satir.Controls.Add(etiket);
        satir.Controls.Add(aciklama);
        satir.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Renk.Cizgi });
        return satir;
    }

    // ---- Oyun profili bölümü ----

    // Hemen kaydedilen kutu (Uygula beklemez).
    Control KutuSatiri(string baslik, string aciklama, bool deger, Action<bool> degisti, out CheckBox kutu)
    {
        var satir = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(10, 6, 10, 4) };
        var chk = new CheckBox { AutoSize = true, Location = new Point(10, 7), Font = kalin, ForeColor = Renk.Yazi, Text = baslik, Checked = deger, Cursor = Cursors.Hand };
        var etiket = new Label { AutoSize = true, Font = kucuk, ForeColor = Renk.Soluk, Text = "· hemen kaydedilir", Location = new Point(chk.PreferredSize.Width + 16, 10) };
        var ac = new Label
        {
            Location = new Point(28, 31), Height = 20, ForeColor = Renk.Soluk, AutoEllipsis = true, Text = aciklama,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
        };
        chk.CheckedChanged += delegate { degisti(chk.Checked); };
        satir.Resize += delegate { ac.Width = Math.Max(100, satir.ClientSize.Width - 40); };
        ipucu.SetToolTip(chk, aciklama);
        ipucu.SetToolTip(ac, aciklama);
        satir.Controls.Add(chk);
        satir.Controls.Add(etiket);
        satir.Controls.Add(ac);
        satir.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Renk.Cizgi });
        kutu = chk;
        return satir;
    }

    void OyunDoldur()
    {
        lblBolumAciklama.Text = "Oyun açılınca güç modu ve RAM kendiliğinden ayarlanır; oyun kapanınca eski hâline döner. Sessiz çalışır.";
        profilNotu = new Label { Dock = DockStyle.Top, Height = 46, Padding = new Padding(12, 8, 12, 0), ForeColor = Renk.Soluk };
        CheckBox kutuProfil, kutuRam = null;
        var profil = KutuSatiri("Oyun profili",
            "Tam ekran bir oyun açılınca (ekran kartını kullanan, ≈10 sn) güç modunu En iyi performans yapar; oyun bitince eski hâline döner. Pencere, ses ya da balon çıkarmaz.",
            OyunProfili.Acik, delegate(bool d)
            {
                OyunProfili.Acik = d;
                if (OyunProfili.Ornek != null) OyunProfili.Ornek.AyarDegisti();
                if (kutuRam != null) kutuRam.Enabled = d;
                ProfilNotunuYaz();
            }, out kutuProfil);
        var ram = KutuSatiri("Oyun açılınca RAM boşalt",
            "Oyun başlarken bir kez, oyun ve öndeki program dışındakilerin belleğini sessizce boşaltır. Hiçbir şey kapatılmaz. Yalnızca Oyun profili açıkken çalışır.",
            OyunProfili.RamBosalt, delegate(bool d) { OyunProfili.RamBosalt = d; }, out kutuRam);
        kutuRam.Enabled = OyunProfili.Acik;
        ProfilNotunuYaz();
        // Dock=Top: son eklenen en üstte; görünüş sırası not, profil, RAM, sonra Oyun Modu ayarı.
        var ayarlar = Gorunenler(TurboAyarlari.BolumOyun);
        foreach (var x in Enumerable.Reverse(ayarlar)) satirlar.Controls.Add(AyarSatiri(x));
        satirlar.Controls.Add(ram);
        satirlar.Controls.Add(profil);
        satirlar.Controls.Add(profilNotu);
    }

    void ProfilNotunuYaz()
    {
        if (profilNotu == null) return;
        string ad = OyunProfili.EtkinAd, m = "";
        bool uyari = false;
        if (ad != null) m = "Şu an etkin: " + ad + ". ";
        if (GucAyarlari.EtkinPlan() != GucAyarlari.DengeliPlan)
        {
            m += "Güç planın Dengeli değil; oyun profili güç modunu değiştiremez (Windows sekmesi → Güç). RAM boşaltma yine çalışır.";
            uyari = true;
        }
        else if (ad == null) m = OyunProfili.Acik ? "Oyun profili açık; tam ekran bir oyun açılınca devreye girer." : "Oyun profili kapalı: hiçbir şey yapmaz.";
        profilNotu.ForeColor = uyari ? Renk.Turuncu : Renk.Soluk;
        profilNotu.Text = m;
    }

    // ---- Durum bölümü ----

    void DurumDoldur()
    {
        lblBolumAciklama.Text = "Bilgisayarının oyuna hazır olup olmadığı. Hiçbir şeyi değiştirmez; \"Düzelt\" ilgili ayarı işaretler, sen Uygula'ya basarsın.";
        var sirali = new List<Control>();
        if (yenidenGerekli)
            sirali.Add(DurumKutusu(new DurumSatiri { Uyari = true, Metin = "Bazı ayarlar bilgisayar yeniden başlayınca geçerli olacak" }));
        foreach (var s in okuma.Satirlar) sirali.Add(DurumKutusu(s));
        if (sirali.Count == 0)
            sirali.Add(new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 12, 12, 0), ForeColor = Renk.Soluk, Text = "Durum okunamadı." });
        sirali.Reverse();   // Dock=Top: son eklenen en üstte
        foreach (var c in sirali) satirlar.Controls.Add(c);
    }

    Control DurumKutusu(DurumSatiri s)
    {
        var satir = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(10, 2, 10, 2) };
        var isaret = new Label
        {
            Dock = DockStyle.Left, Width = 26, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = s.Uyari ? Renk.Turuncu : Yesil, Text = s.Uyari ? "!" : "✓"
        };
        var metin = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = s.Uyari ? Renk.Yazi : Renk.Soluk, Text = s.Metin };
        ipucu.SetToolTip(metin, s.Metin);
        bool duzelt = s.Uyari && (s.TrimDuzelt || s.Bolum != null);
        if (duzelt)
        {
            var b = KucukDugme(s.TrimDuzelt ? "Düzelt · yönetici" : "Düzelt");
            b.Dock = DockStyle.Right;
            b.Width = s.TrimDuzelt ? 140 : 90;
            b.AutoSize = false;
            b.Height = 28;
            b.Click += delegate { Duzelt(s); };
            satir.Controls.Add(metin);
            satir.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            satir.Controls.Add(b);
            b.BringToFront();
        }
        else satir.Controls.Add(metin);
        satir.Controls.Add(isaret);
        satir.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Renk.Cizgi });
        return satir;
    }

    void Duzelt(DurumSatiri s)
    {
        if (isleniyor) return;
        if (s.TrimDuzelt) { TrimDuzelt(); return; }
        var a = s.Ayar != null ? TurboAyarlari.Bul(s.Ayar) : null;
        if (a != null && Etkin(a)) istenen[a.Kimlik] = true;
        BolumSec(s.Bolum ?? secili);
        if (a != null && Etkin(a)) Bildir("\"" + a.Baslik + "\" işaretlendi; Uygula'ya bas.", false);
        else if (s.Bolum == TurboAyarlari.BolumOyun) Bildir("Oyun profili kutusunu buradan açabilirsin.", false);
    }

    async void TrimDuzelt()
    {
        if (MessageBox.Show(FindForm(), "SSD'ye TRIM gönderilmesi açılacak (fsutil behavior set DisableDeleteNotify 0). Zararsızdır ve istediğin zaman geri alınabilir.\n\n" +
                            "Windows bir kez yönetici izni isteyecek. Devam edilsin mi?", "TRIM", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        Mesgul(true, "TRIM açılıyor...");
        int kod = await Task.Run(() => TurboAyarlari.TekOzel("trim-ac", WindowsSekmesi.YoneticiCalistir));
        Mesgul(false, null);
        await OkuAsync(false);
        Bildir(kod == 0 ? "Tamam: SSD için TRIM açıldı." :
               kod == -2 ? "TRIM açılmadı: yönetici izni verilmedi." : "TRIM açılamadı (kod " + kod + ").", kod != 0);
    }

    // ---- Alt düğmeler ----

    void UygulaDugmesiniYaz()
    {
        int n = Uygulanacaklar().Count + GeriAlinacaklar().Count;
        btnUygula.Text = n > 0 ? "Uygula (" + n + " değişiklik)" : "Uygula";
        btnUygula.Enabled = !isleniyor && n > 0;
        btnOneri.Enabled = btnSifirla.Enabled = !isleniyor && okuma != null;
        btnGeriAl.Enabled = !isleniyor && okuma != null && TurboAyarlari.Tum.Any(a => Etkin(a) && Uygulandi(a) && TurboYapti(a));
        bool yonetici = Uygulanacaklar().Concat(GeriAlinacaklar()).Any(a => a.Yonetici);
        chkNokta.Enabled = yonetici;
    }

    void OnerilenleriSec()
    {
        // Bütün bölümlerdeki önerilenler işaretlenir; işaretli olanların işareti kaldırılmaz.
        foreach (var a in TurboAyarlari.Tum) if (a.Oneri && Etkin(a)) istenen[a.Kimlik] = true;
        BolumSec(secili);
        Bildir("Bütün bölümlerdeki önerilen ayarlar işaretlendi. Bölümlere göz at, sonra Uygula'ya bas.", false);
    }

    void Sifirla()
    {
        foreach (var a in TurboAyarlari.Tum) if (okuma != null && okuma.Durum.ContainsKey(a.Kimlik)) istenen[a.Kimlik] = Uygulandi(a);
        BolumSec(secili);
        Bildir("Seçimler bilgisayarın şu anki hâline döndü; hiçbir şey değişmedi.", false);
    }

    void HepsiniGeriAl()
    {
        // Yalnızca Turbo'nun değiştirdikleri: Windows'un ya da kullanıcının kendi açtığı ayarlara dokunulmaz.
        foreach (var a in TurboAyarlari.Tum) if (Etkin(a) && Uygulandi(a) && TurboYapti(a)) istenen[a.Kimlik] = false;
        BolumSec(secili);
        Bildir("Turbo'nun değiştirdiği ayarların hepsi geri alınmak üzere işaretlendi; Uygula'ya bas.", false);
    }

    void Mesgul(bool m, string metin)
    {
        isleniyor = m;
        foreach (var b in bolumDugmeleri.Values) b.Enabled = !m;
        satirlar.Enabled = !m;
        UygulaDugmesiniYaz();
        if (metin != null) Bildir(metin, true);
    }

    async Task UygulaAsync()
    {
        if (isleniyor || okuma == null) return;
        var uygula = Uygulanacaklar();
        var geri = GeriAlinacaklar();
        if (uygula.Count + geri.Count == 0) return;
        bool yonetici = uygula.Concat(geri).Any(a => a.Yonetici);
        bool nokta = yonetici && chkNokta.Checked;

        var soru = new List<string>();
        if (uygula.Count > 0) soru.Add(uygula.Count + " ayar uygulanacak:\n" + string.Join("\n", uygula.Select(a => "  • " + a.Baslik)));
        if (geri.Count > 0) soru.Add(geri.Count + " ayar eski hâline dönecek:\n" + string.Join("\n", geri.Select(a => "  • " + a.Baslik +
                                     (TurboYapti(a) ? "" : " (Turbo değiştirmemişti: Windows varsayılanı yazılır)"))));
        var uyarilar = new List<string>();
        foreach (var a in uygula)
        {
            string u;
            if (okuma.EkUyari.TryGetValue(a.Kimlik, out u)) uyarilar.Add("  • " + a.Baslik + ": " + u);
        }
        if (uyarilar.Count > 0) soru.Add("Dikkat:\n" + string.Join("\n", uyarilar));
        var yeniden = uygula.Concat(geri).Where(a => a.Yeniden).ToList();
        if (yeniden.Count > 0)
            soru.Add("Bilgisayar yeniden başlayınca geçerli olacaklar: " + string.Join(", ", yeniden.Select(a => a.Baslik)) +
                     ".\nBilgisayar kendiliğinden yeniden başlatılmaz.");
        if (yonetici) soru.Add("Bazıları yönetici izni ister; Windows bir kez \"Evet\" diye soracak." +
                               (nokta ? " Önce geri yükleme noktası oluşturulur (biraz sürebilir)." : ""));
        soru.Add("Her ayarın eski değeri yedeklenir; işaretini kaldırıp Uygula'ya basarak geri alabilirsin.");
        if (MessageBox.Show(FindForm(), string.Join("\n\n", soru), "Turbo", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        Mesgul(true, nokta ? "Uygulanıyor (geri yükleme noktası oluşturuluyor, bir dakika sürebilir)..." : "Uygulanıyor...");
        TurboAyarlari.Sonuc s;
        try { s = await Task.Run(() => TurboAyarlari.Uygula(uygula, geri, nokta, WindowsSekmesi.YoneticiCalistir)); }
        catch (Exception e) { s = new TurboAyarlari.Sonuc { Hata = e.Message }; s.Yapilamayan.AddRange(uygula.Concat(geri)); }
        Mesgul(false, null);
        if (s.Yapilan.Any(a => a.Yeniden)) yenidenGerekli = true;
        await OkuAsync(true);

        int yapilanUyg = s.Yapilan.Count(uygula.Contains), yapilanGeri = s.Yapilan.Count(geri.Contains);
        var parca = new List<string>();
        if (yapilanUyg > 0) parca.Add(yapilanUyg + " ayar uygulandı");
        if (yapilanGeri > 0) parca.Add(yapilanGeri + " ayar eski hâline döndü");
        if (s.Yapilamayan.Count > 0)
        {
            var nedenler = s.Yapilamayan.Select(a => { string n; return s.Neden.TryGetValue(a.Kimlik, out n) ? a.Baslik + ": " + n : a.Baslik; }).Take(3).ToList();
            string neden = s.IzinVerilmedi ? "yönetici izni verilmedi" : string.Join("; ", nedenler);
            if (neden.Length == 0 && s.Hata != null) neden = s.Hata;
            parca.Add(s.Yapilamayan.Count + " ayar yapılamadı (" + neden + ")");
        }
        if (s.Yapilan.Any(a => a.Yeniden)) parca.Add("bazıları bilgisayar yeniden başlayınca geçerli olur");
        Bildir(parca.Count > 0 ? "Tamam: " + string.Join("; ", parca) + "." : "Değişiklik yapılmadı.", s.Yapilamayan.Count > 0);
    }
}
