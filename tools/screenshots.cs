// Genera las capturas del README (docs/screenshots) con datos de ejemplo y el mismo estilo que dibuja la app.
// Uso: compilar con csc y ejecutar con la carpeta de salida.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;

static class Screenshots
{
    const float S = 2f; // se dibuja al doble para que se vea nítido
    static readonly Color Warn = Color.FromArgb(239, 159, 39), High = Color.FromArgb(226, 75, 74), Good = Color.FromArgb(99, 153, 34),
                          Blue = Color.FromArgb(55, 138, 221), Clay = Color.FromArgb(217, 119, 87);
    const string GStop = "", GOpen = "", GJoin = "", GFolder = "", GRefresh = "",
                 GChat = "", GList = "", GEdit = "", GCal = "";

    static Font F(string[] names, float px)
    {
        foreach (var n in names) { var f = new Font(n, px * S, GraphicsUnit.Pixel); if (f.Name == n) return f; f.Dispose(); }
        return new Font("Segoe UI", px * S, GraphicsUnit.Pixel);
    }
    static Font Text(float px) { return F(new[] { "Segoe UI Variable Text", "Segoe UI" }, px); }
    static Font Bold(float px) { return F(new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" }, px); }
    static Font Icons(float px) { return F(new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }, px); }

    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "screenshots";
        Directory.CreateDirectory(dir);
        Taskbar(true).Save(Path.Combine(dir, "taskbar-claro.png"));
        Taskbar(false).Save(Path.Combine(dir, "taskbar-oscuro.png"));
        Save(Path.Combine(dir, "panel-cpu.png"), 320, CpuPanel());
        Save(Path.Combine(dir, "panel-claude.png"), 320, ClaudePanel());
        Save(Path.Combine(dir, "panel-jira.png"), 480, JiraPanel());
        Save(Path.Combine(dir, "panel-calendario.png"), 320, CalendarPanel());
        Save(Path.Combine(dir, "panel-repositorios.png"), 320, ReposPanel());
        Console.WriteLine("Capturas en " + Path.GetFullPath(dir));
    }

    // ---------- Taskbar ----------
    static Bitmap Taskbar(bool light)
    {
        int w = (int)(360 * S), h = (int)(48 * S);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        Color bg = light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(28, 28, 28), fg = light ? Color.Black : Color.White;
        using (var g = Graphics.FromImage(bmp))
        {
            Prepare(g);
            g.Clear(bg);
            using (var p = new Pen(light ? Color.FromArgb(220, 220, 220) : Color.FromArgb(50, 50, 50), S)) g.DrawLine(p, 0, 0, w, 0);
            float x = 12 * S, top = 4 * S, box = 40 * S;
            Ring(g, x, top, 23, "23", fg, fg); x += 32 * S;
            Ring(g, x, top, 76, "76", Warn, fg); x += 32 * S;
            using (var p = new Pen(Color.FromArgb(46, fg), S)) g.DrawLine(p, x + 4.5f * S, h * 0.3f, x + 4.5f * S, h * 0.7f);
            x += 9 * S;
            Glyph(g, x, top, GChat, fg, Clay, bg); x += 24 * S;
            Glyph(g, x, top, GList, fg, Blue, bg); x += 24 * S;
            Glyph(g, x, top, GEdit, fg, Warn, bg); x += 24 * S;
            // Calendario con hover, como cuando se pasa el mouse.
            string label = "Daily NEO · 8 min";
            float lw;
            using (var f = Text(12)) lw = g.MeasureString(label, f, PointF.Empty, StringFormat.GenericTypographic).Width;
            var hov = new RectangleF(x, top, 4 * S + 16 * S + 6 * S + lw + 6 * S, box);
            using (var path = Round(hov, 4 * S))
            {
                using (var b = new SolidBrush(light ? Color.FromArgb(180, 255, 255, 255) : Color.FromArgb(22, 255, 255, 255))) g.FillPath(b, path);
                using (var p = new Pen(light ? Color.FromArgb(14, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255), S)) g.DrawPath(p, path);
            }
            DrawGlyph(g, new RectangleF(x + 4 * S, top + 12 * S, 16 * S, 16 * S), GCal, fg);
            using (var f = Text(12)) using (var b = new SolidBrush(fg))
                g.DrawString(label, f, b, new RectangleF(x + 26 * S, top, lw + 4 * S, box),
                    new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center });
        }
        return bmp;
    }

    static void Ring(Graphics g, float x, float top, float value, string text, Color arc, Color fg)
    {
        float d = 24 * S, stroke = 2 * S, cx = x + 4 * S, cy = top + 8 * S;
        var c = new RectangleF(cx + stroke / 2, cy + stroke / 2, d - stroke, d - stroke);
        using (var p = new Pen(Color.FromArgb(55, fg), stroke)) g.DrawEllipse(p, c);
        using (var p = new Pen(arc, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(p, c, -90, 360 * value / 100);
        using (var f = Bold(11)) using (var b = new SolidBrush(fg))
            g.DrawString(text, f, b, new RectangleF(cx, cy + 0.5f * S, d, d), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
    }

    static void Glyph(Graphics g, float x, float top, string glyph, Color fg, Color badge, Color bg)
    {
        var icon = new RectangleF(x + 4 * S, top + 12 * S, 16 * S, 16 * S);
        DrawGlyph(g, icon, glyph, fg);
        float d = 8 * S, ring = 1.5f * S, cx = icon.Right - S, cy = icon.Bottom - S;
        using (var b = new SolidBrush(bg)) g.FillEllipse(b, cx - d / 2 - ring, cy - d / 2 - ring, d + 2 * ring, d + 2 * ring);
        using (var b = new SolidBrush(badge)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
    }

    static void DrawGlyph(Graphics g, RectangleF box, string glyph, Color color)
    {
        using (var f = Icons(16)) using (var b = new SolidBrush(color))
            g.DrawString(glyph, f, b, RectangleF.Inflate(box, 4 * S, 3 * S), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
    }

    // ---------- Tarjetas ----------
    class Line
    {
        public string Kind, Left, Right;
        public Color? RightColor, LeftColor;
        public string[] Buttons = new string[0];
        public bool[] Enabled;
        public float[] Data;
    }

    static Line Title(string t, params string[] buttons) { return new Line { Kind = "title", Left = t, Buttons = buttons }; }
    static Line Row(string l, string r = null, Color? rc = null, params string[] buttons) { return new Line { Kind = "row", Left = l, Right = r, RightColor = rc, Buttons = buttons }; }
    static Line Header(string t) { return new Line { Kind = "header", Left = t }; }
    static Line Wrap(string t) { return new Line { Kind = "wrap", Left = t }; }
    static Line Spark(float[] d) { return new Line { Kind = "spark", Data = d }; }

    static List<Line> CpuPanel()
    {
        var rnd = new Random(7);
        var data = Enumerable.Range(0, 61).Select(i => (float)(22 + 14 * Math.Sin(i / 6.0) + rnd.Next(-6, 7))).ToArray();
        var off = new Line { Kind = "row", Left = "svchost", Right = "0.6%", Buttons = new[] { GStop }, Enabled = new[] { false } };
        return new List<Line>
        {
            Title("CPU 23%"), Spark(data),
            Row("chrome", "8.4%", null, GStop), Row("Code", "5.1%", null, GStop), Row("MSBuild", "3.2%", null, GStop),
            Row("Teams", "1.9%", null, GStop), off,
            new Line { Kind = "row", Left = "explorer", Right = "0.3%", Buttons = new[] { GStop }, Enabled = new[] { false } }
        };
    }

    static List<Line> ClaudePanel()
    {
        return new List<Line>
        {
            Title("4 sesiones de Claude"),
            Row("Refactor de facturación", "Te pregunta", Clay), Wrap("¿Agrego pruebas para las notas de crédito?"),
            Row("Migración de reportes", "Pide permiso", Clay), Wrap("Bash · git push origin feature/reportes"),
            Row("Revisión del PR 482", "Terminó", Good),
            Row("Script de respaldo", "Trabajando", Blue)
        };
    }

    static List<Line> JiraPanel()
    {
        return new List<Line>
        {
            Title("6 tickets abiertos", GRefresh),
            Header("Por hacer · 2"),
            Row("APP-1042  Error al exportar facturas a PDF", null, null, GOpen),
            Row("APP-1057  Validar el número de cliente en el formulario", null, null, GOpen),
            Header("En curso · 3"),
            Row("APP-0987  Nuevo reporte de cartera por vencer", null, null, GOpen),
            Row("APP-1003  Integración con la pasarela de pagos", null, null, GOpen),
            Row("APP-1021  Mejorar el tiempo de carga del tablero", null, null, GOpen),
            Header("En revisión · 1"),
            Row("APP-0954  Ajustes de accesibilidad en el login", null, null, GOpen)
        };
    }

    static List<Line> CalendarPanel()
    {
        return new List<Line>
        {
            Title("Daily del equipo", GJoin, GRefresh),
            Row("Horario", "10:30 – 10:45"), Row("Termina en", "9 min", Blue),
            Header("Resto del día"),
            Row("14:00  Revisión de sprint", "en 3 h 20 min", null, GJoin),
            Row("16:30  1:1 con producto", "en 5 h 50 min", null, GJoin)
        };
    }

    static List<Line> ReposPanel()
    {
        return new List<Line>
        {
            Title("Repositorios"),
            Row("api-facturacion · feature/pdf", "3 cambios", Warn, GFolder),
            Row("web-clientes · develop", "1 cambio", Warn, GFolder),
            Row("infraestructura · main", "limpio", null, GFolder)
        };
    }

    static void Save(string file, float width, List<Line> lines)
    {
        foreach (var light in new[] { true, false })
            using (var bmp = Panel(width, lines, light))
                bmp.Save(light ? file : file.Replace(".png", "-oscuro.png"));
    }

    static Bitmap Panel(float width, List<Line> lines, bool light)
    {
        float pad = 14 * S, w = width * S, inner = w - 2 * pad;
        Color bg = light ? Color.FromArgb(250, 250, 250) : Color.FromArgb(36, 36, 36), border = light ? Color.FromArgb(218, 218, 218) : Color.FromArgb(66, 66, 66),
              fg = light ? Color.FromArgb(26, 26, 26) : Color.FromArgb(238, 238, 238), muted = Color.FromArgb(150, fg);
        Func<Line, float> height = l => l.Kind == "title" ? 26 * S : l.Kind == "header" ? 29 * S : l.Kind == "spark" ? 46 * S : l.Kind == "wrap" ? 19 * S : 24 * S;
        float h = 2 * pad + lines.Sum(height);
        var bmp = new Bitmap((int)w, (int)h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        using (Font title = Bold(14), body = Text(12), head = Bold(12), pill = Bold(11), icons = Icons(12))
        {
            Prepare(g);
            g.Clear(Color.Transparent);
            using (var path = Round(new RectangleF(0.5f, 0.5f, w - 1, h - 1), 9 * S))
            {
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                using (var p = new Pen(border, S)) g.DrawPath(p, path);
            }
            var ellipsis = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
            var center = new StringFormat(ellipsis) { Alignment = StringAlignment.Center };
            float y = pad;
            foreach (var l in lines)
            {
                float lh = height(l), right = pad + inner;
                for (int i = 0; i < l.Buttons.Length; i++)
                {
                    bool on = l.Enabled == null || l.Enabled[i];
                    var br = new RectangleF(right - 26 * S, y + (lh - 22 * S) / 2, 26 * S, 22 * S);
                    using (var path = Round(br, 5 * S)) using (var b = new SolidBrush(Color.FromArgb(on ? 16 : 8, fg))) g.FillPath(b, path);
                    using (var b = new SolidBrush(on ? fg : Color.FromArgb(70, fg))) g.DrawString(l.Buttons[i], icons, b, new RectangleF(br.X, br.Y + S, br.Width, br.Height), center);
                    right = br.Left - 4 * S;
                }
                if (l.Buttons.Length > 0) right -= 4 * S;
                switch (l.Kind)
                {
                    case "title":
                        using (var b = new SolidBrush(fg)) g.DrawString(l.Left, title, b, new RectangleF(pad, y, right - pad, lh), ellipsis);
                        break;
                    case "header":
                        using (var b = new SolidBrush(muted)) g.DrawString(l.Left, head, b, new RectangleF(pad, y + 10 * S, inner, 19 * S), ellipsis);
                        break;
                    case "wrap":
                        using (var b = new SolidBrush(muted)) g.DrawString(l.Left, body, b, new RectangleF(pad, y, inner, lh), ellipsis);
                        break;
                    case "spark":
                        DrawSpark(g, l.Data, new RectangleF(pad, y + 4 * S, inner, 36 * S), muted);
                        break;
                    default:
                        float rw = 0;
                        if (l.Right != null)
                        {
                            Color tone = l.RightColor ?? fg;
                            Color pbg = l.RightColor.HasValue ? Color.FromArgb(light ? 38 : 56, tone) : Color.FromArgb(light ? 13 : 24, fg);
                            Color ptx = !l.RightColor.HasValue ? Color.FromArgb(175, fg) : Mix(tone, light ? Color.Black : Color.White, light ? 0.3f : 0.35f);
                            rw = g.MeasureString(l.Right, pill, PointF.Empty, StringFormat.GenericTypographic).Width + 14 * S;
                            var pr = new RectangleF(right - rw, y + (lh - 18 * S) / 2, rw, 18 * S);
                            using (var path = Round(pr, 9 * S)) using (var b = new SolidBrush(pbg)) g.FillPath(b, path);
                            using (var b = new SolidBrush(ptx))
                                g.DrawString(l.Right, pill, b, new RectangleF(pr.X, pr.Y + 0.5f * S, pr.Width, pr.Height),
                                    new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                        }
                        using (var b = new SolidBrush(fg)) g.DrawString(l.Left, body, b, new RectangleF(pad, y, right - pad - rw - 8 * S, lh), ellipsis);
                        break;
                }
                y += lh;
            }
        }
        return bmp;
    }

    static void DrawSpark(Graphics g, float[] data, RectangleF r, Color color)
    {
        float step = r.Width / (data.Length - 1);
        var pts = data.Select((v, i) => new PointF(r.Left + i * step, r.Bottom - r.Height * v / 100f)).ToArray();
        var area = new List<PointF>(pts) { new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom) };
        using (var b = new SolidBrush(Color.FromArgb(color.A * 36 / 255, color))) g.FillPolygon(b, area.ToArray());
        using (var p = new Pen(color, 1.4f * S) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts);
    }

    static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    static Color Mix(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    static GraphicsPath Round(RectangleF r, float radius)
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
