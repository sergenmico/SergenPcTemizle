@echo off
rem SergenPcTemizle.exe'yi Windows'taki hazir .NET Framework derleyicisiyle (C# 5) uretir. Tek exe, on dort kaynak dosya.
rem Logo exe'ye gomulur: /win32icon (tum boyutlar) Gezgin ve gorev cubugu icin; /resource pencere, tepsi ve sekme seridi
rem icin yalnizca kucuk boyutlar (SergenPcTemizle-kucuk.ico 16-96 px, logo-kucuk.png 96 px): buyuk logo exe'ye iki kez girmesin.
rem Logoyu degistirmek: araclar\LogoHazirla.cs ile kaynaklar\logo-kaynak.png'den iki .ico ve logo-kucuk.png yeniden uretilir.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /out:SergenPcTemizle.exe /win32icon:kaynaklar\SergenPcTemizle.ico /resource:kaynaklar\SergenPcTemizle-kucuk.ico,SergenPcTemizle.ico /resource:kaynaklar\logo-kucuk.png,logo.png /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Web.Extensions.dll Surum.cs SergenPcTemizle.cs Bekci.cs Cokme.cs Baslangic.cs Saglik.cs Kisayol.cs WindowsAyar.cs WindowsSekmesi.cs AgGuc.cs Turbo.cs TurboSekmesi.cs Hakkinda.cs Guncelleme.cs
