// Logo PNG'sinden saydam, kırpılmış logo ve çok boyutlu .ico üretir.
// Kullanım: LogoHazirla.exe <kaynak.png> <cikti-klasoru>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

static class LogoHazirla
{
    static int Main(string[] args)
    {
        var kaynak = new Bitmap(args[0]);
        string cikti = args[1];
        Directory.CreateDirectory(cikti);

        // 1) Koyu arka planı saydam yap: en parlak kanala göre yumuşak geçiş.
        //    Fırçanın koyu lacivert sapı mavi kanalı yüksek olduğu için korunur.
        int w = kaynak.Width, h = kaynak.Height;
        var veri = Oku(kaynak);
        int minx = w, miny = h, maxx = 0, maxy = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int b = veri[i], g = veri[i + 1], r = veri[i + 2], a = veri[i + 3];
                int enParlak = Math.Max(r, Math.Max(g, b));
                // Arka plan ve hale en çok ~35, fırçanın lacivert sapı en az ~70: geçiş 38–70 arası.
                double oran = Math.Max(0, Math.Min(1, (enParlak - 38) / 32.0));
                int yeniA = (int)Math.Round(a * oran);
                veri[i + 3] = (byte)yeniA;
                if (yeniA > 40)
                {
                    if (x < minx) minx = x; if (x > maxx) maxx = x;
                    if (y < miny) miny = y; if (y > maxy) maxy = y;
                }
            }
        var saydam = Yaz(veri, w, h);

        // 2) Kare kırp: içerik kutusu + %6 pay, ortalanmış.
        int kenar = Math.Max(maxx - minx, maxy - miny);
        int pay = (int)(kenar * 0.06);
        kenar += 2 * pay;
        int cx = (minx + maxx) / 2, cy = (miny + maxy) / 2;
        var kare = new Bitmap(kenar, kenar, PixelFormat.Format32bppArgb);
        using (var gr = Graphics.FromImage(kare))
        {
            gr.Clear(Color.Transparent);
            gr.DrawImage(saydam, new Rectangle(0, 0, kenar, kenar), new Rectangle(cx - kenar / 2, cy - kenar / 2, kenar, kenar), GraphicsUnit.Pixel);
        }
        Console.WriteLine("icerik kutusu: {0},{1} - {2},{3}; kare kenar {4}", minx, miny, maxx, maxy, kenar);

        Olcekle(kare, 512).Save(Path.Combine(cikti, "logo-512.png"), ImageFormat.Png);
        Olcekle(kare, 256).Save(Path.Combine(cikti, "logo-256.png"), ImageFormat.Png);

        // 3) .ico: her boyut PNG olarak gömülü (Windows Vista ve sonrası destekler). Büyük .ico exe'nin kendi simgesi
        //    (Gezgin 128/256 ister); küçük .ico ve 96 px PNG pencere içinde kullanılır (exe'ye ikinci kez büyük boyut girmesin).
        var boyutlar = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        var pngler = new Dictionary<int, byte[]>();
        foreach (var s in boyutlar)
            using (var ms = new MemoryStream()) { Olcekle(kare, s).Save(ms, ImageFormat.Png); pngler[s] = ms.ToArray(); }
        IcoYaz(Path.Combine(cikti, "SergenPcTemizle.ico"), boyutlar, pngler);
        IcoYaz(Path.Combine(cikti, "SergenPcTemizle-kucuk.ico"), new[] { 16, 20, 24, 32, 40, 48, 64, 96 }, pngler);
        File.WriteAllBytes(Path.Combine(cikti, "logo-kucuk.png"), pngler[96]);

        // 4) Kontrol görüntüleri: açık ve koyu zemin üstünde farklı boyutlar.
        foreach (var zemin in new[] { Color.White, Color.FromArgb(18, 20, 24), Color.FromArgb(32, 96, 160) })
        {
            var onizleme = new Bitmap(16 + 24 + 32 + 48 + 64 + 128 + 256 + 8 * 9, 272);
            using (var gr = Graphics.FromImage(onizleme))
            {
                gr.Clear(zemin);
                int x = 8;
                foreach (var s in new[] { 16, 24, 32, 48, 64, 128, 256 }) { gr.DrawImage(Olcekle(kare, s), x, 8 + (256 - s) / 2, s, s); x += s + 8; }
            }
            onizleme.Save(Path.Combine(cikti, "onizleme-" + zemin.R + ".png"), ImageFormat.Png);
        }
        return 0;
    }

    static void IcoYaz(string yol, int[] boyutlar, Dictionary<int, byte[]> pngler)
    {
        using (var fs = File.Create(yol))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)boyutlar.Length);
            int ofset = 6 + 16 * boyutlar.Length;
            foreach (int s in boyutlar)
            {
                bw.Write((byte)(s >= 256 ? 0 : s)); bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
                bw.Write(pngler[s].Length); bw.Write(ofset);
                ofset += pngler[s].Length;
            }
            foreach (int s in boyutlar) bw.Write(pngler[s]);
        }
    }

    static Bitmap Olcekle(Bitmap b, int s)
    {
        var o = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(o))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            using (var ia = new ImageAttributes()) { ia.SetWrapMode(WrapMode.TileFlipXY); g.DrawImage(b, new Rectangle(0, 0, s, s), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia); }
        }
        return o;
    }

    static byte[] Oku(Bitmap b)
    {
        var r = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var d = new byte[b.Width * b.Height * 4];
        for (int y = 0; y < b.Height; y++) Marshal.Copy(r.Scan0 + y * r.Stride, d, y * b.Width * 4, b.Width * 4);
        b.UnlockBits(r);
        return d;
    }

    static Bitmap Yaz(byte[] d, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var r = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(d, y * w * 4, r.Scan0 + y * r.Stride, w * 4);
        b.UnlockBits(r);
        return b;
    }
}
