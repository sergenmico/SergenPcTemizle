// Windows sekmesinin motoru: Win11Debloat'taki (github.com/Raphire/Win11Debloat, MIT lisanslı) ayarların seçilmiş,
// geri alınabilir hâli ve hazır gelen uygulamaları kaldırma. Kayıt defteri değerleri betiğin .reg dosyalarından
// alındı (güncelleme paylaşımı hariç: betik Ağ Hizmeti hesabına yazıyor, o anahtar normal kullanıcıya okunmadığı için
// durumu gösterilemiyordu; yerine Windows'un resmî ilkesi DODownloadMode kullanıldı). Edge ve OneDrive'a dokunanlar bilerek yok. Bildirimleri kapatma (bekçinin balonlarını da gizler), BitLocker,
// Depolama Algılayıcısı ve tarayıcı ilkeleri ("kuruluşunuz yönetiyor" yazısı çıkarır) da alınmadı.
//
// Geri alma: bir ayar ilk kez uygulanmadan önce dokunacağı her değerin eski hâli (yoksa "yoktu") yedeklenir
// (%APPDATA%\SergenPcTemizle\windows-yedek.txt); "geri al" bu yedeği aynen geri yazar, ayarın açtığı boş anahtarları
// da siler. Yedek yoksa (ayar başka bir araçla yapılmışsa) Win11Debloat'un "Windows varsayılanı" değerleri yazılır.
// HKCU işleri bu süreçte, HKLM işleri tek yönetici izniyle `--windows-kayit` yardımcısında yapılır.
// Yardımcı HKCU'ya hiç dokunmaz (yükseltilmiş süreçte HKCU başka bir hesap olabilir) ve yalnızca tablodaki
// anahtarlara yazar.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

enum Kok { KU, LM }
enum Etki { Yok, Gezgin, Oturum }
enum IslemTuru { Dw, Qw, Sz, Esz, Coklu, Hex, DegerSil, AnahtarSil, BosAnahtarSil }
enum AyarDurumu { Uygulanmadi, Kismen, Uygulandi }

class RegIslem
{
    public Kok Kok;
    public string Yol, Ad;      // Ad "" = (Varsayılan)
    public IslemTuru Tur;
    public object Deger;        // int, long, string, string[], byte[]
}

static class R
{
    public static RegIslem Dw(Kok k, string yol, string ad, long d) { return new RegIslem { Kok = k, Yol = yol, Ad = ad, Tur = IslemTuru.Dw, Deger = unchecked((int)(uint)d) }; }
    public static RegIslem Sz(Kok k, string yol, string ad, string d) { return new RegIslem { Kok = k, Yol = yol, Ad = ad, Tur = IslemTuru.Sz, Deger = d }; }
    public static RegIslem DegerSil(Kok k, string yol, string ad) { return new RegIslem { Kok = k, Yol = yol, Ad = ad, Tur = IslemTuru.DegerSil }; }
    public static RegIslem AnahtarSil(Kok k, string yol) { return new RegIslem { Kok = k, Yol = yol, Ad = "", Tur = IslemTuru.AnahtarSil }; }
    public static RegIslem Hex(Kok k, string yol, string ad, string d)
    {
        var b = d.Split(',').Where(x => x.Trim().Length > 0).Select(x => byte.Parse(x.Trim(), NumberStyles.HexNumber)).ToArray();
        return new RegIslem { Kok = k, Yol = yol, Ad = ad, Tur = IslemTuru.Hex, Deger = b };
    }
}

class WinAyar
{
    public readonly string Kimlik, Kategori, Baslik, Aciklama;
    public readonly bool Oneri;
    public readonly int EnAzSurum, EnCokSurum;   // Windows yapı numarası; 0 = sınır yok
    public readonly Etki Etki;
    public readonly RegIslem[] Uygula, Varsayilan;

    public WinAyar(string kimlik, string kategori, string baslik, string aciklama, bool oneri, int enAz, int enCok, Etki etki,
                   RegIslem[] uygula, RegIslem[] varsayilan)
    {
        Kimlik = kimlik; Kategori = kategori; Baslik = baslik; Aciklama = aciklama; Oneri = oneri;
        EnAzSurum = enAz; EnCokSurum = enCok; Etki = etki; Uygula = uygula; Varsayilan = varsayilan;
    }

    public bool Yonetici { get { return Uygula.Concat(Varsayilan).Any(i => i.Kok != Kok.KU); } }

    public bool BuSurumdeVar(int yapi)
    {
        return (EnAzSurum == 0 || yapi >= EnAzSurum) && (EnCokSurum == 0 || yapi <= EnCokSurum);
    }
}

class Kategori
{
    public readonly string Kimlik, Ad, Aciklama;
    public Kategori(string kimlik, string ad, string aciklama) { Kimlik = kimlik; Ad = ad; Aciklama = aciklama; }
}

static class WindowsAyarlari
{
    public static readonly Kategori[] Kategoriler =
    {
        new Kategori("gizlilik", "Gizlilik ve öneriler", "Microsoft'a giden kullanım verisi, reklamlar ve Windows'un her yerde çıkan önerileri."),
        new Kategori("yapayzeka", "Yapay zekâ", "Copilot, Recall ve Windows'a eklenen diğer yapay zekâ özellikleri."),
        new Kategori("baslat", "Başlat ve arama", "Başlat menüsü ve arama: web sonuçları, öneriler, telefon paneli."),
        new Kategori("gorevcubugu", "Görev çubuğu", "Görev çubuğundaki düğmeler, hizalama ve pencere öğeleri (widget)."),
        new Kategori("sistem", "Sistem", "Fare, klavye, sağ tık menüsü, açılış ve uyku davranışı."),
        new Kategori("guncelleme", "Windows Update", "Güncellemelerin ne zaman ve nasıl geleceği. Güvenlik güncellemeleri hiçbir ayarda kapanmaz."),
        new Kategori("gorunum", "Görünüm", "Tema, saydamlık ve animasyonlar."),
        new Kategori("gezgin", "Dosya Gezgini", "Dosya uzantıları, gizli dosyalar ve sol paneldeki bölümler."),
        new Kategori("coklugorev", "Çoklu görev", "Pencere yaslama ve Alt+Tab davranışı."),
        new Kategori("oyun", "Oyun", "Xbox Game Bar ve arka planda oyun kaydı."),
        new Kategori("diger", "Diğer", "Ayarlar uygulamasının giriş sayfası.")
    };

    public static readonly WinAyar[] Tum =
    {
        new WinAyar(@"DisableTelemetry", @"gizlilik", @"Telemetriyi, izlemeyi ve kişiye özel reklamları kapat",
            @"Windows'un Microsoft'a gönderdiği kullanım verisini en aza indirir; reklam kimliği, etkinlik geçmişi ve uygulama izleme kapanır.",
            true, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", @"Enabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Privacy", @"TailoredExperiencesWithDiagnosticDataEnabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", @"HasAccepted", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Input\TIPC", @"Enabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\InputPersonalization", @"RestrictImplicitInkCollection", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\InputPersonalization", @"RestrictImplicitTextCollection", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\InputPersonalization\TrainedDataStore", @"HarvestContacts", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Personalization\Settings", @"AcceptedPrivacyPolicy", 0),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", @"AllowTelemetry", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"Start_TrackProgs", 0),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\System", @"PublishUserActivities", 0),
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Siuf\Rules", @"NumberOfSIUFInPeriod", 0),
                R.DegerSil(Kok.KU, @"SOFTWARE\Microsoft\Siuf\Rules", @"PeriodInNanoSeconds") },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", @"Enabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Privacy", @"TailoredExperiencesWithDiagnosticDataEnabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", @"HasAccepted", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Input\TIPC", @"Enabled", 1),
                R.DegerSil(Kok.KU, @"Software\Microsoft\InputPersonalization", @"RestrictImplicitInkCollection"),
                R.DegerSil(Kok.KU, @"Software\Microsoft\InputPersonalization", @"RestrictImplicitTextCollection"),
                R.Dw(Kok.KU, @"Software\Microsoft\InputPersonalization\TrainedDataStore", @"HarvestContacts", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Personalization\Settings", @"AcceptedPrivacyPolicy", 1),
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Diagnostics\DiagTrack", @"ShowedToastAtLevel", 3),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", @"AllowTelemetry", 3),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", @"MaxTelemetryAllowed", 3),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"Start_TrackProgs", 1),
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\System", @"PublishUserActivities"),
                R.DegerSil(Kok.KU, @"Software\Microsoft\Siuf\Rules", @"NumberOfSIUFInPeriod"),
                R.DegerSil(Kok.KU, @"Software\Microsoft\Siuf\Rules", @"PeriodInNanoSeconds") }),
        new WinAyar(@"DisableSuggestions", @"gizlilik", @"İpuçlarını, önerileri ve reklamları kapat",
            @"Başlat ve Ayarlar'daki uygulama önerileri, ""Windows'u en iyi şekilde kullan"" ekranları ve kendiliğinden kurulan önerilen uygulamalar kapanır.",
            true, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-310093Enabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-338388Enabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SystemPaneSuggestionsEnabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"Start_IrisRecommendations", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-338389Enabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SoftLandingEnabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-338393Enabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-353694Enabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-353696Enabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-353698Enabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\SystemSettings\AccountNotifications", @"EnableAccountNotifications", 0),
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\UserProfileEngagement", @"ScoobeSystemSettingEnabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"ShowSyncProviderNotifications", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SilentInstalledAppsEnabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested", @"Enabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Mobility", @"OptedIn", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"Start_AccountNotifications", 0),
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.BackupReminder", @"Enabled", 0) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-310093Enabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-338388Enabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SystemPaneSuggestionsEnabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"Start_IrisRecommendations", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-338389Enabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SoftLandingEnabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-338393Enabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-353694Enabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-353696Enabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-353698Enabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\SystemSettings\AccountNotifications", @"EnableAccountNotifications", 1),
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\UserProfileEngagement", @"ScoobeSystemSettingEnabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"ShowSyncProviderNotifications", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SilentInstalledAppsEnabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested", @"Enabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Mobility", @"OptedIn", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"Start_AccountNotifications", 1),
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.BackupReminder", @"Enabled", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsBackup", @"DisableMonitoring", 0) }),
        new WinAyar(@"DisableLockscreenTips", @"gizlilik", @"Kilit ekranındaki ipuçlarını ve eğlenceli bilgileri kapat",
            @"Kilit ekranında çıkan ipucu ve reklam yazıları gösterilmez.",
            true, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-338387Enabled", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"RotatingLockScreenOverlayEnabled", 0) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"SubscribedContent-338387Enabled", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"RotatingLockScreenOverlayEnabled", 1) }),
        new WinAyar(@"DisableSettings365Ads", @"gizlilik", @"Ayarlar'daki Microsoft 365 reklamlarını gizle",
            @"Ayarlar'ın giriş sayfasındaki Microsoft 365 ve Copilot reklam kartları gösterilmez.",
            true, 22000, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", @"DisableConsumerAccountStateContent", 1) },
            new[] {
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", @"DisableConsumerAccountStateContent") }),
        new WinAyar(@"DisableLocationServices", @"gizlilik", @"Konum hizmetlerini kapat",
            @"Hiçbir uygulama konumunu kullanamaz (hava durumu, harita, saat dilimi otomatiği). Bunları kullanıyorsan açık bırak.",
            false, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", @"DisableLocation", 1) },
            new[] {
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", @"DisableLocation") }),
        new WinAyar(@"DisableFindMyDevice", @"gizlilik", @"Cihazımı Bul'u kapat",
            @"Kaybolan / çalınan dizüstünün yerini Microsoft hesabından bulma özelliği kapanır.",
            false, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\FindMyDevice", @"AllowFindMyDevice", 0) },
            new[] {
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\FindMyDevice", @"AllowFindMyDevice") }),
        new WinAyar(@"DisableCopilot", @"yapayzeka", @"Copilot'u kapat",
            @"Görev çubuğundaki Copilot düğmesi ve Windows'un Copilot yapay zekâ asistanı kapanır.",
            true, 22621, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"ShowCopilotButton", 0),
                R.Dw(Kok.KU, @"Software\Policies\Microsoft\Windows\WindowsCopilot", @"TurnOffWindowsCopilot", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", @"TurnOffWindowsCopilot", 1) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"ShowCopilotButton", 1),
                R.DegerSil(Kok.KU, @"Software\Policies\Microsoft\Windows\WindowsCopilot", @"TurnOffWindowsCopilot"),
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", @"TurnOffWindowsCopilot") }),
        new WinAyar(@"DisableRecall", @"yapayzeka", @"Recall'u kapat",
            @"Ekranın sürekli görüntüsünü alıp arşivleyen Recall özelliği kapanır (yalnızca Copilot+ bilgisayarlarda bulunur).",
            true, 22621, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Policies\Microsoft\Windows\WindowsAI", @"DisableAIDataAnalysis", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", @"DisableAIDataAnalysis", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", @"AllowRecallEnablement", 0),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", @"TurnOffSavingSnapshots", 1) },
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\PolicyManager\default\WindowsAI\DisableAIDataAnalysis", @"value", 0),
                R.DegerSil(Kok.KU, @"Software\Policies\Microsoft\Windows\WindowsAI", @"DisableAIDataAnalysis"),
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", @"DisableAIDataAnalysis"),
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", @"AllowRecallEnablement"),
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", @"TurnOffSavingSnapshots") }),
        new WinAyar(@"DisableClickToDo", @"yapayzeka", @"Click to Do'yu kapat",
            @"Ekrandaki yazı ve resimleri yapay zekâyla inceleyen Click to Do kapanır.",
            true, 22621, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Policies\Microsoft\Windows\WindowsAI", @"DisableClickToDo", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", @"DisableClickToDo", 1) },
            new[] {
                R.DegerSil(Kok.KU, @"Software\Policies\Microsoft\Windows\WindowsAI", @"DisableClickToDo"),
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", @"DisableClickToDo") }),
        new WinAyar(@"DisableAISvcAutoStart", @"yapayzeka", @"Yapay zekâ hizmeti kendiliğinden başlamasın",
            @"WSAIFabricSvc hizmeti açılışta başlamaz, gerekince başlar; arka plan yükü azalır.",
            true, 22621, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SYSTEM\CurrentControlSet\Services\WSAIFabricSvc", @"Start", 3) },
            new[] {
                R.Dw(Kok.LM, @"SYSTEM\CurrentControlSet\Services\WSAIFabricSvc", @"Start", 2) }),
        new WinAyar(@"DisablePaintAI", @"yapayzeka", @"Paint'teki yapay zekâ özelliklerini kapat",
            @"Cocreator, üretken doldurma / silme, resim oluşturucu ve arka plan kaldırma kapanır.",
            false, 22621, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", @"DisableCocreator", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", @"DisableGenerativeFill", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", @"DisableImageCreator", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", @"DisableGenerativeErase", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", @"DisableRemoveBackground", 1) },
            new[] {
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", @"DisableCocreator"),
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", @"DisableGenerativeFill"),
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", @"DisableImageCreator"),
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", @"DisableGenerativeErase"),
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", @"DisableRemoveBackground") }),
        new WinAyar(@"DisableNotepadAI", @"yapayzeka", @"Not Defteri'ndeki yapay zekâ özelliklerini kapat",
            @"Not Defteri'ndeki yeniden yaz / özetle gibi Copilot özellikleri kapanır.",
            false, 22621, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Policies\WindowsNotepad", @"DisableAIFeatures", 1) },
            new[] {
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\WindowsNotepad", @"DisableAIFeatures") }),
        new WinAyar(@"DisableBing", @"baslat", @"Başlat aramasından Bing ve web sonuçlarını kaldır",
            @"Başlat'ta arama yalnızca bilgisayarındaki dosya ve uygulamaları gösterir; daha hızlı ve reklamsız olur.",
            true, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Policies\Microsoft\Windows\Explorer", @"DisableSearchBoxSuggestions", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", @"AllowCortana", 0),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", @"CortanaConsent", 0) },
            new[] {
                R.Dw(Kok.KU, @"Software\Policies\Microsoft\Windows\Explorer", @"DisableSearchBoxSuggestions", 0),
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", @"AllowCortana"),
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", @"CortanaConsent") }),
        new WinAyar(@"DisableStartRecommended", @"baslat", @"Başlat'taki ""Önerilenler"" bölümünü gizle",
            @"Son açılan dosyalar ve önerilen uygulamalar bölümü gösterilmez (bazı Windows sürümlerinde etkisizdir).",
            false, 22621, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", @"HideRecommendedSection", 1) },
            new[] {
                R.Dw(Kok.KU, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", @"HideRecommendedSection", 0),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", @"HideRecommendedSection", 0),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\PolicyManager\current\device\Start", @"HideRecommendedSection", 0),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\PolicyManager\current\device\Education", @"IsEducationEnvironment", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"Start_Layout", 0) }),
        new WinAyar(@"DisableStartPhoneLink", @"baslat", @"Başlat'taki telefon panelini kapat",
            @"Başlat menüsünün yanındaki Telefon Bağlantısı (mobil cihazlar) paneli gösterilmez.",
            false, 22621, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Start\Companions\Microsoft.YourPhone_8wekyb3d8bbwe", @"IsEnabled", 0) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Start\Companions\Microsoft.YourPhone_8wekyb3d8bbwe", @"IsEnabled", 1) }),
        new WinAyar(@"DisableWidgets", @"gorevcubugu", @"Pencere öğelerini (widget) kapat",
            @"Hava durumu / haber paneli kapanır. Arka planda 200–400 MB bellek tutabildiği için bellek dolma sorununa da iyi gelir.",
            true, 0, 0, Etki.Gezgin,
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Dsh", @"AllowNewsAndInterests", 0) },
            new[] {
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Dsh", @"AllowNewsAndInterests") }),
        new WinAyar(@"HideChat", @"gorevcubugu", @"Görev çubuğundaki Sohbet simgesini gizle",
            @"Eski Windows 11 sürümlerindeki Teams Sohbet (Meet Now) simgesi gizlenir.",
            true, 0, 22621, Etki.Gezgin,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"TaskbarMn", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", @"HideSCAMeetNow", 1) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"TaskbarMn", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", @"HideSCAMeetNow", 0) }),
        new WinAyar(@"TaskbarAlignLeft", @"gorevcubugu", @"Görev çubuğunu sola hizala",
            @"Başlat düğmesi ve simgeler Windows 10'daki gibi solda durur.",
            false, 22000, 0, Etki.Gezgin,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"TaskbarAl", 0) },
            new[] {
                R.DegerSil(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"TaskbarAl") }),
        new WinAyar(@"ShowSearchIconTb", @"gorevcubugu", @"Arama kutusu yerine yalnızca simge göster",
            @"Görev çubuğundaki geniş arama kutusu küçük bir büyüteç simgesine döner, yer açılır.",
            false, 22000, 0, Etki.Gezgin,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Search", @"SearchboxTaskbarMode", 1) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Search", @"SearchboxTaskbarMode", 2) }),
        new WinAyar(@"HideTaskview", @"gorevcubugu", @"Görev Görünümü düğmesini gizle",
            @"Masaüstleri / açık pencereler düğmesi gizlenir (Win+Tab yine çalışır).",
            false, 22000, 0, Etki.Gezgin,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"ShowTaskViewButton", 0) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"ShowTaskViewButton", 1) }),
        new WinAyar(@"EnableEndTask", @"gorevcubugu", @"Sağ tık menüsüne ""Görevi sonlandır"" ekle",
            @"Donan bir programı görev çubuğundaki simgesine sağ tıklayıp anında kapatabilirsin.",
            true, 22631, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", @"TaskbarEndTask", 1) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", @"TaskbarEndTask", 0) }),
        new WinAyar(@"EnableLastActiveClick", @"gorevcubugu", @"Simgeye tıklayınca son kullanılan pencereyi aç",
            @"Aynı programın birden çok penceresi varken simgeye her tıklayış sıradaki pencereye geçer.",
            false, 22000, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"LastActiveClick", 1) },
            new[] {
                R.DegerSil(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"LastActiveClick") }),
        new WinAyar(@"DisableModernStandbyNetworking", @"sistem", @"Uykudayken internete bağlanmayı kapat",
            @"Dizüstü uykudayken ağa bağlanıp güncelleme / bildirim çekmez; pil daha az biter, çanta içinde ısınmaz.",
            true, 22000, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\f15576e8-98b7-4186-b944-eafa664402d9", @"ACSettingIndex", 0),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\f15576e8-98b7-4186-b944-eafa664402d9", @"DCSettingIndex", 0) },
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\f15576e8-98b7-4186-b944-eafa664402d9", @"ACSettingIndex", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\f15576e8-98b7-4186-b944-eafa664402d9", @"DCSettingIndex", 1) }),
        new WinAyar(@"DisableDragTray", @"sistem", @"Dosya sürükleyince çıkan paylaşım çubuğunu kapat",
            @"Dosya sürüklerken ekranın üstünde açılan ""Drag Tray"" paylaşım şeridi gösterilmez.",
            true, 26200, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\CDP", @"DragTrayEnabled", 0) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\CDP", @"DragTrayEnabled", 1) }),
        new WinAyar(@"RevertContextMenu", @"sistem", @"Eski (Windows 10) sağ tık menüsünü kullan",
            @"Sağ tıklayınca ""Daha fazla seçenek göster""e basmadan tam menü açılır.",
            false, 22000, 0, Etki.Gezgin,
            new[] {
                R.Sz(Kok.KU, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", @"", @"") },
            new[] {
                R.AnahtarSil(Kok.KU, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}") }),
        new WinAyar(@"DisableMouseAcceleration", @"sistem", @"Fare ivmesini kapat (İşaretçi hassasiyetini artır)",
            @"İmleç el hareketine birebir uyar; oyunlarda nişan almak daha tutarlı olur.",
            false, 0, 0, Etki.Yok,
            new[] {
                R.Sz(Kok.KU, @"Control Panel\Mouse", @"MouseSpeed", @"0"),
                R.Sz(Kok.KU, @"Control Panel\Mouse", @"MouseThreshold1", @"0"),
                R.Sz(Kok.KU, @"Control Panel\Mouse", @"MouseThreshold2", @"0") },
            new[] {
                R.Sz(Kok.KU, @"Control Panel\Mouse", @"MouseSpeed", @"1"),
                R.Sz(Kok.KU, @"Control Panel\Mouse", @"MouseThreshold1", @"6"),
                R.Sz(Kok.KU, @"Control Panel\Mouse", @"MouseThreshold2", @"10") }),
        new WinAyar(@"DisableStickyKeys", @"sistem", @"Yapışkan Tuşlar kısayolunu kapat",
            @"Oyunda Shift'e 5 kez basınca açılan ""Yapışkan Tuşlar"" sorusu bir daha çıkmaz.",
            false, 26100, 0, Etki.Oturum,
            new[] {
                R.Sz(Kok.KU, @"Control Panel\Accessibility\StickyKeys", @"Flags", @"506") },
            new[] {
                R.Sz(Kok.KU, @"Control Panel\Accessibility\StickyKeys", @"Flags", @"510") }),
        new WinAyar(@"DisableFastStartup", @"sistem", @"Hızlı başlatmayı kapat",
            @"""Kapat"" dediğinde bilgisayar gerçekten kapanır; sürücü ve güncelleme sorunları azalır, açılış birkaç saniye uzar.",
            false, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", @"HiberbootEnabled", 0) },
            new[] {
                R.Dw(Kok.LM, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", @"HiberbootEnabled", 1) }),
        new WinAyar(@"PreventUpdateAutoReboot", @"guncelleme", @"Güncellemeden sonra kendiliğinden yeniden başlatmasın",
            @"Oturum açıkken (oyun ortasında) Windows bilgisayarı kendi kendine yeniden başlatmaz; sen başlatınca kurulur.",
            true, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", @"NoAutoRebootWithLoggedOnUsers", 1) },
            new[] {
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", @"NoAutoRebootWithLoggedOnUsers") }),
        new WinAyar(@"DisableDeliveryOptimization", @"guncelleme", @"Güncellemeleri başka bilgisayarlarla paylaşmayı kapat",
            @"İnternetin, indirdiğin güncellemeleri başka bilgisayarlara yüklemek için kullanılmaz.",
            false, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", @"DODownloadMode", 0) },
            new[] {
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", @"DODownloadMode") }),
        new WinAyar(@"DisableDeviceAutoAppDownload", @"guncelleme", @"Aygıt takınca üretici uygulamalarını kendiliğinden kurmasın",
            @"Monitör, fare gibi bir aygıt takınca Windows üreticinin yardımcı uygulamasını kendiliğinden indirmez.",
            false, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\Device Metadata", @"PreventDeviceMetadataFromNetwork", 1) },
            new[] {
                R.DegerSil(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\Device Metadata", @"PreventDeviceMetadataFromNetwork") }),
        new WinAyar(@"DisableUpdateASAP", @"guncelleme", @"Yeni özellikleri çıkar çıkmaz alma",
            @"""En son güncellemeleri hemen al"" kapanır; yeni özellikler herkese açılınca gelir (güvenlik güncellemeleri etkilenmez).",
            false, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", @"IsContinuousInnovationOptedIn", 0) },
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", @"IsContinuousInnovationOptedIn", 1) }),
        new WinAyar(@"EnableDarkMode", @"gorunum", @"Koyu tema",
            @"Windows ve uygulamalar koyu renkli görünür.",
            false, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", @"AppsUseLightTheme", 0),
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", @"SystemUsesLightTheme", 0) },
            new[] {
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", @"AppsUseLightTheme", 1),
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", @"SystemUsesLightTheme", 1) }),
        new WinAyar(@"DisableTransparency", @"gorunum", @"Saydamlık efektlerini kapat",
            @"Başlat, görev çubuğu ve pencerelerdeki buzlu cam efekti kapanır; zayıf bilgisayarlarda biraz hız kazandırır.",
            false, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", @"EnableTransparency", 0) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", @"EnableTransparency", 1) }),
        new WinAyar(@"DisableAnimations", @"gorunum", @"Animasyonları ve görsel efektleri kapat",
            @"Pencere açılış / kapanış animasyonları kapanır, sistem daha çevik hissettirir. Oturumu kapatıp açınca tam etkili olur.",
            false, 0, 0, Etki.Oturum,
            new[] {
                R.Hex(Kok.KU, @"Control Panel\Desktop", @"UserPreferencesMask", "90,12,07,80,10,00,00,00") },
            new[] {
                R.Hex(Kok.KU, @"Control Panel\Desktop", @"UserPreferencesMask", "9e,1e,07,80,12,00,00,00") }),
        new WinAyar(@"HideDesktopSpotlightIcon", @"gorunum", @"""Bu resim hakkında bilgi edinin"" simgesini gizle",
            @"Masaüstü arka planı Windows Spotlight iken çıkan bilgi simgesi gizlenir.",
            false, 0, 0, Etki.Gezgin,
            new[] {
                R.DegerSil(Kok.KU, @"Software\Policies\Microsoft\Windows\CloudContent", @"DisableSpotlightCollectionOnDesktop"),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", @"{2cc5ca98-6485-489a-920e-b3e88a6ccce3}", 1) },
            new[] {
                R.DegerSil(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", @"{2cc5ca98-6485-489a-920e-b3e88a6ccce3}") }),
        new WinAyar(@"ShowKnownFileExt", @"gezgin", @"Dosya uzantılarını göster (.exe, .pdf…)",
            @"""fatura.pdf.exe"" gibi kılık değiştirmiş zararlı dosyaları fark etmeyi kolaylaştırır.",
            true, 0, 0, Etki.Gezgin,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"HideFileExt", 0) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"HideFileExt", 1) }),
        new WinAyar(@"ShowHiddenFolders", @"gezgin", @"Gizli dosya ve klasörleri göster",
            @"AppData gibi gizli klasörler Dosya Gezgini'nde görünür.",
            false, 0, 0, Etki.Gezgin,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"Hidden", 1) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"Hidden", 2) }),
        new WinAyar(@"ExplorerToThisPC", @"gezgin", @"Dosya Gezgini ""Bu bilgisayar"" ile açılsın",
            @"Gezgin ""Giriş"" yerine diskleri gösteren ""Bu bilgisayar"" sayfasıyla açılır.",
            false, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"LaunchTo", 1) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"LaunchTo", 2) }),
        new WinAyar(@"HideHome", @"gezgin", @"Gezgin'in sol panelinden ""Giriş""i gizle",
            @"Sol paneldeki ""Giriş"" (son kullanılanlar) bölümü gösterilmez.",
            false, 22000, 0, Etki.Gezgin,
            new[] {
                R.Sz(Kok.KU, @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", @"", @"CLSID_MSGraphHomeFolder"),
                R.Dw(Kok.KU, @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", @"System.IsPinnedToNameSpaceTree", 0) },
            new[] {
                R.Sz(Kok.KU, @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", @"", @"CLSID_MSGraphHomeFolder"),
                R.Dw(Kok.KU, @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", @"System.IsPinnedToNameSpaceTree", 1) }),
        new WinAyar(@"HideGallery", @"gezgin", @"Gezgin'in sol panelinden ""Galeri""yi gizle",
            @"Sol paneldeki fotoğraf ""Galeri"" bölümü gösterilmez.",
            false, 22000, 0, Etki.Gezgin,
            new[] {
                R.Dw(Kok.KU, @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}", @"System.IsPinnedToNameSpaceTree", 0) },
            new[] {
                R.Dw(Kok.KU, @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}", @"System.IsPinnedToNameSpaceTree", 1) }),
        new WinAyar(@"AddFoldersToThisPC", @"gezgin", @"Masaüstü, İndirilenler gibi klasörleri ""Bu bilgisayar""a geri ekle",
            @"Windows 10'daki gibi ""Bu bilgisayar"" sayfasında sık kullanılan klasörler de görünür.",
            false, 22000, 0, Etki.Gezgin,
            new[] {
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", @"HideIfEnabled"),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", @"HiddenByDefault", 0),
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{d3162b92-9365-467a-956b-92703aca08af}", @"HideIfEnabled"),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{d3162b92-9365-467a-956b-92703aca08af}", @"HiddenByDefault", 0),
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{088e3905-0323-4b02-9826-5d99428e115f}", @"HideIfEnabled"),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{088e3905-0323-4b02-9826-5d99428e115f}", @"HiddenByDefault", 0),
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{3dfdf296-dbec-4fb4-81d1-6a3438bcf4de}", @"HideIfEnabled"),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{1CF1260C-4DD0-4ebb-811F-33C572699FDE}", @"HiddenByDefault", 0),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{3dfdf296-dbec-4fb4-81d1-6a3438bcf4de}", @"HiddenByDefault", 0),
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{24ad3ad4-a569-4530-98e1-ab02f9417aa8}", @"HideIfEnabled"),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{24ad3ad4-a569-4530-98e1-ab02f9417aa8}", @"HiddenByDefault", 0),
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{f86fa3ab-70d2-4fc7-9c99-fcbf05467f3a}", @"HideIfEnabled"),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{f86fa3ab-70d2-4fc7-9c99-fcbf05467f3a}", @"HiddenByDefault", 0) },
            new[] {
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", @"HideIfEnabled", 36354489),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", @"HiddenByDefault", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{d3162b92-9365-467a-956b-92703aca08af}", @"HideIfEnabled", 36354489),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{d3162b92-9365-467a-956b-92703aca08af}", @"HiddenByDefault", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{088e3905-0323-4b02-9826-5d99428e115f}", @"HideIfEnabled", 36354489),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{088e3905-0323-4b02-9826-5d99428e115f}", @"HiddenByDefault", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{3dfdf296-dbec-4fb4-81d1-6a3438bcf4de}", @"HideIfEnabled", 36354489),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{1CF1260C-4DD0-4ebb-811F-33C572699FDE}", @"HiddenByDefault", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{3dfdf296-dbec-4fb4-81d1-6a3438bcf4de}", @"HiddenByDefault", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{24ad3ad4-a569-4530-98e1-ab02f9417aa8}", @"HideIfEnabled", 36354489),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{24ad3ad4-a569-4530-98e1-ab02f9417aa8}", @"HiddenByDefault", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{f86fa3ab-70d2-4fc7-9c99-fcbf05467f3a}", @"HideIfEnabled", 36354489),
                R.Dw(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{f86fa3ab-70d2-4fc7-9c99-fcbf05467f3a}", @"HiddenByDefault", 1) }),
        new WinAyar(@"DisableWindowSnapping", @"coklugorev", @"Pencere yaslamayı kapat",
            @"Pencereyi ekran kenarına sürükleyince kendiliğinden yarım / tam ekrana yaslanmaz.",
            false, 0, 0, Etki.Oturum,
            new[] {
                R.Sz(Kok.KU, @"Control Panel\Desktop", @"WindowArrangementActive", @"0") },
            new[] {
                R.Sz(Kok.KU, @"Control Panel\Desktop", @"WindowArrangementActive", @"1") }),
        new WinAyar(@"DisableSnapAssist", @"coklugorev", @"Yaslarken diğer pencereleri önermesin",
            @"Bir pencereyi yasladıktan sonra boş yarıya hangi pencerenin gideceğini soran öneri çıkmaz.",
            false, 22000, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"SnapAssist", 0) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"SnapAssist", 1) }),
        new WinAyar(@"DisableSnapLayouts", @"coklugorev", @"Yaslama düzenleri menüsünü gizle",
            @"Büyüt düğmesinin üstüne gelince ya da pencereyi üste sürükleyince açılan düzen menüsü çıkmaz.",
            false, 22000, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"EnableSnapBar", 0),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"EnableSnapAssistFlyout", 0) },
            new[] {
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"EnableSnapBar", 1),
                R.Dw(Kok.KU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"EnableSnapAssistFlyout", 1) }),
        new WinAyar(@"HideTabsInAltTab", @"coklugorev", @"Alt+Tab'da tarayıcı sekmelerini gösterme",
            @"Alt+Tab yalnızca pencereleri gösterir; her tarayıcı sekmesi ayrı kutu olarak çıkmaz.",
            false, 22000, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", @"MultiTaskingAltTabFilter", 3) },
            new RegIslem[0]),
        new WinAyar(@"DisableDVR", @"oyun", @"Xbox oyun kaydını (arka plan kaydı) kapat",
            @"Oyunda arka planda sürekli kayıt yapılmaz; FPS kaybı ve takılma azalır. Game Bar ile video kaydediyorsan açık bırak.",
            true, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"System\GameConfigStore", @"GameDVR_Enabled", 0),
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", @"AppCaptureEnabled", 0),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", @"AllowGameDVR", 0) },
            new[] {
                R.Dw(Kok.KU, @"System\GameConfigStore", @"GameDVR_Enabled", 1),
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", @"AppCaptureEnabled", 1),
                R.Dw(Kok.LM, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", @"AllowGameDVR", 1) }),
        new WinAyar(@"DisableGameBarIntegration", @"oyun", @"Kumandanın Xbox tuşu Game Bar'ı açmasın",
            @"Oyun kumandasındaki Xbox tuşuna basınca Game Bar açılmaz.",
            false, 0, 0, Etki.Yok,
            new[] {
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\GameBar", @"UseNexusForGameBarEnabled", 0) },
            new[] {
                R.Dw(Kok.KU, @"SOFTWARE\Microsoft\GameBar", @"UseNexusForGameBarEnabled", 1) }),
        new WinAyar(@"DisableSettingsHome", @"diger", @"Ayarlar'ın ""Giriş"" sayfasını gizle",
            @"Ayarlar doğrudan Sistem sayfasıyla açılır; öneri ve reklam kartlarının olduğu giriş sayfası gösterilmez.",
            false, 22000, 0, Etki.Yok,
            new[] {
                R.Sz(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", @"SettingsPageVisibility", @"hide:home") },
            new[] {
                R.DegerSil(Kok.LM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", @"SettingsPageVisibility") })
    };

    static readonly string YedekDosyasi = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SergenPcTemizle", "windows-yedek.txt");

    static int yapi = -1;

    // Windows yapı numarası kayıt defterinden (Environment.OSVersion uyumluluk katmanı yüzünden yanıltır).
    public static int Yapi
    {
        get
        {
            if (yapi >= 0) return yapi;
            yapi = 0;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int y;
                    if (k != null && int.TryParse(Convert.ToString(k.GetValue("CurrentBuildNumber")), out y)) yapi = y;
                }
            }
            catch { }
            return yapi;
        }
    }

    public static IEnumerable<WinAyar> BuSurumdekiler { get { return Tum.Where(a => a.BuSurumdeVar(Yapi)); } }

    // ---- Kayıt defteri ----

    static RegistryKey KokAc(Kok k)
    {
        return RegistryKey.OpenBaseKey(k == Kok.KU ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
    }

    class Okunan { public bool AnahtarVar, DegerVar, Okunamadi; public RegistryValueKind Tur; public object Deger; }

    static Okunan Oku(Kok k, string yol, string ad)
    {
        var o = new Okunan();
        try
        {
            using (var kok = KokAc(k))
            using (var a = kok.OpenSubKey(yol))
            {
                if (a == null) return o;
                o.AnahtarVar = true;
                if (!a.GetValueNames().Contains(ad, StringComparer.OrdinalIgnoreCase)) return o;
                o.DegerVar = true;
                o.Tur = a.GetValueKind(ad);
                o.Deger = a.GetValue(ad, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
        }
        catch { o.Okunamadi = true; }
        return o;
    }

    static bool Tutuyor(RegIslem i)
    {
        var o = Oku(i.Kok, i.Yol, i.Ad);
        if (o.Okunamadi) return false;
        switch (i.Tur)
        {
            case IslemTuru.DegerSil: return !o.DegerVar;
            case IslemTuru.Dw: return o.DegerVar && o.Tur == RegistryValueKind.DWord && (int)o.Deger == (int)i.Deger;
            case IslemTuru.Sz: return o.DegerVar && (o.Tur == RegistryValueKind.String || o.Tur == RegistryValueKind.ExpandString)
                                      && string.Equals(o.Deger as string, (string)i.Deger, StringComparison.Ordinal);
            case IslemTuru.Hex: return o.DegerVar && o.Tur == RegistryValueKind.Binary && ((byte[])o.Deger).SequenceEqual((byte[])i.Deger);
        }
        return false;
    }

    public static AyarDurumu Durum(WinAyar a)
    {
        int tutan = a.Uygula.Count(Tutuyor);
        if (tutan == a.Uygula.Length) return AyarDurumu.Uygulandi;
        return tutan == 0 ? AyarDurumu.Uygulanmadi : AyarDurumu.Kismen;
    }

    // Tek bir işlemi yazar. Yardımcı (yönetici) ve bu süreç aynı kodu kullanır.
    internal static void Yaz(RegIslem i)
    {
        using (var kok = KokAc(i.Kok))
        {
            string yol = i.Yol;
            switch (i.Tur)
            {
                case IslemTuru.AnahtarSil:
                    kok.DeleteSubKeyTree(yol, false);
                    return;
                case IslemTuru.BosAnahtarSil:
                    BosAgaciSil(kok, yol);
                    return;
                case IslemTuru.DegerSil:
                    using (var a = kok.OpenSubKey(yol, true)) if (a != null) a.DeleteValue(i.Ad, false);
                    return;
            }
            using (var a = kok.CreateSubKey(yol))
            {
                if (a == null) throw new IOException("anahtar açılamadı: " + yol);
                switch (i.Tur)
                {
                    case IslemTuru.Dw: a.SetValue(i.Ad, (int)i.Deger, RegistryValueKind.DWord); break;
                    case IslemTuru.Qw: a.SetValue(i.Ad, (long)i.Deger, RegistryValueKind.QWord); break;
                    case IslemTuru.Sz: a.SetValue(i.Ad, (string)i.Deger, RegistryValueKind.String); break;
                    case IslemTuru.Esz: a.SetValue(i.Ad, (string)i.Deger, RegistryValueKind.ExpandString); break;
                    case IslemTuru.Coklu: a.SetValue(i.Ad, (string[])i.Deger, RegistryValueKind.MultiString); break;
                    case IslemTuru.Hex: a.SetValue(i.Ad, (byte[])i.Deger, RegistryValueKind.Binary); break;
                }
            }
        }
    }

    // Ayarın açtığı anahtar (ve altındakiler) içinde hiç değer kalmadıysa silinir; başka bir şey yazılmışsa dokunulmaz.
    static void BosAgaciSil(RegistryKey kok, string yol)
    {
        using (var a = kok.OpenSubKey(yol))
        {
            if (a == null || !Bos(a)) return;
        }
        kok.DeleteSubKeyTree(yol, false);
    }

    static bool Bos(RegistryKey a)
    {
        if (a.ValueCount > 0) return false;
        foreach (var alt in a.GetSubKeyNames())
            using (var b = a.OpenSubKey(alt)) if (b == null || !Bos(b)) return false;
        return true;
    }

    // Yoldaki ilk olmayan anahtar ("ayarın açacağı" en üst anahtar); hepsi varsa null.
    static string IlkOlmayan(Kok k, string yol)
    {
        try
        {
            using (var kok = KokAc(k))
            {
                var parcalar = yol.Split('\\');
                string birikim = null;
                foreach (var p in parcalar)
                {
                    birikim = birikim == null ? p : birikim + "\\" + p;
                    using (var a = kok.OpenSubKey(birikim)) if (a == null) return birikim;
                }
            }
        }
        catch { }
        return null;
    }

    // Tek bir değerin şu anki hâli; yoksa ya da okunamıyorsa null (Turbo sekmesi özel durumlar için).
    internal static object DegerOku(Kok k, string yol, string ad)
    {
        var o = Oku(k, yol, ad);
        return o.DegerVar ? o.Deger : null;
    }

    // ---- Yedek ----

    class YedekKaydi { public Kok Kok; public string Yol, Ad, Olusan; public RegIslem Eski; }

    static Dictionary<string, List<YedekKaydi>> yedekler;
    static bool yedekOkunamadi;   // dosya var ama okunamadı: üzerine yazılıp eski yedekler kaybolmasın

    static Dictionary<string, List<YedekKaydi>> Yedekler()
    {
        if (yedekler != null) return yedekler;
        yedekler = new Dictionary<string, List<YedekKaydi>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(YedekDosyasi)) return yedekler;
            foreach (var satir in File.ReadAllLines(YedekDosyasi, Encoding.UTF8))
            {
                // Satır: "kimlik|olusanB64" \t işlem (işlemin kendisi de sekmeli: yalnızca ilk sekmeden bölünür).
                int sekme = satir.IndexOf('\t');
                if (sekme <= 0) continue;
                RegIslem i = IslemCoz(satir.Substring(sekme + 1));
                if (i == null) continue;
                var kim = satir.Substring(0, sekme).Split('|');
                List<YedekKaydi> l;
                if (!yedekler.TryGetValue(kim[0], out l)) { l = new List<YedekKaydi>(); yedekler[kim[0]] = l; }
                l.Add(new YedekKaydi { Kok = i.Kok, Yol = i.Yol, Ad = i.Ad, Eski = i, Olusan = kim.Length > 1 && kim[1].Length > 0 ? B64Coz(kim[1]) : null });
            }
        }
        catch { yedekOkunamadi = true; }
        return yedekler;
    }

    internal static void YedekleriKaydet()
    {
        if (yedekOkunamadi) throw new IOException("yedek dosyası okunamadı (" + YedekDosyasi + ")");
        var satirlar = new List<string>();
        foreach (var kv in Yedekler())
            foreach (var y in kv.Value)
                satirlar.Add(kv.Key + "|" + (y.Olusan == null ? "" : B64(y.Olusan)) + "\t" + IslemYaz(y.Eski));
        Directory.CreateDirectory(Path.GetDirectoryName(YedekDosyasi));
        string gecici = YedekDosyasi + ".yeni";
        File.WriteAllLines(gecici, satirlar, new UTF8Encoding(false));
        if (File.Exists(YedekDosyasi)) File.Replace(gecici, YedekDosyasi, null);
        else File.Move(gecici, YedekDosyasi);
    }

    public static bool YedekVar(WinAyar a) { return Yedekler().ContainsKey(a.Kimlik); }

    // Geri alınıp gerçekten eski hâline dönen ayarların yedeği silinir (bir dahaki uygulamada yeniden alınır).
    internal static void YedekKaldir(IEnumerable<string> kimlikler)
    {
        bool degisti = false;
        foreach (var k in kimlikler) if (Yedekler().Remove(k)) degisti = true;
        if (degisti) try { YedekleriKaydet(); } catch { }
    }

    // Ayar uygulanmadan önce: dokunacağı değerlerin şu anki hâli. Önceden yedek varsa (ilk özgün hâl) korunur.
    internal static void Yedekle(WinAyar a)
    {
        if (Yedekler().ContainsKey(a.Kimlik)) return;
        var l = new List<YedekKaydi>();
        var gorulen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in a.Uygula)
        {
            if (!gorulen.Add(i.Kok + "\\" + i.Yol + "\\" + i.Ad)) continue;
            var o = Oku(i.Kok, i.Yol, i.Ad);
            // Okunamayan (yetki) değer "yoktu" sanılıp geri almada silinmesin: bu ayar yedeksiz kalır, geri almada
            // Windows varsayılanı yazılır.
            if (o.Okunamadi) return;
            RegIslem eski;
            if (!o.DegerVar) eski = R.DegerSil(i.Kok, i.Yol, i.Ad);
            else eski = new RegIslem { Kok = i.Kok, Yol = i.Yol, Ad = i.Ad, Tur = TurdenIslem(o.Tur), Deger = o.Deger };
            if (eski.Tur == IslemTuru.DegerSil && o.DegerVar) continue;   // bilinmeyen tür (ör. REG_NONE): yedeklenemez, dokunulmaz
            l.Add(new YedekKaydi { Kok = i.Kok, Yol = i.Yol, Ad = i.Ad, Eski = eski, Olusan = o.AnahtarVar ? null : IlkOlmayan(i.Kok, i.Yol) });
        }
        Yedekler()[a.Kimlik] = l;
    }

    static IslemTuru TurdenIslem(RegistryValueKind t)
    {
        switch (t)
        {
            case RegistryValueKind.DWord: return IslemTuru.Dw;
            case RegistryValueKind.QWord: return IslemTuru.Qw;
            case RegistryValueKind.String: return IslemTuru.Sz;
            case RegistryValueKind.ExpandString: return IslemTuru.Esz;
            case RegistryValueKind.MultiString: return IslemTuru.Coklu;
            case RegistryValueKind.Binary: return IslemTuru.Hex;
        }
        return IslemTuru.DegerSil;
    }

    // Geri almak için yazılacaklar: yedek varsa aynen eski hâl (+ ayarın açtığı boş anahtarlar), yoksa Windows varsayılanı.
    internal static List<RegIslem> GeriAlmaIslemleri(WinAyar a)
    {
        var l = new List<RegIslem>();
        List<YedekKaydi> y;
        if (Yedekler().TryGetValue(a.Kimlik, out y))
        {
            foreach (var k in y) l.Add(k.Eski);
            foreach (var olusan in y.Where(k => k.Olusan != null).Select(k => new { k.Kok, k.Olusan }).Distinct())
                l.Add(new RegIslem { Kok = olusan.Kok, Yol = olusan.Olusan, Ad = "", Tur = IslemTuru.BosAnahtarSil });
            return l;
        }
        if (a.Varsayilan.Length > 0) return a.Varsayilan.ToList();
        foreach (var i in a.Uygula) if (i.Tur != IslemTuru.DegerSil) l.Add(R.DegerSil(i.Kok, i.Yol, i.Ad));
        return l;
    }

    // ---- Uygulama ----

    public class Sonuc
    {
        public List<WinAyar> Yapilan = new List<WinAyar>(), Yapilamayan = new List<WinAyar>();
        public bool IzinVerilmedi, GezginGerekli, OturumGerekli;
        public string Hata;
    }

    // uygulanacak: işaretlenip henüz uygulanmamışlar; geriAlinacak: işareti kaldırılan uygulanmışlar.
    // yoneticiCalistir: yardımcıyı (argümanlarla) yönetici olarak açıp çıkış kodunu döner; -2 = izin verilmedi.
    public static Sonuc Uygula(List<WinAyar> uygulanacak, List<WinAyar> geriAlinacak, bool geriYuklemeNoktasi,
                               Func<string, int> yoneticiCalistir)
    {
        var s = new Sonuc();
        var isler = new List<KeyValuePair<WinAyar, List<RegIslem>>>();
        try
        {
            foreach (var a in uygulanacak) Yedekle(a);
            YedekleriKaydet();
        }
        catch (Exception e) { s.Hata = "Yedek alınamadı: " + e.Message; s.Yapilamayan.AddRange(uygulanacak); s.Yapilamayan.AddRange(geriAlinacak); return s; }
        foreach (var a in uygulanacak) isler.Add(new KeyValuePair<WinAyar, List<RegIslem>>(a, a.Uygula.ToList()));
        foreach (var a in geriAlinacak) isler.Add(new KeyValuePair<WinAyar, List<RegIslem>>(a, GeriAlmaIslemleri(a)));

        // Önce yönetici işleri (tek izin): izin verilmezse o ayarların HKCU kısmı da yazılmaz, yarım kalmasınlar.
        var yoneticiIsleri = isler.Where(kv => kv.Value.Any(i => i.Kok != Kok.KU)).ToList();
        var basarisiz = new HashSet<WinAyar>();
        if (yoneticiIsleri.Count > 0)
        {
            string dosya = Path.Combine(Path.GetTempPath(), "SergenPcTemizle-windows-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllLines(dosya, yoneticiIsleri.SelectMany(kv => kv.Value).Where(i => i.Kok != Kok.KU).Select(IslemYaz), new UTF8Encoding(false));
                int kod = yoneticiCalistir("--windows-kayit " + Tirnak(dosya) + (geriYuklemeNoktasi ? " nokta" : ""));
                if (kod != 0)
                {
                    foreach (var kv in yoneticiIsleri) basarisiz.Add(kv.Key);
                    if (kod == -2) s.IzinVerilmedi = true;
                    else s.Hata = "Yönetici işleri yapılamadı (kod " + kod + ").";
                }
            }
            catch (Exception e) { foreach (var kv in yoneticiIsleri) basarisiz.Add(kv.Key); s.Hata = e.Message; }
            finally { try { File.Delete(dosya); } catch { } }
        }

        foreach (var kv in isler)
        {
            var a = kv.Key;
            if (basarisiz.Contains(a)) { s.Yapilamayan.Add(a); continue; }
            try
            {
                foreach (var i in kv.Value) if (i.Kok == Kok.KU) Yaz(i);
                s.Yapilan.Add(a);
                if (a.Etki == Etki.Gezgin) s.GezginGerekli = true;
                if (a.Etki == Etki.Oturum) s.OturumGerekli = true;
            }
            catch (Exception e) { s.Yapilamayan.Add(a); if (s.Hata == null) s.Hata = e.Message; }
        }

        // Geri alınan ve gerçekten eski hâline dönenlerin yedeği silinir (bir dahaki uygulamada yeniden alınır).
        bool degisti = false;
        foreach (var a in geriAlinacak)
            if (s.Yapilan.Contains(a) && Yedekler().Remove(a.Kimlik)) degisti = true;
        if (degisti) try { YedekleriKaydet(); } catch { }

        Duyur(s.Yapilan);
        return s;
    }

    // Değişikliği açık programlara duyurur: tema / ilke / görev çubuğu hemen tazelenir, fare hızı hemen geçerli olur.
    static void Duyur(List<WinAyar> yapilan)
    {
        try
        {
            if (yapilan.Any(a => a.Uygula.Any(i => i.Kok == Kok.KU && i.Yol.Equals(@"Control Panel\Mouse", StringComparison.OrdinalIgnoreCase))))
            {
                var m = Oku(Kok.KU, @"Control Panel\Mouse", "MouseThreshold1").Deger as string;
                var n = Oku(Kok.KU, @"Control Panel\Mouse", "MouseThreshold2").Deger as string;
                var h = Oku(Kok.KU, @"Control Panel\Mouse", "MouseSpeed").Deger as string;
                int t1, t2, hz;
                if (int.TryParse(m, out t1) && int.TryParse(n, out t2) && int.TryParse(h, out hz))
                {
                    var d = new[] { t1, t2, hz };
                    SystemParametersInfo(0x0004 /* SPI_SETMOUSE */, 0, d, 0x2 /* SPIF_SENDCHANGE */);
                }
            }
            IntPtr sonuc;
            foreach (var konu in new[] { "ImmersiveColorSet", "Policy", "TraySettings", "WindowsThemeElement" })
                SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, konu, 0x2 /* SMTO_ABORTIFHUNG */, 1000, out sonuc);
        }
        catch { }
    }

    // Gezgin'i yeniden başlatır (görev çubuğu ve sağ tık menüsü ayarları için). Açık Gezgin pencereleri kapanır.
    public static void GezginiYenidenBaslat()
    {
        int oturum;
        using (var b = Process.GetCurrentProcess()) oturum = b.SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
            using (p) try { if (p.SessionId == oturum) p.Kill(); } catch { }
        // Windows kabuğu kendiliğinden yeniden açar; açmazsa biz açarız.
        for (int i = 0; i < 30; i++)
        {
            System.Threading.Thread.Sleep(200);
            if (Process.GetProcessesByName("explorer").Any(p => { using (p) return p.SessionId == oturum; })) return;
        }
        try { Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")); } catch { }
    }

    // ---- Yönetici yardımcısı ----

    // exe --windows-kayit <dosya> [nokta]. Her satır bir işlem; yalnızca tablodaki HKLM anahtarlarına
    // (ve ayarın açtığı boş anahtarlara) dokunur. 0 = hepsi yapıldı, 1 = hata, 2 = izin verilmeyen satır (hiçbiri yapılmaz).
    public static int YoneticiYaz(string[] args)
    {
        try
        {
            if (args.Length < 1 || args.Length > 2 || !File.Exists(args[0])) return 1;
            bool nokta = args.Length == 2 && args[1] == "nokta";
            var islemler = File.ReadAllLines(args[0], Encoding.UTF8).Where(x => x.Length > 0).Select(IslemCoz).ToList();
            if (islemler.Any(i => i == null || !Izinli(i))) return 2;
            if (nokta) GeriYuklemeNoktasi();
            int hata = 0;
            foreach (var i in islemler) { try { Yaz(i); } catch { hata++; } }
            return hata == 0 ? 0 : 1;
        }
        catch { return 1; }
    }

    internal static bool Izinli(RegIslem i)
    {
        if (i.Kok == Kok.KU) return false;
        // Windows ayarları + Turbo sekmesinin kayıt defteri ayarları (aynı yardımcı kuralları).
        var tablo = Tum.Concat(TurboAyarlari.KayitAyarlari).SelectMany(a => a.Uygula.Concat(a.Varsayilan)).Where(t => t.Kok == i.Kok).ToList();
        if (i.Tur == IslemTuru.BosAnahtarSil)
            return i.Yol.Split('\\').Length >= 3
                   && tablo.Any(t => t.Yol.Equals(i.Yol, StringComparison.OrdinalIgnoreCase) || t.Yol.StartsWith(i.Yol + "\\", StringComparison.OrdinalIgnoreCase));
        if (i.Tur == IslemTuru.AnahtarSil)
            return tablo.Any(t => t.Tur == IslemTuru.AnahtarSil && t.Yol.Equals(i.Yol, StringComparison.OrdinalIgnoreCase));
        return tablo.Any(t => t.Yol.Equals(i.Yol, StringComparison.OrdinalIgnoreCase) && t.Ad.Equals(i.Ad, StringComparison.OrdinalIgnoreCase));
    }

    internal static void GeriYuklemeNoktasi(string aciklama = "Windows ayarlari oncesi")
    {
        try
        {
            var bilgi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description 'Sergen PC Temizle: " + aciklama + "' " +
                "-RestorePointType MODIFY_SETTINGS -ErrorAction SilentlyContinue\"")
            { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(bilgi)) p.WaitForExit(120000);
        }
        catch { }
    }

    // ---- Satır biçimi: tür \t kök \t yol(b64) \t ad(b64) \t değer ----

    internal static string IslemYaz(RegIslem i)
    {
        string d = "";
        switch (i.Tur)
        {
            case IslemTuru.Dw: d = ((int)i.Deger).ToString(CultureInfo.InvariantCulture); break;
            case IslemTuru.Qw: d = ((long)i.Deger).ToString(CultureInfo.InvariantCulture); break;
            case IslemTuru.Sz: case IslemTuru.Esz: d = B64((string)i.Deger); break;
            case IslemTuru.Coklu: d = string.Join(",", ((string[])i.Deger).Select(B64)); break;
            case IslemTuru.Hex: d = BitConverter.ToString((byte[])i.Deger).Replace("-", ""); break;
        }
        return i.Tur + "\t" + i.Kok + "\t" + B64(i.Yol) + "\t" + B64(i.Ad ?? "") + "\t" + d;
    }

    internal static RegIslem IslemCoz(string satir)
    {
        try
        {
            var p = satir.Split('\t');
            if (p.Length != 5) return null;
            var i = new RegIslem
            {
                Tur = (IslemTuru)Enum.Parse(typeof(IslemTuru), p[0]),
                Kok = (Kok)Enum.Parse(typeof(Kok), p[1]),
                Yol = B64Coz(p[2]),
                Ad = B64Coz(p[3])
            };
            if (!Enum.IsDefined(typeof(IslemTuru), i.Tur) || !Enum.IsDefined(typeof(Kok), i.Kok)) return null;
            switch (i.Tur)
            {
                case IslemTuru.Dw: i.Deger = int.Parse(p[4], CultureInfo.InvariantCulture); break;
                case IslemTuru.Qw: i.Deger = long.Parse(p[4], CultureInfo.InvariantCulture); break;
                case IslemTuru.Sz: case IslemTuru.Esz: i.Deger = B64Coz(p[4]); break;
                case IslemTuru.Coklu: i.Deger = p[4].Length == 0 ? new string[0] : p[4].Split(',').Select(B64Coz).ToArray(); break;
                case IslemTuru.Hex:
                    if (p[4].Length % 2 != 0) return null;
                    var b = new byte[p[4].Length / 2];
                    for (int j = 0; j < b.Length; j++) b[j] = byte.Parse(p[4].Substring(j * 2, 2), NumberStyles.HexNumber);
                    i.Deger = b;
                    break;
            }
            if (string.IsNullOrEmpty(i.Yol) || i.Yol.StartsWith("\\") || i.Yol.Contains("\\\\")) return null;
            return i;
        }
        catch { return null; }
    }

    static string B64(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }
    static string B64Coz(string s) { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
    internal static string Tirnak(string s) { return "\"" + s + "\""; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, string l, uint bayrak, uint zaman, out IntPtr sonuc);
    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint islem, uint p, int[] d, uint bayrak);
}

// Hazır gelen (Microsoft Store) uygulamaları: yalnızca bu kullanıcı için kaldırılır, Microsoft Store'dan yeniden kurulabilir.
// Liste Win11Debloat'un varsayılan listesinden; Edge, OneDrive, Store, Xbox (Game Pass için gerekli) ve Windows
// parçaları bilerek yok. "Önerilen" = oyunlar, reklam uygulamaları ve Microsoft'un bıraktığı eski uygulamalar.
class HazirUygulama
{
    public readonly string Kimlik, Ad, Aciklama;
    public readonly bool Oneri;
    public HazirUygulama(string kimlik, string ad, string aciklama, bool oneri) { Kimlik = kimlik; Ad = ad; Aciklama = aciklama; Oneri = oneri; }
}

static class HazirUygulamalar
{
    public static readonly HazirUygulama[] Liste =
    {
        new HazirUygulama("Clipchamp.Clipchamp", "Clipchamp", "Microsoft'un video düzenleyicisi", true),
        new HazirUygulama("Microsoft.3DBuilder", "3D Builder", "Basit 3B modelleme", true),
        new HazirUygulama("Microsoft.549981C3F5F10", "Cortana", "Microsoft'un bıraktığı sesli asistan", true),
        new HazirUygulama("Microsoft.BingFinance", "Bing Finans", "Bing finans haberleri (Microsoft bıraktı)", true),
        new HazirUygulama("Microsoft.BingFoodAndDrink", "Bing Yemek", "Tarif ve yemek haberleri (Microsoft bıraktı)", true),
        new HazirUygulama("Microsoft.BingHealthAndFitness", "Bing Sağlık", "Sağlık ve spor haberleri (Microsoft bıraktı)", true),
        new HazirUygulama("Microsoft.BingNews", "Microsoft Haberler", "Haber toplayıcı", true),
        new HazirUygulama("Microsoft.BingSports", "Bing Spor", "Spor haberleri ve skorlar (Microsoft bıraktı)", true),
        new HazirUygulama("Microsoft.BingTranslator", "Bing Çeviri", "Çeviri uygulaması", false),
        new HazirUygulama("Microsoft.BingTravel", "Bing Seyahat", "Seyahat haberleri (Microsoft bıraktı)", true),
        new HazirUygulama("Microsoft.BingWeather", "Hava Durumu", "Bing hava durumu", false),
        new HazirUygulama("Microsoft.Copilot", "Microsoft Copilot", "Copilot yapay zekâ uygulaması (görev çubuğu ayarı Yapay zekâ kategorisinde)", true),
        new HazirUygulama("Microsoft.Windows.AIHub", "Copilot+ AI Hub", "Copilot+ yapay zekâ merkezi", true),
        new HazirUygulama("Microsoft.PCManager", "Microsoft PC Manager", "Microsoft'un temizlik aracı", false),
        new HazirUygulama("Microsoft.Getstarted", "Başlarken / İpuçları", "Windows tanıtım ve ipuçları", true),
        new HazirUygulama("Microsoft.Messaging", "Mesajlaşma", "Eski mesajlaşma uygulaması (Microsoft bıraktı)", true),
        new HazirUygulama("Microsoft.Microsoft3DViewer", "3D Görüntüleyici", "3B model görüntüleyici", true),
        new HazirUygulama("Microsoft.MicrosoftJournal", "Microsoft Journal", "Kalemle not alma", false),
        new HazirUygulama("Microsoft.MicrosoftOfficeHub", "Microsoft 365 Copilot (Office)", "Office / Microsoft 365 Copilot reklam ve kısayol uygulaması", true),
        new HazirUygulama("Microsoft.MicrosoftPowerBIForWindows", "Power BI", "İş analizi uygulaması", true),
        new HazirUygulama("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "Kart oyunları (reklamlı)", true),
        new HazirUygulama("Microsoft.MicrosoftStickyNotes", "Yapışkan Notlar", "Masaüstü notları", false),
        new HazirUygulama("Microsoft.MixedReality.Portal", "Karma Gerçeklik Portalı", "Windows VR gözlükleri için", true),
        new HazirUygulama("Microsoft.NetworkSpeedTest", "Ağ Hız Testi", "İnternet hız testi", false),
        new HazirUygulama("Microsoft.Office.OneNote", "OneNote (eski)", "Eski OneNote uygulaması", false),
        new HazirUygulama("Microsoft.Office.Sway", "Sway", "Sunum uygulaması", true),
        new HazirUygulama("Microsoft.OneConnect", "Mobil Planlar", "Operatör hat yönetimi", true),
        new HazirUygulama("Microsoft.Print3D", "Print 3D", "3B yazıcı hazırlığı", true),
        new HazirUygulama("Microsoft.PowerAutomateDesktop", "Power Automate", "Masaüstü otomasyon aracı", false),
        new HazirUygulama("Microsoft.SkypeApp", "Skype (eski)", "Eski Skype uygulaması (Microsoft bıraktı)", true),
        new HazirUygulama("Microsoft.Todos", "Microsoft To Do", "Yapılacaklar listesi", false),
        new HazirUygulama("Microsoft.Windows.DevHome", "Dev Home", "Geliştirici paneli (Microsoft bıraktı)", true),
        new HazirUygulama("Microsoft.WindowsAlarms", "Alarmlar ve Saat", "Alarm, zamanlayıcı, kronometre", false),
        new HazirUygulama("Microsoft.WindowsFeedbackHub", "Geri Bildirim Merkezi", "Microsoft'a geri bildirim gönderme", true),
        new HazirUygulama("Microsoft.WindowsMaps", "Haritalar", "Windows Haritalar", false),
        new HazirUygulama("Microsoft.WindowsSoundRecorder", "Ses Kaydedici", "Basit ses kaydı", false),
        new HazirUygulama("Microsoft.XboxApp", "Xbox Console Companion", "Eski Xbox uygulaması (yenisi Xbox; o listede yok)", true),
        new HazirUygulama("Microsoft.ZuneVideo", "Filmler ve TV", "Satın alınmış filmleri oynatma", false),
        new HazirUygulama("MicrosoftCorporationII.MicrosoftFamily", "Aile Güvenliği", "Çocuk hesabı denetimi", false),
        new HazirUygulama("MicrosoftCorporationII.QuickAssist", "Hızlı Yardım", "Uzaktan yardım aracı", false),
        new HazirUygulama("MicrosoftTeams", "Microsoft Teams (eski)", "Eski kişisel Teams", true),
        new HazirUygulama("MSTeams", "Microsoft Teams", "Yeni Teams (iş / okul / kişisel)", false),
        new HazirUygulama("ACGMediaPlayer", "ACG Media Player", "Önceden kurulu medya oynatıcı", true),
        new HazirUygulama("ActiproSoftwareLLC", "Actipro Software", "Üreticinin eklediği yazılım parçası", true),
        new HazirUygulama("AdobeSystemsIncorporated.AdobePhotoshopExpress", "Adobe Photoshop Express", "Basit fotoğraf düzenleyici", false),
        new HazirUygulama("Amazon.com.Amazon", "Amazon", "Alışveriş uygulaması", true),
        new HazirUygulama("AmazonVideo.PrimeVideo", "Prime Video", "Amazon dizi / film", false),
        new HazirUygulama("Asphalt8Airborne", "Asphalt 8", "Yarış oyunu (reklam olarak gelir)", true),
        new HazirUygulama("AutodeskSketchBook", "Autodesk SketchBook", "Çizim uygulaması", true),
        new HazirUygulama("CaesarsSlotsFreeCasino", "Caesars Slots", "Kumar makinesi oyunu", true),
        new HazirUygulama("COOKINGFEVER", "Cooking Fever", "Restoran oyunu", true),
        new HazirUygulama("CyberLinkMediaSuiteEssentials", "CyberLink Media Suite", "Üreticinin eklediği medya paketi", true),
        new HazirUygulama("DisneyMagicKingdoms", "Disney Magic Kingdoms", "Tema parkı oyunu", true),
        new HazirUygulama("Disney.37853FC22B2CE", "Disney+", "Disney dizi / film", false),
        new HazirUygulama("DrawboardPDF", "Drawboard PDF", "PDF üzerinde not alma", true),
        new HazirUygulama("Duolingo-LearnLanguagesforFree", "Duolingo", "Dil öğrenme", false),
        new HazirUygulama("FACEBOOK.FACEBOOK", "Facebook", "Facebook uygulaması", true),
        new HazirUygulama("FarmVille2CountryEscape", "FarmVille 2", "Çiftlik oyunu", true),
        new HazirUygulama("Flipboard", "Flipboard", "Dergi tarzı haber uygulaması", true),
        new HazirUygulama("HiddenCity", "Hidden City", "Gizli nesne oyunu", true),
        new HazirUygulama("HULULLC.HULUPLUS", "Hulu", "Dizi / film (Türkiye'de yok)", true),
        new HazirUygulama("iHeartRadio", "iHeartRadio", "İnternet radyosu", true),
        new HazirUygulama("Facebook.Instagram", "Instagram", "Instagram uygulaması", false),
        new HazirUygulama("king.com.BubbleWitch3Saga", "Bubble Witch 3", "King bulmaca oyunu (reklam olarak gelir)", true),
        new HazirUygulama("king.com.CandyCrushSaga", "Candy Crush Saga", "King bulmaca oyunu (reklam olarak gelir)", true),
        new HazirUygulama("king.com.CandyCrushSodaSaga", "Candy Crush Soda", "King bulmaca oyunu (reklam olarak gelir)", true),
        new HazirUygulama("LinkedInforWindows", "LinkedIn", "LinkedIn uygulaması", true),
        new HazirUygulama("MarchofEmpires", "March of Empires", "Strateji oyunu", true),
        new HazirUygulama("4DF9E0F8.Netflix", "Netflix", "Netflix uygulaması", false),
        new HazirUygulama("NYTCrossword", "NYT Crossword", "Bulmaca", true),
        new HazirUygulama("OneCalendar", "One Calendar", "Takvim uygulaması", true),
        new HazirUygulama("PandoraMediaInc", "Pandora", "Müzik (Türkiye'de yok)", true),
        new HazirUygulama("PhototasticCollage", "Phototastic Collage", "Fotoğraf kolajı", true),
        new HazirUygulama("PicsArt-PhotoStudio", "PicsArt", "Fotoğraf düzenleme", true),
        new HazirUygulama("PolarrPhotoEditorAcademicEdition", "Polarr", "Fotoğraf düzenleme", true),
        new HazirUygulama("flaregamesGmbH.RoyalRevolt", "Royal Revolt", "Strateji oyunu", true),
        new HazirUygulama("Sidia.LiveWallpaper", "Live Wallpaper", "Hareketli duvar kâğıdı", true),
        new HazirUygulama("SlingTV", "Sling TV", "Canlı TV (Türkiye'de yok)", true),
        new HazirUygulama("SpotifyAB.SpotifyMusic", "Spotify", "Müzik uygulaması", false),
        new HazirUygulama("BytedancePte.Ltd.TikTok", "TikTok", "TikTok uygulaması", false),
        new HazirUygulama("TuneInRadio", "TuneIn Radio", "İnternet radyosu", true),
        new HazirUygulama("WinZipUniversal", "WinZip", "Sıkıştırma (deneme sürümü)", true)
    };

    const string PaketYolu = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    static readonly Regex PaketAdi = new Regex(@"^[A-Za-z0-9][A-Za-z0-9.\-]*$");

    // Bu kullanıcıda kurulu olanlar: uygulama → bulunan paket adları (sürüm ve mimari olmadan). Kayıt defterinden, hızlı.
    public static Dictionary<HazirUygulama, List<string>> Kurulu()
    {
        var sonuc = new Dictionary<HazirUygulama, List<string>>();
        var adlar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(PaketYolu))
                if (k != null)
                    foreach (var tam in k.GetSubKeyNames())
                    {
                        int alt = tam.IndexOf('_');
                        if (alt > 0) adlar.Add(tam.Substring(0, alt));
                    }
        }
        catch { }
        foreach (var u in Liste)
        {
            var bulunan = adlar.Where(a => a.IndexOf(u.Kimlik, StringComparison.OrdinalIgnoreCase) >= 0 && PaketAdi.IsMatch(a)).ToList();
            if (bulunan.Count > 0) sonuc[u] = bulunan;
        }
        return sonuc;
    }

    // Paketleri bu kullanıcı için kaldırır (PowerShell Remove-AppxPackage, yönetici gerekmez). Kaldırılamayanların adı döner.
    public static List<string> Kaldir(List<string> paketler)
    {
        var temiz = paketler.Where(p => PaketAdi.IsMatch(p)).Distinct().ToList();
        if (temiz.Count == 0) return new List<string>();
        string liste = string.Join(",", temiz.Select(p => "'" + p + "'"));
        var bilgi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"foreach ($a in @(" + liste + ")) { " +
            "Get-AppxPackage -Name $a | Remove-AppxPackage -ErrorAction SilentlyContinue }\"")
        { UseShellExecute = false, CreateNoWindow = true };
        try { using (var p = Process.Start(bilgi)) p.WaitForExit(300000); } catch { }
        var kalan = new HashSet<string>(Kurulu().SelectMany(kv => kv.Value), StringComparer.OrdinalIgnoreCase);
        return temiz.Where(kalan.Contains).ToList();
    }
}
