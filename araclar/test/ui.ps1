param([string[]]$Adimlar)
# Adımlar: tikla|Metin, bekle|sn, foto|ad, dialogfoto|Başlık|ad, dialogtikla|Başlık|Düğme, tus|Metin|vk
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class UI { [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Ri,B; }
public delegate bool CB(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, CB cb, IntPtr l);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
public static IntPtr Sinif(IntPtr p, string c) { IntPtr s = IntPtr.Zero; EnumChildWindows(p, (h,l) => { var b = new StringBuilder(256); GetClassName(h,b,256); if (b.ToString().Contains(c) && IsWindowVisible(h)) { s=h; return false; } return true; }, IntPtr.Zero); return s; }
public static IntPtr Bul(IntPtr p, string t) { IntPtr s = IntPtr.Zero; EnumChildWindows(p, (h,l) => { var b = new StringBuilder(256); GetWindowText(h,b,256); if (b.ToString()==t) { s=h; return false; } return true; }, IntPtr.Zero); return s; } }
'@
$dir = Join-Path $PSScriptRoot "goruntuler"; New-Item -ItemType Directory -Force $dir | Out-Null
function Shot($h, $name) { $r = New-Object UI+R; [UI]::GetWindowRect($h, [ref]$r) | Out-Null
 $bmp = New-Object System.Drawing.Bitmap ($r.Ri-$r.L), ($r.B-$r.T); $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); [UI]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc)
 $bmp.Save("$dir\$name.png") }
$p = Start-Process ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\SergenPcTemizle.exe"))) -PassThru
Start-Sleep -Seconds 5; $p.Refresh(); $ana = $p.MainWindowHandle
foreach ($a in $Adimlar) {
  $x = $a.Split('|')
  switch ($x[0]) {
    'tikla' { $h = [UI]::Bul($ana, $x[1]); "tikla $($x[1]) -> $h"; [UI]::PostMessage($h, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
    'kaydir' { $h = [UI]::Sinif($ana, "SysListView32"); "kaydir -> $h"; [UI]::PostMessage($h, 0x115, [IntPtr]7, [IntPtr]::Zero) | Out-Null }
    'formfoto' { $d = [UI]::FindWindow([NullString]::Value, $x[1]); "form $($x[1]) -> $d"; if ($d -ne [IntPtr]::Zero) { Shot $d $x[2] } }
    'formtikla' { $d = [UI]::FindWindow([NullString]::Value, $x[1]); $b = [UI]::Bul($d, $x[2]); "formtikla $($x[2]) -> $b"; [UI]::PostMessage($b, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
    'bekle' { Start-Sleep -Seconds ([int]$x[1]) }
    'foto' { $p.Refresh(); "yanit veriyor: $($p.Responding)"; [UI]::ShowWindow($ana, 9) | Out-Null; Start-Sleep -Milliseconds 500; Shot $ana $x[1] }
    'dialogfoto' { $d = [UI]::FindWindow("#32770", $x[1]); "dialog $($x[1]) -> $d"; if ($d -ne [IntPtr]::Zero) { Shot $d $x[2] } }
    'dialogtikla' { $d = [UI]::FindWindow("#32770", $x[1]); $b = [UI]::Bul($d, $x[2]); "dialogtikla $($x[2]) -> $b"; [UI]::PostMessage($b, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
  }
}
Stop-Process -Id $p.Id

