# Bir exe'yi RSA-SHA256 ile imzalar; <exe>.imza (Base64, tek satır) yazar, sonra açık anahtarla doğrular.
# Gizli anahtar kullanıcının Belgeler\SergenPcTemizle-imza klasöründedir ve depoya ASLA girmez; içeriği ekrana yazılmaz.
# Kullanım: powershell -File araclar\imzala.ps1 -Exe SergenPcTemizle.exe
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Anahtar = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'SergenPcTemizle-imza\gizli-anahtar.xml')
)
$ErrorActionPreference = 'Stop'

# Programdaki (Guncelleme.cs) açık anahtarla aynı olmalı.
$acikAnahtar = '<RSAKeyValue><Modulus>38DdNomK9GkU8XufaHwwvWHg3FrcZs1upBYXzfbHOIP5Fylho44sMytZQcb2ERFINxFUBHKpYoidyLXRo2FF3Z4CMz7Ptie8G8cqsnbzlo0aZ5rRHI7l9ugCpjHuWT2kKQZ4uxzXFIKCqwNk/KBGYJuRfr/9yPhm1jlG2UnYxbNekaOcNi9MzwBGb/uTVOy78/GFs0eadQq2yKvTdXbWmaApV8STIuhClJqB5sLlPGYKm7ZLptVBulELixHHaRasmFJdKQxg5dfxF8okNH/5qZF7KjP54Ytp3oQJbD825egTUXWD1BS/y3pCVmH1PoZtNygRR13CNAYIkFgAVCFkO8cfJU9DTs4OlFA/jgUhXC8FH83ESXGaDFMJZEdFIau72NJBEGCVN0wEFo7TzSb71idzelHO5oH9xjBnDN1aHEwcN5hj7mAdLpomcU5KqcbG4cHFMzJseWS23ekk2KA5+Z+oVC+DhkJ3tIg1GT0E8gAbYMOJ3sDmTRL5359RZl35</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>'

if (-not (Test-Path -LiteralPath $Exe)) { throw "Exe bulunamadı: $Exe" }
if (-not (Test-Path -LiteralPath $Anahtar)) {
    throw "Gizli anahtar dosyası yok: $Anahtar`nAnahtar dosyası olmadan imzalanamaz (ve gizli anahtar depoya konmaz)."
}
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$baytlar = [IO.File]::ReadAllBytes($Exe)

$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
try {
    $rsa.FromXmlString([IO.File]::ReadAllText($Anahtar))
    $imza = $rsa.SignData($baytlar, 'SHA256')
} finally { $rsa.Dispose() }

$dogrulayici = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$dogrulayici.PersistKeyInCsp = $false
try {
    $dogrulayici.FromXmlString($acikAnahtar)
    $tamam = $dogrulayici.VerifyData($baytlar, 'SHA256', $imza)
} finally { $dogrulayici.Dispose() }
if (-not $tamam) { throw 'İmza açık anahtarla doğrulanamadı: gizli anahtar bu programdaki açık anahtarla eşleşmiyor.' }

$imzaDosyasi = $Exe + '.imza'
[IO.File]::WriteAllText($imzaDosyasi, [Convert]::ToBase64String($imza), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "İmzalandı ve doğrulandı: $imzaDosyasi"
