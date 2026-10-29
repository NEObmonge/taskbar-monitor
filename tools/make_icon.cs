// Genera app.ico (ícono "Anillo") con todos los tamaños que usa Windows, en PNG dentro del .ico.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static void Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var images = new List<byte[]>();
        foreach (int n in sizes) images.Add(Render(n));
        if (args.Length > 1) File.WriteAllBytes(args[1], images[images.Count - 1]); // vista previa en 256
        using (var fs = File.Create(args[0]))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }

    static byte[] Render(int n)
    {
        float k = n / 64f;
        using (var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                using (var path = RoundRect(new RectangleF(0, 0, n, n), 14 * k))
                using (var b = new SolidBrush(Color.FromArgb(0x1f, 0x1f, 0x1f))) g.FillPath(b, path);

                float stroke = Math.Max(6 * k, 2f);
                var circle = new RectangleF(15 * k, 15 * k, 34 * k, 34 * k);
                using (var p = new Pen(Color.FromArgb(46, 255, 255, 255), stroke)) g.DrawEllipse(p, circle);
                using (var p = new Pen(Color.White, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, circle, -90, 270);
                float dot = Math.Max(3 * k, 1.3f);
                using (var b = new SolidBrush(Color.FromArgb(0xEF, 0x9F, 0x27))) g.FillEllipse(b, 15 * k - dot, 32 * k - dot, dot * 2, dot * 2);
            }
            using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
