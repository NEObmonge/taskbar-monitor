// TaskBar Monitor: CPU y RAM en anillos sobre el lado izquierdo de la barra de tareas, más indicadores que aparecen
// solo cuando hay algo que mostrar (compilación, sesiones de Claude, próxima reunión, Jira y repositorios con
// cambios). Al pasar el mouse sobre cada indicador se abre su panel, que se puede usar (detener procesos, abrir
// tickets, unirse a la reunión...). Un clic en un indicador hace su acción (CPU/RAM abren el Administrador de tareas,
// Claude abre la sesión...); doble clic o clic derecho abre los Ajustes, donde se configura todo.
//
// Compilar: .\build.ps1 (ver README.md).
//
// Otros modos:
//   TaskbarMonitor.exe --claude-hook              lo ejecuta Claude Code en cada evento (lee el JSON por stdin)
//   TaskbarMonitor.exe --hooks-install [archivo]  conecta Claude Code (por defecto ~/.claude/settings.json)
//   TaskbarMonitor.exe --hooks-uninstall [archivo]
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

// Nombre que muestra Windows (Administrador de tareas, aplicaciones de inicio).
[assembly: System.Reflection.AssemblyTitle("TaskBar Monitor")]
[assembly: System.Reflection.AssemblyProduct("TaskBar Monitor")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    public static void LogError(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(Paths.Data, "errores.log"), DateTime.Now.ToString("s") + " " + ex + "\r\n\r\n"); }
        catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        if (mode == "--claude-hook")
        {
            try { ClaudeHook.Run(); } catch { }
            return;
        }
        if (mode == "--uninstall")
        {
            Application.EnableVisualStyles();
            Installer.Uninstall();
            return;
        }
        if (mode == "--hooks-install" || mode == "--hooks-uninstall")
        {
            string file = args.Length > 1 ? args[1] : ClaudeHooks.SettingsPath;
            if (mode == "--hooks-install") ClaudeHooks.Install(file); else ClaudeHooks.Uninstall(file);
            return;
        }
        bool created;
        using (var mutex = new Mutex(true, "TaskbarMonitor_SingleInstance", out created))
        {
            if (!created)
            {
                // Ya está corriendo: abrir el .exe otra vez abre sus ajustes.
                AllowSetForegroundWindow(-1);
                try { using (var ev = EventWaitHandle.OpenExisting(MonitorForm.OpenSettingsEvent)) ev.Set(); } catch { }
                return;
            }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            // Un error puntual (un dato raro, una ventana que se cierra a destiempo) queda anotado y la app sigue.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => LogError(e.Exception);
            Directory.CreateDirectory(Paths.Data);
            Application.Run(new MonitorForm());
        }
    }
}

#region Utilidades
static class Paths
{
    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarMonitor");
    public static readonly string Claude = Path.Combine(Data, "claude");
    public static readonly string Config = Path.Combine(Data, "config.ini");
}

// config.ini por usuario. Los secretos (token de Jira, dirección del calendario) se guardan cifrados con DPAPI:
// solo este usuario de Windows en este equipo puede leerlos.
static class Config
{
    static readonly object gate = new object();
    static Dictionary<string, string> values;

    public static bool Exists { get { return File.Exists(Paths.Config); } }

    public static string Get(string key, string def)
    {
        lock (gate)
        {
            Load();
            string v;
            return values.TryGetValue(key, out v) && v != "" ? v : def;
        }
    }

    public static bool Flag(string key) { return Get(key, "1") == "1"; }

    public static int GetInt(string key, int def)
    {
        int v;
        return int.TryParse(Get(key, ""), out v) ? v : def;
    }

    public static string GetSecret(string key)
    {
        string v = Get(key, "");
        if (!v.StartsWith("dpapi:")) return v;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v.Substring(6)), null, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }

    public static void SetSecret(string key, string plain)
    {
        Set(key, plain == "" ? "" :
            "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser)));
    }

    public static void Set(string key, string value)
    {
        lock (gate)
        {
            Load();
            values[key] = value;
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllLines(Paths.Config, values.Select(p => p.Key + "=" + p.Value));
        }
    }

    static void Load()
    {
        if (values != null) return;
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(Paths.Config)) return;
        foreach (var line in File.ReadAllLines(Paths.Config))
        {
            int i = line.IndexOf('=');
            if (i > 0) values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
        }
    }
}

static class Json
{
    public static string Str(Dictionary<string, object> d, string key)
    {
        object v;
        if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
    }

    public static IEnumerable<object> List(Dictionary<string, object> d, string key)
    {
        object v;
        var e = d != null && d.TryGetValue(key, out v) ? v as IEnumerable : null;
        return e == null || e is string ? Enumerable.Empty<object>() : e.Cast<object>();
    }

    // JavaScriptSerializer escribe todo en una línea; esto lo deja legible para quien abra settings.json.
    public static string Pretty(string json)
    {
        var sb = new StringBuilder();
        int indent = 0;
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; sb.Append(c); break;
                case '{':
                case '[':
                    if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c).Append(json[++i]); break; }
                    sb.Append(c).Append('\n').Append(' ', ++indent * 2);
                    break;
                case '}':
                case ']': sb.Append('\n').Append(' ', --indent * 2).Append(c); break;
                case ',': sb.Append(c).Append('\n').Append(' ', indent * 2); break;
                case ':': sb.Append(": "); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

static class Fmt
{
    public static string Cut(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return (int)t.TotalSeconds + " s";
        if (t.TotalHours < 1) return (int)t.TotalMinutes + " min " + t.Seconds.ToString("00") + " s";
        return (int)t.TotalHours + " h " + t.Minutes + " min";
    }

    public static string Until(TimeSpan t)
    {
        int min = (int)Math.Ceiling(t.TotalMinutes);
        if (min < 60) return min + " min";
        return (min / 60 + " h " + (min % 60 > 0 ? min % 60 + " min" : "")).Trim();
    }

    public static string Ago(DateTime utc)
    {
        var t = DateTime.UtcNow - utc;
        if (t.TotalMinutes < 1) return "hace un momento";
        if (t.TotalHours < 1) return "hace " + (int)t.TotalMinutes + " min";
        return "hace " + (int)t.TotalHours + " h";
    }

    public static string Memory(long bytes)
    {
        double gb = bytes / 1073741824.0;
        return gb >= 1 ? gb.ToString("0.0") + " GB" : (bytes / 1048576.0).ToString("0") + " MB";
    }
}

static class Anim
{
    public static float Clamp(float t) { return t < 0 ? 0 : t > 1 ? 1 : t; }
    public static float EaseOut(float t) { t = Clamp(t); return 1 - (1 - t) * (1 - t) * (1 - t); }
    public static float EaseInOut(float t) { t = Clamp(t); return t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2; }

    // Acerca un valor a su objetivo con un movimiento suave que no depende de los cuadros por segundo.
    public static float Approach(float current, float target, float dtMs, float tauMs)
    {
        float next = current + (target - current) * (1 - (float)Math.Exp(-dtMs / tauMs));
        return Math.Abs(target - next) < 0.002f ? target : next;
    }

    public static Color Fade(Color c, float alpha) { return Color.FromArgb((int)(c.A * Clamp(alpha)), c); }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Clamp(t);
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uCallbackMessage;
        public uint uEdge; public RECT rc; public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    public static Rectangle TaskbarRect(out uint edge)
    {
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
        SHAppBarMessage(5, ref abd); // ABM_GETTASKBARPOS
        edge = abd.uEdge;
        return Rectangle.FromLTRB(abd.rc.Left, abd.rc.Top, abd.rc.Right, abd.rc.Bottom);
    }

    public static void KeepOnTop(IntPtr h)
    {
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // Aplica un bitmap con canal alfa a una ventana WS_EX_LAYERED: transparencia real por píxel.
    public static void ApplyBitmap(IntPtr hwnd, Bitmap bmp, int x, int y, byte opacity)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE(bmp.Width, bmp.Height);
            var src = new POINT(0, 0);
            var dst = new POINT(x, y);
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // El color de acento que la persona eligió en Windows; en tema oscuro, más claro como hace Windows 11.
    public static Color AccentColor(bool light)
    {
        var c = Color.FromArgb(0, 103, 192);
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
            {
                object v = k != null ? k.GetValue("AccentColor") : null;
                if (v is int) { int abgr = (int)v; c = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF); }
            }
        }
        catch { }
        return light ? c : Anim.Mix(c, Color.White, 0.35f);
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
#endregion

#region Claude Code
class ClaudeSession
{
    public string Id, Title, State, Detail, Cwd;
    public DateTime Time; // UTC

    public bool NeedsYou { get { return State == "pregunta" || State == "permiso"; } }

    public string StateLabel
    {
        get
        {
            switch (State)
            {
                case "pregunta": return "Te pregunta";
                case "permiso": return "Pide permiso";
                case "terminó": return "Terminó";
                case "error": return "Error";
                default: return "Trabajando";
            }
        }
    }

    public int Priority
    {
        get { return NeedsYou ? 0 : State == "error" ? 1 : State == "terminó" ? 2 : 3; }
    }

    public string Describe()
    {
        switch (State)
        {
            case "pregunta": return "Te hizo una pregunta" + (Detail != "" ? ": " + Fmt.Cut(Detail, 140) : "");
            case "permiso": return "Pide permiso" + (Detail != "" ? ": " + Fmt.Cut(Detail, 140) : "");
            case "terminó": return "Terminó y espera tu mensaje";
            case "error": return "Se detuvo por un error" + (Detail != "" ? ": " + Fmt.Cut(Detail, 140) : "");
            default: return "Trabajando…";
        }
    }

    public static List<ClaudeSession> LoadAll()
    {
        var list = new List<ClaudeSession>();
        if (!Directory.Exists(Paths.Claude)) return list;
        var js = new JavaScriptSerializer();
        foreach (var f in Directory.GetFiles(Paths.Claude, "*.json"))
        {
            try
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(f);
                if (age.TotalDays > 3) { File.Delete(f); continue; }
                if (age.TotalHours > 12) continue;
                var d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                var s = new ClaudeSession
                {
                    Id = Path.GetFileNameWithoutExtension(f),
                    Title = Json.Str(d, "title"),
                    State = Json.Str(d, "state"),
                    Detail = Json.Str(d, "detail"),
                    Cwd = Json.Str(d, "cwd"),
                    Time = DateTime.Parse(Json.Str(d, "time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()
                };
                if (s.State == "inicio") continue; // sesión abierta sin actividad todavía
                if (s.Title == "") s.Title = s.Cwd != "" ? Path.GetFileName(s.Cwd.TrimEnd('\\', '/')) : "Sesión";
                list.Add(s);
            }
            catch { }
        }
        return list.OrderBy(s => s.Priority).ThenByDescending(s => s.Time).ToList();
    }
}

// Lo ejecuta Claude Code en cada evento configurado; deja el estado de la sesión en un .json que lee el widget.
static class ClaudeHook
{
    public static void Run()
    {
        string input;
        using (var r = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))) input = r.ReadToEnd();
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var d = js.Deserialize<Dictionary<string, object>>(input);
        string ev = Json.Str(d, "hook_event_name"), sid = Json.Str(d, "session_id");
        if (sid == "" || sid.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
        Directory.CreateDirectory(Paths.Claude);
        string file = Path.Combine(Paths.Claude, sid + ".json");
        if (ev == "SessionEnd") { if (File.Exists(file)) File.Delete(file); return; }

        var prev = new Dictionary<string, object>();
        if (File.Exists(file)) try { prev = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(file)); } catch { }
        string prevState = Json.Str(prev, "state");
        var toolInput = Json.Obj(d, "tool_input");

        string state = null, detail = "";
        switch (ev)
        {
            case "SessionStart":
                if (prevState == "") state = "inicio";
                break;
            case "UserPromptSubmit":
                state = "trabajando";
                break;
            case "PreToolUse":
                if (Json.Str(d, "tool_name") == "AskUserQuestion") { state = "pregunta"; detail = FirstQuestion(toolInput); }
                break;
            case "PermissionRequest":
                state = "permiso"; detail = ToolSummary(Json.Str(d, "tool_name"), toolInput);
                break;
            case "PostToolUse":
                if (prevState == "pregunta" || prevState == "permiso") state = "trabajando";
                break;
            case "Notification":
                string type = Json.Str(d, "notification_type");
                if (type == "permission_prompt" && prevState != "permiso") { state = "permiso"; detail = Json.Str(d, "message"); }
                else if (type == "elicitation_dialog") { state = "pregunta"; detail = Json.Str(d, "message"); }
                break;
            case "Stop":
                state = "terminó";
                break;
            case "StopFailure":
                state = "error"; detail = Json.Str(d, "error");
                break;
        }
        if (state == null) return;

        var o = new Dictionary<string, object>();
        o["state"] = state;
        o["detail"] = detail;
        o["cwd"] = Json.Str(d, "cwd") != "" ? Json.Str(d, "cwd") : Json.Str(prev, "cwd");
        o["title"] = ReadTitle(Json.Str(d, "transcript_path"), Json.Str(prev, "title"), js);
        o["time"] = DateTime.UtcNow.ToString("o");
        File.WriteAllText(file, js.Serialize(o), new UTF8Encoding(false));
    }

    static string FirstQuestion(Dictionary<string, object> input)
    {
        foreach (var q in Json.List(input, "questions"))
        {
            var qd = q as Dictionary<string, object>;
            if (qd != null) return Json.Str(qd, "question");
        }
        return "";
    }

    static string ToolSummary(string tool, Dictionary<string, object> input)
    {
        foreach (var key in new[] { "command", "file_path", "url", "pattern", "description" })
        {
            string v = Json.Str(input, key);
            if (v != "") return tool + " · " + Fmt.Cut(v, 100);
        }
        return tool;
    }

    // El título que muestra la app de escritorio queda en el transcript como {"type":"custom-title",...}.
    static string ReadTitle(string transcript, string fallback, JavaScriptSerializer js)
    {
        try
        {
            if (transcript == "" || !File.Exists(transcript)) return fallback;
            string tail;
            using (var fs = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(Math.Max(0, fs.Length - 512 * 1024), SeekOrigin.Begin);
                using (var r = new StreamReader(fs, Encoding.UTF8)) tail = r.ReadToEnd();
            }
            var matches = Regex.Matches(tail, "\"customTitle\":\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (matches.Count == 0) return fallback;
            return js.Deserialize<string>("\"" + matches[matches.Count - 1].Groups[1].Value + "\"");
        }
        catch { return fallback; }
    }
}

// Registra la app en Windows para la persona actual (sin permisos de administrador): acceso directo en Inicio
// (Todas las aplicaciones) y entrada en Configuración → Aplicaciones instaladas, con su desinstalador.
static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskBarMonitor";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string ShortcutPath
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TaskBar Monitor.lnk"); }
    }

    public static void Register()
    {
        string exe = Application.ExecutablePath;
        CreateShortcut(ShortcutPath, exe);
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "TaskBar Monitor");
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("DisplayVersion", "1.0.0");
            k.SetValue("Publisher", "TaskBar Monitor");
            k.SetValue("InstallLocation", Path.GetDirectoryName(exe));
            k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        }
    }

    static void CreateShortcut(string lnk, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        try
        {
            object sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var t = sc.GetType();
            Action<string, object> set = (name, value) => t.InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, sc, new[] { value });
            set("TargetPath", target);
            set("WorkingDirectory", Path.GetDirectoryName(target));
            set("Description", "Uso del sistema y avisos en la barra de tareas");
            set("IconLocation", target + ",0");
            t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
            Marshal.FinalReleaseComObject(sc);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    // Lo ejecuta Windows al desinstalar desde Aplicaciones instaladas.
    public static void Uninstall()
    {
        if (MessageBox.Show("¿Desinstalar TaskBar Monitor?\n\nSe quitan el widget del taskbar, el inicio automático, la conexión con " +
                "Claude Code y los ajustes guardados en este equipo.", "TaskBar Monitor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        string exe = Application.ExecutablePath, dir = Path.GetDirectoryName(exe);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            using (p)
                if (p.Id != Process.GetCurrentProcess().Id) try { p.Kill(); p.WaitForExit(3000); } catch { }
        try { ClaudeHooks.Uninstall(ClaudeHooks.SettingsPath); } catch { }
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (k != null) { k.DeleteValue("TaskBar Monitor", false); k.DeleteValue("TaskbarMonitor", false); }
        }
        catch { }
        try { File.Delete(ShortcutPath); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { Directory.Delete(Paths.Data, true); } catch { }
        // Borra solo los archivos propios y la carpeta si queda vacía (nunca una carpeta con otras cosas),
        // un momento después de que este proceso termine.
        string files = string.Join(" ", new[] { Path.GetFileName(exe), "google-oauth.json", "TaskbarMonitor.cs" }
            .Select(f => "\"" + Path.Combine(dir, f) + "\""));
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & del /f /q " + files + " & rmdir \"" + dir + "\"")
        {
            CreateNoWindow = true, UseShellExecute = false
        });
    }
}

// La app de escritorio guarda cada sesión en claude-code-sessions\...\local_<id>.json junto con el ID de Claude Code;
// con ese ID local, claude://code/continue?session=local_<id> abre la sesión.
static class ClaudeDesktop
{
    static IEnumerable<string> SessionFolders()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string data = Path.Combine(local, "Claude-Data");
        if (Directory.Exists(data))
            foreach (var profile in Directory.GetDirectories(data))
                yield return Path.Combine(profile, "claude-code-sessions");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");
        string packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
            foreach (var package in Directory.GetDirectories(packages, "Claude_*"))
                yield return Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude-code-sessions");
    }

    public static string FindLocalId(string cliSessionId)
    {
        var pattern = new Regex("\"cliSessionId\"\\s*:\\s*\"" + Regex.Escape(cliSessionId) + "\"");
        foreach (var folder in SessionFolders().Where(Directory.Exists))
            foreach (var file in Directory.GetFiles(folder, "local_*.json", SearchOption.AllDirectories))
                try { if (pattern.IsMatch(File.ReadAllText(file))) return Path.GetFileNameWithoutExtension(file); }
                catch { }
        return null;
    }
}

// Conecta o desconecta los hooks de Claude Code en settings.json, sin tocar los demás hooks que haya.
static class ClaudeHooks
{
    public static string SettingsPath
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json"); }
    }

    static readonly string[,] Events =
    {
        { "SessionStart", "" }, { "UserPromptSubmit", "" }, { "PreToolUse", "AskUserQuestion" }, { "PermissionRequest", "" },
        { "PostToolUse", "" }, { "Notification", "" }, { "Stop", "" }, { "StopFailure", "" }, { "SessionEnd", "" }
    };

    public static bool IsInstalled(string file)
    {
        try
        {
            var hooks = Json.Obj(Load(file), "hooks");
            return hooks != null && hooks.Keys.Any(name =>
                Json.List(hooks, name).OfType<Dictionary<string, object>>().Any(e => Json.List(e, "hooks").Any(IsOurs)));
        }
        catch { return false; }
    }

    public static void Install(string file)
    {
        var settings = Load(file);
        RemoveOurs(settings);
        var hooks = Json.Obj(settings, "hooks");
        if (hooks == null) settings["hooks"] = hooks = new Dictionary<string, object>();
        string exe = Application.ExecutablePath.Replace('\\', '/');
        for (int i = 0; i < Events.GetLength(0); i++)
        {
            string name = Events[i, 0], matcher = Events[i, 1];
            var hook = new Dictionary<string, object>();
            hook["type"] = "command";
            hook["command"] = exe;
            hook["args"] = new List<object> { "--claude-hook" };
            hook["timeout"] = 10;
            if (name == "PostToolUse") hook["async"] = true; // corre en cada herramienta: no debe frenar a Claude
            var entry = new Dictionary<string, object>();
            if (matcher != "") entry["matcher"] = matcher;
            entry["hooks"] = new List<object> { hook };
            var list = Json.List(hooks, name).ToList();
            list.Add(entry);
            hooks[name] = list;
        }
        Save(file, settings);
    }

    // Si la app se movió de carpeta después de conectar Claude, los hooks apuntan a la ruta vieja: se reinstalan.
    public static void Repair(string file)
    {
        var hooks = Json.Obj(Load(file), "hooks");
        if (hooks == null) return;
        string exe = Application.ExecutablePath.Replace('\\', '/');
        var ours = hooks.Keys.SelectMany(name => Json.List(hooks, name)).OfType<Dictionary<string, object>>()
            .SelectMany(e => Json.List(e, "hooks")).Where(IsOurs).OfType<Dictionary<string, object>>().ToList();
        if (ours.Any(h => !string.Equals(Json.Str(h, "command"), exe, StringComparison.OrdinalIgnoreCase))) Install(file);
    }

    public static void Uninstall(string file)
    {
        if (!File.Exists(file)) return;
        var settings = Load(file);
        RemoveOurs(settings);
        Save(file, settings);
    }

    static bool IsOurs(object hook)
    {
        var h = hook as Dictionary<string, object>;
        return h != null && Json.Str(h, "command").EndsWith("TaskbarMonitor.exe", StringComparison.OrdinalIgnoreCase);
    }

    static void RemoveOurs(Dictionary<string, object> settings)
    {
        var hooks = Json.Obj(settings, "hooks");
        if (hooks == null) return;
        foreach (var name in hooks.Keys.ToList())
        {
            var entries = new List<object>();
            foreach (var e in Json.List(hooks, name))
            {
                var entry = e as Dictionary<string, object>;
                if (entry == null) { entries.Add(e); continue; }
                var kept = Json.List(entry, "hooks").Where(h => !IsOurs(h)).ToList();
                if (kept.Count == 0) continue;
                entry["hooks"] = kept;
                entries.Add(entry);
            }
            if (entries.Count == 0) hooks.Remove(name); else hooks[name] = entries;
        }
        if (hooks.Count == 0) settings.Remove("hooks");
    }

    static Dictionary<string, object> Load(string file)
    {
        if (!File.Exists(file)) return new Dictionary<string, object>();
        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file)) ?? new Dictionary<string, object>();
    }

    static void Save(string file, Dictionary<string, object> settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        if (File.Exists(file)) File.Copy(file, file + ".bak", true);
        File.WriteAllText(file, Json.Pretty(new JavaScriptSerializer().Serialize(settings)) + "\n", new UTF8Encoding(false));
    }
}
#endregion

#region Calendario (iCal)
class Meeting
{
    public DateTime Start, End; // hora local
    public string Title, Link;
}

static class Calendar
{
    class Prop
    {
        public string Name, Value;
        public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Param(string key) { string v; return Params.TryGetValue(key, out v) ? v.Trim('"') : ""; }

        public static Prop Parse(string line)
        {
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') quoted = !quoted;
                else if (line[i] == ':' && !quoted)
                {
                    var p = new Prop { Value = line.Substring(i + 1) };
                    var parts = line.Substring(0, i).Split(';');
                    p.Name = parts[0].ToUpperInvariant();
                    foreach (var part in parts.Skip(1))
                    {
                        int eq = part.IndexOf('=');
                        if (eq > 0) p.Params[part.Substring(0, eq)] = part.Substring(eq + 1);
                    }
                    return p;
                }
            }
            return null;
        }
    }

    // Google usa zonas IANA; .NET Framework solo conoce las de Windows.
    static readonly Dictionary<string, string> Zones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "America/Costa_Rica", "Central America Standard Time" }, { "America/Guatemala", "Central America Standard Time" },
        { "America/El_Salvador", "Central America Standard Time" }, { "America/Tegucigalpa", "Central America Standard Time" },
        { "America/Managua", "Central America Standard Time" }, { "America/Mexico_City", "Central Standard Time (Mexico)" },
        { "America/Panama", "SA Pacific Standard Time" }, { "America/Bogota", "SA Pacific Standard Time" },
        { "America/Lima", "SA Pacific Standard Time" }, { "America/New_York", "Eastern Standard Time" },
        { "America/Chicago", "Central Standard Time" }, { "America/Denver", "Mountain Standard Time" },
        { "America/Los_Angeles", "Pacific Standard Time" }, { "America/Santiago", "Pacific SA Standard Time" },
        { "America/Argentina/Buenos_Aires", "Argentina Standard Time" }, { "America/Sao_Paulo", "E. South America Standard Time" },
        { "Europe/Madrid", "Romance Standard Time" }, { "Europe/London", "GMT Standard Time" }, { "UTC", "UTC" }, { "Etc/UTC", "UTC" }
    };

    public static string Normalize(string url)
    {
        url = url.Trim();
        return url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + url.Substring(9) : url;
    }

    public static List<Meeting> Fetch(string url)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        string ics;
        using (var wc = new WebClient { Encoding = Encoding.UTF8 }) ics = wc.DownloadString(url);
        if (!ics.Contains("BEGIN:VCALENDAR")) throw new InvalidDataException("La dirección no devuelve un calendario iCal");
        var today = DateTime.Now.Date;
        return Parse(ics, today, today.AddDays(2));
    }

    static List<Meeting> Parse(string ics, DateTime from, DateTime to)
    {
        var lines = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if ((raw.StartsWith(" ") || raw.StartsWith("\t")) && lines.Count > 0) lines[lines.Count - 1] += raw.Substring(1);
            else lines.Add(raw);
        }

        string owner = "";
        var events = new List<Dictionary<string, List<Prop>>>();
        Dictionary<string, List<Prop>> cur = null;
        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT") { cur = new Dictionary<string, List<Prop>>(); continue; }
            if (line == "END:VEVENT") { if (cur != null) events.Add(cur); cur = null; continue; }
            var p = Prop.Parse(line);
            if (p == null) continue;
            if (cur == null) { if (p.Name == "X-WR-CALNAME") owner = p.Value.ToLowerInvariant(); continue; }
            if (!cur.ContainsKey(p.Name)) cur[p.Name] = new List<Prop>();
            cur[p.Name].Add(p);
        }

        // Ocurrencias de una serie que se movieron o cambiaron: vienen como eventos aparte con RECURRENCE-ID.
        var overridden = new HashSet<string>();
        foreach (var ev in events.Where(e => e.ContainsKey("RECURRENCE-ID")))
        {
            bool allDay;
            var rid = ParseDate(ev["RECURRENCE-ID"][0], out allDay);
            if (rid != null) overridden.Add(Get(ev, "UID") + "|" + Key(rid.Value));
        }

        var result = new List<Meeting>();
        foreach (var ev in events)
        {
            if (Get(ev, "STATUS").ToUpperInvariant() == "CANCELLED" || Declined(ev, owner) || !ev.ContainsKey("DTSTART")) continue;
            bool allDay, endAllDay;
            var start = ParseDate(ev["DTSTART"][0], out allDay);
            if (start == null || allDay) continue;
            var endProp = ev.ContainsKey("DTEND") ? ParseDate(ev["DTEND"][0], out endAllDay) : null;
            var length = (endProp ?? start.Value.AddMinutes(30)) - start.Value;
            string title = Unescape(Get(ev, "SUMMARY")), link = FindLink(ev), uid = Get(ev, "UID");

            string rrule = Get(ev, "RRULE");
            if (rrule == "" || ev.ContainsKey("RECURRENCE-ID"))
            {
                if (start.Value + length > from && start.Value < to)
                    result.Add(new Meeting { Start = start.Value, End = start.Value + length, Title = title, Link = link });
                continue;
            }

            var excluded = new HashSet<string>();
            if (ev.ContainsKey("EXDATE"))
                foreach (var ex in ev["EXDATE"])
                    foreach (var v in ex.Value.Split(','))
                    {
                        bool exAllDay;
                        var d = ParseDate(new Prop { Name = "EXDATE", Value = v, Params = ex.Params }, out exAllDay);
                        if (d != null) excluded.Add(Key(d.Value));
                    }

            foreach (var occ in Expand(start.Value, rrule, from, to, length))
            {
                if (excluded.Contains(Key(occ)) || overridden.Contains(uid + "|" + Key(occ))) continue;
                result.Add(new Meeting { Start = occ, End = occ + length, Title = title, Link = link });
            }
        }
        return result.OrderBy(m => m.Start).ToList();
    }

    static IEnumerable<DateTime> Expand(DateTime start, string rrule, DateTime from, DateTime to, TimeSpan length)
    {
        var r = rrule.Split(';').Select(x => x.Split('=')).Where(x => x.Length == 2)
            .ToDictionary(x => x[0].ToUpperInvariant(), x => x[1].ToUpperInvariant());
        string freq;
        r.TryGetValue("FREQ", out freq);
        int interval = 1, count = int.MaxValue;
        if (r.ContainsKey("INTERVAL")) int.TryParse(r["INTERVAL"], out interval);
        if (interval < 1) interval = 1;
        if (r.ContainsKey("COUNT") && !int.TryParse(r["COUNT"], out count)) count = int.MaxValue;
        DateTime until = DateTime.MaxValue;
        if (r.ContainsKey("UNTIL"))
        {
            bool ad;
            var u = ParseDate(new Prop { Name = "UNTIL", Value = r["UNTIL"] }, out ad);
            if (u != null) until = ad ? u.Value.AddDays(1) : u.Value;
        }
        var days = new List<DayOfWeek>();
        if (r.ContainsKey("BYDAY"))
            foreach (var t in r["BYDAY"].Split(','))
            {
                int i = Array.IndexOf(new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" }, Regex.Replace(t, "[^A-Z]", ""));
                if (i >= 0) days.Add((DayOfWeek)i);
            }
        if (days.Count == 0) days.Add(start.DayOfWeek);

        int n = 0;
        var monday = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
        for (int k = 0; k < 20000; k++)
        {
            IEnumerable<DateTime> batch;
            if (freq == "DAILY") batch = new[] { start.AddDays((double)k * interval) };
            else if (freq == "WEEKLY")
            {
                var week = monday.AddDays(7.0 * k * interval);
                batch = days.Select(dw => week.AddDays(((int)dw + 6) % 7) + start.TimeOfDay).OrderBy(x => x).ToList();
            }
            else if (freq == "MONTHLY") batch = new[] { start.AddMonths(k * interval) };
            else if (freq == "YEARLY") batch = new[] { start.AddYears(k * interval) };
            else yield break;

            foreach (var occ in batch)
            {
                if (occ < start) continue;
                if (occ > until || n >= count || occ >= to) yield break;
                n++;
                if (occ + length > from) yield return occ;
            }
        }
    }

    static DateTime? ParseDate(Prop p, out bool allDay)
    {
        string v = p.Value.Trim();
        allDay = p.Param("VALUE").ToUpperInvariant() == "DATE" || v.Length == 8;
        DateTime dt;
        if (allDay)
            return DateTime.TryParseExact(v.Substring(0, Math.Min(8, v.Length)), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out dt) ? dt : (DateTime?)null;
        bool utc = v.EndsWith("Z");
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return null;
        if (utc) return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
        string zoneId;
        if (Zones.TryGetValue(p.Param("TZID"), out zoneId))
            try { return TimeZoneInfo.ConvertTime(dt, TimeZoneInfo.FindSystemTimeZoneById(zoneId), TimeZoneInfo.Local); }
            catch { }
        return dt; // zona desconocida: se toma como hora local
    }

    static bool Declined(Dictionary<string, List<Prop>> ev, string owner)
    {
        if (!owner.Contains("@") || !ev.ContainsKey("ATTENDEE")) return false;
        return ev["ATTENDEE"].Any(a => a.Value.ToLowerInvariant().Contains(owner) && a.Param("PARTSTAT").ToUpperInvariant() == "DECLINED");
    }

    static string FindLink(Dictionary<string, List<Prop>> ev)
    {
        string conf = Get(ev, "X-GOOGLE-CONFERENCE");
        if (conf != "") return conf;
        var m = Regex.Match(Get(ev, "DESCRIPTION") + " " + Get(ev, "LOCATION"),
            @"https?://(meet\.google\.com/[a-z\-]+|[\w.\-]*zoom\.us/[^\s\\<>""]+|teams\.microsoft\.com/[^\s\\<>""]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Value : "";
    }

    static string Get(Dictionary<string, List<Prop>> ev, string name)
    {
        return ev.ContainsKey(name) ? ev[name][0].Value : "";
    }

    static string Unescape(string s)
    {
        return s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
    }

    static string Key(DateTime d) { return d.ToString("yyyyMMddHHmm"); }
}
#endregion

#region Google Calendar
// Inicio de sesión con Google para apps de escritorio (navegador + puerto local + PKCE) y lectura del calendario.
// El cliente OAuth se crea una vez en Google Cloud y su JSON va junto al .exe como google-oauth.json.
static class Google
{
    const string Scope = "openid email https://www.googleapis.com/auth/calendar.readonly";
    static string cachedToken;
    static DateTime cachedUntil;

    public static string ClientFile
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "google-oauth.json"); }
    }

    public static bool HasClient { get { string id, secret; return ReadClient(out id, out secret); } }

    public static bool SignedIn { get { return Config.GetSecret("GoogleRefreshToken") != ""; } }

    // Lee el JSON tal como lo descarga Google Cloud para un cliente "App de escritorio".
    static bool ReadClient(out string id, out string secret)
    {
        id = secret = "";
        try
        {
            if (!File.Exists(ClientFile)) return false;
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(ClientFile));
            var c = Json.Obj(d, "installed") ?? Json.Obj(d, "web") ?? d;
            id = Json.Str(c, "client_id");
            secret = Json.Str(c, "client_secret");
            return id != "";
        }
        catch { return false; }
    }

    // Abre el navegador, espera la respuesta de Google en un puerto local y guarda la sesión. Devuelve el correo.
    public static string SignIn()
    {
        string id, secret;
        if (!ReadClient(out id, out secret)) throw new Exception("Falta google-oauth.json junto a la app");
        string verifier = RandomUrl(48), state = RandomUrl(16);
        string challenge;
        using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            string redirect = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            Native.Open("https://accounts.google.com/o/oauth2/v2/auth?client_id=" + E(id) + "&redirect_uri=" + E(redirect) +
                "&response_type=code&scope=" + E(Scope) + "&code_challenge=" + challenge + "&code_challenge_method=S256" +
                "&state=" + state + "&access_type=offline&prompt=consent");

            string code = null;
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (code == null)
            {
                var accept = listener.AcceptTcpClientAsync();
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !accept.Wait(left)) throw new Exception("No se completó el inicio de sesión a tiempo");
                using (var client = accept.Result)
                using (var stream = client.GetStream())
                {
                    // El navegador puede abrir conexiones vacías por adelantado: se ignoran.
                    stream.ReadTimeout = 5000;
                    string line;
                    try { line = new StreamReader(stream).ReadLine(); } catch { continue; }
                    if (line == null || !line.Contains("?")) continue;
                    var query = ParseQuery(line);
                    string got, error;
                    query.TryGetValue("code", out got);
                    query.TryGetValue("error", out error);
                    bool ok = !string.IsNullOrEmpty(got) && query.ContainsKey("state") && query["state"] == state;
                    Reply(stream, ok ? "Listo, ya puedes cerrar esta pestaña y volver a la app." : "No se pudo iniciar sesión.");
                    if (!ok) throw new Exception(error == "access_denied" ? "Cancelaste el inicio de sesión" : "No se pudo iniciar sesión");
                    code = got;
                }
            }

            var token = Post("code=" + E(code) + "&client_id=" + E(id) + "&client_secret=" + E(secret) +
                "&redirect_uri=" + E(redirect) + "&grant_type=authorization_code&code_verifier=" + verifier);
            string refresh = Json.Str(token, "refresh_token");
            if (refresh == "") throw new Exception("Google no devolvió una sesión permanente");
            string email = EmailFromIdToken(Json.Str(token, "id_token"));
            Config.SetSecret("GoogleRefreshToken", refresh);
            Config.Set("GoogleEmail", email);
            cachedToken = Json.Str(token, "access_token");
            cachedUntil = DateTime.UtcNow.AddSeconds(Convert.ToInt32(token["expires_in"]) - 60);
            return email;
        }
        finally { listener.Stop(); }
    }

    public static void SignOut()
    {
        string refresh = Config.GetSecret("GoogleRefreshToken");
        Config.SetSecret("GoogleRefreshToken", "");
        Config.Set("GoogleEmail", "");
        cachedToken = null;
        if (refresh != "")
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { using (var wc = new WebClient()) wc.UploadString("https://oauth2.googleapis.com/revoke?token=" + E(refresh), ""); } catch { }
            });
    }

    public static List<Meeting> Fetch()
    {
        string token = AccessToken();
        var today = DateTime.Now.Date;
        string url = "https://www.googleapis.com/calendar/v3/calendars/primary/events?singleEvents=true&orderBy=startTime&maxResults=100" +
            "&timeMin=" + E(new DateTimeOffset(today).ToString("yyyy-MM-ddTHH:mm:sszzz")) +
            "&timeMax=" + E(new DateTimeOffset(today.AddDays(2)).ToString("yyyy-MM-ddTHH:mm:sszzz"));
        string body;
        using (var wc = new WebClient { Encoding = Encoding.UTF8 })
        {
            wc.Headers["Authorization"] = "Bearer " + token;
            body = wc.DownloadString(url);
        }
        var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(body);
        var list = new List<Meeting>();
        foreach (var item in Json.List(d, "items").OfType<Dictionary<string, object>>())
        {
            if (Json.Str(item, "status") == "cancelled") continue;
            string start = Json.Str(Json.Obj(item, "start"), "dateTime"), end = Json.Str(Json.Obj(item, "end"), "dateTime");
            if (start == "" || end == "") continue; // eventos de todo el día
            bool declined = Json.List(item, "attendees").OfType<Dictionary<string, object>>()
                .Any(a => Json.Str(a, "self").ToLowerInvariant() == "true" && Json.Str(a, "responseStatus") == "declined");
            if (declined) continue;
            string link = Json.Str(item, "hangoutLink");
            if (link == "")
                link = Json.List(Json.Obj(item, "conferenceData"), "entryPoints").OfType<Dictionary<string, object>>()
                    .Where(p => Json.Str(p, "entryPointType") == "video").Select(p => Json.Str(p, "uri")).FirstOrDefault() ?? "";
            list.Add(new Meeting
            {
                Start = DateTimeOffset.Parse(start, CultureInfo.InvariantCulture).LocalDateTime,
                End = DateTimeOffset.Parse(end, CultureInfo.InvariantCulture).LocalDateTime,
                Title = Json.Str(item, "summary") != "" ? Json.Str(item, "summary") : "(Sin título)",
                Link = link
            });
        }
        return list.OrderBy(m => m.Start).ToList();
    }

    static string AccessToken()
    {
        if (cachedToken != null && DateTime.UtcNow < cachedUntil) return cachedToken;
        string id, secret, refresh = Config.GetSecret("GoogleRefreshToken");
        if (!ReadClient(out id, out secret) || refresh == "") throw new Exception("No hay sesión de Google");
        Dictionary<string, object> token;
        try { token = Post("refresh_token=" + E(refresh) + "&client_id=" + E(id) + "&client_secret=" + E(secret) + "&grant_type=refresh_token"); }
        catch (Exception ex)
        {
            if (ex.Message.Contains("invalid_grant")) throw new Exception("La sesión de Google venció. Vuelve a iniciar sesión.");
            throw;
        }
        cachedToken = Json.Str(token, "access_token");
        cachedUntil = DateTime.UtcNow.AddSeconds(Convert.ToInt32(token["expires_in"]) - 60);
        return cachedToken;
    }

    static Dictionary<string, object> Post(string form)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        try
        {
            using (var wc = new WebClient { Encoding = Encoding.UTF8 })
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(wc.UploadString("https://oauth2.googleapis.com/token", form));
            }
        }
        catch (WebException ex)
        {
            string detail = "";
            if (ex.Response != null) using (var r = new StreamReader(ex.Response.GetResponseStream())) detail = r.ReadToEnd();
            throw new Exception("Google rechazó la solicitud " + detail);
        }
    }

    static void Reply(Stream stream, string message)
    {
        string html = "<!doctype html><html><head><meta charset='utf-8'><title>TaskBar Monitor</title></head>" +
            "<body style='font-family:Segoe UI,sans-serif;text-align:center;padding-top:80px;color:#1b1b1b'><h2 style='font-weight:600'>" +
            message + "</h2></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
        stream.Write(head, 0, head.Length);
        stream.Write(body, 0, body.Length);
    }

    static Dictionary<string, string> ParseQuery(string requestLine)
    {
        var result = new Dictionary<string, string>();
        var parts = requestLine.Split(' ');
        string target = parts.Length > 1 ? parts[1] : "";
        int q = target.IndexOf('?');
        if (q < 0) return result;
        foreach (var pair in target.Substring(q + 1).Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0) result[pair.Substring(0, eq)] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
        }
        return result;
    }

    static string EmailFromIdToken(string idToken)
    {
        try
        {
            string payload = idToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return Json.Str(d, "email");
        }
        catch { return ""; }
    }

    static string RandomUrl(int bytes)
    {
        var data = new byte[bytes];
        using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(data);
        return Base64Url(data);
    }

    static string Base64Url(byte[] data) { return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

    static string E(string s) { return Uri.EscapeDataString(s); }
}
#endregion

#region Jira
class JiraIssue { public string Key, Summary, Status, Category, Project, Priority; }

class JiraInfo
{
    public bool Configured;
    public string Site = "", Error = "";
    public List<JiraIssue> Issues = new List<JiraIssue>();
    public int ToDo { get { return Issues.Count(i => i.Category == "new"); } }
}

// Jira Cloud por su API REST, con el correo y un token de API de la persona.
static class Jira
{
    // Orden elegido en Ajustes → campo del ORDER BY de Jira; la dirección se elige aparte.
    public static readonly string[,] Sorts =
    {
        { "updated", "Fecha de actualización", "updated" }, { "priority", "Prioridad", "priority" },
        { "rank", "Orden de atención", "rank" }, // el orden en que están priorizados en el tablero o backlog
        { "created", "Fecha de creación", "created" }, { "key", "Clave", "key" }
    };

    public static readonly string[,] Directions = { { "desc", "Descendente" }, { "asc", "Ascendente" } };

    public static readonly string[,] Groups =
    {
        { "status", "Estado" }, { "project", "Proyecto" }, { "priority", "Prioridad" }, { "none", "Sin agrupar" }
    };

    public static string Jql(string sort)
    {
        string field = "updated";
        for (int i = 0; i < Sorts.GetLength(0); i++) if (Sorts[i, 0] == sort) field = Sorts[i, 2];
        string dir = Config.Get("JiraSortDir", "desc") == "asc" ? "ASC" : "DESC";
        return "assignee = currentUser() AND statusCategory != Done ORDER BY " + field + " " + dir + (field == "updated" ? "" : ", updated DESC");
    }

    public static string Normalize(string site)
    {
        site = site.Trim().TrimEnd('/');
        if (site == "") return "";
        if (!site.Contains(".") && !site.Contains("/")) site += ".atlassian.net"; // "tuempresa" basta
        if (!site.StartsWith("http", StringComparison.OrdinalIgnoreCase)) site = "https://" + site;
        return site;
    }

    public static string Myself(string site, string email, string token)
    {
        var me = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Call(site, email, token, "GET", "/rest/api/3/myself", null));
        return Json.Str(me, "displayName");
    }

    public static JiraInfo Load(string site, string email, string token, string sort)
    {
        var info = new JiraInfo { Configured = true, Site = site };
        try
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string body = js.Serialize(new Dictionary<string, object>
            {
                { "jql", Jql(sort) }, { "fields", new[] { "summary", "status", "project", "priority" } }, { "maxResults", 100 }
            });
            var d = js.Deserialize<Dictionary<string, object>>(Call(site, email, token, "POST", "/rest/api/3/search/jql", body));
            foreach (var i in Json.List(d, "issues").OfType<Dictionary<string, object>>())
            {
                var fields = Json.Obj(i, "fields");
                var status = Json.Obj(fields, "status");
                info.Issues.Add(new JiraIssue
                {
                    Key = Json.Str(i, "key"),
                    Summary = Json.Str(fields, "summary"),
                    Status = Json.Str(status, "name"),
                    Category = Json.Str(Json.Obj(status, "statusCategory"), "key"),
                    Project = Json.Str(Json.Obj(fields, "project"), "name"),
                    Priority = Json.Str(Json.Obj(fields, "priority"), "name")
                });
            }
        }
        catch (Exception ex) { info.Error = Describe(ex); }
        return info;
    }

    public static string Describe(Exception ex)
    {
        var web = ex as WebException;
        var resp = web != null ? web.Response as HttpWebResponse : null;
        if (resp != null)
        {
            int code = (int)resp.StatusCode;
            if (code == 401 || code == 403) return "Correo o token incorrectos";
            if (code == 404) return "No encontré ese sitio de Jira";
            return "Jira respondió con el error " + code;
        }
        if (web != null) return "Sin conexión con Jira";
        return ex.Message;
    }

    static string Call(string site, string email, string token, string method, string path, string body)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var req = (HttpWebRequest)WebRequest.Create(site + path);
        req.Method = method;
        req.Accept = "application/json";
        req.Timeout = 15000;
        req.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + token));
        if (body != null)
        {
            req.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
        }
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return r.ReadToEnd();
    }
}
#endregion

#region Datos de fondo
class ProcStat { public string Name; public double Cpu; public long Mem; }

class BuildInfo
{
    public Process Proc;
    public int Pid;
    public string Repo, Branch, Project;
    public DateTime Start;
    public DateTime? End;
    public bool? Ok;
}

class RepoInfo { public string Name, Path, Branch; public int Changes; }
#endregion

#region Panel de detalle
class PanelAction
{
    public string Id, Icon, Tip;
    public Action Run;
    public bool Confirm;        // pide un segundo clic antes de ejecutar
    public bool Enabled = true; // deshabilitado se ve tenue y solo muestra su tooltip
}

// Glifos de Segoe Fluent Icons / Segoe MDL2 Assets.
static class Icons
{
    public const string Stop = "", Cancel = "", Open = "", Join = "",
                        Settings = "", Folder = "", Confirm = "",
                        Chat = "", List = "", Edit = "", Calendar = "\uE787", Refresh = "";
}

class PanelLine
{
    public enum K { Title, Muted, Header, Row, Spark, Wrap }
    public K Kind;
    public string Left, Right;
    public Color? LeftColor, RightColor;
    public float[] Data;
    public float Phase, Lo, Hi = 100;
    public PanelAction Act;

    public static PanelLine Title(string text) { return new PanelLine { Kind = K.Title, Left = text }; }
    public static PanelLine Muted(string text, Color? color = null) { return new PanelLine { Kind = K.Muted, Left = text, LeftColor = color }; }
    public static PanelLine Header(string text) { return new PanelLine { Kind = K.Header, Left = text }; }
    public static PanelLine Wrap(string text, Color? color) { return new PanelLine { Kind = K.Wrap, Left = text, LeftColor = color }; }
    public static PanelLine Spark(IEnumerable<float> data, float phase, float lo = 0, float hi = 100)
    {
        return new PanelLine { Kind = K.Spark, Data = data.ToArray(), Phase = phase, Lo = lo, Hi = hi };
    }
    public static PanelLine Row(string left, string right, Color? rightColor = null)
    {
        return new PanelLine { Kind = K.Row, Left = left, Right = right, RightColor = rightColor };
    }

    public PanelAction RowAct; // toda la línea responde al clic (por ejemplo, una sesión de Claude)
    public PanelAction Act2;   // segundo botón, a la izquierda del primero

    public PanelLine With(PanelAction act) { Act = act; return this; }
    public PanelLine With(PanelAction act, PanelAction act2) { Act = act; Act2 = act2; return this; }
    public PanelLine OnClick(PanelAction act) { RowAct = act; return this; }
}

// Panel que aparece sobre el indicador. Se puede usar: el mouse puede entrar y presionar sus botones.
class InfoPopup : Form
{
    public Func<string, List<PanelLine>> Source;
    public float S = 1;
    public bool Light;
    public string Key { get; private set; }
    public Rectangle AnchorRect;
    public uint Edge;

    RectangleF cur;
    float alpha, alphaTarget;
    readonly List<KeyValuePair<RectangleF, PanelAction>> hits = new List<KeyValuePair<RectangleF, PanelAction>>();
    string hoverId, confirmId;
    int confirmUntil, hoverStart;
    Font titleFont, bodyFont, headFont, buttonFont, iconFont, pillFont;

    public InfoPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x80000 | 0x08000000 | 0x8; // TOOLWINDOW | LAYERED | NOACTIVATE | TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE → MA_NOACTIVATE: no le quita el foco a nadie
        base.WndProc(ref m);
    }

    public bool IsOpen { get { return Visible && alphaTarget > 0; } }

    public bool Contains(Point screen) { return Visible && Bounds.Contains(screen); }

    public void OpenFor(string key, Rectangle anchor, uint edge)
    {
        bool changed = key != Key;
        Key = key;
        AnchorRect = anchor;
        Edge = edge;
        if (changed) { confirmId = null; hoverId = null; }
        var lines = Source(key);
        if (lines == null || lines.Count == 0) { Close(true); return; }
        alphaTarget = 1;
        Frame();
    }

    // Abre, cambia y cierra de golpe, sin transiciones.
    public void Close(bool animated)
    {
        alphaTarget = 0;
        Key = null;
        if (Visible) Hide();
    }

    public void Frame()
    {
        if (Key == null) return;
        var lines = Source(Key);
        if (lines == null || lines.Count == 0) { Close(false); return; }
        float[] heights;
        cur = TargetRect(Measure(lines, out heights));
        alpha = 1;
        if (confirmId != null && Environment.TickCount - confirmUntil > 0) confirmId = null;
        Render(lines, heights, cur, 1);
    }

    RectangleF TargetRect(SizeF size)
    {
        var screen = Screen.FromRectangle(AnchorRect).Bounds;
        float gap = 10 * S, x = AnchorRect.Left, y;
        if (Edge == Native.ABE_TOP) y = AnchorRect.Bottom + gap;
        else if (Edge == Native.ABE_LEFT) { x = AnchorRect.Right + gap; y = AnchorRect.Top; }
        else if (Edge == Native.ABE_RIGHT) { x = AnchorRect.Left - size.Width - gap; y = AnchorRect.Top; }
        else y = AnchorRect.Top - size.Height - gap;
        x = Math.Max(screen.Left + 4, Math.Min(x, screen.Right - size.Width - 4));
        y = Math.Max(screen.Top + 4, Math.Min(y, screen.Bottom - size.Height - 4));
        return new RectangleF(x, y, size.Width, size.Height);
    }

    void EnsureFonts()
    {
        if (titleFont != null) return;
        titleFont = PickFont(new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" }, 14f * S);
        bodyFont = PickFont(new[] { "Segoe UI Variable Text", "Segoe UI" }, 12f * S);
        headFont = PickFont(new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" }, 12f * S);
        buttonFont = PickFont(new[] { "Segoe UI Variable Text", "Segoe UI" }, 12f * S);
        pillFont = PickFont(new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" }, 11f * S);
        iconFont = PickFont(new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }, 12f * S);
    }

    public static Font PickFont(string[] names, float px)
    {
        foreach (var name in names)
        {
            var f = new Font(name, px, GraphicsUnit.Pixel);
            if (f.Name == name) return f; // GDI+ sustituye en silencio las fuentes que no existen
            f.Dispose();
        }
        return new Font("Segoe UI Symbol", px, GraphicsUnit.Pixel);
    }

    // La tarjeta de Jira es más ancha para que se lean los títulos de los tickets.
    float PanelWidth { get { return (Key == "jira" ? 480 : 320) * S; } }
    float Pad { get { return 14 * S; } }

    SizeF Measure(List<PanelLine> lines, out float[] heights)
    {
        EnsureFonts();
        heights = new float[lines.Count];
        float inner = PanelWidth - 2 * Pad;
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                switch (l.Kind)
                {
                    case PanelLine.K.Title: heights[i] = 26 * S; break;
                    case PanelLine.K.Muted: heights[i] = 19 * S; break;
                    case PanelLine.K.Header: heights[i] = (i == 0 ? 0 : 10 * S) + 19 * S; break;
                    case PanelLine.K.Row: heights[i] = 24 * S; break;
                    case PanelLine.K.Spark: heights[i] = 46 * S; break;
                    default: heights[i] = g.MeasureString(l.Left, bodyFont, (int)inner).Height + 4 * S; break;
                }
            }
        return new SizeF(PanelWidth, 2 * Pad + heights.Sum());
    }

    void Render(List<PanelLine> lines, float[] heights, RectangleF rect, float contentAlpha)
    {
        Color bg = Light ? Color.FromArgb(250, 250, 250) : Color.FromArgb(36, 36, 36);
        Color border = Light ? Color.FromArgb(218, 218, 218) : Color.FromArgb(66, 66, 66);
        Color fg = Light ? Color.FromArgb(26, 26, 26) : Color.FromArgb(238, 238, 238);
        Color muted = Color.FromArgb(150, fg);
        Color danger = Color.FromArgb(226, 75, 74);
        Func<Color, Color> c = col => Anim.Fade(col, contentAlpha);

        int w = Math.Max(1, (int)Math.Round(rect.Width)), h = Math.Max(1, (int)Math.Round(rect.Height));
        float pad = Pad, inner = w - 2 * pad;
        hits.Clear();
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                using (var path = Native.RoundRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), 9 * S))
                {
                    using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                    using (var p = new Pen(border, 1)) g.DrawPath(p, path);
                    g.SetClip(path);
                }

                var ellipsis = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
                var right = new StringFormat(ellipsis) { Alignment = StringAlignment.Far };
                var center = new StringFormat(ellipsis) { Alignment = StringAlignment.Center };
                float y = pad + (1 - contentAlpha) * 5 * S;
                RectangleF? tipAnchor = null;
                string tipText = null;
                for (int i = 0; i < lines.Count; i++)
                {
                    var l = lines[i];
                    float lh = heights[i];
                    float textRight = pad + inner;
                    if (l.RowAct != null)
                    {
                        // Las líneas de un mismo elemento comparten Id, así el fondo de hover las cubre juntas.
                        var row = new RectangleF(pad - 6 * S, y, inner + 12 * S, lh);
                        if (hoverId == l.RowAct.Id)
                            using (var b = new SolidBrush(c(Color.FromArgb(16, fg)))) g.FillRectangle(b, row);
                        hits.Add(new KeyValuePair<RectangleF, PanelAction>(row, l.RowAct));
                    }
                    float buttonRight = pad + inner;
                    // Botones de tamaño fijo, de derecha a izquierda: confirmar o pasar el mouse nunca mueve lo de alrededor.
                    foreach (var act in new[] { l.Act, l.Act2 })
                    {
                        if (act == null || (l.Kind != PanelLine.K.Title && l.Kind != PanelLine.K.Row)) continue;
                        bool confirming = confirmId == act.Id;
                        bool hover = hoverId == act.Id && act.Enabled;
                        float bw = 26 * S, bh = 22 * S;
                        var br = new RectangleF(buttonRight - bw, y + (lh - bh) / 2, bw, bh);
                        buttonRight = br.Left - 4 * S;
                        using (var path = Native.RoundRect(br, 5 * S))
                        {
                            Color fill = confirming ? danger : Color.FromArgb(!act.Enabled ? 8 : hover ? 42 : 16, fg);
                            using (var b = new SolidBrush(c(fill))) g.FillPath(b, path);
                        }
                        Color iconColor = confirming ? Color.White : act.Enabled ? fg : Color.FromArgb(70, fg);
                        using (var b = new SolidBrush(c(iconColor)))
                            g.DrawString(confirming ? Icons.Confirm : act.Icon, iconFont, b, new RectangleF(br.X, br.Y + 1, br.Width, br.Height), center);
                        hits.Add(new KeyValuePair<RectangleF, PanelAction>(br, act));
                        if (hoverId == act.Id) { tipAnchor = br; tipText = confirming ? "Haz clic otra vez para confirmar" : act.Tip; }
                        textRight = br.Left - 8 * S;
                    }
                    float avail = textRight - pad;
                    switch (l.Kind)
                    {
                        case PanelLine.K.Title:
                            using (var b = new SolidBrush(c(l.LeftColor ?? fg))) g.DrawString(l.Left, titleFont, b, new RectangleF(pad, y, avail, lh), ellipsis);
                            break;
                        case PanelLine.K.Muted:
                            using (var b = new SolidBrush(c(l.LeftColor ?? muted))) g.DrawString(l.Left, bodyFont, b, new RectangleF(pad, y, inner, lh), ellipsis);
                            break;
                        case PanelLine.K.Header:
                            using (var b = new SolidBrush(c(muted))) g.DrawString(l.Left, headFont, b, new RectangleF(pad, y + lh - 19 * S, inner, 19 * S), ellipsis);
                            break;
                        case PanelLine.K.Row:
                            float rw = 0;
                            if (!string.IsNullOrEmpty(l.Right))
                            {
                                // Etiqueta: fondo con un tono claro del color y el texto en su tono fuerte.
                                bool tinted = l.RightColor.HasValue;
                                Color tone = l.RightColor ?? fg;
                                Color pillBg = tinted ? Color.FromArgb(Light ? 38 : 56, tone) : Color.FromArgb(Light ? 13 : 24, fg);
                                Color pillText = !tinted ? Color.FromArgb(175, fg)
                                    : Light ? Anim.Mix(tone, Color.Black, 0.3f) : Anim.Mix(tone, Color.White, 0.35f);
                                float ph = 18 * S;
                                rw = Math.Min(g.MeasureString(l.Right, pillFont, PointF.Empty, StringFormat.GenericTypographic).Width + 14 * S, avail * 0.6f);
                                var pr = new RectangleF(textRight - rw, y + (lh - ph) / 2, rw, ph);
                                using (var path = Native.RoundRect(pr, ph / 2))
                                using (var b = new SolidBrush(c(pillBg))) g.FillPath(b, path);
                                var pillFmt = new StringFormat(StringFormat.GenericTypographic)
                                {
                                    Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                                    FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter
                                };
                                using (var b = new SolidBrush(c(pillText)))
                                    g.DrawString(l.Right, pillFont, b, new RectangleF(pr.X + 5 * S, pr.Y + 0.5f * S, pr.Width - 10 * S, pr.Height), pillFmt);
                            }
                            using (var b = new SolidBrush(c(l.LeftColor ?? fg)))
                                g.DrawString(l.Left, bodyFont, b, new RectangleF(pad, y, avail - rw - 8 * S, lh), ellipsis);
                            break;
                        case PanelLine.K.Spark:
                            DrawSpark(g, l.Data, l.Phase, l.Lo, l.Hi, new RectangleF(pad, y + 4 * S, inner, 36 * S), c(muted));
                            break;
                        case PanelLine.K.Wrap:
                            using (var b = new SolidBrush(c(l.LeftColor ?? muted))) g.DrawString(l.Left, bodyFont, b, new RectangleF(pad, y, inner, lh));
                            break;
                    }
                    y += lh;
                }

                // El tooltip aparece cuando el mouse lleva un momento sobre el botón.
                if (tipAnchor.HasValue && !string.IsNullOrEmpty(tipText))
                {
                    float ta = Anim.Clamp((Environment.TickCount - hoverStart - 450) / 150f) * contentAlpha;
                    if (ta > 0) DrawTip(g, tipText, tipAnchor.Value, w, ta, fg, bg);
                }
            }

            int x = (int)Math.Round(rect.X), top = (int)Math.Round(rect.Y);
            Bounds = new Rectangle(x, top, w, h);
            Native.ApplyBitmap(Handle, bmp, x, top, (byte)(255 * Anim.Clamp(alpha)));
        }
        if (!Visible) Show();
        Native.KeepOnTop(Handle);
    }

    // El gráfico se desliza continuamente: el último punto entra desde la derecha durante el segundo que dura.
    void DrawTip(Graphics g, string text, RectangleF anchor, int panelWidth, float a, Color fg, Color bg)
    {
        var size = g.MeasureString(text, buttonFont);
        float tw = size.Width + 12 * S, th = size.Height + 6 * S;
        float x = Math.Max(6 * S, Math.Min(anchor.Right - tw, panelWidth - tw - 6 * S));
        float y = anchor.Top - th - 4 * S;
        if (y < 6 * S) y = anchor.Bottom + 4 * S;
        var r = new RectangleF(x, y, tw, th);
        using (var path = Native.RoundRect(r, 5 * S))
        using (var b = new SolidBrush(Anim.Fade(fg, a * 0.94f))) g.FillPath(b, path);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var b = new SolidBrush(Anim.Fade(bg, a))) g.DrawString(text, buttonFont, b, r, fmt);
    }

    void DrawSpark(Graphics g, float[] data, float phase, float lo, float hi, RectangleF r, Color color)
    {
        if (data.Length < 2) return;
        // Grilla fija de 60 segundos: un punto nuevo no cambia el espaciado de los demás, la línea solo se desliza.
        float step = r.Width / 59f, range = Math.Max(1, hi - lo);
        var pts = new PointF[data.Length];
        for (int i = 0; i < data.Length; i++)
            pts[i] = new PointF(r.Right + step * (1 - phase) - (data.Length - 1 - i) * step,
                                r.Bottom - r.Height * (Math.Max(lo, Math.Min(hi, data[i])) - lo) / range);
        var state = g.Save();
        g.IntersectClip(r);
        var area = new List<PointF>(pts) { new PointF(pts[pts.Length - 1].X, r.Bottom), new PointF(pts[0].X, r.Bottom) };
        using (var b = new SolidBrush(Color.FromArgb(color.A * 36 / 255, color))) g.FillPolygon(b, area.ToArray());
        using (var p = new Pen(color, 1.4f * S) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts);
        g.Restore(state);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        PanelAction over = null;
        foreach (var h in hits) if (h.Key.Contains(e.Location)) over = h.Value;
        string id = over != null ? over.Id : null;
        // La mano solo sobre lo que se puede presionar; se ajusta en cada movimiento.
        Cursor = over != null && over.Enabled ? Cursors.Hand : Cursors.Default;
        if (id == hoverId) return;
        hoverId = id;
        hoverStart = Environment.TickCount;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoverId = null;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // De atrás hacia adelante: un botón dentro de una fila clicable tiene prioridad sobre la fila.
        foreach (var h in Enumerable.Reverse(hits))
        {
            if (!h.Key.Contains(e.Location)) continue;
            var act = h.Value;
            if (!act.Enabled) break;
            if (act.Confirm && confirmId != act.Id) { confirmId = act.Id; confirmUntil = Environment.TickCount + 3000; }
            else { confirmId = null; try { act.Run(); } catch { } }
            break;
        }
    }
}
#endregion

#region Ajustes
// Interruptor al estilo de Windows 11.
class ToggleSwitch : Control
{
    bool on;
    float knob;
    readonly WinTimer anim = new WinTimer { Interval = 15 };
    public Color Accent, Track, TrackBorder, Thumb, OnThumb;
    public event EventHandler CheckedChanged;

    public ToggleSwitch(float s)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size((int)(40 * s), (int)(20 * s));
        Cursor = Cursors.Hand;
        anim.Tick += (o, e) =>
        {
            knob = Anim.Approach(knob, on ? 1 : 0, 15, 45);
            if (knob == (on ? 1 : 0)) anim.Stop();
            Invalidate();
        };
    }

    // Carga el estado sin animación ni evento.
    public bool Checked
    {
        get { return on; }
        set { on = value; knob = value ? 1 : 0; Invalidate(); }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        on = !on;
        anim.Start();
        if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Native.RoundRect(r, r.Height / 2))
        {
            using (var b = new SolidBrush(Anim.Mix(Track, Accent, knob))) g.FillPath(b, path);
            using (var p = new Pen(Anim.Fade(TrackBorder, 1 - knob), 1)) g.DrawPath(p, path);
        }
        float h = r.Height, d = h * (0.6f + 0.1f * knob), cx = r.X + h / 2 + (r.Width - h) * knob, cy = r.Y + h / 2;
        using (var b = new SolidBrush(Anim.Mix(Thumb, OnThumb, knob))) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
    }
}

// Texto suavizado (sin el hinting duro de GDI que lo hace ver pixelado), como en la configuración de Windows 11.
class SmoothLabel : Label
{
    public SmoothLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        UseMnemonic = false;
    }

    public static SizeF Measure(string text, Font font)
    {
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            return g.MeasureString(text ?? "", font, PointF.Empty, StringFormat.GenericTypographic);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var m = Measure(Text, Font);
        return new Size((int)Math.Ceiling(m.Width) + Padding.Horizontal + 2, (int)Math.Ceiling(Font.GetHeight()) + Padding.Vertical + 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        string align = TextAlign.ToString();
        var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = AutoEllipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None,
            Alignment = align.EndsWith("Center") ? StringAlignment.Center : align.EndsWith("Right") ? StringAlignment.Far : StringAlignment.Near,
            LineAlignment = align.StartsWith("Middle") ? StringAlignment.Center : align.StartsWith("Bottom") ? StringAlignment.Far : StringAlignment.Near
        };
        using (var b = new SolidBrush(ForeColor))
            g.DrawString(Text, Font, b, new RectangleF(Padding.Left, Padding.Top + 1, Width - Padding.Horizontal, Height - Padding.Vertical), fmt);
    }
}

// Botón de acento al estilo de Windows 11: esquinas de 4 px, borde sutil y estados de hover, presionado y deshabilitado.
class FluentButton : Control
{
    public Color Accent, Fore;
    readonly float s;
    bool hover, down;

    public FluentButton(float s, string text, Font font)
    {
        this.s = s;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Text = text;
        Font = font;
        Cursor = Cursors.Hand;
        Size = new Size(TextRenderer.MeasureText(text, font).Width + (int)(36 * s), (int)(32 * s));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = !Enabled ? Anim.Mix(Accent, BackColor, 0.55f)
            : down ? Anim.Mix(Accent, BackColor, 0.2f)
            : hover ? Anim.Mix(Accent, Color.White, 0.1f)
            : Accent;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Native.RoundRect(r, 4 * s))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            // Borde como en Windows 11: apenas más claro arriba y más oscuro abajo.
            if (Enabled && !down)
                using (var edge = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), Color.FromArgb(28, 255, 255, 255), Color.FromArgb(70, 0, 0, 0), 90f))
                using (var p = new Pen(edge, 1)) g.DrawPath(p, path);
        }
        Color text = Enabled ? Fore : Anim.Mix(Fore, fill, 0.35f);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var b = new SolidBrush(text)) g.DrawString(Text, Font, b, new RectangleF(0, down ? 1 : 0, Width, Height), fmt);
    }
}

// El "?" que muestra la explicación de un ajuste al pasar el mouse.
class HelpIcon : Control
{
    public Color Ink;

    public HelpIcon(float s)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size((int)(16 * s), (int)(16 * s));
        Font = new Font("Segoe UI Semibold", 7.5f);
        Cursor = Cursors.Help;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(BackColor);
        using (var p = new Pen(Ink, 1)) g.DrawEllipse(p, 0.5f, 0.5f, Width - 2, Height - 2);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var b = new SolidBrush(Ink)) g.DrawString("?", Font, b, new RectangleF(0, 0, Width - 1, Height - 0.5f), fmt);
    }
}

// Tarjeta con esquinas redondeadas; las filas se apilan con separadores finos.
class Card : Panel
{
    public readonly Color Fill, Border;
    readonly float s;
    readonly List<int> separators = new List<int>();
    public int Y;

    public Card(float s, int width, Color page, Color fill, Color border)
    {
        this.s = s;
        Fill = fill;
        Border = border;
        Width = width;
        BackColor = page;
        DoubleBuffered = true;
        Margin = new Padding(0, 0, 0, (int)(4 * s));
    }

    public int Pad { get { return (int)(16 * s); } }
    public int Inner { get { return Width - 2 * Pad; } }
    public bool HasRows { get { return Y > 0; } }

    public void Separator() { separators.Add(Y); }

    public void Reset()
    {
        foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
        separators.Clear();
        Y = 0;
    }

    public void Finish()
    {
        Height = Math.Max(Y, (int)(8 * s));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Native.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 6 * s))
        {
            using (var b = new SolidBrush(Fill)) g.FillPath(b, path);
            using (var p = new Pen(Border, 1)) g.DrawPath(p, path);
        }
        g.SmoothingMode = SmoothingMode.None;
        using (var p = new Pen(Border, 1)) foreach (var y in separators) g.DrawLine(p, Pad, y, Width - Pad, y);
    }
}

// Caja de texto plana con borde redondeado y línea de acento cuando tiene el foco, como en Windows 11.
class FieldBox : Panel
{
    public readonly TextBox Box;
    readonly Color fill, line, focus;
    readonly float s;

    public FieldBox(float s, int width, Color fill, Color fg, Color line, Color focus, bool secret, Font font)
    {
        this.s = s;
        this.fill = fill;
        this.line = line;
        this.focus = focus;
        DoubleBuffered = true;
        Size = new Size(width, (int)(32 * s));
        Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = fill, ForeColor = fg, UseSystemPasswordChar = secret, Font = font };
        Box.Width = width - (int)(22 * s);
        Box.Location = new Point((int)(11 * s), (Height - Box.PreferredHeight) / 2 + 1);
        Controls.Add(Box);
        Box.GotFocus += (o, e) => Invalidate();
        Box.LostFocus += (o, e) => Invalidate();
        Click += (o, e) => Box.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        using (var path = Native.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 4 * s))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var p = new Pen(line, 1)) g.DrawPath(p, path);
        }
        if (Box.Focused)
            using (var b = new SolidBrush(focus)) g.FillRectangle(b, 4 * s, Height - 2 * s, Width - 8 * s, 2 * s);
    }
}

// Ajustes de cada persona. Todo se aplica al momento: no hay botón de guardar.
class SettingsForm : Form
{
    readonly float s;
    readonly bool light;
    readonly Action onChanged;
    readonly Color page, cardFill, cardBorder, fg, muted, fieldFill, fieldLine, accent, good, bad;
    readonly Font bodyFont, titleFont, sectionFont, smallFont, iconFont;
    readonly ToolTip tips = new ToolTip { InitialDelay = 250, AutoPopDelay = 30000, ReshowDelay = 100 };
    readonly FlowLayoutPanel flow;
    readonly int contentW;
    readonly WinTimer calDelay = new WinTimer { Interval = 700 }, jiraDelay = new WinTimer { Interval = 700 };
    readonly FieldBox calUrl, jiraSite, jiraEmail, jiraToken;
    readonly Label calStatus, jiraStatus;
    readonly Card reposCard, googleCard;
    readonly FlowLayoutPanel googleActions;
    readonly int googleRowY;
    int calVersion, jiraVersion;

    public SettingsForm(bool light, Action onChanged)
    {
        this.light = light;
        this.onChanged = onChanged;
        s = DeviceDpi / 96f;
        accent = Native.AccentColor(light);
        page = light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
        cardFill = light ? Color.FromArgb(251, 251, 251) : Color.FromArgb(43, 43, 43);
        cardBorder = light ? Color.FromArgb(229, 229, 229) : Color.FromArgb(29, 29, 29);
        fg = light ? Color.FromArgb(27, 27, 27) : Color.White;
        muted = light ? Color.FromArgb(96, 96, 96) : Color.FromArgb(170, 170, 170);
        fieldFill = light ? Color.White : Color.FromArgb(55, 55, 55);
        fieldLine = light ? Color.FromArgb(213, 213, 213) : Color.FromArgb(68, 68, 68);
        good = light ? Color.FromArgb(15, 123, 15) : Color.FromArgb(108, 203, 95);
        bad = light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 153, 164);
        // Tamaños de la configuración de Windows 11: texto de 14 px, encabezados seminegrita y título de 28 px.
        bodyFont = InfoPopup.PickFont(new[] { "Segoe UI Variable Text", "Segoe UI" }, 14f * s);
        smallFont = InfoPopup.PickFont(new[] { "Segoe UI Variable Text", "Segoe UI" }, 12f * s);
        sectionFont = InfoPopup.PickFont(new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" }, 14f * s);
        titleFont = InfoPopup.PickFont(new[] { "Segoe UI Variable Display Semibold", "Segoe UI Variable Text Semibold", "Segoe UI Semibold" }, 28f * s);
        iconFont = InfoPopup.PickFont(new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }, 12f * s);

        Text = "TaskBar Monitor";
        AutoScaleMode = AutoScaleMode.None;
        Font = bodyFont;
        BackColor = page;
        ForeColor = fg;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { } // el ícono del anillo
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        contentW = S(560);
        ClientSize = new Size(contentW + S(64) + SystemInformation.VerticalScrollBarWidth, S(720));
        MinimumSize = new Size(Width, S(360));
        MaximumSize = new Size(Width, 10000);

        flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Padding = new Padding(S(32), S(20), S(32), S(32)), BackColor = page
        };
        Controls.Add(flow);
        flow.Controls.Add(new SmoothLabel { Text = "Ajustes", Font = titleFont, ForeColor = fg, BackColor = page, AutoSize = true, Margin = new Padding(0, 0, 0, S(4)) });

        Section("Indicadores", null);
        var card = NewCard();
        ToggleRow(card, "Recursos", "Uso de CPU y RAM. Al pasar el mouse ves qué programas consumen más y puedes detenerlos.", "ShowResources");
        ToggleRow(card, "Compilaciones", "Aparece mientras MSBuild compila: repositorio, rama y tiempo. Al terminar muestra si salió bien.", "ShowBuild");
        ToggleRow(card, "Claude", "Aparece cuando tienes sesiones de Claude Code y avisa cuál te hizo una pregunta, pide permiso o terminó. " +
            "Requiere conectar Claude Code más abajo.", "ShowClaude");
        ToggleRow(card, "Calendario", "Muestra cuánto falta para tu próxima reunión, desde una hora antes, y te deja unirte.", "ShowMeeting");
        ToggleRow(card, "Jira", "Muestra cuántos tickets asignados tienes por hacer y te deja abrirlos.", "ShowJira");
        ToggleRow(card, "Repositorios", "Avisa cuando un repositorio tiene cambios sin commit.", "ShowRepos");
        card.Finish();

        Section("Claude Code", null);
        card = NewCard();
        var claude = MakeToggle(ClaudeHooks.IsInstalled(ClaudeHooks.SettingsPath));
        claude.CheckedChanged += (o, e) =>
        {
            try
            {
                if (claude.Checked) ClaudeHooks.Install(ClaudeHooks.SettingsPath); else ClaudeHooks.Uninstall(ClaudeHooks.SettingsPath);
                onChanged();
            }
            catch (Exception ex)
            {
                claude.Checked = !claude.Checked;
                MessageBox.Show(this, "No se pudo cambiar la configuración de Claude Code.\n\n" + ex.Message, "Ajustes");
            }
        };
        Row(card, "Conectar con Claude Code", "Agrega hooks en ~/.claude/settings.json para saber qué sesión trabaja, " +
            "te hizo una pregunta, pide permiso o terminó. Aplica a las sesiones que abras después.", claude);
        card.Finish();

        Section("Calendario", null);
        card = NewCard();
        googleCard = card;
        googleRowY = card.Y;
        googleActions = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = cardFill, Margin = new Padding(0)
        };
        Row(card, "Cuenta de Google", Google.HasClient
            ? "Inicia sesión para que la app lea tus eventos (solo lectura). La sesión se guarda cifrada en este equipo."
            : "Para usarlo, coloca google-oauth.json junto al .exe de TaskBar Monitor. Mientras tanto puedes usar la dirección iCal.",
            googleActions);
        card.Separator();
        calUrl = FieldRow(card, "O dirección secreta iCal",
            "Alternativa sin iniciar sesión. En Google Calendar: Configuración → tu calendario → Integrar el calendario. " +
            "Se guarda cifrada en este equipo.", Config.GetSecret("CalendarIcsUrl"), true, null);
        calStatus = StatusRow(card);
        card.Finish();
        UpdateGoogleRow();
        calUrl.Box.TextChanged += (o, e) => Restart(calDelay);
        calDelay.Tick += (o, e) => { calDelay.Stop(); ApplyCalendar(true); };

        Section("Jira", null);
        card = NewCard();
        jiraSite = FieldRow(card, "Sitio", "Por ejemplo tuempresa.atlassian.net, o solo tuempresa.", Config.Get("JiraSite", ""), false, null);
        card.Separator();
        jiraEmail = FieldRow(card, "Correo", null, Config.Get("JiraEmail", ""), false, null);
        card.Separator();
        jiraToken = FieldRow(card, "Token de API", "Atlassian pide el correo junto con el token. Se guarda cifrado en este equipo.",
            Config.GetSecret("JiraToken"), true, Link("Crear token", "https://id.atlassian.com/manage-profile/security/api-tokens", cardFill));
        jiraStatus = StatusRow(card);
        card.Separator();
        var clickUrl = FieldRow(card, "Al hacer clic en el ícono, abrir",
            "Dirección que se abre al hacer clic en el ícono de Jira del taskbar, por ejemplo tu tablero. Si la dejas vacía, abre tus tickets abiertos.",
            Config.Get("JiraClickUrl", ""), false, null);
        clickUrl.Box.TextChanged += (o, e) => Config.Set("JiraClickUrl", clickUrl.Box.Text.Trim());
        card.Separator();
        Row(card, "Agrupar por", "Cómo se agrupan los tickets en la tarjeta de Jira.", Select(Jira.Groups, "JiraGroup", "status", false));
        card.Separator();
        Row(card, "Ordenar por", "Orden de los tickets dentro de cada grupo.", Select(Jira.Sorts, "JiraSort", "updated", true));
        card.Separator();
        Row(card, "Dirección", "Descendente: lo más reciente o importante primero. En \"Orden de atención\", ascendente sigue el orden del tablero.",
            Select(Jira.Directions, "JiraSortDir", "desc", true));
        card.Finish();
        foreach (var f in new[] { jiraSite, jiraEmail, jiraToken }) f.Box.TextChanged += (o, e) => Restart(jiraDelay);
        jiraDelay.Tick += (o, e) => { jiraDelay.Stop(); ApplyJira(true); };

        Section("Repositorios", "Cada carpeta puede ser un repositorio o contener varios. Se usan para avisar cambios sin commit " +
            "y para saber en qué repositorio corre una compilación.");
        reposCard = NewCard();
        BuildRepos();
        // El relleno inferior de un FlowLayoutPanel con scroll no cuenta: este espacio evita que la última tarjeta quede cortada.
        flow.Controls.Add(new Panel { Size = new Size(1, S(28)), BackColor = page, Margin = new Padding(0) });

        // Al abrir, muestra cómo están las conexiones que ya están configuradas.
        TestCalendar();
        if (jiraSite.Box.Text.Trim() != "" && jiraEmail.Box.Text.Trim() != "" && jiraToken.Box.Text.Trim() != "")
            TestJira(Jira.Normalize(jiraSite.Box.Text), jiraEmail.Box.Text.Trim(), jiraToken.Box.Text.Trim());
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = light ? 0 : 1;
        Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4); // barra de título oscura en tema oscuro
    }

    // Al volver a la ventana, la cuenta de Google refleja lo que haya cambiado (por ejemplo, si se agregó google-oauth.json).
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        UpdateGoogleRow();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Lo que se escribió justo antes de cerrar también se guarda.
        if (calDelay.Enabled) { calDelay.Stop(); ApplyCalendar(false); }
        if (jiraDelay.Enabled) { jiraDelay.Stop(); ApplyJira(false); }
        base.OnFormClosing(e);
    }

    int S(float v) { return (int)Math.Round(v * s); }

    static void Restart(WinTimer t) { t.Stop(); t.Start(); }

    static string WrapText(string text, int width)
    {
        var sb = new StringBuilder();
        int col = 0;
        foreach (var word in text.Split(' '))
        {
            if (col > 0 && col + word.Length > width) { sb.Append('\n'); col = 0; }
            else if (col > 0) { sb.Append(' '); col++; }
            sb.Append(word);
            col += word.Length;
        }
        return sb.ToString();
    }

    Label MakeLabel(string text, Font font, Color color, Color bg)
    {
        var l = new SmoothLabel { Text = text, Font = font, ForeColor = color, BackColor = bg, AutoSize = false };
        l.Size = l.GetPreferredSize(Size.Empty);
        return l;
    }

    HelpIcon Help(string text, Color bg)
    {
        var h = new HelpIcon(s) { BackColor = bg, Ink = muted };
        tips.SetToolTip(h, WrapText(text, 60));
        return h;
    }

    void Section(string title, string help)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = page,
            Margin = new Padding(0, S(18), 0, S(8))
        };
        row.Controls.Add(new SmoothLabel { Text = title, Font = sectionFont, ForeColor = fg, BackColor = page, AutoSize = true, Margin = new Padding(0) });
        if (help != null)
        {
            var h = Help(help, page);
            h.Margin = new Padding(S(6), S(3), 0, 0);
            row.Controls.Add(h);
        }
        flow.Controls.Add(row);
    }

    Card NewCard()
    {
        var c = new Card(s, contentW, page, cardFill, cardBorder);
        flow.Controls.Add(c);
        return c;
    }

    // Lista desplegable plana; options es { valor, texto, ... } por fila. Al cambiar se guarda y se recarga Jira.
    ComboBox Select(string[,] options, string key, string def, bool reloads)
    {
        var box = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = bodyFont,
            BackColor = fieldFill, ForeColor = fg, Width = S(220)
        };
        string current = Config.Get(key, def);
        for (int i = 0; i < options.GetLength(0); i++)
        {
            box.Items.Add(options[i, 1]);
            if (options[i, 0] == current) box.SelectedIndex = i;
        }
        if (box.SelectedIndex < 0) box.SelectedIndex = 0;
        box.SelectedIndexChanged += (o, e) =>
        {
            Config.Set(key, options[box.SelectedIndex, 0]);
            if (reloads) onChanged(); // el orden lo hace Jira; agrupar se aplica al dibujar la tarjeta
        };
        return box;
    }

    ToggleSwitch MakeToggle(bool value)
    {
        var t = new ToggleSwitch(s)
        {
            Accent = accent, Track = cardFill, TrackBorder = muted, Thumb = muted,
            OnThumb = light ? Color.White : Color.Black, BackColor = cardFill
        };
        t.Checked = value;
        return t;
    }

    void ToggleRow(Card card, string title, string help, string key)
    {
        var t = MakeToggle(Config.Flag(key));
        t.CheckedChanged += (o, e) => { Config.Set(key, t.Checked ? "1" : "0"); onChanged(); };
        if (card.HasRows) card.Separator();
        Row(card, title, help, t);
    }

    // Fila de una línea: título (con su "?") a la izquierda y el control a la derecha.
    void Row(Card card, string title, string help, Control right)
    {
        int h = S(52);
        var l = MakeLabel(title, bodyFont, fg, card.Fill);
        l.Location = new Point(card.Pad, card.Y + (h - l.Height) / 2);
        card.Controls.Add(l);
        if (help != null)
        {
            var hi = Help(help, card.Fill);
            hi.Location = new Point(l.Right + S(4), card.Y + (h - hi.Height) / 2);
            card.Controls.Add(hi);
        }
        right.Location = new Point(card.Width - card.Pad - right.Width, card.Y + (h - right.Height) / 2);
        card.Controls.Add(right);
        card.Y += h;
    }

    // Fila con título arriba y caja de texto a todo lo ancho.
    FieldBox FieldRow(Card card, string title, string help, string value, bool secret, Control extra)
    {
        var l = MakeLabel(title, bodyFont, fg, card.Fill);
        l.Location = new Point(card.Pad, card.Y + S(14));
        card.Controls.Add(l);
        if (help != null)
        {
            var hi = Help(help, card.Fill);
            hi.Location = new Point(l.Right + S(4), l.Top + (l.Height - hi.Height) / 2);
            card.Controls.Add(hi);
        }
        if (extra != null)
        {
            extra.Location = new Point(card.Width - card.Pad - extra.Width, l.Top + (l.Height - extra.Height) / 2);
            card.Controls.Add(extra);
        }
        var f = new FieldBox(s, card.Inner, fieldFill, fg, fieldLine, accent, secret, bodyFont) { BackColor = card.Fill };
        f.Box.Text = value;
        f.Location = new Point(card.Pad, l.Bottom + S(6));
        card.Controls.Add(f);
        card.Y = f.Bottom + S(14);
        return f;
    }

    Label StatusRow(Card card)
    {
        var l = new SmoothLabel
        {
            AutoSize = false, Size = new Size(card.Inner, S(20)), Location = new Point(card.Pad, card.Y - S(6)),
            BackColor = card.Fill, ForeColor = muted, Font = smallFont, AutoEllipsis = true
        };
        card.Controls.Add(l);
        card.Y += S(18);
        return l;
    }

    Label Link(string text, string url, Color bg)
    {
        var l = MakeLabel(text, smallFont, accent, bg);
        l.Cursor = Cursors.Hand;
        l.Click += (o, e) => Native.Open(url);
        return l;
    }

    Label IconButton(string glyph, string tip, Color bg)
    {
        var b = new SmoothLabel
        {
            Text = glyph, Font = iconFont, ForeColor = muted, BackColor = bg, Size = new Size(S(28), S(28)),
            TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand
        };
        b.MouseEnter += (o, e) => { b.BackColor = fieldLine; b.ForeColor = fg; };
        b.MouseLeave += (o, e) => { b.BackColor = bg; b.ForeColor = muted; };
        tips.SetToolTip(b, tip);
        return b;
    }

    void SetStatus(Label l, string text, Color color)
    {
        l.Text = text;
        l.ForeColor = color;
    }

    // Corre la comprobación en segundo plano; solo se muestra el resultado de la última que se pidió.
    void Background(Func<string> work, Action<bool, string> done)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string result;
            bool ok = true;
            try { result = work(); }
            catch (Exception ex) { ok = false; result = ex.Message; }
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed) done(ok, result); })); } catch { }
        });
    }

    void ApplyCalendar(bool test)
    {
        string url = Calendar.Normalize(calUrl.Box.Text);
        if (url != "" && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(calStatus, "La dirección debe empezar con https://", bad);
            return;
        }
        Config.SetSecret("CalendarIcsUrl", url);
        onChanged();
        if (test) TestCalendar();
    }

    // Comprueba la fuente que va a usar el widget: la cuenta de Google si hay sesión, si no la dirección iCal.
    void TestCalendar()
    {
        bool google = Google.SignedIn && Google.HasClient;
        string url = Calendar.Normalize(calUrl.Box.Text);
        if (!google && url == "") { calVersion++; SetStatus(calStatus, "", muted); return; }
        int v = ++calVersion;
        SetStatus(calStatus, "Comprobando…", muted);
        Background(() =>
        {
            List<Meeting> list;
            if (google) list = Google.Fetch();
            else
            {
                try { list = Calendar.Fetch(url); }
                catch (WebException) { throw new Exception("No se pudo leer el calendario. Revisa la dirección."); }
            }
            int today = list.Count(m => m.Start.Date == DateTime.Today);
            return (google ? "Conectado con Google · " : "Conectado · ") + (today == 1 ? "1 reunión hoy" : today + " reuniones hoy");
        }, (ok, text) => { if (v == calVersion) SetStatus(calStatus, (ok ? "● " : "") + text, ok ? good : bad); });
    }

    void UpdateGoogleRow()
    {
        googleActions.SuspendLayout();
        foreach (Control c in googleActions.Controls.Cast<Control>().ToList()) c.Dispose();
        if (!Google.HasClient)
        {
            var l = MutedLabel("No configurado");
            l.Margin = new Padding(0);
            googleActions.Controls.Add(l);
        }
        else if (Google.SignedIn)
        {
            googleActions.Controls.Add(MutedLabel(Config.Get("GoogleEmail", "Sesión iniciada")));
            googleActions.Controls.Add(Link("Cerrar sesión", () =>
            {
                Google.SignOut();
                onChanged();
                UpdateGoogleRow();
                TestCalendar();
            }));
        }
        else
        {
            var button = AccentButton("Iniciar sesión");
            button.Click += (o, e) => SignInGoogle(button);
            googleActions.Controls.Add(button);
        }
        googleActions.ResumeLayout();
        var size = googleActions.PreferredSize;
        googleActions.Size = size;
        googleActions.Location = new Point(googleCard.Width - googleCard.Pad - size.Width, googleRowY + (S(52) - size.Height) / 2);
    }

    void SignInGoogle(Control button)
    {
        button.Enabled = false;
        calVersion++;
        SetStatus(calStatus, "Termina el inicio de sesión en el navegador…", muted);
        Background(Google.SignIn, (ok, text) =>
        {
            if (!ok) { button.Enabled = true; SetStatus(calStatus, text, bad); return; }
            onChanged();
            UpdateGoogleRow();
            TestCalendar();
        });
    }

    Label MutedLabel(string text)
    {
        var l = MakeLabel(text, bodyFont, muted, cardFill);
        l.Margin = new Padding(0, 0, S(12), 0);
        return l;
    }

    Label Link(string text, Action click)
    {
        var l = MakeLabel(text, bodyFont, accent, cardFill);
        l.Margin = new Padding(0);
        l.Cursor = Cursors.Hand;
        l.Click += (o, e) => click();
        return l;
    }

    FluentButton AccentButton(string text)
    {
        return new FluentButton(s, text, bodyFont)
        {
            Accent = accent, Fore = accent.GetBrightness() > 0.6f ? Color.Black : Color.White,
            BackColor = cardFill, Margin = new Padding(0)
        };
    }

    void ApplyJira(bool test)
    {
        string site = Jira.Normalize(jiraSite.Box.Text), email = jiraEmail.Box.Text.Trim(), token = jiraToken.Box.Text.Trim();
        Config.Set("JiraSite", site);
        Config.Set("JiraEmail", email);
        Config.SetSecret("JiraToken", token);
        onChanged();
        if (site == "" || email == "" || token == "")
        {
            jiraVersion++;
            SetStatus(jiraStatus, site + email + token == "" ? "" : "Falta completar sitio, correo o token", muted);
            return;
        }
        if (test) TestJira(site, email, token);
    }

    void TestJira(string site, string email, string token)
    {
        int v = ++jiraVersion;
        SetStatus(jiraStatus, "Comprobando…", muted);
        Background(() =>
        {
            string name;
            try { name = Jira.Myself(site, email, token); }
            catch (Exception ex) { throw new Exception(Jira.Describe(ex)); }
            var info = Jira.Load(site, email, token, Config.Get("JiraSort", "updated"));
            if (info.Error != "") throw new Exception(info.Error);
            return "Conectado como " + name + " · " + (info.Issues.Count == 1 ? "1 ticket abierto" : info.Issues.Count + " tickets abiertos");
        }, (ok, text) => { if (v == jiraVersion) SetStatus(jiraStatus, (ok ? "● " : "") + text, ok ? good : bad); });
    }

    static List<string> RepoFolders()
    {
        return Config.Get("ReposRoots", Config.Get("ReposRoot", "")).Split('|').Select(p => p.Trim())
            .Where(p => p != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    void SaveRepos(List<string> folders)
    {
        Config.Set("ReposRoots", string.Join("|", folders));
        onChanged();
        BuildRepos();
    }

    void BuildRepos()
    {
        reposCard.SuspendLayout();
        reposCard.Reset();
        int h = S(48);
        foreach (var folder in RepoFolders())
        {
            var path = folder;
            if (reposCard.HasRows) reposCard.Separator();
            var remove = IconButton(Icons.Cancel, "Quitar carpeta", cardFill);
            remove.Click += (o, e) =>
            {
                var list = RepoFolders();
                list.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                SaveRepos(list);
            };
            var l = MakeLabel(path, bodyFont, fg, cardFill);
            l.AutoEllipsis = true;
            l.Width = Math.Min(l.Width, reposCard.Inner - remove.Width - S(12));
            l.Location = new Point(reposCard.Pad, reposCard.Y + (h - l.Height) / 2);
            remove.Location = new Point(reposCard.Width - reposCard.Pad - remove.Width + S(6), reposCard.Y + (h - remove.Height) / 2);
            reposCard.Controls.Add(l);
            reposCard.Controls.Add(remove);
            reposCard.Y += h;
        }
        if (reposCard.HasRows) reposCard.Separator();
        var add = MakeLabel("+  Agregar carpeta", bodyFont, accent, cardFill);
        add.Cursor = Cursors.Hand;
        add.Location = new Point(reposCard.Pad, reposCard.Y + (h - add.Height) / 2);
        add.Click += (o, e) =>
        {
            using (var dlg = new FolderBrowserDialog { Description = "Carpeta de repositorios" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var list = RepoFolders();
                if (!list.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase)) list.Add(dlg.SelectedPath);
                SaveRepos(list);
            }
        };
        reposCard.Controls.Add(add);
        reposCard.Y += h;
        reposCard.Finish();
        reposCard.ResumeLayout();
    }
}
#endregion

class MonitorForm : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "TaskBar Monitor";
    static readonly string[] Order = { "cpu", "ram", "build", "claude", "jira", "wt", "meet", "idle" }; // el calendario siempre al final

    static readonly Color Warn = Color.FromArgb(239, 159, 39);
    static readonly Color High = Color.FromArgb(226, 75, 74);
    static readonly Color Good = Color.FromArgb(99, 153, 34);
    static readonly Color Blue = Color.FromArgb(55, 138, 221);
    static readonly Color Clay = Color.FromArgb(217, 119, 87);

    // Procesos del sistema que no se ofrecen para detener.
    static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "services", "lsass", "winlogon", "svchost", "dwm", "explorer",
        "MsMpEng", "Memory Compression", "fontdrvhost", "Secure System", "LsaIso", "spoolsv", "sihost", "ctfmon",
        "audiodg", "conhost", "WUDFHost", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TaskbarMonitor", "TaskBar Monitor"
    };

    readonly WinTimer timer = new WinTimer { Interval = 1000 };
    readonly WinTimer animTimer = new WinTimer { Interval = 16 };
    readonly WinTimer hoverTimer = new WinTimer { Interval = 40 };
    readonly InfoPopup popup = new InfoPopup();
    SettingsForm settings;

    long prevIdle, prevKernel, prevUser;
    float cpu, ram, shownCpu, shownRam;
    double ramUsedGb, ramTotalGb;
    readonly List<float> cpuHist = new List<float>(), ramHist = new List<float>();
    int hotSeconds, lastSampleTick, lastFrameTick, lastInsideTick;
    bool lightTheme, vertical;
    readonly float scale;
    readonly Font textFont, glyphFont, numberFont, smallNumberFont;
    readonly Dictionary<string, float> presence = new Dictionary<string, float>(), highlight = new Dictionary<string, float>();
    readonly List<KeyValuePair<string, RectangleF>> layout = new List<KeyValuePair<string, RectangleF>>();
    float separatorAlpha;
    string hovered;
    Rectangle taskbar;
    uint edge = Native.ABE_BOTTOM;
    string notice, noticeKey;
    int noticeUntil;
    string frozenKey;
    List<string> frozenNames = new List<string>();
    float ramLo, ramHi;
    int ramScaleTick;
    bool ramScaleSet;

    // El número de cada anillo salta directo al valor nuevo, con un fundido corto entre el anterior y el nuevo.
    class NumberFade { public string Cur = "", Prev = ""; public int Tick = -100000; }
    const int NumberFadeMs = 220;
    readonly Dictionary<string, NumberFade> numbers = new Dictionary<string, NumberFade>();

    // Lo actualiza el hilo de fondo; la interfaz solo lee estas instantáneas.
    volatile List<ProcStat> procs = new List<ProcStat>();
    volatile List<ClaudeSession> sessions = new List<ClaudeSession>();
    volatile List<Meeting> meetings = new List<Meeting>();
    volatile List<RepoInfo> repos = new List<RepoInfo>();
    volatile JiraInfo jira = new JiraInfo();
    volatile BuildInfo[] buildsRunning = new BuildInfo[0], buildsDone = new BuildInfo[0];
    volatile bool refreshCalendar, refreshJira, refreshRepos, calendarConfigured;
    volatile string calendarError = "";

    public MonitorForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        scale = DeviceDpi / 96f;
        // Las mismas que usa el taskbar de Windows 11: texto Segoe UI Variable a 12 px y glifos Segoe Fluent Icons.
        textFont = InfoPopup.PickFont(new[] { "Segoe UI Variable Text", "Segoe UI" }, 12f * scale);
        glyphFont = InfoPopup.PickFont(new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }, 16f * scale);
        numberFont = InfoPopup.PickFont(new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" }, 11f * scale);
        smallNumberFont = InfoPopup.PickFont(new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" }, 9f * scale);
        foreach (var k in Order) { presence[k] = k == "cpu" || k == "ram" ? 1 : 0; highlight[k] = 0; }
        popup.S = scale;
        popup.Source = Panel;
        RegisterStartup();
        Safe(Installer.Register); // aparece en Inicio y en Aplicaciones instaladas
        Safe(() => ClaudeHooks.Repair(ClaudeHooks.SettingsPath));

        Native.GetSystemTimes(out prevIdle, out prevKernel, out prevUser);

        timer.Tick += (s, e) => Tick();
        animTimer.Tick += (s, e) => Frame();
        hoverTimer.Tick += (s, e) => TrackMouse();
        clickTimer.Tick += (s, e) => { clickTimer.Stop(); if (pendingClick != null) PrimaryAction(pendingClick); };
        timer.Start();
        hoverTimer.Start();
        new Thread(Worker) { IsBackground = true }.Start();

        var openRequest = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsEvent);
        new Thread(() =>
        {
            while (true)
            {
                openRequest.WaitOne();
                try { BeginInvoke((Action)OpenSettings); } catch { }
            }
        }) { IsBackground = true }.Start();
    }

    public const string OpenSettingsEvent = "TaskbarMonitor_OpenSettings";

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Tick();
        if (!Config.Exists) BeginInvoke((Action)OpenSettings); // primera vez: que la persona configure lo suyo
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x80000 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    // Clic: la acción del indicador. Doble clic o clic derecho: ajustes. El clic simple espera el tiempo de doble clic
    // de Windows para no ejecutarse cuando en realidad era un doble clic.
    readonly WinTimer clickTimer = new WinTimer();
    string pendingClick;
    bool skipNextUp;

    // Arrastrar con el botón izquierdo mueve el widget a lo largo de la barra; la posición se guarda.
    int offset = (int)Config.GetInt("Offset", 0), dragStartX, dragStartOffset;
    bool pressing, dragging;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || vertical) return;
        pressing = true;
        dragging = false;
        dragStartX = Cursor.Position.X;
        dragStartOffset = offset;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!pressing) return;
        int dx = Cursor.Position.X - dragStartX;
        if (!dragging && Math.Abs(dx) < SystemInformation.DragSize.Width) return;
        if (!dragging) { dragging = true; clickTimer.Stop(); popup.Close(false); Cursor = Cursors.SizeWE; }
        offset = dragStartOffset + dx;
        Reposition();
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right) { clickTimer.Stop(); OpenSettings(); return; }
        if (e.Button != MouseButtons.Left) return;
        pressing = false;
        if (dragging)
        {
            dragging = false;
            Cursor = Cursors.Default;
            Config.Set("Offset", offset.ToString());
            return; // un arrastre no es un clic
        }
        if (skipNextUp) { skipNextUp = false; return; }
        pendingClick = ItemAt(Cursor.Position);
        if (pendingClick == null) return;
        clickTimer.Stop();
        clickTimer.Interval = SystemInformation.DoubleClickTime;
        clickTimer.Start();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        clickTimer.Stop();
        skipNextUp = true;
        OpenSettings();
    }

    void PrimaryAction(string key)
    {
        popup.Close(false);
        switch (key)
        {
            case "cpu":
            case "ram":
                Native.Open("taskmgr.exe");
                break;
            case "claude":
                // La sesión más urgente (las que te esperan van primero); sin sesiones, solo trae la app al frente.
                var list = sessions;
                if (list.Count > 0) OpenClaudeSession(list[0].Id); else FocusClaude();
                break;
            case "meet":
                bool inProgress;
                var m = CurrentMeeting(out inProgress);
                Native.Open(m != null && m.Link != "" ? m.Link : "https://calendar.google.com/");
                break;
            case "jira":
                var j = jira;
                if (j.Error != "") OpenSettings();
                else
                {
                    // La dirección que eligió la persona en Ajustes; si no hay, sus tickets abiertos.
                    string url = Config.Get("JiraClickUrl", "");
                    Native.Open(url != "" ? url : j.Site + "/issues/?jql=" + Uri.EscapeDataString(Jira.Jql(Config.Get("JiraSort", "updated"))));
                }
                break;
            case "wt":
                var dirty = repos.FirstOrDefault(r => r.Changes > 0);
                if (dirty != null) Native.Open(dirty.Path);
                break;
            case "build":
                var b = buildsRunning.Length > 0 ? buildsRunning[0] : buildsDone.FirstOrDefault();
                var repo = b != null ? repos.FirstOrDefault(r => r.Name == b.Repo) : null;
                if (repo != null) Native.Open(repo.Path);
                break;
            default:
                OpenSettings();
                break;
        }
    }

    #region Ciclo principal
    void Tick()
    {
        long idle, kernel, user;
        Native.GetSystemTimes(out idle, out kernel, out user);
        long total = (kernel - prevKernel) + (user - prevUser); // kernel incluye idle
        if (total > 0) cpu = Math.Max(0, Math.Min(100, 100f * (total - (idle - prevIdle)) / total));
        prevIdle = idle; prevKernel = kernel; prevUser = user;

        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX)) };
        if (Native.GlobalMemoryStatusEx(ref m))
        {
            ramTotalGb = m.ullTotalPhys / 1073741824.0;
            ramUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            ram = (float)(100.0 * (m.ullTotalPhys - m.ullAvailPhys) / m.ullTotalPhys);
        }
        Push(cpuHist, cpu);
        Push(ramHist, ram);
        lastSampleTick = Environment.TickCount;
        hotSeconds = cpu >= 90 ? hotSeconds + 1 : 0;

        var theme = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        lightTheme = theme != null && Convert.ToInt32(theme.GetValue("SystemUsesLightTheme", 0)) == 1;
        popup.Light = lightTheme;
        taskbar = Native.TaskbarRect(out edge);
        vertical = edge == Native.ABE_LEFT || edge == Native.ABE_RIGHT;

        bool fullscreen = ForegroundIsFullscreen();
        if (fullscreen && Visible) { popup.Close(false); Hide(); }
        else if (!fullscreen && !Visible) Show();
        if (Visible) Native.KeepOnTop(Handle);
        Frame();
    }

    static void Push(List<float> list, float v)
    {
        list.Add(v);
        if (list.Count > 61) list.RemoveAt(0);
    }

    // Un cuadro de animación: acerca los valores mostrados a los reales, redibuja y decide si hace falta otro.
    void Frame()
    {
        if (!IsHandleCreated) return;
        int now = Environment.TickCount;
        float dt = Math.Max(1, Math.Min(100, now - lastFrameTick));
        lastFrameTick = now;

        shownCpu = Anim.Approach(shownCpu, cpu, dt, 260);
        shownRam = Anim.Approach(shownRam, ram, dt, 260);
        bool moving = shownCpu != cpu || shownRam != ram || numbers.Values.Any(n => now - n.Tick < NumberFadeMs);
        var visible = new HashSet<string>(VisibleItems());
        foreach (var k in Order)
        {
            float target = visible.Contains(k) ? 1 : 0;
            presence[k] = Anim.Approach(presence[k], target, dt, 110);
            float hl = (hovered == k || popup.Key == k) ? 1 : 0;
            highlight[k] = Anim.Approach(highlight[k], hl, dt, 70);
            moving |= presence[k] != target || highlight[k] != hl;
        }

        if (Visible)
        {
            Reposition();
            Render();
            if (popup.Visible)
            {
                var anchor = AnchorFor(popup.Key);
                if (anchor.HasValue) popup.AnchorRect = anchor.Value;
                popup.Frame();
            }
        }
        animTimer.Enabled = Visible && (moving || popup.Visible || hotSeconds >= 30 || buildsRunning.Length > 0 || sessions.Any(s => s.NeedsYou));
    }

    // El panel se abre al pasar sobre un indicador y sigue abierto mientras el mouse esté en el indicador o en el panel.
    void TrackMouse()
    {
        if (dragging) return; // mientras se arrastra no se abren paneles
        var p = Cursor.Position;
        string key = Visible && Bounds.Contains(p) ? ItemAt(p) : null;
        if (key != hovered) { hovered = key; Frame(); }
        int now = Environment.TickCount;
        if (key != null)
        {
            lastInsideTick = now;
            if (popup.Key != key || !popup.IsOpen)
            {
                var anchor = AnchorFor(key);
                if (anchor.HasValue) popup.OpenFor(key, anchor.Value, edge);
                Frame();
            }
        }
        else if (popup.Contains(p) || (Visible && Bounds.Contains(p))) lastInsideTick = now;
        else if (popup.IsOpen && now - lastInsideTick > 350) { popup.Close(true); Frame(); }
    }

    void Worker()
    {
        for (int t = 0; ; t++)
        {
            if (t % 60 == 0 || refreshRepos) { refreshRepos = false; Safe(() => repos = LoadRepos()); }
            if (t % 2 == 0)
            {
                Safe(() => procs = SampleProcesses());
                Safe(CheckBuilds);
                Safe(() => sessions = ClaudeSession.LoadAll());
            }
            if (t % 300 == 0 || refreshJira) { refreshJira = false; Safe(() => jira = LoadJira()); }
            if (t % 600 == 0 || refreshCalendar) { refreshCalendar = false; Safe(LoadCalendar); }
            Thread.Sleep(1000);
        }
    }

    static void Safe(Action a) { try { a(); } catch { } }
    #endregion

    #region Ubicación y dibujo
    List<string> VisibleItems()
    {
        var keys = new List<string>();
        if (Config.Flag("ShowResources")) { keys.Add("cpu"); keys.Add("ram"); }
        var done = buildsDone;
        if (Config.Flag("ShowBuild") && (buildsRunning.Length > 0 ||
            (done.Length > 0 && done[0].End.HasValue && (DateTime.Now - done[0].End.Value).TotalMinutes < 10)))
            keys.Add("build");
        if (Config.Flag("ShowClaude") && sessions.Count > 0) keys.Add("claude");
        if (Config.Flag("ShowJira") && jira.Configured) keys.Add("jira");
        if (Config.Flag("ShowRepos") && repos.Any(r => r.Changes > 0)) keys.Add("wt");
        if (Config.Flag("ShowMeeting") && calendarConfigured) keys.Add("meet");
        if (keys.Count == 0) keys.Add("idle"); // con todo apagado queda un punto para poder abrir los ajustes
        return keys;
    }

    void Reposition()
    {
        if (taskbar.Width == 0) return;
        var size = ComputeLayout();
        int margin = (int)(8 * scale), x, y;
        if (vertical)
        {
            size.Width = Math.Min(size.Width, taskbar.Width - 4);
            x = taskbar.Left + (taskbar.Width - size.Width) / 2;
            y = taskbar.Top + margin;
        }
        else
        {
            size.Height = Math.Min(size.Height, taskbar.Height - 4);
            // Posición elegida arrastrando el widget; siempre dentro de la barra.
            offset = Math.Max(0, Math.Min(offset, taskbar.Width - size.Width - 2 * margin));
            x = taskbar.Left + margin + offset;
            y = taskbar.Top + (taskbar.Height - size.Height) / 2;
        }
        var bounds = new Rectangle(new Point(x, y), size);
        if (bounds != Bounds) Bounds = bounds;
    }

    // Medidas del botón de Inicio de Windows 11: fondo de 40 × 40 px con el ícono de 24 px centrado (8 px de relleno)
    // y esquinas de 4 px. Los indicadores van pegados; solo la barra separa CPU/RAM del resto.
    // En taskbars más bajos (Windows 10, íconos pequeños) todo se achica para caber en la barra real.
    float BoxH
    {
        get
        {
            float bar = vertical ? taskbar.Width : taskbar.Height;
            return bar > 0 ? Math.Min(40 * scale, bar - 4 * scale) : 40 * scale;
        }
    }
    float SeparatorSpace { get { return 9 * scale; } }
    float Slot { get { return Math.Min(24 * scale, BoxH - 6 * scale); } } // 24 px: el espacio del ícono, como en los botones nativos
    float Pad { get { return (BoxH - Slot) / 2; } }
    float HPad { get { return 10 * scale; } }
    float RingPad { get { return 5 * scale; } }
    float RingD { get { return Slot; } }
    float GlyphSize { get { return 16 * scale; } }

    // Los indicadores entran y salen animando su ancho, así los demás se acomodan sin saltos.
    Size ComputeLayout()
    {
        layout.Clear();
        separatorAlpha = Math.Min(Math.Max(presence["cpu"], presence["ram"]), Order.Skip(2).Where(k => k != "idle").Max(k => presence[k]));
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
        {
            float pos = 0;
            bool separated = false;
            foreach (var k in Order)
            {
                float p = presence[k];
                if (p <= 0.001f) continue;
                float e = Anim.EaseInOut(p);
                if (k != "cpu" && k != "ram" && k != "idle" && !separated && layout.Count > 0)
                {
                    pos += SeparatorSpace * separatorAlpha;
                    separated = true;
                }
                float size = (vertical ? BoxH : ItemWidth(g, k)) * e;
                layout.Add(new KeyValuePair<string, RectangleF>(k, vertical ? new RectangleF(0, pos, BoxH, size) : new RectangleF(pos, 0, size, BoxH)));
                pos += size;
            }
            int box = (int)Math.Ceiling(BoxH), length = Math.Max(1, (int)Math.Ceiling(pos));
            return vertical ? new Size(box, length) : new Size(length, box);
        }
    }

    float ItemWidth(Graphics g, string key)
    {
        // CPU y RAM son solo el anillo: un botón cuadrado de 40 px, como el de Inicio.
        // Solo el calendario lleva texto, y solo cuando hay una reunión en curso o cerca.
        // Relleno horizontal de 4 px, como el botón "Mostrar íconos ocultos": glifos en 24 px, anillos en 32 px.
        if (vertical) return BoxH;
        if (key == "cpu" || key == "ram") return RingPad + RingD + RingPad;
        if (key != "meet" || Line(key) == "") return HPad + GlyphSize + HPad;
        return HPad + GlyphSize + 6 * scale + Measure(g, Line(key)) + HPad + 2 * scale;
    }

    float Measure(Graphics g, string text)
    {
        return g.MeasureString(text, textFont, PointF.Empty, StringFormat.GenericTypographic).Width;
    }

    // Texto corto junto al ícono; el detalle está en el panel y el estado en el punto de color.
    string Line(string key)
    {
        switch (key)
        {
            case "build":
                if (buildsRunning.Length > 0) return "Compilando";
                var done = buildsDone;
                return done.Length > 0 && done[0].Ok == true ? "Compilado" : "Falló";
            case "claude":
                return "Claude";
            case "meet":
                bool inProgress;
                var m = CurrentMeeting(out inProgress);
                if (m == null) return "";
                // Nombre de la reunión (máximo 16 caracteres) y cuánto falta.
                return Fmt.Cut(m.Title, 16) + " · " + (inProgress ? "Ahora" : Fmt.Until(m.Start - DateTime.Now));
            case "jira":
                return jira.Error != "" ? "Jira" : "Jira " + jira.ToDo;
            case "wt":
                int n = repos.Count(r => r.Changes > 0);
                return n == 1 ? "1 repo" : n + " repos";
            default:
                return "";
        }
    }

    // Mismo color que el texto nativo del taskbar.
    Color Fg() { return lightTheme ? Color.Black : Color.White; }

    static Color Level(float v, Color normal) { return v >= 85 ? High : v >= 60 ? Warn : normal; }

    string ItemAt(Point screen)
    {
        var local = new PointF(screen.X - Left, screen.Y - Top);
        foreach (var item in layout) if (presence[item.Key] > 0.5f && item.Value.Contains(local)) return item.Key;
        return null;
    }

    Rectangle? AnchorFor(string key)
    {
        foreach (var item in layout)
            if (item.Key == key) return Rectangle.Round(new RectangleF(Left + item.Value.X, Top + item.Value.Y, item.Value.Width, item.Value.Height));
        return null;
    }

    void Render()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        using (var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                // Alfa 1: invisible, pero la ventana sigue recibiendo el mouse.
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Color fg = Fg();
                var extra = layout.FirstOrDefault(i => i.Key != "cpu" && i.Key != "ram");
                if (extra.Key != null && separatorAlpha > 0.01f)
                    using (var p = new Pen(Anim.Fade(Color.FromArgb(46, fg), separatorAlpha), 1))
                    {
                        float mid = SeparatorSpace * separatorAlpha / 2;
                        if (vertical) g.DrawLine(p, Width * 0.3f, extra.Value.Top - mid, Width * 0.7f, extra.Value.Top - mid);
                        else g.DrawLine(p, extra.Value.Left - mid, Height * 0.3f, extra.Value.Left - mid, Height * 0.7f);
                    }
                // Hover como los botones nativos: más claro que el taskbar, con un borde muy suave.
                Color hoverFill = lightTheme ? Color.FromArgb(180, 255, 255, 255) : Color.FromArgb(22, 255, 255, 255);
                Color hoverEdge = lightTheme ? Color.FromArgb(14, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255);
                foreach (var item in layout)
                {
                    var r = item.Value;
                    float hl = highlight[item.Key] * presence[item.Key];
                    if (hl > 0.01f)
                        using (var path = Native.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 4 * scale))
                        {
                            using (var b = new SolidBrush(Anim.Fade(hoverFill, hl))) g.FillPath(b, path);
                            using (var p = new Pen(Anim.Fade(hoverEdge, hl), 1)) g.DrawPath(p, path);
                        }
                    var state = g.Save();
                    g.IntersectClip(r);
                    DrawItem(g, item.Key, r, presence[item.Key]);
                    g.Restore(state);
                }
            }
            Native.ApplyBitmap(Handle, bmp, Left, Top, 255);
        }
    }

    void DrawItem(Graphics g, string key, RectangleF r, float a)
    {
        int t = Environment.TickCount & int.MaxValue;
        float pulse = 0.35f + 0.65f * (0.5f + 0.5f * (float)Math.Cos(2 * Math.PI * (t % 1400) / 1400.0));
        Color fg = Fg();
        if (key == "cpu" || key == "ram")
        {
            float value = key == "cpu" ? shownCpu : shownRam, real = key == "cpu" ? cpu : ram;
            float d = RingD;
            var ring = new RectangleF(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
            DrawRing(g, key, ring, a, value / 100f, Math.Round(real).ToString("0"), Level(value, fg), key == "cpu" && hotSeconds >= 30 ? pulse : 1f);
            return;
        }
        if (key == "idle")
        {
            float d = 6 * scale;
            using (var b = new SolidBrush(Anim.Fade(Color.FromArgb(110, fg), a)))
                g.FillEllipse(b, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
            return;
        }

        // Íconos siempre en el color del texto (se leen igual que los nativos); el estado va en un punto de color,
        // como los indicadores que Windows pone sobre los íconos de las apps.
        // El glifo de 16 px va centrado en el espacio de 24 px, igual que el anillo.
        float iconX = vertical || key != "meet" ? r.X + (r.Width - GlyphSize) / 2 : r.X + HPad;
        var icon = new RectangleF(iconX, r.Y + (r.Height - GlyphSize) / 2, GlyphSize, GlyphSize);
        Color? badge = null;
        float badgePulse = 1;
        switch (key)
        {
            case "build":
                if (buildsRunning.Length > 0) DrawSpinner(g, icon, a, t);
                else
                {
                    var done = buildsDone;
                    bool ok = done.Length > 0 && done[0].Ok == true;
                    DrawGlyph(g, icon, a, ok ? Icons.Confirm : Icons.Cancel, fg);
                    badge = ok ? Good : High;
                }
                break;
            case "meet":
                bool inProgress;
                var m = CurrentMeeting(out inProgress);
                DrawGlyph(g, icon, a, Icons.Calendar, fg);
                if (m != null) badge = inProgress ? Blue : (m.Start - DateTime.Now).TotalMinutes <= 5 ? Warn : (Color?)null;
                break;
            case "claude":
                var list = sessions;
                bool needsYou = list.Any(s => s.NeedsYou);
                DrawGlyph(g, icon, a, Icons.Chat, fg);
                if (needsYou) { badge = Clay; badgePulse = pulse; }
                else if (list.Any(s => s.State == "error")) badge = High;
                else if (list.Any(s => s.State == "terminó" && (DateTime.UtcNow - s.Time).TotalMinutes < 60)) badge = Good;
                break;
            case "jira":
                DrawGlyph(g, icon, a, Icons.List, fg);
                if (jira.Error != "") badge = High;
                else if (jira.ToDo > 0) badge = Blue; // hay tickets por hacer
                break;
            case "wt":
                DrawGlyph(g, icon, a, Icons.Edit, fg);
                badge = Warn;
                break;
        }
        if (badge.HasValue) DrawBadge(g, icon, a, badge.Value, badgePulse);
        if (!vertical && key == "meet" && Line(key) != "") DrawLine(g, key, icon.Right + 6 * scale, r, a);
    }

    void DrawRing(Graphics g, string key, RectangleF box, float a, float frac, string number, Color arc, float pulse)
    {
        float stroke = 2 * scale, inset = stroke / 2;
        var circle = new RectangleF(box.X + inset, box.Y + inset, box.Width - 2 * inset, box.Height - 2 * inset);
        using (var track = new Pen(Anim.Fade(Color.FromArgb(55, Fg()), a), stroke)) g.DrawEllipse(track, circle);
        if (frac > 0.005f)
            using (var pen = new Pen(Anim.Fade(arc, a * pulse), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, circle, -90, 360f * Math.Min(1f, frac));

        // El número salta directo al valor nuevo, con un fundido corto entre el anterior y el nuevo.
        NumberFade nf;
        if (!numbers.TryGetValue(key, out nf)) numbers[key] = nf = new NumberFade { Cur = number };
        if (nf.Cur != number) { nf.Prev = nf.Cur; nf.Cur = number; nf.Tick = Environment.TickCount; }
        float t = Anim.EaseOut((Environment.TickCount - nf.Tick) / (float)NumberFadeMs), shift = 3 * scale;
        if (t < 1 && nf.Prev != "") DrawNumber(g, nf.Prev, box, -shift * t, a * (1 - t));
        DrawNumber(g, nf.Cur, box, shift * (1 - t), a * t);
    }

    void DrawNumber(Graphics g, string text, RectangleF box, float dy, float a)
    {
        var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var b = new SolidBrush(Anim.Fade(Fg(), a)))
            g.DrawString(text, text.Length > 2 ? smallNumberFont : numberFont, b, new RectangleF(box.X, box.Y + dy + 0.5f * scale, box.Width, box.Height), center);
    }

    void DrawGlyph(Graphics g, RectangleF box, float a, string glyph, Color color)
    {
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var b = new SolidBrush(Anim.Fade(color, a)))
            g.DrawString(glyph, glyphFont, b, new RectangleF(box.X - 4 * scale, box.Y - 3 * scale, box.Width + 8 * scale, box.Height + 6 * scale), fmt);
    }

    void DrawSpinner(Graphics g, RectangleF box, float a, int t)
    {
        float stroke = 2 * scale, inset = stroke / 2 + 0.5f * scale;
        var circle = new RectangleF(box.X + inset, box.Y + inset, box.Width - 2 * inset, box.Height - 2 * inset);
        using (var pen = new Pen(Anim.Fade(Fg(), a), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, circle, (t % 1100) * 360f / 1100 - 90, 100);
    }

    // Punto de estado en la esquina inferior derecha del ícono, con un borde del color del taskbar para separarlo.
    void DrawBadge(Graphics g, RectangleF icon, float a, Color color, float pulse)
    {
        float d = 8 * scale, ring = 1.5f * scale;
        float cx = icon.Right - 1 * scale, cy = icon.Bottom - 1 * scale;
        Color taskbarColor = lightTheme ? Color.FromArgb(238, 238, 238) : Color.FromArgb(28, 28, 28);
        using (var b = new SolidBrush(Anim.Fade(taskbarColor, a))) g.FillEllipse(b, cx - d / 2 - ring, cy - d / 2 - ring, d + 2 * ring, d + 2 * ring);
        using (var b = new SolidBrush(Anim.Fade(color, a * pulse))) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
    }

    void DrawLine(Graphics g, string key, float x, RectangleF r, float a)
    {
        var fmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using (var b = new SolidBrush(Anim.Fade(Fg(), a)))
            g.DrawString(Line(key), textFont, b, new RectangleF(x, r.Y, r.Right - x, r.Height), fmt);
    }


    bool ForegroundIsFullscreen()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle || fg == popup.Handle) return false;
        var sb = new StringBuilder(64);
        Native.GetClassName(fg, sb, sb.Capacity);
        string cls = sb.ToString();
        if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
        Native.RECT r;
        if (!Native.GetWindowRect(fg, out r)) return false;
        var screen = Screen.FromHandle(fg).Bounds;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }
    #endregion

    #region Paneles
    List<PanelLine> Panel(string key)
    {
        var lines = new List<PanelLine>();
        float phase = Anim.Clamp((Environment.TickCount - lastSampleTick) / 1000f);
        switch (key)
        {
            case "cpu":
                lines.Add(PanelLine.Title("CPU " + Math.Round(cpu) + "%"));
                if (hotSeconds >= 30) lines.Add(PanelLine.Muted("Sobre 90% desde hace " + Fmt.Duration(TimeSpan.FromSeconds(hotSeconds)), High));
                lines.Add(PanelLine.Spark(cpuHist, phase));
                foreach (var p in TopProcesses(key, x => x.Cpu))
                    lines.Add(PanelLine.Row(p.Name, p.Cpu.ToString("0.0") + "%").With(StopAction(p.Name)));
                break;

            case "ram":
                lines.Add(PanelLine.Title("RAM " + Math.Round(ram) + "% · " + ramUsedGb.ToString("0.0") + " / " + ramTotalGb.ToString("0.0") + " GB"));
                // La RAM cambia poco: el gráfico usa una escala ajustada a lo que pasó para que se note.
                float lo = ramHist.Count > 0 ? ramHist.Min() : 0, hi = ramHist.Count > 0 ? ramHist.Max() : 100;
                float mid = (lo + hi) / 2, span = Math.Max(20, hi - lo + 10);
                // La escala se acerca suave a la nueva, para que el gráfico no salte cuando cambia el rango.
                int nowTick = Environment.TickCount;
                float dtScale = Math.Max(1, Math.Min(100, nowTick - ramScaleTick));
                ramScaleTick = nowTick;
                ramLo = ramScaleSet ? Anim.Approach(ramLo, Math.Max(0, mid - span / 2), dtScale, 400) : Math.Max(0, mid - span / 2);
                ramHi = ramScaleSet ? Anim.Approach(ramHi, Math.Min(100, mid + span / 2), dtScale, 400) : Math.Min(100, mid + span / 2);
                ramScaleSet = true;
                lines.Add(PanelLine.Spark(ramHist, phase, ramLo, ramHi));
                foreach (var p in TopProcesses(key, x => x.Mem))
                    lines.Add(PanelLine.Row(p.Name, Fmt.Memory(p.Mem)).With(StopAction(p.Name)));
                break;

            case "build":
                var running = buildsRunning;
                var done = buildsDone;
                if (running.Length > 0)
                {
                    var b = running[0];
                    lines.Add(PanelLine.Title("Compilando " + b.Repo).With(new PanelAction
                    {
                        Id = "cancel:" + b.Pid, Icon = Icons.Cancel, Tip = "Cancelar compilación", Confirm = true, Run = () => CancelBuild(b)
                    }));
                    if (!string.IsNullOrEmpty(b.Branch)) lines.Add(PanelLine.Row("Rama", b.Branch));
                    if (!string.IsNullOrEmpty(b.Project)) lines.Add(PanelLine.Row("Proyecto", Path.GetFileName(b.Project)));
                    lines.Add(PanelLine.Row("Tiempo", Fmt.Duration(DateTime.Now - b.Start)));
                    foreach (var other in running.Skip(1))
                        lines.Add(PanelLine.Row("También " + other.Repo, Fmt.Duration(DateTime.Now - other.Start)));
                }
                else if (done.Length > 0)
                {
                    var b = done[0];
                    lines.Add(PanelLine.Title(b.Ok == true ? "Compilación correcta" : b.Ok == false ? "Compilación con errores" : "Compilación terminada"));
                    lines.Add(PanelLine.Row(b.Repo + (string.IsNullOrEmpty(b.Branch) ? "" : " · " + b.Branch), "hace " + Fmt.Duration(DateTime.Now - b.End.Value)));
                }
                var previous = done.Skip(running.Length > 0 ? 0 : 1).ToList();
                if (previous.Count > 0)
                {
                    lines.Add(PanelLine.Header("Anteriores"));
                    foreach (var b in previous)
                        lines.Add(PanelLine.Row(b.Repo + (string.IsNullOrEmpty(b.Branch) ? "" : " · " + b.Branch),
                            (b.Ok == true ? "✓ " : b.Ok == false ? "✗ " : "") + Fmt.Duration(b.End.Value - b.Start),
                            b.Ok == true ? Good : b.Ok == false ? High : (Color?)null));
                }
                break;

            case "claude":
                var list = sessions;
                lines.Add(PanelLine.Title(list.Count == 1 ? "1 sesión de Claude" : list.Count + " sesiones de Claude"));
                foreach (var s in list.Take(6))
                {
                    // Clic en la sesión: se abre esa sesión en la app de Claude.
                    var session = s;
                    var open = new PanelAction { Id = "session:" + s.Id, Run = () => OpenClaudeSession(session.Id) };
                    lines.Add(PanelLine.Row(s.Title, s.StateLabel, StateColor(s)).OnClick(open));
                    if (s.Detail != "" && (s.NeedsYou || s.State == "error"))
                        lines.Add(PanelLine.Wrap(Fmt.Cut(s.Detail, 140), null).OnClick(open));
                }
                if (list.Count > 6) lines.Add(PanelLine.Muted("y " + (list.Count - 6) + " más"));
                break;

            case "meet":
                bool inProgress;
                var current = CurrentMeeting(out inProgress);
                var now = DateTime.Now;
                // La tarjeta detallada solo cuando la reunión está en curso o empieza en 5 minutos o menos;
                // antes de eso se ve la lista del día, como cuando falta más de una hora.
                var m = current != null && (inProgress || (current.Start - now).TotalMinutes <= 5) ? current : null;
                if (m != null)
                {
                    lines.Add(PanelLine.Title(m.Title).With(JoinAction(m), RefreshAction(key)));
                    lines.Add(PanelLine.Row("Horario", m.Start.ToString("HH:mm") + " – " + m.End.ToString("HH:mm")));
                    lines.Add(inProgress ? PanelLine.Row("Termina en", Fmt.Until(m.End - now), Blue)
                                         : PanelLine.Row("Empieza en", Fmt.Until(m.Start - now), m.Start - now <= TimeSpan.FromMinutes(5) ? Warn : (Color?)null));
                }
                else lines.Add(PanelLine.Title("Calendario").With(RefreshAction(key)));
                if (calendarError != "") lines.Add(PanelLine.Muted(calendarError, High));
                var rest = meetings.Where(x => x != m && x.End > now && x.Start.Date == now.Date).ToList();
                if (rest.Count > 0)
                {
                    lines.Add(PanelLine.Header(m != null ? "Resto del día" : "Hoy"));
                    foreach (var x in rest.Take(6))
                        lines.Add(PanelLine.Row(x.Start.ToString("HH:mm") + "  " + x.Title,
                                x.Start > now ? "en " + Fmt.Until(x.Start - now) : "en curso", x.Start > now ? (Color?)null : Blue)
                            .With(JoinAction(x)));
                }
                else if (m == null && calendarError == "") lines.Add(PanelLine.Muted("No tienes más reuniones hoy"));
                break;

            case "jira":
                var j = jira;
                if (j.Error != "")
                {
                    lines.Add(PanelLine.Title("Jira").With(RefreshAction(key)));
                    lines.Add(PanelLine.Muted(j.Error, High));
                    lines.Add(PanelLine.Row("Revisa la conexión", "").With(new PanelAction { Id = "settings", Icon = Icons.Settings, Tip = "Abrir ajustes", Run = OpenSettings }));
                    break;
                }
                // Abrir Jira ya lo hace el clic en el ícono: la tarjeta solo lista los tickets, agrupados y ordenados según Ajustes.
                lines.Add(PanelLine.Title(j.Issues.Count == 1 ? "1 ticket abierto" : j.Issues.Count + " tickets abiertos").With(RefreshAction(key)));
                string grouping = Config.Get("JiraGroup", "status");
                Func<JiraIssue, string> groupOf = x => x.Status;
                if (grouping == "project") groupOf = x => x.Project;
                else if (grouping == "priority") groupOf = x => x.Priority;
                else if (grouping == "none") groupOf = x => "";
                var groups = j.Issues.GroupBy(x => groupOf(x) ?? "").ToList();
                if (grouping == "status") groups = groups.OrderBy(gr => gr.First().Category == "new" ? 0 : 1).ToList(); // por hacer primero
                int shown = 0;
                foreach (var group in groups)
                {
                    if (shown >= 10) break;
                    if (grouping != "none") lines.Add(PanelLine.Header((group.Key == "" ? "Sin dato" : group.Key) + " · " + group.Count()));
                    foreach (var issue in group)
                    {
                        if (shown >= 10) break;
                        shown++;
                        var i = issue;
                        // Si no se agrupa por estado, el estado va como etiqueta.
                        lines.Add(PanelLine.Row(i.Key + "  " + i.Summary, grouping == "status" ? "" : i.Status, i.Category == "new" ? Blue : (Color?)null)
                            .With(new PanelAction
                            {
                                Id = "jira:" + i.Key, Icon = Icons.Open, Tip = "Abrir en Jira", Run = () => Native.Open(j.Site + "/browse/" + i.Key)
                            }));
                    }
                }
                if (j.Issues.Count > shown) lines.Add(PanelLine.Muted("y " + (j.Issues.Count - shown) + " más"));
                break;

            case "wt":
                lines.Add(PanelLine.Title("Repositorios"));
                foreach (var r in repos)
                {
                    var repo = r;
                    lines.Add(PanelLine.Row(r.Name + (r.Branch != "" ? " · " + r.Branch : ""),
                        r.Changes == 0 ? "limpio" : r.Changes == 1 ? "1 cambio" : r.Changes + " cambios",
                        r.Changes > 0 ? Warn : (Color?)null)
                        .With(new PanelAction { Id = "repo:" + r.Path, Icon = Icons.Folder, Tip = "Abrir carpeta", Run = () => Native.Open(repo.Path) }));
                }
                break;
        }
        if (lines.Count > 0 && noticeKey == key && Environment.TickCount - noticeUntil < 0) lines.Add(PanelLine.Muted(notice));
        return lines;
    }

    // Pide al hilo de fondo que vuelva a consultar Jira o el calendario ya, sin esperar el próximo ciclo.
    PanelAction RefreshAction(string key)
    {
        return new PanelAction
        {
            Id = "refresh:" + key, Icon = Icons.Refresh, Tip = "Actualizar ahora",
            Run = () =>
            {
                if (key == "jira") refreshJira = true; else refreshCalendar = true;
                Notice(key, "Actualizando…");
            }
        };
    }

    // Mientras el mouse está dentro del panel la lista no se reordena: así los botones no se mueven bajo el cursor.
    List<ProcStat> TopProcesses(string key, Func<ProcStat, double> by)
    {
        var all = procs;
        if (frozenKey == key && popup.Key == key && popup.Contains(Cursor.Position))
            return frozenNames.Select(n => all.FirstOrDefault(p => p.Name == n) ?? new ProcStat { Name = n }).ToList();
        var top = all.OrderByDescending(by).Take(6).ToList();
        frozenKey = key;
        frozenNames = top.Select(p => p.Name).ToList();
        return top;
    }

    PanelAction StopAction(string name)
    {
        bool system = Protected.Contains(name);
        return new PanelAction
        {
            Id = "stop:" + name, Icon = Icons.Stop, Confirm = true, Enabled = !system,
            Tip = system ? "Proceso del sistema: no se puede detener" : "Detener " + name,
            Run = () => StopProcesses(name)
        };
    }

    static PanelAction JoinAction(Meeting m)
    {
        bool link = !string.IsNullOrEmpty(m.Link);
        return new PanelAction
        {
            Id = "join:" + m.Start.Ticks, Icon = Icons.Join, Enabled = link,
            Tip = link ? "Unirse a la reunión" : "Sin enlace de videollamada", Run = () => Native.Open(m.Link)
        };
    }

    void StopProcesses(string name)
    {
        int stopped = 0, failed = 0;
        foreach (var p in Process.GetProcessesByName(name))
            using (p)
            {
                try { p.Kill(); stopped++; } catch { failed++; }
            }
        Notice(popup.Key, failed == 0 ? "Detenido: " + name
            : stopped == 0 ? "No se pudo detener " + name + " (sin permiso)"
            : "Detenidos " + stopped + " procesos de " + name + "; " + failed + " sin permiso");
    }

    void CancelBuild(BuildInfo b)
    {
        Safe(() => Process.Start(new ProcessStartInfo("taskkill", "/PID " + b.Pid + " /T /F") { CreateNoWindow = true, UseShellExecute = false }));
        Notice("build", "Compilación cancelada");
    }

    void Notice(string key, string text)
    {
        notice = text;
        noticeKey = key;
        noticeUntil = Environment.TickCount + 4000;
    }

    static Color StateColor(ClaudeSession s)
    {
        if (s.NeedsYou) return Clay;
        if (s.State == "error") return High;
        if (s.State == "terminó") return Good;
        return Blue;
    }

    static void OpenClaudeSession(string cliSessionId)
    {
        string localId = ClaudeDesktop.FindLocalId(cliSessionId);
        if (localId != null) Native.Open("claude://code/continue?session=" + localId);
        else FocusClaude();
    }

    static void FocusClaude()
    {
        foreach (var p in Process.GetProcessesByName("claude"))
            using (p)
            {
                IntPtr h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                if (Native.IsIconic(h)) Native.ShowWindow(h, 9); // SW_RESTORE
                Native.SetForegroundWindow(h);
                return;
            }
    }

    void OpenSettings()
    {
        try { ShowSettings(); }
        catch (Exception ex) { Program.LogError(ex); }
    }

    void ShowSettings()
    {
        popup.Close(false);
        if (settings != null && !settings.IsDisposed)
        {
            if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
            Native.SetForegroundWindow(settings.Handle);
            settings.Activate();
            return;
        }
        settings = new SettingsForm(lightTheme, () => { refreshCalendar = refreshJira = refreshRepos = true; Frame(); });
        settings.Show();
        Native.SetForegroundWindow(settings.Handle); // el clic acaba de llegar a esta app, así que Windows la deja pasar al frente
        settings.Activate();
    }
    #endregion

    #region Fuentes de datos (hilo de fondo)
    Dictionary<int, TimeSpan> prevCpu = new Dictionary<int, TimeSpan>();
    DateTime prevSample = DateTime.UtcNow;

    List<ProcStat> SampleProcesses()
    {
        var now = DateTime.UtcNow;
        double elapsed = (now - prevSample).TotalMilliseconds * Environment.ProcessorCount;
        var current = new Dictionary<int, TimeSpan>();
        var byName = new Dictionary<string, ProcStat>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                if (p.Id == 0) continue;
                ProcStat s;
                if (!byName.TryGetValue(p.ProcessName, out s)) byName[p.ProcessName] = s = new ProcStat { Name = p.ProcessName };
                try { s.Mem += p.WorkingSet64; } catch { }
                try
                {
                    var used = p.TotalProcessorTime;
                    current[p.Id] = used;
                    TimeSpan before;
                    if (elapsed > 0 && prevCpu.TryGetValue(p.Id, out before))
                        s.Cpu += 100.0 * (used - before).TotalMilliseconds / elapsed;
                }
                catch { }
            }
        prevCpu = current;
        prevSample = now;
        return byName.Values.ToList();
    }

    readonly HashSet<int> seenBuildPids = new HashSet<int>();
    readonly List<BuildInfo> running = new List<BuildInfo>(), history = new List<BuildInfo>();

    void CheckBuilds()
    {
        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("MSBuild"))
        {
            alive.Add(p.Id);
            if (!seenBuildPids.Add(p.Id)) { p.Dispose(); continue; }
            string cmd = (CommandLine(p.Id) ?? "").Replace('/', '\\');
            // Los nodos de trabajo de MSBuild (/nodemode) no son una compilación propia.
            if (cmd.IndexOf("nodemode", StringComparison.OrdinalIgnoreCase) >= 0) { p.Dispose(); continue; }

            var b = new BuildInfo { Proc = p, Pid = p.Id, Start = DateTime.Now };
            try { var h = p.Handle; b.Start = p.StartTime; } catch { }
            foreach (var r in repos)
                if (cmd.IndexOf(r.Path + "\\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    b.Repo = r.Name;
                    b.Branch = Git(r.Path, "branch --show-current").Trim();
                    break;
                }
            var m = Regex.Match(cmd, @"([^\\""\s]+\.(sln|csproj|vbproj|proj))", RegexOptions.IgnoreCase);
            if (m.Success) b.Project = m.Groups[1].Value;
            if (b.Repo == null) b.Repo = b.Project != null ? Path.GetFileNameWithoutExtension(b.Project) : "MSBuild";
            running.Add(b);
        }
        seenBuildPids.IntersectWith(alive);

        foreach (var b in running.ToList())
        {
            bool exited;
            try { exited = b.Proc.HasExited; } catch { exited = true; }
            if (!exited) continue;
            b.End = DateTime.Now;
            try { b.Ok = b.Proc.ExitCode == 0; } catch { b.Ok = null; }
            b.Proc.Dispose();
            running.Remove(b);
            history.Insert(0, b);
            if (history.Count > 5) history.RemoveAt(5);
            refreshRepos = true;
        }
        buildsRunning = running.ToArray();
        buildsDone = history.ToArray();
    }

    static string CommandLine(int pid)
    {
        try
        {
            using (var search = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                foreach (ManagementObject o in search.Get()) return o["CommandLine"] as string;
        }
        catch { }
        return null;
    }

    static string Git(string dir, string args)
    {
        var psi = new ProcessStartInfo("git", "-C \"" + dir + "\" " + args)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8
        };
        using (var p = Process.Start(psi))
        {
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return output;
        }
    }

    // Cada carpeta configurada puede ser un repositorio o contener varios.
    static List<RepoInfo> LoadRepos()
    {
        var list = new List<RepoInfo>();
        var roots = Config.Get("ReposRoots", Config.Get("ReposRoot", "")).Split('|').Select(p => p.Trim()).Where(p => p != "" && Directory.Exists(p));
        foreach (var root in roots)
            foreach (var dir in IsRepo(root) ? new[] { root } : Directory.GetDirectories(root).Where(IsRepo).ToArray())
            {
                if (list.Any(r => string.Equals(r.Path, dir, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new RepoInfo
                {
                    Name = Path.GetFileName(dir.TrimEnd('\\')),
                    Path = dir,
                    Branch = Git(dir, "branch --show-current").Trim(),
                    Changes = Git(dir, "status --porcelain").Split('\n').Count(l => l.Trim() != "")
                });
            }
        return list;
    }

    static bool IsRepo(string dir)
    {
        string git = Path.Combine(dir, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    static JiraInfo LoadJira()
    {
        string site = Config.Get("JiraSite", ""), email = Config.Get("JiraEmail", ""), token = Config.GetSecret("JiraToken");
        if (site == "" || email == "" || token == "") return new JiraInfo();
        return Jira.Load(site, email, token, Config.Get("JiraSort", "updated"));
    }

    // Con sesión de Google se usa la API del calendario; si no, la dirección iCal (si hay).
    void LoadCalendar()
    {
        bool google = Google.SignedIn && Google.HasClient;
        string url = google ? "" : Config.GetSecret("CalendarIcsUrl");
        calendarConfigured = google || url != "";
        if (!calendarConfigured) { meetings = new List<Meeting>(); calendarError = ""; return; }
        try
        {
            meetings = google ? Google.Fetch() : Calendar.Fetch(url);
            calendarError = "";
        }
        catch (Exception ex)
        {
            // Se conservan las reuniones que ya se tenían; el panel avisa que no se pudo actualizar.
            calendarError = ex.Message.Contains("venció") ? ex.Message : "No se pudo actualizar el calendario";
        }
    }

    // Una reunión que empieza en 10 minutos o menos tiene prioridad sobre la que está en curso.
    Meeting CurrentMeeting(out bool inProgress)
    {
        var now = DateTime.Now;
        var list = meetings;
        var next = list.FirstOrDefault(x => x.Start > now && (x.Start - now).TotalMinutes <= 60);
        var current = list.FirstOrDefault(x => x.Start <= now && x.End > now);
        inProgress = false;
        if (next != null && (current == null || (next.Start - now).TotalMinutes <= 10)) return next;
        if (current != null) { inProgress = true; return current; }
        return null;
    }
    #endregion

    // Se registra en cada arranque para que siempre inicie con Windows, aunque el .exe cambie de carpeta.
    static void RegisterStartup()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            k.DeleteValue("TaskbarMonitor", false); // nombre anterior
            k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
        }
    }
}
