# Yeni sürümü derler, imzalar ve GitHub Release olarak yayınlar.
# Kullanım: powershell -File araclar\yayinla.ps1 -Notlar "Sürüm notları metni"   (ya da bir .md/.txt dosyasının yolu)
param([Parameter(Mandatory = $true)][string]$Notlar)
$ErrorActionPreference = 'Stop'

Write-Host 'HATIRLATMA: Hakkında özetini (Hakkinda.cs) ve Surum.Metin''i (Surum.cs) güncelledin mi?' -ForegroundColor Yellow

$kok = Split-Path -Parent $PSScriptRoot
$depo = 'sergenmico/SergenPcTemizle'

# Sürüm: Surum.cs -> Metin
$kaynak = Get-Content -LiteralPath (Join-Path $kok 'Surum.cs') -Raw -Encoding UTF8
if ($kaynak -notmatch 'Metin\s*=\s*"(\d+\.\d+\.\d+)"') { throw 'Surum.cs içinde sürüm bulunamadı.' }
$surum = $Matches[1]
$etiket = "v$surum"
Write-Host "Sürüm: $etiket"

# Derle
$exe = Join-Path $kok 'SergenPcTemizle.exe'
if (Test-Path -LiteralPath $exe) { Remove-Item -LiteralPath $exe -Force }
& cmd /c "`"$(Join-Path $kok 'derle.cmd')`""
if (-not (Test-Path -LiteralPath $exe)) { throw 'Derleme başarısız: exe oluşmadı.' }
$dosyaSurumu = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if ($dosyaSurumu -ne "$surum.0") { throw "Exe sürümü ($dosyaSurumu) Surum.cs ($surum) ile uyuşmuyor." }

# İmzala
& (Join-Path $PSScriptRoot 'imzala.ps1') -Exe $exe
$imza = $exe + '.imza'
if (-not (Test-Path -LiteralPath $imza)) { throw 'İmza dosyası oluşmadı.' }

# Etiket zaten var mı?
$ErrorActionPreference = 'Continue'   # gh, sürüm yokken stderr'e yazar; hata sayılmasın
& gh release view $etiket --repo $depo 2>&1 | Out-Null
$varMi = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = 'Stop'
if ($varMi) { throw "$etiket sürümü GitHub'da zaten var. Surum.cs'te sürümü artır." }

# Sürüm notları: dosya yolu ya da düz metin
$notDosyasi = Join-Path ([IO.Path]::GetTempPath()) ("sergen-notlar-" + [Guid]::NewGuid().ToString('N') + '.md')
try {
    if (Test-Path -LiteralPath $Notlar -PathType Leaf) { $metin = Get-Content -LiteralPath $Notlar -Raw -Encoding UTF8 } else { $metin = $Notlar }
    [IO.File]::WriteAllText($notDosyasi, $metin, (New-Object System.Text.UTF8Encoding($false)))
    & gh release create $etiket $exe $imza --repo $depo --title $etiket --notes-file $notDosyasi
    if ($LASTEXITCODE -ne 0) { throw 'gh release create başarısız.' }
} finally {
    if (Test-Path -LiteralPath $notDosyasi) { Remove-Item -LiteralPath $notDosyasi -Force }
}
Write-Host "Yayınlandı: $etiket"
