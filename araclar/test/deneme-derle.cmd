@echo off
rem Deneme derlemesi: /define:DENEME ile RAM bosaltma hicbir surece dokunmaz, olanlar %TEMP%\sergen-deneme.log dosyasina yazilir.
rem Kullanim: deneme-derle.cmd <cikti-klasoru>   (gercek bilgisayarda kisayolu sinamak icin; paylasilmaz)
cd /d "%~dp0..\.."
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /platform:anycpu /define:DENEME /out:"%~1\SergenPcTemizle.exe" /win32icon:kaynaklar\SergenPcTemizle.ico /resource:kaynaklar\SergenPcTemizle-kucuk.ico,SergenPcTemizle.ico /resource:kaynaklar\logo-kucuk.png,logo.png /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Web.Extensions.dll Surum.cs SergenPcTemizle.cs Bekci.cs Cokme.cs Baslangic.cs Saglik.cs Kisayol.cs WindowsAyar.cs WindowsSekmesi.cs AgGuc.cs Turbo.cs TurboSekmesi.cs Hakkinda.cs Guncelleme.cs
