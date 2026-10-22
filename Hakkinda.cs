// HER YENİ SÜRÜMDE bu özeti ve Surum.Metin'i güncelle.
//
// "Hakkında" penceresi: sürüm, özellik özeti, açık kaynak bağlantısı ve güncelleme ayarları.
// Açılış: sekme şeridindeki logo ve tepsi menüsündeki "Hakkında". Aynı anda tek pencere.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

class Hakkinda : Form
{
    public const string Depo = "github.com/sergenmico/SergenPcTemizle";

    // Özellik özeti (kullanıcı diliyle). README.md'deki "Neler yapabilir" bölümüyle uyumlu tut.
    public const string Ozet =
        "• Sistem: işlemci, ekran kartı, RAM, ekran kartı belleği, sanal bellek ve ısıyı canlı gösterir; RAM'i boşaltır, gereksiz programları kapatmana yardım eder.\r\n" +
        "• Disk: gereksiz dosyaları (derin temizlik dahil) temizler; hangi klasör ne kadar yer tutuyor gösterir.\r\n" +
        "• Sürücü: NVIDIA ekran kartı sürücüsünün yeni sürümünü denetler, indirip kurar.\r\n" +
        "• Sağlık: bellek bekçisi ve sızıntı şüphelileri, çökme geçmişi, destek raporu, genel kısayol (Ctrl+Alt+O), bildirim sesi, oyun modu.\r\n" +
        "• Başlangıç: Windows ile açılan programları listeler, açıp kapatmanı sağlar.\r\n" +
        "• Windows: geri alınabilir Windows ayarları, hazır uygulamaları kaldırma, DNS ve ağ testi, güç modu.\r\n" +
        "• Turbo: oyun ayarları, oyun profili, donanım kontrolü.\r\n" +
        "• Sanal bellek ayarı: önerilen boyutu uygular, geri alınabilir.\r\n" +
        "• Otomatik / Full optimize: tek tıkla temizlik ve hızlandırma.\r\n" +
        "• Tepside bekçi: pencere kapalıyken de bellek dolarsa haber verir.\r\n" +
        "• Otomatik güncelleme: günde bir kez bakar, yeni sürümü imzasını doğrulayıp sorarak kurar. Elle denetlemek için: aşağıdaki \"Şimdi denetle\" ya da saatin yanındaki simge → \"Güncellemeleri denetle\".";

    static Hakkinda acik;

    readonly CheckBox chkDenetle = new CheckBox();
    readonly Label lblSon = new Label(), lblSonuc = new Label();
    readonly Button btnDenetle = new Button();
    bool denetliyor;

    public static void Goster()
    {
        if (acik != null && !acik.IsDisposed)
        {
            if (acik.WindowState == FormWindowState.Minimized) acik.WindowState = FormWindowState.Normal;
            acik.Show();
            acik.Activate();
            return;
        }
        acik = new Hakkinda();
        acik.Show();
        acik.Activate();
    }

    Hakkinda()
    {
        Text = "Hakkında";
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Renk.Arka;
        ForeColor = Renk.Yazi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 560);
        Icon = Logo.Simge();

        var logo = new PictureBox
        {
            Left = 232, Top = 12, Width = 96, Height = 96, SizeMode = PictureBoxSizeMode.Zoom, Image = Logo.Resim()
        };
        var baslik = Etiket("Sergen PC Temizle", 0, 110, 20, new Font("Segoe UI", 15f, FontStyle.Bold), Renk.Yazi);
        var surum = Etiket("Sürüm " + Surum.Metin, 0, 142, 20, Font, Renk.Soluk);
        var yapimci = Etiket("Sergen tarafından yapıldı · çaylar senden", 0, 164, 20, Font, Renk.Soluk);
        foreach (var l in new[] { baslik, surum, yapimci }) { l.Left = 0; l.Width = 560; l.TextAlign = ContentAlignment.MiddleCenter; }
        baslik.Height = 30;

        var ozet = new TextBox
        {
            Left = 20, Top = 196, Width = 520, Height = 180, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = Renk.Panel, ForeColor = Renk.Yazi, BorderStyle = BorderStyle.FixedSingle, Text = Ozet, TabStop = false
        };
        ozet.Select(0, 0);

        var acikKaynak = Etiket("Açık kaynak:", 20, 386, 20, Font, Renk.Yazi);
        acikKaynak.Width = 90;
        var baglanti = new LinkLabel
        {
            Text = Depo, Left = 110, Top = 386, Width = 340, Height = 20, AutoSize = false,
            LinkColor = Color.FromArgb(100, 170, 255), ActiveLinkColor = Color.White, VisitedLinkColor = Color.FromArgb(100, 170, 255),
            LinkBehavior = LinkBehavior.HoverUnderline
        };
        baglanti.LinkClicked += delegate
        {
            try { Process.Start("https://" + Depo); } catch { }
        };
        var lisans = Etiket("MIT lisansı", 20, 408, 20, Font, Renk.Soluk);

        var ayirac = new Panel { Left = 20, Top = 440, Width = 520, Height = 1, BackColor = Renk.Cizgi };

        chkDenetle.Text = "Güncellemeleri kendiliğinden denetle";
        chkDenetle.SetBounds(20, 452, 400, 24);
        chkDenetle.Checked = Guncelleyici.Acik;
        chkDenetle.CheckedChanged += delegate { Ayarlar.Yaz(Guncelleyici.AyarDenetle, chkDenetle.Checked); };
        lblSon.SetBounds(20, 480, 400, 20);
        lblSon.ForeColor = Renk.Soluk;
        SonYaz();

        btnDenetle.Text = "Şimdi denetle";
        btnDenetle.SetBounds(20, 508, 130, 32);
        Duzenle(btnDenetle);
        btnDenetle.Click += delegate { Denetle(); };
        lblSonuc.SetBounds(160, 508, 250, 32);
        lblSonuc.TextAlign = ContentAlignment.MiddleLeft;
        lblSonuc.AutoEllipsis = true;

        var btnKapat = new Button { Text = "Kapat" };
        btnKapat.SetBounds(440, 508, 100, 32);
        Duzenle(btnKapat);
        btnKapat.Click += delegate { Close(); };
        CancelButton = btnKapat;

        Controls.AddRange(new Control[] { logo, baslik, surum, yapimci, ozet, acikKaynak, baglanti, lisans, ayirac,
                                          chkDenetle, lblSon, btnDenetle, lblSonuc, btnKapat });
        FormClosed += delegate { if (acik == this) acik = null; };
    }

    static Label Etiket(string metin, int sol, int ust, int yukseklik, Font font, Color renk)
    {
        return new Label { Text = metin, Left = sol, Top = ust, Height = yukseklik, Width = 500, Font = font, ForeColor = renk };
    }

    static void Duzenle(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.Cursor = Cursors.Hand;
        b.BackColor = Renk.Panel;
        b.ForeColor = Renk.Yazi;
        b.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Renk.Cizgi;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int koyu = 1; Yerel.DwmSetWindowAttribute(Handle, 20, ref koyu, 4); } catch { }
    }

    void SonYaz()
    {
        var son = Guncelleyici.SonDenetim;
        lblSon.Text = "Son denetim: " + (son == null ? "henüz yapılmadı" : son.Value.ToString("d MMMM yyyy HH:mm"));
    }

    async void Denetle()
    {
        var g = Guncelleyici.Ornek;
        if (denetliyor || g == null) return;
        denetliyor = true;
        btnDenetle.Enabled = false;
        lblSonuc.ForeColor = Renk.Soluk;
        lblSonuc.Text = "Denetleniyor...";
        DenetimYaniti y;
        try { y = await g.SimdiDenetle(); }
        catch (Exception e) { y = new DenetimYaniti { Sonuc = DenetimSonucu.Hata, Hata = GuncellemeMotoru.KisaNeden(e) }; }
        denetliyor = false;
        if (IsDisposed) return;
        btnDenetle.Enabled = true;
        SonYaz();
        switch (y.Sonuc)
        {
            case DenetimSonucu.Guncel:
                lblSonuc.ForeColor = Renk.Yesil;
                lblSonuc.Text = "Güncelsin (v" + Surum.Metin + ")";
                break;
            case DenetimSonucu.Yeni:
                lblSonuc.ForeColor = Renk.Turuncu;
                lblSonuc.Text = "Yeni sürüm var (v" + Guncelleyici.SurumMetni(y.Bilgi.Surum) + ")";
                g.FormuAc(y.Bilgi, this);
                break;
            case DenetimSonucu.Eksik:
                lblSonuc.ForeColor = Renk.Turuncu;
                lblSonuc.Text = "Yeni sürüm var ama dosyaları eksik; sonra dene.";
                break;
            default:
                lblSonuc.ForeColor = Renk.Turuncu;
                lblSonuc.Text = "Denetlenemedi: " + y.Hata;
                break;
        }
    }
}
