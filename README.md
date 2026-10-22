# Sergen PC Temizle

**Ücretsiz ve açık kaynak PC temizleme ve hızlandırma programı.** Windows 10 / 11 için sade, koyu temalı, Türkçe bir sistem izleyici ve temizleyici: RAM'i boşaltır (RAM temizleyici), gereksiz dosyaları temizler, "bellek yetersiz" hatalarının sebebini bulur ve oyunlarda takılmayı azaltıp FPS'i artıran ayarlarla bilgisayarını oyun için hazırlar.

Tanıtım sayfası: https://sergenmico.github.io/SergenPcTemizle/

**[En son sürümü indir](https://github.com/sergenmico/SergenPcTemizle/releases/latest)** · Windows 10 / 11 · Kurulum gerektirmez (tek exe) · Türkçe · Ücretsiz ve açık kaynak ([MIT](LICENSE))

## Neler yapabilir

- **Sistem** — İşlemci, ekran kartı, RAM, ekran kartı belleği, sanal bellek ve ısıyı canlı gösterir. Altta kapatılabilir programları (işlemci, bellek, ekran kartı kullanımıyla) listeler; seçtiklerini kapatır ve RAM'i boşaltır. Sistem parçaları, sürücüler, antivirüs ve benzeri programlar listelenmez.
- **Disk** — Geçici dosyaları, önbellekleri ve Windows kalıntılarını temizler (normal ve derin temizlik). Depolama analizi hangi klasörün ne kadar yer tuttuğunu gösterir.
- **Sürücü** — NVIDIA ekran kartı sürücüsünün yeni sürümünü denetler; istersen indirir, imzasını kontrol eder ve geri yükleme noktası oluşturarak kurar.
- **Sağlık** — Bellek bekçisi (sürekli büyüyen programları yakalar), son 7 günün çökme geçmişi, masaüstüne kaydedilen destek raporu, genel kısayol (varsayılan Ctrl+Alt+O ile RAM boşaltma), bildirim sesi ve oyun modu.
- **Başlangıç** — Windows ile birlikte açılan programları listeler; açıp kapatmanı sağlar (Görev Yöneticisi ile aynı ayar).
- **Windows** — Geri alınabilir Windows ayarları, hazır uygulamaları kaldırma, DNS seçimi ve ağ bağlantı testi, Wi-Fi güç tasarrufu, güç modu ve güç planı.
- **Turbo** — Oyun ayarları (işlemci, güç, ekran kartı, bellek ve disk), oyun açıkken güç modunu yükselten oyun profili ve donanım kontrolü.
- **Sanal bellek ayarı** — Bilgisayarının RAM'ine göre önerilen boyutu uygular; önceki ayara dönebilirsin.
- **Otomatik / Full optimize** — Tek tıkla temizlik ve hızlandırma. Otomatik optimize sormadan yalnızca risksiz işleri yapar; Full optimize seçenekleri sana bırakır.
- **Tepside bekçi** — Pencereyi kapatınca saatin yanında çalışmaya devam eder; sanal bellek ya da RAM dolarsa haber verir.
- **Otomatik güncelleme** — Yeni sürümü kendi içinde bulur, imzasını doğrular ve sen onaylayınca kurar. Elle denetlemek için sağ üstteki **"v… · Hakkında"** yazısına tıkla → **Şimdi denetle**, ya da saatin yanındaki simge → **Güncellemeleri denetle**.

## İndirme

1. [Releases](https://github.com/sergenmico/SergenPcTemizle/releases) sayfasından en son sürümün **SergenPcTemizle.exe** dosyasını indir.
2. Masaüstü ya da Belgeler gibi bir klasöre koy ve çalıştır. Kurulum yok; tek bir exe.

Windows 10 / 11 gerekir. İlk açılışta Windows SmartScreen "bilinmeyen yayıncı" uyarısı gösterebilir: **Ek bilgi → Yine de çalıştır**. (Program ücretli bir kod imzası sertifikası kullanmıyor; kaynak kodu açık ve güncellemeler aşağıda anlatıldığı gibi ayrıca imzalanıyor.)

Bazı işler (sanal bellek ayarı, Windows kalıntılarını temizleme, sürücü kurulumu, makine geneli Windows ve ağ ayarları, DNS değiştirme) Windows'ta yönetici izni ister; yalnızca o iş için ve o an Windows'un izin penceresiyle sorulur, program normalde yönetici olmadan çalışır.

## Güvenli mi?

- **Açık kaynak.** Bütün kod bu depoda; ne yaptığını okuyabilir, kendin derleyebilirsin.
- **Kendiliğinden önemli bir şey yapmaz.** Program kapatma, disk temizliği, Windows ayarları, sanal bellek, DNS ve sürücü kurulumu yalnızca sen ilgili düğmeye basınca çalışır (silme ve optimize işleri önce uyarı penceresiyle sorar). Sorusuz çalışan tek şey RAM boşaltma ile "Otomatik optimize"dir; bunlar program kapatmaz, ayar değiştirmez, yalnızca RAM'i boşaltır ve yönetici izni gerektirmeyen geçici dosya/önbellek temizliği yapar. Turbo'daki "Oyun profili"ni açarsan (varsayılan kapalı) oyun süresince Windows güç modunu "En iyi performans" yapar ve oyun bitince eski hâline döndürür.
- **Değişiklikler geri alınabilir.** Windows, Turbo, DNS ve sanal bellek ayarlarını değiştirmeden önce eski değerler `%APPDATA%\SergenPcTemizle` klasörüne yedeklenir; ilgili sekmelerden eski hâle dönebilirsin. NVIDIA sürücüsü kurulmadan önce geri yükleme noktası oluşturulur.
- **İnternete az çıkar.** Yalnızca: güncelleme denetimi (GitHub), NVIDIA sürücü kontrolü ve indirmesi (NVIDIA'nın sitesi) ve ağ bağlantı testi (ağ geçidi, 1.1.1.1 ve 8.8.8.8'e ping). Güncelleme denetimini Hakkında penceresinden kapatabilirsin.
- **Kişisel veri toplamaz, göndermez.** Telemetri yok. Destek raporu yalnızca masaüstüne bir dosya olarak yazılır; kimseye gönderilmez, kullanıcı ve bilgisayar adı raporda gizlenir.
- **Güncellemeler imzalı.** Aşağıya bak.

## Otomatik güncelleme ve imza doğrulaması

Program en çok günde bir kez GitHub'daki son sürüme bakar (Hakkında penceresinden kapatılabilir). Yeni sürüm varsa sana sorar; sen "Güncelle" dersen dosyayı program içinde indirir, tarayıcı açılmaz.

Her sürümün `SergenPcTemizle.exe` dosyası yazarın **gizli anahtarıyla imzalanır** (`SergenPcTemizle.exe.imza`, RSA-3072 / SHA-256). Programın içinde yalnızca karşılığı olan **açık anahtar** gömülüdür. İndirilen dosyanın imzası tutmazsa güncelleme kurulmaz; ayrıca sürüm numarası etiketle uyuşmuyorsa ya da şimdikinden eski ise de kurulmaz. Gizli anahtar bu depoda bulunmaz.

## Derleme

Ek araç gerekmez: Windows'un hazır gelen .NET Framework 4.x C# derleyicisi kullanılır.

```
derle.cmd
```

`SergenPcTemizle.exe` aynı klasörde oluşur. (Kodu C# 5 sözdizimiyle yazılmıştır.) Sürüm numarası `Surum.cs` içindedir.

## Teşekkür

Windows sekmesindeki ayarların kaynağı [Win11Debloat](https://github.com/Raphire/Win11Debloat) (MIT lisanslı, Raphire).

## Lisans

[MIT](LICENSE) © 2026 Sergen
