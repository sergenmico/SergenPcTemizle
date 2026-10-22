// Sürüm numarası TEK yerde: Metin. Değişince exe'nin dosya sürümü de (aşağıdaki öznitelikler) onunla değişir.
// HER YENİ SÜRÜMDE Metin'i ve Hakkinda.Ozet'i güncelle.

using System;
using System.Reflection;

[assembly: AssemblyVersion(Surum.Metin + ".0")]
[assembly: AssemblyFileVersion(Surum.Metin + ".0")]
[assembly: AssemblyInformationalVersion(Surum.Metin)]
[assembly: AssemblyTitle("Sergen PC Temizle")]
[assembly: AssemblyProduct("Sergen PC Temizle")]
[assembly: AssemblyCompany("Sergen")]
[assembly: AssemblyCopyright("© 2026 Sergen · MIT lisansı")]

static class Surum
{
    public const string Metin = "1.0.0";

    public static readonly Version Simdiki = new Version(Metin + ".0");

    // "v1.2.3" / "1.2.3" / "1.2.3.0" → dört parçalı sürüm (Version karşılaştırmasında 1.2.3 ile 1.2.3.0 eşit olsun).
    public static Version Coz(string metin)
    {
        if (string.IsNullOrEmpty(metin)) return null;
        metin = metin.Trim().TrimStart('v', 'V');
        Version v;
        if (!Version.TryParse(metin, out v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }
}
