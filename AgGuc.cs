// Windows sekmesinin "Ağ" ve "Güç" bölümleri: DNS seçimi, Wi-Fi güç tasarrufu, ağ testi, DNS önbelleği / ağ sıfırlama,
// güç planı ve güç modu. İnternet hızına göre (100 / 1000 Mbps) TCP ayarları bilerek yok: güncel Windows pencere boyutunu
// kendisi ayarlar, elle yapılan "hızlandırma" ayarları etkisiz ya da zararlı.
// Yönetici gerekenler (DNS değiştirme, ağ sıfırlama) `--ag-dns` / `--ag-sifirla` yardımcılarıyla; diğerleri bu süreçte.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

static class GucAyarlari
{
    public static readonly Guid DengeliPlan = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
    // Windows 11 güç modu (Ayarlar → Sistem → Güç): yalnızca Dengeli planda geçerli.
    public static readonly Guid[] Modlar =
    {
        new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"), Guid.Empty, new Guid("ded574b5-45a0-4f42-8737-46345c09c238")
    };
    public static readonly string[] ModAdlari = { "En iyi pil verimliliği", "Dengeli", "En iyi performans" };

    static readonly Guid WifiAlt = new Guid("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
    static readonly Guid WifiAyar = new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a");
    public static readonly string[] WifiAdlari = { "En yüksek performans", "Düşük tasarruf", "Orta tasarruf", "En yüksek tasarruf" };

    const string ModAnahtari = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    public class Plan { public Guid Kimlik; public string Ad; public override string ToString() { return Ad; } }

    public static bool PilVar { get { return SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery; } }
    public static bool Prizde { get { return !PilVar || SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline; } }

    public static Guid EtkinPlan()
    {
        IntPtr p;
        if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return Guid.Empty;
        try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
        finally { LocalFree(p); }
    }

    public static List<Plan> Planlar()
    {
        var l = new List<Plan>();
        for (uint i = 0; i < 64; i++)
        {
            var tampon = new byte[16];
            uint boyut = 16;
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /* ACCESS_SCHEME */, i, tampon, ref boyut) != 0) break;
            var g = new Guid(tampon);
            l.Add(new Plan { Kimlik = g, Ad = PlanAdi(g) });
        }
        return l;
    }

    public static string PlanAdi(Guid g)
    {
        try
        {
            uint boyut = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref boyut);
            if (boyut == 0) return g.ToString();
            var b = new byte[boyut];
            if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, b, ref boyut) != 0) return g.ToString();
            return Encoding.Unicode.GetString(b).TrimEnd('\0');
        }
        catch { return g.ToString(); }
    }

    public static bool PlanSec(Guid g) { return PowerSetActiveScheme(IntPtr.Zero, ref g) == 0; }

    // Prizdeki / pildeki güç modu (Windows'un kaydı). Okunamazsa -1.
    public static int Mod(bool prizde)
    {
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(ModAnahtari))
            {
                var d = k == null ? null : k.GetValue(prizde ? "ActiveOverlayAcPowerScheme" : "ActiveOverlayDcPowerScheme") as string;
                Guid g = string.IsNullOrEmpty(d) ? Guid.Empty : new Guid(d);
                return Array.IndexOf(Modlar, g);
            }
        }
        catch { return -1; }
    }

    // Windows Ayarlar'daki gibi: seçilen mod şu anki güç kaynağına (prizde / pilde) uygulanır.
    public static bool ModSec(int mod)
    {
        if (mod < 0 || mod >= Modlar.Length) return false;
        return PowerSetActiveOverlayScheme(Modlar[mod]) == 0;
    }

    // Etkin plandaki Wi-Fi güç tasarrufu (0 = en yüksek performans … 3 = en yüksek tasarruf). Okunamazsa -1.
    public static int Wifi(bool prizde)
    {
        Guid plan = EtkinPlan(), alt = WifiAlt, ayar = WifiAyar;
        uint d;
        uint s = prizde ? PowerReadACValueIndex(IntPtr.Zero, ref plan, ref alt, ref ayar, out d)
                        : PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref alt, ref ayar, out d);
        return s == 0 && d < WifiAdlari.Length ? (int)d : -1;
    }

    public static bool WifiYaz(int prizde, int pilde)
    {
        Guid plan = EtkinPlan(), alt = WifiAlt, ayar = WifiAyar;
        if (prizde >= 0 && PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref alt, ref ayar, (uint)prizde) != 0) return false;
        if (pilde >= 0 && PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref alt, ref ayar, (uint)pilde) != 0) return false;
        return PowerSetActiveScheme(IntPtr.Zero, ref plan) == 0;   // değişiklik hemen geçerli olsun
    }

    // Turbo sekmesi: etkin plandaki herhangi bir ayarın prizdeki değeri (0 = başarılı).
    public static uint ACOku(Guid plan, Guid alt, Guid ayar, out uint d)
    {
        return PowerReadACValueIndex(IntPtr.Zero, ref plan, ref alt, ref ayar, out d);
    }

    // Prizdeki değeri yazar; plan etkinse hemen geçerli olsun diye yeniden seçilir (etkin olmayan plan seçilmez).
    public static bool ACYaz(Guid plan, Guid alt, Guid ayar, uint d)
    {
        if (PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref alt, ref ayar, d) != 0) return false;
        return plan != EtkinPlan() || PowerSetActiveScheme(IntPtr.Zero, ref plan) == 0;
    }

    // Planı çoğaltır (Nihai Performans için). 0 = başarılı; değilse Windows hata kodu.
    public static uint PlanKopyala(Guid kaynak, out Guid yeni)
    {
        yeni = Guid.Empty;
        IntPtr p;
        uint s = PowerDuplicateScheme(IntPtr.Zero, ref kaynak, out p);
        if (s != 0) return s;
        try { yeni = (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
        finally { LocalFree(p); }
        return 0;
    }

    public static uint PlanSil(Guid g) { return PowerDeleteScheme(IntPtr.Zero, ref g); }

    [DllImport("powrprof.dll")] static extern uint PowerDuplicateScheme(IntPtr kok, ref Guid kaynak, out IntPtr yeni);
    [DllImport("powrprof.dll")] static extern uint PowerDeleteScheme(IntPtr kok, ref Guid plan);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr kok, out IntPtr plan);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr kok, ref Guid plan);
    [DllImport("powrprof.dll")] static extern uint PowerEnumerate(IntPtr kok, IntPtr plan, IntPtr alt, uint tur, uint sira, byte[] tampon, ref uint boyut);
    [DllImport("powrprof.dll")] static extern uint PowerReadFriendlyName(IntPtr kok, ref Guid plan, IntPtr alt, IntPtr ayar, byte[] tampon, ref uint boyut);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid mod);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr kok, ref Guid plan, ref Guid alt, ref Guid ayar, out uint d);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr kok, ref Guid plan, ref Guid alt, ref Guid ayar, out uint d);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr kok, ref Guid plan, ref Guid alt, ref Guid ayar, uint d);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr kok, ref Guid plan, ref Guid alt, ref Guid ayar, uint d);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
}

static class AgAyarlari
{
    public class DnsSaglayici
    {
        public string Kimlik, Ad;
        public string[] Adresler;   // IPv4 + IPv6; boş = otomatik (DHCP)
        public override string ToString() { return Ad; }
    }

    public static readonly DnsSaglayici[] Saglayicilar =
    {
        new DnsSaglayici { Kimlik = "otomatik", Ad = "Otomatik (internet sağlayıcısı)", Adresler = new string[0] },
        new DnsSaglayici { Kimlik = "cloudflare", Ad = "Cloudflare (1.1.1.1) · hızlı", Adresler = new[] { "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001" } },
        new DnsSaglayici { Kimlik = "google", Ad = "Google (8.8.8.8)", Adresler = new[] { "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844" } },
        new DnsSaglayici { Kimlik = "quad9", Ad = "Quad9 (9.9.9.9) · zararlı siteleri engeller", Adresler = new[] { "9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9" } }
    };

    public class Bagdastirici
    {
        public int Indeks;
        public string Kimlik, Ad, Aciklama;
        public bool Kablosuz, Bagli;
        public string[] Elle;   // elle girilmiş DNS (IPv4 + IPv6); boş = otomatik
    }

    // Sanal / VPN / tünel bağdaştırıcıları DNS değişikliğine dahil edilmez (VPN ve DPI araçları kendi DNS'ini yönetir).
    static readonly Regex Sanal = new Regex(@"virtual|vpn|tap-|tap |tun|wireguard|hyper-v|vmware|virtualbox|loopback|bluetooth|miniport|npcap|zerotier|tailscale|radmin|hamachi|openvpn|wintun|vethernet|kernel debug",
                                            RegexOptions.IgnoreCase);

    public static List<Bagdastirici> Bagdastiricilar()
    {
        var l = new List<Bagdastirici>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (n.NetworkInterfaceType != NetworkInterfaceType.Ethernet && n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211
                    && n.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet) continue;
                if (Sanal.IsMatch(n.Description) || Sanal.IsMatch(n.Name)) continue;
                var p = n.GetIPProperties();
                IPv4InterfaceProperties v4 = null;
                try { v4 = p.GetIPv4Properties(); } catch { }
                if (v4 == null) continue;
                l.Add(new Bagdastirici
                {
                    Indeks = v4.Index, Kimlik = n.Id, Ad = n.Name, Aciklama = n.Description,
                    Kablosuz = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    Bagli = n.OperationalStatus == OperationalStatus.Up,
                    Elle = ElleDns(n.Id)
                });
            }
            catch { }
        }
        return l;
    }

    static string[] ElleDns(string kimlik)
    {
        var l = new List<string>();
        foreach (var kok in new[] { @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\", @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\" })
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(kok + kimlik))
                {
                    var d = k == null ? null : k.GetValue("NameServer") as string;
                    // Windows bu değerin sonunda NUL karakterleri bırakabiliyor: ayırıcı sayılıp atılır.
                    if (!string.IsNullOrWhiteSpace(d))
                        l.AddRange(d.Split(new[] { ',', ' ', '\0', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0));
                }
            }
            catch { }
        }
        return l.ToArray();
    }

    // Bağlı bağdaştırıcıların hepsi aynı sağlayıcıyı kullanıyorsa onun Saglayicilar'daki sırası (otomatik = 0), değilse -1.
    public static int SuankiSaglayici(List<Bagdastirici> l)
    {
        var bagli = l.Where(b => b.Bagli).ToList();
        if (bagli.Count == 0) return -1;
        var siralar = bagli.Select(b =>
        {
            var v4 = b.Elle.Where(a => a.Contains('.')).ToList();
            if (b.Elle.Length == 0) return 0;
            for (int i = 1; i < Saglayicilar.Length; i++)
                if (v4.Count > 0 && v4.All(a => Saglayicilar[i].Adresler.Contains(a))) return i;
            return -1;
        }).Distinct().ToList();
        return siralar.Count == 1 ? siralar[0] : -1;
    }

    // Bağlı bağdaştırıcıların DNS'i hangi sağlayıcıya benziyor? (Hepsi aynıysa o; değilse "karışık".)
    public static string DurumMetni(List<Bagdastirici> l)
    {
        var bagli = l.Where(b => b.Bagli).ToList();
        if (bagli.Count == 0) return "Bağlı ağ bağdaştırıcısı yok.";
        var parca = new List<string>();
        foreach (var b in bagli)
        {
            string ne;
            if (b.Elle.Length == 0) ne = "otomatik";
            else
            {
                var s = Saglayicilar.FirstOrDefault(x => x.Adresler.Length > 0 && b.Elle.Where(a => a.Contains('.')).All(a => x.Adresler.Contains(a)));
                ne = s != null ? s.Ad.Split(' ')[0] : "elle: " + string.Join(", ", b.Elle.Where(a => a.Contains('.')).Take(3));
            }
            parca.Add(b.Ad + " → " + ne);
        }
        return "Şu an: " + string.Join(" · ", parca);
    }

    // ---- DNS değiştirme ----

    static readonly string YedekDosyasi = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SergenPcTemizle", "dns-yedek.txt");

    public static bool YedekVar { get { return File.Exists(YedekDosyasi); } }

    // saglayici: Saglayicilar'dan biri ya da null = yedekteki önceki ayar. 0 = tamam, -2 = izin verilmedi, diğer = hata.
    public static int DnsUygula(DnsSaglayici saglayici, Func<string, int> yoneticiCalistir)
    {
        var l = Bagdastiricilar();
        var satirlar = new List<string>();
        if (saglayici == null)
        {
            if (!YedekVar) return 1;
            foreach (var y in File.ReadAllLines(YedekDosyasi, Encoding.UTF8))
            {
                var p = y.Split('\t');
                var b = p.Length == 2 ? l.FirstOrDefault(x => x.Kimlik.Equals(p[0], StringComparison.OrdinalIgnoreCase)) : null;
                if (b != null) satirlar.Add(b.Indeks + "\t" + p[1]);
            }
        }
        else
        {
            // İlk değişiklikten önce özgün ayar saklanır (sonraki değişiklikler yedeği ezmez).
            if (!YedekVar)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(YedekDosyasi));
                File.WriteAllLines(YedekDosyasi, l.Select(b => b.Kimlik + "\t" + string.Join(",", b.Elle)), new UTF8Encoding(false));
            }
            foreach (var b in l) satirlar.Add(b.Indeks + "\t" + string.Join(",", saglayici.Adresler));
        }
        if (satirlar.Count == 0) return 1;
        string dosya = Path.Combine(Path.GetTempPath(), "SergenPcTemizle-dns-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllLines(dosya, satirlar, new UTF8Encoding(false));
            int kod = yoneticiCalistir("--ag-dns \"" + dosya + "\"");
            if (kod == 0)
            {
                DnsOnbellegiTemizle();
                if (saglayici == null) try { File.Delete(YedekDosyasi); } catch { }
            }
            return kod;
        }
        finally { try { File.Delete(dosya); } catch { } }
    }

    // Yönetici olarak: exe --ag-dns <dosya>. Satır: "arabirim sırası \t adres,adres" (boş = otomatik). Adresler IP olarak
    // doğrulanır; PowerShell'e yalnızca sayı ve doğrulanmış IP gider. 0 = tamam, 1 = hata, 2 = geçersiz satır.
    public static int YoneticiDns(string[] args)
    {
        try
        {
            if (args.Length != 1 || !File.Exists(args[0])) return 1;
            var komut = new StringBuilder("$ErrorActionPreference='Stop'; ");
            int n = 0;
            foreach (var satir in File.ReadAllLines(args[0], Encoding.UTF8))
            {
                if (satir.Length == 0) continue;
                var p = satir.Split('\t');
                int indeks;
                if (p.Length != 2 || !int.TryParse(p[0], out indeks) || indeks <= 0) return 2;
                var adresler = p[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                IPAddress ip;
                if (adresler.Length > 8 || adresler.Any(a => !IPAddress.TryParse(a, out ip))) return 2;
                var ipler = adresler.Select(IPAddress.Parse).ToList();
                var v4 = ipler.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => "'" + a + "'").ToList();
                var v6 = ipler.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Select(a => "'" + a + "'").ToList();
                komut.Append("Set-DnsClientServerAddress -InterfaceIndex ").Append(indeks).Append(" -ResetServerAddresses; ");
                // IPv4 ve IPv6 ayrı yazılır: IPv6'sı kapalı bir kartta IPv6 adresi hata verir, IPv4 yine de yazılmış olmalı.
                if (v4.Count > 0)
                    komut.Append("Set-DnsClientServerAddress -InterfaceIndex ").Append(indeks)
                         .Append(" -ServerAddresses @(").Append(string.Join(",", v4)).Append("); ");
                if (v6.Count > 0)
                    komut.Append("try { Set-DnsClientServerAddress -InterfaceIndex ").Append(indeks)
                         .Append(" -ServerAddresses @(").Append(string.Join(",", v6)).Append(") } catch { }; ");
                n++;
            }
            if (n == 0) return 1;
            return PowerShell(komut.ToString());
        }
        catch { return 1; }
    }

    // ---- Önbellek / sıfırlama ----

    public static bool DnsOnbellegiTemizle() { try { return DnsFlushResolverCache(); } catch { return false; } }

    // Yönetici olarak: exe --ag-sifirla. Winsock ve TCP/IP yığınını varsayılana döndürür (yeniden başlatma gerekir).
    public static int YoneticiSifirla()
    {
        int a = Calistir("netsh.exe", "winsock reset");
        int b = Calistir("netsh.exe", "int ip reset");
        DnsOnbellegiTemizle();
        return a == 0 && b == 0 ? 0 : 1;
    }

    static int PowerShell(string komut)
    {
        return Calistir("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + komut.Replace("\"", "'") + "\"");
    }

    static int Calistir(string exe, string argumanlar)
    {
        try
        {
            using (var p = Process.Start(new ProcessStartInfo(exe, argumanlar) { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (!p.WaitForExit(120000)) return 1;
                return p.ExitCode;
            }
        }
        catch { return 1; }
    }

    [DllImport("dnsapi.dll")] static extern bool DnsFlushResolverCache();

    // ---- Ağ testi ----

    public class TestSonucu
    {
        public string Ad, Adres;
        public int Gonderilen, Kayip;
        public double Ortalama, Dalgalanma, EnCok;
        public double KayipYuzde { get { return Gonderilen == 0 ? 100 : 100.0 * Kayip / Gonderilen; } }
    }

    // Modeme (ağ geçidi) ve internete (Cloudflare, Google) aynı anda ~5 sn ping atar. Arka plan iş parçacığında çağrılır.
    public static List<TestSonucu> Test(int adet)
    {
        var hedefler = new List<KeyValuePair<string, string>>();
        string gecit = AgGecidi();
        if (gecit != null) hedefler.Add(new KeyValuePair<string, string>("Modem / ev ağı", gecit));
        hedefler.Add(new KeyValuePair<string, string>("İnternet (Cloudflare)", "1.1.1.1"));
        hedefler.Add(new KeyValuePair<string, string>("İnternet (Google)", "8.8.8.8"));
        var isler = hedefler.Select(h => Task.Run(() => Pingle(h.Key, h.Value, adet))).ToArray();
        Task.WaitAll(isler);
        return isler.Select(t => t.Result).ToList();
    }

    static TestSonucu Pingle(string ad, string adres, int adet)
    {
        var s = new TestSonucu { Ad = ad, Adres = adres };
        var sureler = new List<double>();
        using (var p = new Ping())
        {
            for (int i = 0; i < adet; i++)
            {
                s.Gonderilen++;
                try
                {
                    var c = p.Send(adres, 1000);
                    if (c.Status == IPStatus.Success) sureler.Add(c.RoundtripTime); else s.Kayip++;
                }
                catch { s.Kayip++; }
                System.Threading.Thread.Sleep(200);
            }
        }
        if (sureler.Count > 0)
        {
            s.Ortalama = sureler.Average();
            s.EnCok = sureler.Max();
            s.Dalgalanma = sureler.Count > 1 ? sureler.Zip(sureler.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
        }
        return s;
    }

    static string AgGecidi()
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (n.OperationalStatus != OperationalStatus.Up || Sanal.IsMatch(n.Description)) continue;
                var g = n.GetIPProperties().GatewayAddresses.FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork
                                                                                && !x.Address.Equals(IPAddress.Any));
                if (g != null) return g.Address.ToString();
            }
            catch { }
        }
        return null;
    }

    // Wi-Fi sinyal gücü (%), bağlı değilse -1. netsh çıktısı dile göre değişir; "%" ile biten tek sayı aranır.
    public static int WifiSinyali()
    {
        try
        {
            var bilgi = new ProcessStartInfo("netsh.exe", "wlan show interfaces")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage) };
            using (var p = Process.Start(bilgi))
            {
                string c = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                var m = Regex.Match(c, @":\s*(\d{1,3})\s*%");
                return m.Success ? int.Parse(m.Groups[1].Value) : -1;
            }
        }
        catch { return -1; }
    }

    // Sonuçların düz Türkçe yorumu: sorun evdeki ağda mı (Wi-Fi / modem), internet bağlantısında mı?
    public static string Yorum(List<TestSonucu> l, int sinyal)
    {
        var ev = l.FirstOrDefault(x => x.Ad.StartsWith("Modem"));
        var net = l.Where(x => x.Ad.StartsWith("İnternet")).ToList();
        bool netKotu = net.All(x => x.KayipYuzde >= 5 || x.Dalgalanma >= 20 || x.Ortalama >= 120);
        bool evKotu = ev != null && ev.KayipYuzde < 100 && (ev.KayipYuzde >= 5 || ev.Dalgalanma >= 10 || ev.Ortalama >= 20);
        string sinyalNotu = sinyal >= 0 && sinyal < 60 ? " Wi-Fi sinyali zayıf (%" + sinyal + "): modeme yaklaş ya da kablo kullan." : "";
        if (net.All(x => x.KayipYuzde >= 100)) return "İnternete hiç ulaşılamıyor. Modemi yeniden başlat; düzelmezse \"Ağı sıfırla\"yı dene." + sinyalNotu;
        if (evKotu) return "Sorun evdeki ağda (Wi-Fi ya da modem): bilgisayar ile modem arasında gecikme / kayıp var." + sinyalNotu;
        if (netKotu) return "Ev ağı temiz; sorun internet bağlantısında (internet sağlayıcısı). Sürüyorsa sağlayıcını ara." + sinyalNotu;
        return "Bağlantı sağlıklı: kayıp yok, gecikme ve dalgalanma düşük." + sinyalNotu;
    }
}
