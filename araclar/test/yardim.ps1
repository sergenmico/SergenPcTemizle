# SergenPcTemizle ekran testleri için yardımcılar. Kullanım: . .\yardim.ps1
Add-Type -AssemblyName System.Drawing
if (-not ("T" -as [type])) {
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class T {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Ri,B; }
  public delegate bool CB(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, CB cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(CB cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public static IntPtr Cocuk(IntPtr p, string metin) { IntPtr s = IntPtr.Zero; EnumChildWindows(p, (h,l) => { var b = new StringBuilder(512); GetWindowText(h,b,512); if (b.ToString()==metin) { s=h; return false; } return true; }, IntPtr.Zero); return s; }
  // Bir sürecin görünür üst düzey pencereleri: "tutamaç|başlık|sınıf"
  public static string[] Pencereler(int pid) { var l = new System.Collections.Generic.List<string>(); EnumWindows((h,x) => { int p; GetWindowThreadProcessId(h, out p); if (p==pid && IsWindowVisible(h)) { var b = new StringBuilder(256); GetWindowText(h,b,256); var c = new StringBuilder(256); GetClassName(h,c,256); l.Add(h.ToInt64()+"|"+b+"|"+c); } return true; }, IntPtr.Zero); return l.ToArray(); }
  public static IntPtr[] Sinif(IntPtr p, string c) { var l = new System.Collections.Generic.List<IntPtr>(); EnumChildWindows(p, (h,x) => { var b = new StringBuilder(256); GetClassName(h,b,256); if (b.ToString().Contains(c) && IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero); return l.ToArray(); }
  public static IntPtr AnaPencere(int pid) { IntPtr s = IntPtr.Zero; EnumWindows((h,x) => { int p; GetWindowThreadProcessId(h, out p); if (p==pid) { var b = new StringBuilder(256); GetWindowText(h,b,256); if (b.ToString().StartsWith("Sergen PC Temizle")) { s=h; return false; } } return true; }, IntPtr.Zero); return s; }
}
'@
}
$global:Exe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\SergenPcTemizle.exe"))
$global:Kaz = Join-Path $PSScriptRoot "goruntuler"
New-Item -ItemType Directory -Force $Kaz | Out-Null

function Foto([IntPtr]$h, [string]$ad) {
  $r = New-Object T+R; [T]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.Ri - $r.L; $y = $r.B - $r.T; if ($w -lt 50) { "foto $ad : pencere küçük/gizli ($w x $y)"; return }
  $bmp = New-Object System.Drawing.Bitmap $w, $y; $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  [T]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save("$Kaz\$ad.png"); "foto: $Kaz\$ad.png"
}
function Tikla([IntPtr]$ust, [string]$metin) { $h = [T]::Cocuk($ust, $metin); if ($h -eq [IntPtr]::Zero) { "BULUNAMADI: $metin" } else { [T]::PostMessage($h, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null } }
function Ana([int]$surecNo) { [T]::AnaPencere($surecNo) }
function Gorunur([IntPtr]$h) { [T]::IsWindowVisible($h) }
function Surecler { @(Get-Process SergenPcTemizle -ErrorAction SilentlyContinue) }
function HepsiniKapat { Stop-Process -Name SergenPcTemizle -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 600 }
Add-Type -Namespace B -Name W -MemberDefinition '[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);' -ErrorAction SilentlyContinue
function Bul([string]$baslik, [string]$sinif = $null) { if ($sinif) { [B.W]::FindWindow($sinif, $baslik) } else { [B.W]::FindWindow([NullString]::Value, $baslik) } }

function Tus([IntPtr]$h, [int]$vk) { [T]::PostMessage($h, 0x100, [IntPtr]$vk, [IntPtr]::Zero) | Out-Null; [T]::PostMessage($h, 0x101, [IntPtr]$vk, [IntPtr]::Zero) | Out-Null; Start-Sleep -Milliseconds 150 }
function Onay([string]$ad) { $k = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -ErrorAction SilentlyContinue; if ($k -and $k.$ad) { '0x{0:X2}' -f $k.$ad[0] } else { '(yok)' } }
function RunDeger([string]$ad) { $k = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue; if ($k) { $k.$ad } }

