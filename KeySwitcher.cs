// KeySwitcher - 全局快捷键映射工具（随时切换 / 方案管理 / 托盘常驻）
// 依赖：.NET Framework 4.x（Windows 自带），无需安装任何运行时。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

[assembly: System.Reflection.AssemblyTitle("KeySwitcher")]
[assembly: System.Reflection.AssemblyProduct("KeySwitcher")]
[assembly: System.Reflection.AssemblyDescription("可随时切换的全局快捷键映射工具")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
namespace KeySwitcher
{
    // =====================================================================
    // 入口
    // =====================================================================
    internal static class Program
    {
        private static Mutex _mutex;
        private static KeyboardHook _hook;

        [STAThread]
        private static void Main(string[] args)
        {
            if (HasArg(args, "--selftest")) { Environment.Exit(SelfTest.Run()); return; }
            if (HasArg(args, "--hooktest")) { Environment.Exit(HookTest.Run()); return; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { CrashLog(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { CrashLog(e.ExceptionObject as Exception); };

            bool created;
            _mutex = new Mutex(true, "Local\\KeySwitcher.SingleInstance", out created);
            if (!created)
            {
                // 可能正被“以管理员身份重启”接管，短暂重试
                for (int i = 0; i < 30 && !created; i++)
                {
                    Thread.Sleep(100);
                    try { created = _mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { created = true; }
                }
                if (!created)
                {
                    MessageBox.Show("KeySwitcher 已经在运行了。\n请在屏幕右下角的系统托盘里找到它的图标。", "KeySwitcher",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            AppState.Initialize();

            _hook = new KeyboardHook();
            bool hookOk = _hook.Start();

            bool startHidden = HasArg(args, "--tray");
            var form = new MainForm(startHidden, hookOk);
            Application.Run(form);

            MainForm.TraceLog("Application.Run returned");
            if (_hook != null) { _hook.Stop(); _hook.Dispose(); } 
            AppState.Save();
            GC.KeepAlive(_mutex);
        }

        private static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void CrashLog(Exception ex)
        {
            try
            {
                if (ex == null) return;
                string dir = AppState.ConfigDir;
                if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    // =====================================================================
    // 全局状态
    // =====================================================================
    internal static class AppState
    {
        private static readonly object _sync = new object();
        private static readonly object _saveSync = new object();

        public static AppConfig Config = AppConfig.CreateDefault();
        public static string ConfigDir = "";
        public static string ConfigPath = "";
        public static bool FirstRun;

        public static event Action MasterChanged;   // 可能来自钩子线程
        public static event Action DataChanged;

        private static bool EnsureWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static void Initialize()
        {
            // 优先 %APPDATA%；若被策略/沙箱禁止写入，回退到程序同级目录
            ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeySwitcher");
            if (!EnsureWritable(ConfigDir))
                ConfigDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KeySwitcher-data");
            EnsureWritable(ConfigDir);
            ConfigPath = Path.Combine(ConfigDir, "config.ini");

            FirstRun = !File.Exists(ConfigPath);
            Config = ConfigStore.Load(ConfigPath);
            Config.AutoStart = AutoStart.IsEnabled();
            Engine.Rebuild(Config);
        }

        public static ProfileSpec ActiveProfile
        {
            get { return Config.Active; }
        }

        public static void Apply()
        {
            lock (_sync) { Engine.Rebuild(Config); }
            Save();
            if (DataChanged != null) DataChanged();
        }

        public static void SetMaster(bool value)
        {
            lock (_sync)
            {
                if (Config.Master == value) return;
                Config.Master = value;
                Engine.Rebuild(Config);
            }
            Save();
            if (MasterChanged != null) MasterChanged();
        }

        public static void ToggleMaster()
        {
            bool now;
            lock (_sync)
            {
                Config.Master = !Config.Master;
                now = Config.Master;
                Engine.Rebuild(Config);
            }
            // 钩子线程里不要做磁盘 IO
            ThreadPool.QueueUserWorkItem(delegate { Save(); });
            if (MasterChanged != null) MasterChanged();
        }

        public static void Save()
        {
            string text;
            lock (_sync) { text = ConfigStore.Serialize(Config); }
            lock (_saveSync)
            {
                try
                {
                    if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                    string tmp = ConfigPath + ".tmp";
                    File.WriteAllText(tmp, text, new UTF8Encoding(true));
                    if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
                    File.Move(tmp, ConfigPath);
                }
                catch { }
            }
        }

        public static void Normalize()
        {
            if (Config.Profiles == null) Config.Profiles = new List<ProfileSpec>();
            if (Config.Profiles.Count == 0)
                Config.Profiles.Add(new ProfileSpec("默认"));
            for (int i = 0; i < Config.Profiles.Count; i++)
                if (string.IsNullOrWhiteSpace(Config.Profiles[i].Name))
                    Config.Profiles[i].Name = "方案" + (i + 1);
            if (Config.Active == null) Config.ActiveProfile = Config.Profiles[0].Name;
        }
    }

    // =====================================================================
    // 数据模型
    // =====================================================================
    internal sealed class RuleSpec
    {
        public bool Enabled = true;
        public string Name = "";
        public ushort TrigVk;
        public bool TrigCtrl, TrigAlt, TrigShift;
        public ushort TargetVk;
        public bool TargetCtrl, TargetAlt, TargetShift;
        public bool Hold;   // true = 按住持续；false = 单击一次

        public string TriggerSpec { get { return KeyCodec.FormatCombo(TrigCtrl, TrigAlt, TrigShift, TrigVk); } }
        public string TargetSpec { get { return KeyCodec.FormatCombo(TargetCtrl, TargetAlt, TargetShift, TargetVk); } }
        public string TriggerDisplay { get { return KeyCodec.DisplayCombo(TrigCtrl, TrigAlt, TrigShift, TrigVk); } }
        public string TargetDisplay { get { return KeyCodec.DisplayCombo(TargetCtrl, TargetAlt, TargetShift, TargetVk); } }
        public string ModeText { get { return Hold ? "按住" : "单击"; } }

        public RuleSpec Copy() { return (RuleSpec)MemberwiseClone(); }

        public string AutoName()
        {
            return TriggerDisplay + " → " + TargetDisplay;
        }
    }

    internal sealed class ProfileSpec
    {
        public string Name = "默认";
        public List<RuleSpec> Rules = new List<RuleSpec>();

        public ProfileSpec() { }
        public ProfileSpec(string name) { Name = name; }
    }

    internal sealed class AppConfig
    {
        public bool Master = true;
        public string ActiveProfile = "默认";
        public bool ToggleCtrl = true, ToggleAlt = true, ToggleShift = false;
        public ushort ToggleVk = 0x78; // F9
        public bool AutoStart = false;
        public bool MinimizeToTray = true;
        public List<ProfileSpec> Profiles = new List<ProfileSpec>();

        public ProfileSpec Active
        {
            get
            {
                if (Profiles == null) Profiles = new List<ProfileSpec>();
                if (Profiles.Count == 0) Profiles.Add(new ProfileSpec("默认"));
                for (int i = 0; i < Profiles.Count; i++)
                    if (string.Equals(Profiles[i].Name, ActiveProfile, StringComparison.Ordinal)) return Profiles[i];
                ActiveProfile = Profiles[0].Name;
                return Profiles[0];
            }
        }

        public static AppConfig CreateDefault()
        {
            var cfg = new AppConfig();
            var p = new ProfileSpec("默认");
            p.Rules.Add(new RuleSpec
            {
                Name = "Alt + , → ←",
                TrigAlt = true,
                TrigVk = 0xBC,      // VK_OEM_COMMA
                TargetVk = 0x25     // VK_LEFT
            });
            p.Rules.Add(new RuleSpec
            {
                Name = "Alt + . → →",
                TrigAlt = true,
                TrigVk = 0xBE,      // VK_OEM_PERIOD
                TargetVk = 0x27     // VK_RIGHT
            });
            cfg.Profiles.Add(p);
            cfg.ActiveProfile = p.Name;
            return cfg;
        }
    }

    // =====================================================================
    // 配置读写（人类可编辑的简易文本格式，UTF-8）
    // =====================================================================
    internal static class ConfigStore
    {
        public static AppConfig Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return AppConfig.CreateDefault();
                string text = File.ReadAllText(path, Encoding.UTF8);
                AppConfig cfg = Parse(text);
                if (cfg == null)
                {
                    TryBackup(path);
                    return AppConfig.CreateDefault();
                }
                return cfg;
            }
            catch
            {
                return AppConfig.CreateDefault();
            }
        }

        private static void TryBackup(string path)
        {
            try
            {
                string bak = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(path, bak, true);
            }
            catch { }
        }

        public static AppConfig Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            AppConfig cfg = AppConfig.CreateDefault();
            cfg.Profiles.Clear();
            ProfileSpec current = null;
            bool anyProfile = false;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#' || line[0] == ';') continue;

                if (line.StartsWith("[profile=", StringComparison.OrdinalIgnoreCase) && line.EndsWith("]"))
                {
                    string name = line.Substring(9, line.Length - 10).Trim();
                    if (name.Length == 0) name = "方案" + (cfg.Profiles.Count + 1);
                    current = new ProfileSpec(name);
                    cfg.Profiles.Add(current);
                    anyProfile = true;
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "master": cfg.Master = IsTrue(val); break;
                    case "active": cfg.ActiveProfile = val; break;
                    case "autostart": cfg.AutoStart = IsTrue(val); break;
                    case "tray": cfg.MinimizeToTray = IsTrue(val); break;
                    case "toggle":
                        {
                            bool c, a, s; ushort vk;
                            if (KeyCodec.TryParse(val, out c, out a, out s, out vk))
                            {
                                cfg.ToggleCtrl = c; cfg.ToggleAlt = a; cfg.ToggleShift = s; cfg.ToggleVk = vk;
                            }
                            break;
                        }
                    case "rule":
                        {
                            if (current == null) break;
                            RuleSpec r = ParseRule(val);
                            if (r != null) current.Rules.Add(r);
                            break;
                        }
                }
            }

            if (!anyProfile) return null;
            if (string.IsNullOrEmpty(cfg.ActiveProfile)) cfg.ActiveProfile = cfg.Profiles[0].Name;
            return cfg;
        }

        private static RuleSpec ParseRule(string value)
        {
            string[] parts = value.Split('|');
            if (parts.Length != 5) return null;

            var r = new RuleSpec();
            r.Enabled = parts[0].Trim() != "0";
            r.Name = parts[1].Trim();

            bool c, a, s;
            ushort vk;

            if (!KeyCodec.TryParse(parts[2].Trim(), out c, out a, out s, out vk) || vk == 0) return null;
            r.TrigCtrl = c; r.TrigAlt = a; r.TrigShift = s; r.TrigVk = vk;

            if (!KeyCodec.TryParse(parts[3].Trim(), out c, out a, out s, out vk) || vk == 0) return null;
            r.TargetCtrl = c; r.TargetAlt = a; r.TargetShift = s; r.TargetVk = vk;

            r.Hold = string.Equals(parts[4].Trim(), "hold", StringComparison.OrdinalIgnoreCase);
            return r;
        }

        private static bool IsTrue(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            v = v.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on" || v == "是";
        }

        public static string Serialize(AppConfig cfg)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# KeySwitcher 配置（可直接用记事本编辑，请存为 UTF-8）");
            sb.AppendLine("# rule = 启用(1/0) | 名称 | 触发键 | 目标键 | 模式(tap/hold)");
            sb.AppendLine("version=1");
            sb.AppendLine("master=" + (cfg.Master ? "1" : "0"));
            sb.AppendLine("active=" + Sanitize(cfg.ActiveProfile));
            sb.AppendLine("toggle=" + KeyCodec.FormatCombo(cfg.ToggleCtrl, cfg.ToggleAlt, cfg.ToggleShift, cfg.ToggleVk));
            sb.AppendLine("autostart=" + (cfg.AutoStart ? "1" : "0"));
            sb.AppendLine("tray=" + (cfg.MinimizeToTray ? "1" : "0"));

            for (int i = 0; i < cfg.Profiles.Count; i++)
            {
                ProfileSpec p = cfg.Profiles[i];
                sb.AppendLine();
                sb.AppendLine("[profile=" + Sanitize(p.Name) + "]");
                for (int j = 0; j < p.Rules.Count; j++)
                {
                    RuleSpec r = p.Rules[j];
                    sb.AppendLine("rule=" + (r.Enabled ? "1" : "0") + "|" + Sanitize(r.Name) + "|" +
                                  r.TriggerSpec + "|" + r.TargetSpec + "|" + (r.Hold ? "hold" : "tap"));
                }
            }
            return sb.ToString();
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }

    // =====================================================================
    // 按键名称编解码
    // =====================================================================
    internal static class KeyCodec
    {
        private static readonly Dictionary<ushort, string> Names = new Dictionary<ushort, string>();
        private static readonly Dictionary<ushort, string> Displays = new Dictionary<ushort, string>();
        private static readonly Dictionary<string, ushort> FromName = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);

        static KeyCodec()
        {
            // 字母与数字
            for (int i = 0; i < 26; i++)
            {
                char ch = (char)('A' + i);
                Add((ushort)(0x41 + i), ch.ToString(), ch.ToString());
            }
            for (int i = 0; i < 10; i++)
            {
                char ch = (char)('0' + i);
                Add((ushort)(0x30 + i), ch.ToString(), ch.ToString());
            }
            for (int i = 0; i < 24; i++)
                Add((ushort)(0x70 + i), "F" + (i + 1), "F" + (i + 1));

            Add(0x08, "Backspace", "⌫");
            Add(0x09, "Tab", "Tab");
            Add(0x0D, "Enter", "Enter");
            Add(0x13, "Pause", "Pause");
            Add(0x14, "CapsLock", "Caps");
            Add(0x1B, "Escape", "Esc");
            Add(0x20, "Space", "空格");
            Add(0x21, "PageUp", "PgUp");
            Add(0x22, "PageDown", "PgDn");
            Add(0x23, "End", "End");
            Add(0x24, "Home", "Home");
            Add(0x25, "Left", "←");
            Add(0x26, "Up", "↑");
            Add(0x27, "Right", "→");
            Add(0x28, "Down", "↓");
            Add(0x2C, "PrintScreen", "PrtSc");
            Add(0x2D, "Insert", "Ins");
            Add(0x2E, "Delete", "Del");

            Add(0x90, "NumLock", "NumLock");
            Add(0x91, "ScrollLock", "ScrLk");

            for (int i = 0; i < 10; i++)
                Add((ushort)(0x60 + i), "Numpad" + i, "Num" + i);
            Add(0x6A, "NumpadMultiply", "Num*");
            Add(0x6B, "NumpadAdd", "Num+");
            Add(0x6D, "NumpadSubtract", "Num-");
            Add(0x6E, "NumpadDecimal", "Num.");
            Add(0x6F, "NumpadDivide", "Num/");

            Add(0xBA, "OemSemicolon", ";");
            Add(0xBB, "OemPlus", "+");
            Add(0xBC, "OemComma", ",");
            Add(0xBD, "OemMinus", "-");
            Add(0xBE, "OemPeriod", ".");
            Add(0xBF, "OemQuestion", "/");
            Add(0xC0, "OemTilde", "`");
            Add(0xDB, "OemOpenBrackets", "[");
            Add(0xDC, "OemPipe", "\\");
            Add(0xDD, "OemCloseBrackets", "]");
            Add(0xDE, "OemQuotes", "'");
            Add(0xE2, "Oem102", "\\");

            Add(0x5B, "LWin", "Win");
            Add(0x5C, "RWin", "Win");

            Add(0xA6, "BrowserBack", "浏览器后退");
            Add(0xA7, "BrowserForward", "浏览器前进");
            Add(0xA8, "BrowserRefresh", "刷新");
            Add(0xA9, "BrowserStop", "停止");
            Add(0xAB, "BrowserFavorites", "收藏夹");
            Add(0xAC, "BrowserHome", "主页");

            Add(0xAD, "VolumeMute", "静音");
            Add(0xAE, "VolumeDown", "音量-");
            Add(0xAF, "VolumeUp", "音量+");

            Add(0xB0, "MediaPrev", "上一曲");
            Add(0xB1, "MediaNext", "下一曲");
            Add(0xB2, "MediaStop", "停止播放");
            Add(0xB3, "MediaPlayPause", "播放/暂停");
        }

        private static void Add(ushort vk, string name, string display)
        {
            Names[vk] = name;
            Displays[vk] = display;
            FromName[name] = vk;
        }

        public static string NameOf(ushort vk)
        {
            string name;
            if (Names.TryGetValue(vk, out name)) return name;
            return "VK:" + vk.ToString("X2");
        }

        public static string DisplayOf(ushort vk)
        {
            string d;
            if (Displays.TryGetValue(vk, out d)) return d;
            return "键" + vk;
        }

        public static ushort VkOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            ushort vk;
            if (FromName.TryGetValue(name.Trim(), out vk)) return vk;
            if (name.StartsWith("VK:", StringComparison.OrdinalIgnoreCase))
            {
                ushort parsed;
                if (ushort.TryParse(name.Substring(3), System.Globalization.NumberStyles.HexNumber, null, out parsed))
                    return parsed;
            }
            return 0;
        }

        public static string FormatCombo(bool ctrl, bool alt, bool shift, ushort vk)
        {
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (vk != 0) parts.Add(NameOf(vk));
            return string.Join("+", parts.ToArray());
        }

        public static string DisplayCombo(bool ctrl, bool alt, bool shift, ushort vk)
        {
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (vk != 0) parts.Add(DisplayOf(vk));
            if (parts.Count == 0) return "未设置";
            return string.Join(" + ", parts.ToArray());
        }

        public static bool TryParse(string spec, out bool ctrl, out bool alt, out bool shift, out ushort vk)
        {
            ctrl = alt = shift = false;
            vk = 0;
            if (string.IsNullOrEmpty(spec)) return false;

            string[] parts = spec.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;

                if (string.Equals(p, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, "Control", StringComparison.OrdinalIgnoreCase)) { ctrl = true; continue; }
                if (string.Equals(p, "Alt", StringComparison.OrdinalIgnoreCase)) { alt = true; continue; }
                if (string.Equals(p, "Shift", StringComparison.OrdinalIgnoreCase)) { shift = true; continue; }
                if (string.Equals(p, "Win", StringComparison.OrdinalIgnoreCase)) continue;

                ushort parsed = VkOf(p);
                if (parsed == 0) return false;
                vk = parsed;
            }
            return vk != 0;
        }
    }

    // =====================================================================
    // 映射引擎（钩子线程只读快照）
    // =====================================================================
    internal static class Engine
    {
        internal sealed class Snapshot
        {
            public RuleSpec[] Rules = new RuleSpec[0];
            public bool Master;
            public bool HasToggle;
            public bool ToggleCtrl, ToggleAlt, ToggleShift;
            public ushort ToggleVk;
        }

        private static volatile Snapshot _current = new Snapshot();
        public static volatile bool Suspended;
        public static volatile string LastActionText = "";

        public static Snapshot Current { get { return _current; } }

        public static void Rebuild(AppConfig cfg)
        {
            var snap = new Snapshot();
            snap.Master = cfg.Master;
            snap.HasToggle = cfg.ToggleVk != 0;
            snap.ToggleCtrl = cfg.ToggleCtrl;
            snap.ToggleAlt = cfg.ToggleAlt;
            snap.ToggleShift = cfg.ToggleShift;
            snap.ToggleVk = cfg.ToggleVk;

            ProfileSpec p = cfg.Active;
            if (p != null)
            {
                var list = new List<RuleSpec>();
                for (int i = 0; i < p.Rules.Count; i++)
                {
                    RuleSpec r = p.Rules[i];
                    if (r.Enabled && r.TrigVk != 0 && r.TargetVk != 0) list.Add(r);
                }
                snap.Rules = list.ToArray();
            }
            _current = snap;
        }

        public static RuleSpec FindMatch(RuleSpec[] rules, ushort vk, bool ctrl, bool alt, bool shift)
        {
            if (rules == null) return null;
            for (int i = 0; i < rules.Length; i++)
            {
                RuleSpec r = rules[i];
                if (r.TrigVk != vk) continue;
                if (r.TrigCtrl != ctrl || r.TrigAlt != alt || r.TrigShift != shift) continue;
                return r;
            }
            return null;
        }
    }

    // =====================================================================
    // 全局键盘钩子 + 注入
    // =====================================================================
    internal sealed class KeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint WM_QUIT = 0x0012;
        private const uint PM_NOREMOVE = 0x0000;

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static readonly UIntPtr Magic = new UIntPtr(0x4B53574B); // "KSWK"

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private readonly LowLevelKeyboardProc _proc;
        private readonly Dictionary<uint, ActiveMapping> _active = new Dictionary<uint, ActiveMapping>();
        private Thread _thread;
        private uint _threadId;
        private IntPtr _handle;
        private ManualResetEvent _ready;
        private volatile bool _installed;

        // 调试统计（--hooktest 用）
        public static int MatchCount;
        public static int InjectCount;
        public static int ToggleCount;
        public static int RepeatCount;
        public static int ErrorCount;
        public static int AllEvents;
        public static bool Diagnostics;
        public static volatile string LastEventInfo = "";

        // 自检用：可替换的按键状态源与注入出口（正式运行时均为 null）
        internal static Func<ushort, bool> TestKeyState;
        internal static Action<ushort, bool, bool> TestSink;

        private bool _maskNextAltUp;

        private sealed class ActiveMapping
        {
            public RuleSpec Rule;
            public bool Hold;
            public List<ModKey> Released;
        }

        private struct ModKey
        {
            public ushort Vk;
            public bool Extended;
            public ModKey(ushort vk, bool ext) { Vk = vk; Extended = ext; }
        }

        public KeyboardHook()
        {
            _proc = HookProc;
        }

        public bool Start()
        {
            if (_installed) return true;
            _ready = new ManualResetEvent(false);
            _thread = new Thread(ThreadProc);
            _thread.IsBackground = true;
            _thread.Name = "KeySwitcher.Hook";
            _thread.Start();
            _ready.WaitOne(3000);
            return _installed;
        }

        public void Stop()
        {
            if (_thread == null) return;
            try { if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); }
            catch { }
            try { _thread.Join(2000); } catch { }
            _thread = null;
            _handle = IntPtr.Zero;
            _installed = false;
        }

        public void Dispose()
        {
            Stop();
            if (_ready != null) { _ready.Close(); _ready = null; }
        }

        private void ThreadProc()
        {
            _threadId = GetCurrentThreadId();
            MSG msg;
            PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_NOREMOVE); // 建立消息队列

            _handle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            _installed = _handle != IntPtr.Zero;
            _ready.Set();
            if (!_installed) return;

            while (true)
            {
                int r = GetMessage(out msg, IntPtr.Zero, 0, 0);
                if (r <= 0) break;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(_handle);
            _handle = IntPtr.Zero;
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            try
            {
                var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if (Diagnostics)
                {
                    Interlocked.Increment(ref AllEvents);
                    LastEventInfo = "vk=" + info.vkCode + " flags=" + info.flags + " extra=" + info.dwExtraInfo;
                }
                if (info.dwExtraInfo == Magic) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

                int m = wParam.ToInt32();
                bool down = (m == WM_KEYDOWN || m == WM_SYSKEYDOWN);
                bool up = (m == WM_KEYUP || m == WM_SYSKEYUP);
                if (!down && !up) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

                uint vk = info.vkCode;

                if (down)
                {
                    ActiveMapping existing;
                    if (_active.TryGetValue(vk, out existing))
                    {
                        // 同一按键的自动重复：必须和首次一样重新释放修饰键，
                        // 否则按住不放时会发出 Alt+←（浏览器后退）
                        Interlocked.Increment(ref RepeatCount);
                        if (existing.Hold) return new IntPtr(1);
                        List<ModKey> repeatReleased = ReleaseTriggerModifiers(existing.Rule);
                        TapTarget(existing.Rule);
                        RestoreModifiers(repeatReleased);
                        return new IntPtr(1);
                    }

                    if (Engine.Suspended) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

                    Engine.Snapshot snap = Engine.Current;
                    if (snap == null) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

                    bool ctrl = CtrlDown();
                    bool alt = AltDown();
                    bool shift = ShiftDown();

                    // 总开关热键优先
                    if (snap.HasToggle && vk == snap.ToggleVk &&
                        ctrl == snap.ToggleCtrl && alt == snap.ToggleAlt && shift == snap.ToggleShift)
                    {
                        _active[vk] = new ActiveMapping();
                        Interlocked.Increment(ref ToggleCount);
                        AppState.ToggleMaster();
                        return new IntPtr(1);
                    }

                    if (!snap.Master) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

                    RuleSpec rule = Engine.FindMatch(snap.Rules, (ushort)vk, ctrl, alt, shift);
                    if (rule == null) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

                    Interlocked.Increment(ref MatchCount);
                    Engine.LastActionText = rule.TriggerDisplay + "  →  " + rule.TargetDisplay;

                    List<ModKey> released = ReleaseTriggerModifiers(rule);
                    var am = new ActiveMapping();
                    am.Rule = rule;
                    am.Hold = rule.Hold;
                    am.Released = released;

                    if (rule.Hold)
                    {
                        PressTarget(rule);
                        _active[vk] = am;
                    }
                    else
                    {
                        _active[vk] = am;
                        TapTarget(rule);
                        RestoreModifiers(released);
                    }
                    return new IntPtr(1);
                }

                // key up
                ActiveMapping held;
                if (_active.TryGetValue(vk, out held))
                {
                    _active.Remove(vk);
                    if (held.Rule != null && held.Hold)
                    {
                        ReleaseTarget(held.Rule);
                        RestoreModifiers(held.Released);
                    }
                    return new IntPtr(1);
                }

                // 用户先松开修饰键：取消待恢复，防止“Alt/Ctrl 卡在按下状态”
                if (vk == 0xA0 || vk == 0xA1 || vk == 0xA2 || vk == 0xA3 || vk == 0xA4 || vk == 0xA5)
                    ForgetReleasedModifier((ushort)vk);

                // 物理 Alt 抬起前也补一个掩码，避免菜单栏闪一下
                if ((vk == 0xA4 || vk == 0xA5) && _maskNextAltUp)
                {
                    _maskNextAltUp = false;
                    SendMenuMask();
                }
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref ErrorCount);
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }
        }

        private void ForgetReleasedModifier(ushort vk)
        {
            foreach (ActiveMapping am in _active.Values)
            {
                if (am.Released == null) continue;
                am.Released.RemoveAll(delegate(ModKey m) { return m.Vk == vk; });
            }
        }

        // 仅自检用：把一条伪造的键盘事件喂给同一个处理函数
        internal IntPtr TestHookEvent(int msg, uint vk, uint flags)
        {
            var s = new KBDLLHOOKSTRUCT();
            s.vkCode = vk;
            s.flags = flags;
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(KBDLLHOOKSTRUCT)));
            try
            {
                Marshal.StructureToPtr(s, p, false);
                return HookProc(0, new IntPtr(msg), p);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        internal void TestReset()
        {
            _active.Clear();
            _maskNextAltUp = false;
        }

        // ---------- 修饰键处理 ----------
        private static bool IsPhysDown(ushort vk)
        {
            if (TestKeyState != null) return TestKeyState(vk);
            return ((ushort)GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private static bool CtrlDown() { return IsPhysDown(0xA2) || IsPhysDown(0xA3); }
        private static bool AltDown() { return IsPhysDown(0xA4) || IsPhysDown(0xA5); }
        private static bool ShiftDown() { return IsPhysDown(0xA0) || IsPhysDown(0xA1); }

        private List<ModKey> ReleaseTriggerModifiers(RuleSpec rule)
        {
            var list = new List<ModKey>(3);
            if (rule.TrigCtrl)
            {
                if (IsPhysDown(0xA2)) { SendKey(0xA2, true, false); list.Add(new ModKey(0xA2, false)); }
                else if (IsPhysDown(0xA3)) { SendKey(0xA3, true, true); list.Add(new ModKey(0xA3, true)); }
            }
            if (rule.TrigAlt)
            {
                // 先发掩码键，否则 Alt 单独抬起会激活窗口菜单栏
                SendMenuMask();
                if (IsPhysDown(0xA4)) { SendKey(0xA4, true, false); list.Add(new ModKey(0xA4, false)); _maskNextAltUp = true; }
                else if (IsPhysDown(0xA5)) { SendKey(0xA5, true, true); list.Add(new ModKey(0xA5, true)); _maskNextAltUp = true; }
            }
            if (rule.TrigShift)
            {
                if (IsPhysDown(0xA0)) { SendKey(0xA0, true, false); list.Add(new ModKey(0xA0, false)); }
                else if (IsPhysDown(0xA1)) { SendKey(0xA1, true, false); list.Add(new ModKey(0xA1, false)); }
            }
            return list;
        }

        private static void RestoreModifiers(List<ModKey> released)
        {
            if (released == null) return;
            for (int i = released.Count - 1; i >= 0; i--)
                SendKey(released[i].Vk, false, released[i].Extended);
        }

        // ---------- 目标键动作 ----------
        private static void PressModifiers(RuleSpec r)
        {
            if (r.TargetCtrl) SendKey(0xA2, false, false);
            if (r.TargetAlt) SendKey(0xA4, false, false);
            if (r.TargetShift) SendKey(0xA0, false, false);
        }

        private static void ReleaseModifiers(RuleSpec r)
        {
            if (r.TargetShift) SendKey(0xA0, true, false);
            if (r.TargetAlt) SendKey(0xA4, true, false);
            if (r.TargetCtrl) SendKey(0xA2, true, false);
        }

        // 轻敲一个 Ctrl，让系统认为“Alt 期间按过别的键”，从而不激活菜单栏
        private static void SendMenuMask()
        {
            SendKey(0xA2, false, false);
            SendKey(0xA2, true, false);
        }

        private static void TapTarget(RuleSpec r)
        {
            PressModifiers(r);
            bool ext = IsExtendedKey(r.TargetVk);
            SendKey(r.TargetVk, false, ext);
            SendKey(r.TargetVk, true, ext);
            ReleaseModifiers(r);
        }

        private static void PressTarget(RuleSpec r)
        {
            PressModifiers(r);
            SendKey(r.TargetVk, false, IsExtendedKey(r.TargetVk));
        }

        private static void ReleaseTarget(RuleSpec r)
        {
            SendKey(r.TargetVk, true, IsExtendedKey(r.TargetVk));
            ReleaseModifiers(r);
        }

        private static bool IsExtendedKey(ushort vk)
        {
            switch (vk)
            {
                case 0x21: case 0x22: case 0x23: case 0x24:
                case 0x25: case 0x26: case 0x27: case 0x28:
                case 0x2D: case 0x2E: case 0x2C:
                case 0x90: case 0x6F:
                case 0xA3: case 0xA5:
                    return true;
            }
            return false;
        }

        private static void SendKey(ushort vk, bool up, bool extended)
        {
            SendKeyEx(vk, up, extended, Magic);
        }

        private static void SendKeyEx(ushort vk, bool up, bool extended, UIntPtr extra)
        {
            if (TestSink != null) { TestSink(vk, up, extended); return; }

            var inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = (uint)((up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0)),
                time = 0,
                dwExtraInfo = extra
            };
            uint sent = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != 0) Interlocked.Increment(ref InjectCount);
        }

        // 仅供 --hooktest：注入一个“看似物理”的按键（不带魔数，钩子会正常处理）
        internal static void SimulatePhysicalKey(ushort vk, bool up)
        {
            SendKeyEx(vk, up, IsExtendedKey(vk), UIntPtr.Zero);
        }

        // ---------- Win32 ----------
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    }

    // =====================================================================
    // 开机自启
    // =====================================================================
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "KeySwitcher";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    object v = k.GetValue(ValueName);
                    return v != null && v.ToString().Length > 0;
                }
            }
            catch { return false; }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (enabled)
                        k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --tray");
                    else
                        k.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }
    }

    // =====================================================================
    // 视觉主题
    // =====================================================================
    internal static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentDark = Color.FromArgb(29, 78, 216);
        public static readonly Color Text = Color.FromArgb(15, 23, 42);
        public static readonly Color Muted = Color.FromArgb(100, 116, 139);
        public static readonly Color Border = Color.FromArgb(203, 213, 225);
        public static readonly Color PanelBg = Color.FromArgb(248, 250, 252);
        public static readonly Color RowAlt = Color.FromArgb(250, 251, 253);
        public static readonly Color Selection = Color.FromArgb(219, 234, 254);

        public static Font UiFont()
        {
            try { return new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point); }
            catch { return SystemFonts.MessageBoxFont; }
        }

        public static Font UiFontBold()
        {
            try { return new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point); }
            catch { return SystemFonts.MessageBoxFont; }
        }

        public static Button MakeButton(string text, int width, bool primary)
        {
            var b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 30;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            b.Font = UiFont();
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? Accent : Border;
            b.BackColor = primary ? Accent : Color.White;
            b.ForeColor = primary ? Color.White : Text;
            b.FlatAppearance.MouseOverBackColor = primary ? AccentDark : Color.FromArgb(241, 245, 249);
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(30, 64, 175) : Color.FromArgb(226, 232, 240);
            return b;
        }
    }

    // =====================================================================
    // 主窗口
    // =====================================================================
    internal sealed class MainForm : Form
    {
        private readonly bool _startHidden;
        private readonly bool _hookOk;

        private Panel _header;
        private Panel _footer;
        private DataGridView _grid;
        private ComboBox _cbProfile;
        private CheckBox _chkMaster;
        private Label _lblToggle;
        private Label _lblHint;
        private Button _btnNewProfile, _btnRenameProfile, _btnDeleteProfile;
        private Button _btnEditToggle;
        private Button _btnAdd, _btnEdit, _btnDelete, _btnUp, _btnDown, _btnImport, _btnExport, _btnElevate;
        private CheckBox _chkAutoStart, _chkMinimize;
        private StatusStrip _status;
        private ToolStripStatusLabel _lblLast, _lblCount;
        private Timer _uiTimer;
        private NotifyIcon _tray;
        private ContextMenuStrip _trayMenu;
        private Icon _appIcon;

        private bool _sync;         // UI 回填中，忽略事件
        private bool _realExit;
        private bool _trayHintShown;
        private string _lastSeenAction = "";

        public MainForm(bool startHidden, bool hookOk)
        {
            _startHidden = startHidden;
            _hookOk = hookOk;

            Text = "KeySwitcher · 快捷键映射";
            ClientSize = new Size(980, 640);
            MinimumSize = new Size(860, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = Theme.UiFont();
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            KeyPreview = true;

            try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            if (_appIcon != null) Icon = _appIcon;

            BuildUi();
            SetupTray();
            BindEvents();

            RefreshProfiles();
            RefreshGrid(null);
            SyncFromConfig();
            UpdateStatusBar();

            if (!hookOk)
            {
                _lblLast.Text = "⚠ 键盘钩子安装失败，映射未生效";
                _lblLast.ForeColor = Color.FromArgb(185, 28, 28);
            }
        }

        private AppConfig Cfg { get { return AppState.Config; } }

        // ---------------- 界面构建 ----------------
        private void BuildUi()
        {
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.BackColor = Color.White;
            root.Margin = Padding.Empty;
            root.Padding = Padding.Empty;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            Controls.Add(root);

            // ---- 顶栏 ----
            _header = new Panel();
            _header.Dock = DockStyle.Fill;
            _header.BackColor = Theme.PanelBg;
            _header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(226, 232, 240)))
                    e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
            };
            root.Controls.Add(_header, 0, 0);

            var lblProfile = new Label();
            lblProfile.Text = "方案";
            lblProfile.AutoSize = true;
            lblProfile.ForeColor = Theme.Muted;
            lblProfile.Location = new Point(20, 20);
            _header.Controls.Add(lblProfile);

            _cbProfile = new ComboBox();
            _cbProfile.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbProfile.FlatStyle = FlatStyle.Flat;
            _cbProfile.Location = new Point(62, 15);
            _cbProfile.Width = 210;
            _cbProfile.Height = 28;
            _header.Controls.Add(_cbProfile);

            _btnNewProfile = Theme.MakeButton("新建", 56, false);
            _btnNewProfile.Location = new Point(282, 14);
            _header.Controls.Add(_btnNewProfile);

            _btnRenameProfile = Theme.MakeButton("重命名", 68, false);
            _btnRenameProfile.Location = new Point(344, 14);
            _header.Controls.Add(_btnRenameProfile);

            _btnDeleteProfile = Theme.MakeButton("删除", 56, false);
            _btnDeleteProfile.Location = new Point(418, 14);
            _header.Controls.Add(_btnDeleteProfile);

            _chkMaster = new CheckBox();
            _chkMaster.Text = "启用映射";
            _chkMaster.AutoSize = true;
            _chkMaster.Font = Theme.UiFontBold();
            _chkMaster.ForeColor = Theme.Text;
            _chkMaster.Location = new Point(700, 16);
            _header.Controls.Add(_chkMaster);

            _lblToggle = new Label();
            _lblToggle.AutoSize = true;
            _lblToggle.ForeColor = Theme.Muted;
            _lblToggle.Text = "总开关";
            _lblToggle.Location = new Point(560, 56);
            _header.Controls.Add(_lblToggle);

            _btnEditToggle = Theme.MakeButton("修改", 52, false);
            _btnEditToggle.Height = 26;
            _btnEditToggle.Location = new Point(900, 51);
            _header.Controls.Add(_btnEditToggle);

            _lblHint = new Label();
            _lblHint.AutoSize = false;
            _lblHint.Text = "提示：双击任意一行可编辑；单击「启用」列可单独开关一条映射。修改后立即生效。";
            _lblHint.ForeColor = Theme.Muted;
            _lblHint.Location = new Point(20, 58);
            _lblHint.Size = new Size(560, 22);
            _header.Controls.Add(_lblHint);

            // ---- 表格 ----
            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.None;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.AllowUserToOrderColumns = false;
            _grid.RowHeadersVisible = false;
            _grid.MultiSelect = false;
            _grid.ReadOnly = true;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.GridColor = Color.FromArgb(241, 245, 249);
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersHeight = 38;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.RowTemplate.Height = 34;
            _grid.DefaultCellStyle.Font = new Font(Theme.UiFont().FontFamily, 9.5F);
            _grid.DefaultCellStyle.ForeColor = Theme.Text;
            _grid.DefaultCellStyle.BackColor = Color.White;
            _grid.DefaultCellStyle.SelectionBackColor = Theme.Selection;
            _grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
            _grid.DefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.RowAlt;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.PanelBg;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(71, 85, 105);
            _grid.ColumnHeadersDefaultCellStyle.Font = Theme.UiFontBold();
            _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.PanelBg;
            root.Controls.Add(_grid, 0, 1);

            var cEnabled = new DataGridViewCheckBoxColumn();
            cEnabled.HeaderText = "启用";
            cEnabled.FillWeight = 8F;
            cEnabled.MinimumWidth = 60;
            cEnabled.SortMode = DataGridViewColumnSortMode.NotSortable;
            cEnabled.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            cEnabled.DefaultCellStyle.Padding = new Padding(0);

            var cName = new DataGridViewTextBoxColumn();
            cName.HeaderText = "名称";
            cName.FillWeight = 26F;
            cName.MinimumWidth = 120;
            cName.SortMode = DataGridViewColumnSortMode.NotSortable;

            var cTrigger = new DataGridViewTextBoxColumn();
            cTrigger.HeaderText = "触发键";
            cTrigger.FillWeight = 22F;
            cTrigger.MinimumWidth = 120;
            cTrigger.SortMode = DataGridViewColumnSortMode.NotSortable;

            var cTarget = new DataGridViewTextBoxColumn();
            cTarget.HeaderText = "发送为";
            cTarget.FillWeight = 22F;
            cTarget.MinimumWidth = 120;
            cTarget.SortMode = DataGridViewColumnSortMode.NotSortable;

            var cMode = new DataGridViewTextBoxColumn();
            cMode.HeaderText = "方式";
            cMode.FillWeight = 9F;
            cMode.MinimumWidth = 70;
            cMode.SortMode = DataGridViewColumnSortMode.NotSortable;

            _grid.Columns.AddRange(new DataGridViewColumn[] { cEnabled, cName, cTrigger, cTarget, cMode });

            // ---- 底栏 ----
            _footer = new Panel();
            _footer.Dock = DockStyle.Fill;
            _footer.BackColor = Color.White;
            _footer.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(226, 232, 240)))
                    e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
            };
            root.Controls.Add(_footer, 0, 2);

            _btnAdd = Theme.MakeButton("＋ 添加映射", 104, true);
            _btnAdd.Location = new Point(20, 12);
            _footer.Controls.Add(_btnAdd);

            _btnEdit = Theme.MakeButton("编辑", 64, false);
            _btnEdit.Location = new Point(132, 12);
            _footer.Controls.Add(_btnEdit);

            _btnDelete = Theme.MakeButton("删除", 64, false);
            _btnDelete.Location = new Point(204, 12);
            _footer.Controls.Add(_btnDelete);

            _btnUp = Theme.MakeButton("上移", 64, false);
            _btnUp.Location = new Point(292, 12);
            _footer.Controls.Add(_btnUp);

            _btnDown = Theme.MakeButton("下移", 64, false);
            _btnDown.Location = new Point(364, 12);
            _footer.Controls.Add(_btnDown);

            _btnImport = Theme.MakeButton("导入", 64, false);
            _btnImport.Location = new Point(760, 12);
            _footer.Controls.Add(_btnImport);

            _btnExport = Theme.MakeButton("导出", 64, false);
            _btnExport.Location = new Point(832, 12);
            _footer.Controls.Add(_btnExport);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机自动启动";
            _chkAutoStart.AutoSize = true;
            _chkAutoStart.ForeColor = Theme.Muted;
            _chkAutoStart.Location = new Point(20, 58);
            _footer.Controls.Add(_chkAutoStart);

            _chkMinimize = new CheckBox();
            _chkMinimize.Text = "关闭窗口时最小化到托盘";
            _chkMinimize.AutoSize = true;
            _chkMinimize.ForeColor = Theme.Muted;
            _chkMinimize.Location = new Point(148, 58);
            _footer.Controls.Add(_chkMinimize);

            var linkConfig = new LinkLabel();
            linkConfig.Text = "打开配置目录";
            linkConfig.AutoSize = true;
            linkConfig.LinkColor = Theme.Accent;
            linkConfig.ActiveLinkColor = Theme.AccentDark;
            linkConfig.VisitedLinkColor = Theme.Accent;
            linkConfig.Location = new Point(392, 60);
            _footer.Controls.Add(linkConfig);
            linkConfig.LinkClicked += delegate { OpenConfigFolder(); };

            _btnElevate = Theme.MakeButton("以管理员身份重启", 132, false);
            _btnElevate.Height = 26;
            _btnElevate.Location = new Point(800, 54);
            _footer.Controls.Add(_btnElevate);

            // ---- 状态栏 ----
            _status = new StatusStrip();
            _status.SizingGrip = false;
            _status.BackColor = Theme.PanelBg;
            _status.ForeColor = Theme.Muted;
            _lblLast = new ToolStripStatusLabel();
            _lblLast.Text = "最近触发：—";
            _lblLast.Spring = true;
            _lblLast.TextAlign = ContentAlignment.MiddleLeft;
            _lblCount = new ToolStripStatusLabel();
            _lblCount.Text = "";
            _lblCount.TextAlign = ContentAlignment.MiddleRight;
            _lblCount.ToolTipText = AppState.ConfigPath;
            _status.Items.Add(_lblLast);
            _status.Items.Add(_lblCount);
            Controls.Add(_status);

            _uiTimer = new Timer();
            _uiTimer.Interval = 400;
            _uiTimer.Tick += delegate { TickUi(); };
            _uiTimer.Start();

            Relayout();
        }

        private void Relayout()
        {
            if (_header != null)
            {
                int w = _header.ClientSize.Width;
                _chkMaster.Location = new Point(w - _chkMaster.Width - 22, 16);
                _btnEditToggle.Location = new Point(w - _btnEditToggle.Width - 22, 51);
                _lblToggle.Location = new Point(w - _btnEditToggle.Width - 30 - _lblToggle.Width, 56);
                _lblHint.Width = Math.Max(200, w - _chkMaster.Width - 60);
            }
            if (_footer != null)
            {
                int w = _footer.ClientSize.Width;
                _btnExport.Location = new Point(w - _btnExport.Width - 22, 12);
                _btnImport.Location = new Point(_btnExport.Left - _btnImport.Width - 8, 12);
                _btnElevate.Location = new Point(w - _btnElevate.Width - 22, 54);
            }
        }

        private void BindEvents()
        {
            _header.Resize += delegate { Relayout(); };
            _footer.Resize += delegate { Relayout(); };

            _chkMaster.CheckedChanged += delegate
            {
                if (_sync) return;
                AppState.SetMaster(_chkMaster.Checked);
                UpdateStatusBar();
            };

            _cbProfile.SelectedIndexChanged += delegate
            {
                if (_sync) return;
                string name = _cbProfile.SelectedItem as string;
                if (string.IsNullOrEmpty(name)) return;
                Cfg.ActiveProfile = name;
                AppState.Apply();
                RefreshGrid(null);
                UpdateStatusBar();
            };

            _btnNewProfile.Click += delegate { NewProfile(); };
            _btnRenameProfile.Click += delegate { RenameProfile(); };
            _btnDeleteProfile.Click += delegate { DeleteProfile(); };
            _btnEditToggle.Click += delegate { EditToggleHotkey(); };

            _btnAdd.Click += delegate { AddRule(); };
            _btnEdit.Click += delegate { EditSelectedRule(); };
            _btnDelete.Click += delegate { DeleteSelectedRule(); };
            _btnUp.Click += delegate { MoveSelected(-1); };
            _btnDown.Click += delegate { MoveSelected(1); };
            _btnImport.Click += delegate { ImportConfig(); };
            _btnExport.Click += delegate { ExportConfig(); };
            _btnElevate.Click += delegate { RestartElevated(); };

            _chkAutoStart.CheckedChanged += delegate
            {
                if (_sync) return;
                Cfg.AutoStart = _chkAutoStart.Checked;
                AutoStart.Set(Cfg.AutoStart);
                AppState.Save();
            };

            _chkMinimize.CheckedChanged += delegate
            {
                if (_sync) return;
                Cfg.MinimizeToTray = _chkMinimize.Checked;
                AppState.Save();
            };

            _grid.CellClick += delegate(object s, DataGridViewCellEventArgs e) { OnGridCellClick(e); };
            _grid.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && e.ColumnIndex != 0) EditSelectedRule();
            };
            _grid.SelectionChanged += delegate { UpdateButtons(); };
            _grid.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) { DeleteSelectedRule(); e.Handled = true; }
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("添加映射", null, delegate { AddRule(); });
            menu.Items.Add("编辑", null, delegate { EditSelectedRule(); });
            menu.Items.Add("删除", null, delegate { DeleteSelectedRule(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("上移", null, delegate { MoveSelected(-1); });
            menu.Items.Add("下移", null, delegate { MoveSelected(1); });
            _grid.ContextMenuStrip = menu;

            AppState.MasterChanged += OnMasterChanged;
            AppState.DataChanged += OnDataChanged;
        }

        private void OnMasterChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(OnMasterChanged)); } catch { }
                return;
            }
            SyncFromConfig();
            UpdateStatusBar();
        }

        private void OnDataChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(OnDataChanged)); } catch { }
                return;
            }
            SyncFromConfig();
        }

        private void SyncFromConfig()
        {
            _sync = true;
            try
            {
                _chkMaster.Checked = Cfg.Master;
                _chkAutoStart.Checked = Cfg.AutoStart;
                _chkMinimize.Checked = Cfg.MinimizeToTray;
                UpdateToggleLabel();
            }
            finally { _sync = false; }
            UpdateButtons();
            UpdateTrayText();
        }

        private void TickUi()
        {
            string act = Engine.LastActionText;
            if (act == null) act = "";
            if (!string.Equals(act, _lastSeenAction, StringComparison.Ordinal))
            {
                _lastSeenAction = act;
                _lblLast.Text = string.IsNullOrEmpty(act) ? "最近触发：—" : "最近触发：" + act;
            }
        }

        // ---------------- 列表 ----------------
        private void RefreshProfiles()
        {
            _sync = true;
            try
            {
                _cbProfile.Items.Clear();
                for (int i = 0; i < Cfg.Profiles.Count; i++)
                    _cbProfile.Items.Add(Cfg.Profiles[i].Name);
                _cbProfile.SelectedItem = Cfg.Active.Name;
            }
            finally { _sync = false; }
        }

        private void RefreshGrid(RuleSpec select)
        {
            _sync = true;
            try
            {
                _grid.Rows.Clear();
                List<RuleSpec> rules = Cfg.Active.Rules;
                int selectIndex = -1;
                for (int i = 0; i < rules.Count; i++)
                {
                    RuleSpec r = rules[i];
                    int idx = _grid.Rows.Add((object)r.Enabled, r.Name, r.TriggerDisplay, r.TargetDisplay, r.ModeText);
                    _grid.Rows[idx].Tag = r;
                    if (select != null && ReferenceEquals(r, select)) selectIndex = idx;
                }
                if (selectIndex >= 0 && selectIndex < _grid.Rows.Count)
                {
                    _grid.ClearSelection();
                    _grid.Rows[selectIndex].Selected = true;
                    _grid.CurrentCell = _grid.Rows[selectIndex].Cells[1];
                }
                else if (_grid.Rows.Count > 0)
                {
                    _grid.ClearSelection();
                    _grid.Rows[0].Selected = true;
                }
            }
            finally { _sync = false; }
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool has = SelectedRule() != null;
            _btnEdit.Enabled = has;
            _btnDelete.Enabled = has;
            _btnUp.Enabled = has && SelectedIndex() > 0;
            _btnDown.Enabled = has && SelectedIndex() < Cfg.Active.Rules.Count - 1;
        }

        private RuleSpec SelectedRule()
        {
            if (_grid.SelectedRows.Count == 0) return null;
            return _grid.SelectedRows[0].Tag as RuleSpec;
        }

        private int SelectedIndex()
        {
            if (_grid.SelectedRows.Count == 0) return -1;
            return _grid.SelectedRows[0].Index;
        }

        private void OnGridCellClick(DataGridViewCellEventArgs e)
        {
            if (_sync) return;
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
            RuleSpec r = _grid.Rows[e.RowIndex].Tag as RuleSpec;
            if (r == null) return;
            r.Enabled = !r.Enabled;
            _sync = true;
            try { _grid.Rows[e.RowIndex].Cells[0].Value = r.Enabled; }
            finally { _sync = false; }
            AppState.Apply();
            UpdateStatusBar();
        }

        // ---------------- 规则操作 ----------------
        private void AddRule()
        {
            var draft = new RuleSpec();
            draft.Enabled = true;
            draft.TrigAlt = true;
            using (var dlg = new RuleDialog(Cfg.Active, draft, null))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Cfg.Active.Rules.Add(dlg.Result);
                AppState.Apply();
                RefreshGrid(dlg.Result);
                UpdateStatusBar();
            }
        }

        private void EditSelectedRule()
        {
            RuleSpec rule = SelectedRule();
            if (rule == null) return;
            RuleSpec draft = rule.Copy();
            using (var dlg = new RuleDialog(Cfg.Active, draft, rule))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                CopyRule(dlg.Result, rule);
                AppState.Apply();
                RefreshGrid(rule);
                UpdateStatusBar();
            }
        }

        private static void CopyRule(RuleSpec from, RuleSpec to)
        {
            to.Enabled = from.Enabled;
            to.Name = from.Name;
            to.TrigVk = from.TrigVk;
            to.TrigCtrl = from.TrigCtrl;
            to.TrigAlt = from.TrigAlt;
            to.TrigShift = from.TrigShift;
            to.TargetVk = from.TargetVk;
            to.TargetCtrl = from.TargetCtrl;
            to.TargetAlt = from.TargetAlt;
            to.TargetShift = from.TargetShift;
            to.Hold = from.Hold;
        }

        private void DeleteSelectedRule()
        {
            RuleSpec rule = SelectedRule();
            if (rule == null) return;
            if (MessageBox.Show(this, "确定删除映射“" + rule.Name + "”？", "KeySwitcher",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Cfg.Active.Rules.Remove(rule);
            AppState.Apply();
            RefreshGrid(null);
            UpdateStatusBar();
        }

        private void MoveSelected(int delta)
        {
            RuleSpec rule = SelectedRule();
            if (rule == null) return;
            List<RuleSpec> rules = Cfg.Active.Rules;
            int i = rules.IndexOf(rule);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= rules.Count) return;
            rules.RemoveAt(i);
            rules.Insert(j, rule);
            AppState.Apply();
            RefreshGrid(rule);
        }

        // ---------------- 方案 ----------------
        private void NewProfile()
        {
            string name = PromptDialog.Ask(this, "新建方案", "方案名称", "新方案");
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) return;
            if (FindProfile(name) != null)
            {
                MessageBox.Show(this, "已存在同名方案。", "KeySwitcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var p = new ProfileSpec(name);
            Cfg.Profiles.Add(p);
            Cfg.ActiveProfile = name;
            AppState.Apply();
            RefreshProfiles();
            RefreshGrid(null);
            UpdateStatusBar();
        }

        private void RenameProfile()
        {
            ProfileSpec p = Cfg.Active;
            string name = PromptDialog.Ask(this, "重命名方案", "方案名称", p.Name);
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0 || name == p.Name) return;
            ProfileSpec other = FindProfile(name);
            if (other != null && !ReferenceEquals(other, p))
            {
                MessageBox.Show(this, "已存在同名方案。", "KeySwitcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            p.Name = name;
            Cfg.ActiveProfile = name;
            AppState.Apply();
            RefreshProfiles();
            UpdateStatusBar();
        }

        private void DeleteProfile()
        {
            if (Cfg.Profiles.Count <= 1)
            {
                MessageBox.Show(this, "至少要保留一个方案。", "KeySwitcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ProfileSpec p = Cfg.Active;
            if (MessageBox.Show(this, "确定删除方案“" + p.Name + "”及其全部映射？", "KeySwitcher",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Cfg.Profiles.Remove(p);
            Cfg.ActiveProfile = Cfg.Profiles[0].Name;
            AppState.Apply();
            RefreshProfiles();
            RefreshGrid(null);
            UpdateStatusBar();
        }

        private ProfileSpec FindProfile(string name)
        {
            for (int i = 0; i < Cfg.Profiles.Count; i++)
                if (string.Equals(Cfg.Profiles[i].Name, name, StringComparison.Ordinal)) return Cfg.Profiles[i];
            return null;
        }

        private void SwitchProfile(string name)
        {
            if (FindProfile(name) == null) return;
            Cfg.ActiveProfile = name;
            AppState.Apply();
            RefreshProfiles();
            RefreshGrid(null);
            UpdateStatusBar();
        }

        // ---------------- 总开关热键 ----------------
        private void EditToggleHotkey()
        {
            using (var dlg = new CaptureDialog("录制总开关快捷键", "设置“启用 / 暂停映射”的全局快捷键"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!dlg.Captured) return;
                for (int i = 0; i < Cfg.Active.Rules.Count; i++)
                {
                    RuleSpec r = Cfg.Active.Rules[i];
                    if (r.TrigVk == dlg.CapturedVk && r.TrigCtrl == dlg.Ctrl && r.TrigAlt == dlg.Alt && r.TrigShift == dlg.Shift)
                    {
                        MessageBox.Show(this, "这个组合键已经被映射“" + r.Name + "”占用，请换一个。",
                            "KeySwitcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
                Cfg.ToggleCtrl = dlg.Ctrl;
                Cfg.ToggleAlt = dlg.Alt;
                Cfg.ToggleShift = dlg.Shift;
                Cfg.ToggleVk = dlg.CapturedVk;
                AppState.Apply();
                UpdateToggleLabel();
                UpdateStatusBar();
            }
        }

        private void UpdateToggleLabel()
        {
            _lblToggle.Text = "总开关  " + KeyCodec.DisplayCombo(Cfg.ToggleCtrl, Cfg.ToggleAlt, Cfg.ToggleShift, Cfg.ToggleVk);
            Relayout();
        }

        // ---------------- 导入导出 ----------------
        private void ExportConfig()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "导出配置";
                dlg.Filter = "KeySwitcher 配置 (*.ini)|*.ini|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dlg.FileName = "KeySwitcher-config.ini";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, ConfigStore.Serialize(Cfg), new UTF8Encoding(true));
                    MessageBox.Show(this, "已导出到：\n" + dlg.FileName, "KeySwitcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "KeySwitcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ImportConfig()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "导入配置";
                dlg.Filter = "KeySwitcher 配置 (*.ini;*.txt)|*.ini;*.txt|所有文件 (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AppConfig imported;
                try { imported = ConfigStore.Parse(File.ReadAllText(dlg.FileName, Encoding.UTF8)); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "读取失败：" + ex.Message, "KeySwitcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (imported == null || imported.Profiles.Count == 0)
                {
                    MessageBox.Show(this, "这个文件不是有效的 KeySwitcher 配置。", "KeySwitcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (MessageBox.Show(this, "导入会替换当前的全部方案与设置，确定继续？", "KeySwitcher",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                AppState.Config = imported;
                AppState.Normalize();
                AppState.Apply();
                RefreshProfiles();
                RefreshGrid(null);
                SyncFromConfig();
                UpdateStatusBar();
            }
        }

        // ---------------- 托盘 ----------------
        private void SetupTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = _appIcon != null ? _appIcon : SystemIcons.Application;
            _tray.Text = "KeySwitcher · 快捷键映射";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowMainWindow(); };

            _trayMenu = new ContextMenuStrip();
            _trayMenu.Opening += delegate { BuildTrayMenu(); };
            _tray.ContextMenuStrip = _trayMenu;
        }

        private void BuildTrayMenu()
        {
            _trayMenu.Items.Clear();

            var show = new ToolStripMenuItem("显示主窗口");
            show.Font = Theme.UiFontBold();
            show.Click += delegate { ShowMainWindow(); };
            _trayMenu.Items.Add(show);

            _trayMenu.Items.Add(new ToolStripSeparator());

            var master = new ToolStripMenuItem("启用映射");
            master.Checked = Cfg.Master;
            master.Click += delegate { AppState.ToggleMaster(); SyncFromConfig(); UpdateStatusBar(); };
            _trayMenu.Items.Add(master);

            var prof = new ToolStripMenuItem("切换方案");
            for (int i = 0; i < Cfg.Profiles.Count; i++)
            {
                ProfileSpec p = Cfg.Profiles[i];
                var item = new ToolStripMenuItem(p.Name);
                item.Checked = p.Name == Cfg.Active.Name;
                string name = p.Name;
                item.Click += delegate { SwitchProfile(name); };
                prof.DropDownItems.Add(item);
            }
            _trayMenu.Items.Add(prof);

            _trayMenu.Items.Add(new ToolStripSeparator());

            var auto = new ToolStripMenuItem("开机自动启动");
            auto.Checked = Cfg.AutoStart;
            auto.Click += delegate
            {
                Cfg.AutoStart = !Cfg.AutoStart;
                AutoStart.Set(Cfg.AutoStart);
                AppState.Save();
                SyncFromConfig();
            };
            _trayMenu.Items.Add(auto);

            var openCfg = new ToolStripMenuItem("打开配置目录");
            openCfg.Click += delegate { OpenConfigFolder(); };
            _trayMenu.Items.Add(openCfg);

            _trayMenu.Items.Add(new ToolStripSeparator());

            var quit = new ToolStripMenuItem("退出 KeySwitcher");
            quit.Click += delegate { ExitApp(); };
            _trayMenu.Items.Add(quit);
        }

        private void UpdateTrayText()
        {
            if (_tray == null) return;
            string s = "KeySwitcher · " + Cfg.Active.Name + (Cfg.Master ? "" : " · 已暂停");
            if (s.Length > 60) s = s.Substring(0, 60);
            _tray.Text = s;
        }

        private void ShowMainWindow()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
            BringToFront();
        }

        private void OpenConfigFolder()
        {
            try
            {
                if (!Directory.Exists(AppState.ConfigDir)) Directory.CreateDirectory(AppState.ConfigDir);
                Process.Start("explorer.exe", "\"" + AppState.ConfigDir + "\"");
            }
            catch { }
        }

        private void ExitApp()
        {
            _realExit = true;
            Close();
        }

        // ---------------- 状态栏 ----------------
        private void UpdateStatusBar()
        {
            ProfileSpec p = Cfg.Active;
            int on = 0;
            for (int i = 0; i < p.Rules.Count; i++) if (p.Rules[i].Enabled) on++;
            _lblCount.Text = string.Format("方案「{0}」 · {1} 条映射（启用 {2}） · {3}",
                p.Name, p.Rules.Count, on, Cfg.Master ? "映射运行中" : "已暂停");
            _lblCount.ToolTipText = AppState.ConfigPath;
            UpdateTrayText();
        }

        // ---------------- 提升权限 ----------------
        private void RestartElevated()
        {
            if (IsElevated())
            {
                MessageBox.Show(this, "当前已经是管理员权限。", "KeySwitcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = "--elevated";
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                _realExit = true;
                Close();
            }
            catch (Exception)
            {
                MessageBox.Show(this, "已取消或以管理员身份启动失败。", "KeySwitcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(id);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        // ---------------- 生命周期 ----------------
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _grid.Focus();
            if (_startHidden)
            {
                Hide();
                ShowTrayHint();
            }
            if (AppState.FirstRun)
            {
                AppState.FirstRun = false;
            }
        }

        internal static void TraceLog(string msg)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("KEYSWITCHER_TRACE") != "1") return;
                string dir = string.IsNullOrEmpty(AppState.ConfigDir) ? AppDomain.CurrentDomain.BaseDirectory : AppState.ConfigDir;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "trace.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            TraceLog("OnFormClosing reason=" + e.CloseReason + " realExit=" + _realExit + " minimize=" + Cfg.MinimizeToTray);
            if (!_realExit && Cfg.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowTrayHint();
                return;
            }
            TraceLog("OnFormClosing -> proceed to close");
            base.OnFormClosing(e);
        }

        private void ShowTrayHint()
        {
            if (_trayHintShown || _tray == null) return;
            _trayHintShown = true;
            try
            {
                _tray.BalloonTipTitle = "KeySwitcher 仍在后台运行";
                _tray.BalloonTipText = "快捷键映射继续生效。右键托盘图标可以暂停映射或退出。";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(3000);
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            TraceLog("OnFormClosed reason=" + e.CloseReason);
            try
            {
                AppState.MasterChanged -= OnMasterChanged;
                AppState.DataChanged -= OnDataChanged;
                if (_uiTimer != null) { _uiTimer.Stop(); _uiTimer.Dispose(); }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            }
            catch { }
            base.OnFormClosed(e);
        }
    }

    // =====================================================================
    // 规则编辑对话框
    // =====================================================================
    internal sealed class RuleDialog : Form
    {
        private readonly ProfileSpec _profile;
        private readonly RuleSpec _original;
        private readonly RuleSpec _draft;

        private TextBox _txtName;
        private TextBox _txtTrigger;
        private TextBox _txtTarget;
        private RadioButton _rbTap;
        private RadioButton _rbHold;
        private CheckBox _chkEnabled;
        private Label _lblWarn;
        private Button _btnSave;

        public RuleSpec Result;

        public RuleDialog(ProfileSpec profile, RuleSpec draft, RuleSpec original)
        {
            _profile = profile;
            _draft = draft;
            _original = original;

            Text = original == null ? "添加映射" : "编辑映射";
            ClientSize = new Size(560, 466);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = Theme.UiFont();
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi;

            BuildUi();
            LoadDraft();
        }

        private void BuildUi()
        {
            var lName = new Label();
            lName.Text = "名称";
            lName.AutoSize = true;
            lName.ForeColor = Theme.Muted;
            lName.Location = new Point(22, 22);
            Controls.Add(lName);

            _txtName = new TextBox();
            _txtName.Location = new Point(78, 18);
            _txtName.Width = 458;
            _txtName.Font = Theme.UiFont();
            Controls.Add(_txtName);

            var g1 = new GroupBox();
            g1.Text = " 当按下这个组合键 ";
            g1.ForeColor = Theme.Text;
            g1.Font = Theme.UiFontBold();
            g1.Location = new Point(20, 60);
            g1.Size = new Size(518, 88);
            Controls.Add(g1);

            _txtTrigger = new TextBox();
            _txtTrigger.ReadOnly = true;
            _txtTrigger.BackColor = Theme.PanelBg;
            _txtTrigger.Font = new Font(Theme.UiFont().FontFamily, 10F);
            _txtTrigger.Location = new Point(16, 32);
            _txtTrigger.Width = 356;
            _txtTrigger.Height = 28;
            _txtTrigger.TabStop = false;
            g1.Controls.Add(_txtTrigger);

            var btnCap1 = Theme.MakeButton("录制…", 110, false);
            btnCap1.Location = new Point(386, 30);
            btnCap1.Height = 30;
            g1.Controls.Add(btnCap1);
            btnCap1.Click += delegate { CaptureInto(true); };

            var g2 = new GroupBox();
            g2.Text = " 就发送这个按键 ";
            g2.ForeColor = Theme.Text;
            g2.Font = Theme.UiFontBold();
            g2.Location = new Point(20, 156);
            g2.Size = new Size(518, 88);
            Controls.Add(g2);

            _txtTarget = new TextBox();
            _txtTarget.ReadOnly = true;
            _txtTarget.BackColor = Theme.PanelBg;
            _txtTarget.Font = new Font(Theme.UiFont().FontFamily, 10F);
            _txtTarget.Location = new Point(16, 32);
            _txtTarget.Width = 356;
            _txtTarget.Height = 28;
            _txtTarget.TabStop = false;
            g2.Controls.Add(_txtTarget);

            var btnCap2 = Theme.MakeButton("录制…", 110, false);
            btnCap2.Location = new Point(386, 30);
            btnCap2.Height = 30;
            g2.Controls.Add(btnCap2);
            btnCap2.Click += delegate { CaptureInto(false); };

            var g3 = new GroupBox();
            g3.Text = " 触发方式 ";
            g3.ForeColor = Theme.Text;
            g3.Font = Theme.UiFontBold();
            g3.Location = new Point(20, 252);
            g3.Size = new Size(518, 88);
            Controls.Add(g3);

            _rbTap = new RadioButton();
            _rbTap.Text = "单击 —— 按一下触发一次（适合方向键、复制粘贴等）";
            _rbTap.AutoSize = true;
            _rbTap.Location = new Point(14, 24);
            _rbTap.Font = Theme.UiFont();
            g3.Controls.Add(_rbTap);

            _rbHold = new RadioButton();
            _rbHold.Text = "按住 —— 按下持续、松开结束（适合需要长按的操作）";
            _rbHold.AutoSize = true;
            _rbHold.Location = new Point(14, 50);
            _rbHold.Font = Theme.UiFont();
            g3.Controls.Add(_rbHold);

            _chkEnabled = new CheckBox();
            _chkEnabled.Text = "启用这条映射";
            _chkEnabled.AutoSize = true;
            _chkEnabled.Location = new Point(22, 352);
            _chkEnabled.ForeColor = Theme.Text;
            Controls.Add(_chkEnabled);

            _lblWarn = new Label();
            _lblWarn.AutoSize = false;
            _lblWarn.Location = new Point(22, 378);
            _lblWarn.Size = new Size(516, 38);
            _lblWarn.ForeColor = Color.FromArgb(185, 28, 28);
            _lblWarn.Text = "";
            Controls.Add(_lblWarn);

            _btnSave = Theme.MakeButton("保存", 96, true);
            _btnSave.Location = new Point(340, 422);
            _btnSave.DialogResult = DialogResult.None;
            Controls.Add(_btnSave);

            var btnCancel = Theme.MakeButton("取消", 96, false);
            btnCancel.Location = new Point(444, 422);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            AcceptButton = _btnSave;
            CancelButton = btnCancel;

            _btnSave.Click += delegate { OnSave(); };
        }

        private void LoadDraft()
        {
            _txtName.Text = _draft.Name;
            _chkEnabled.Checked = _draft.Enabled;
            _rbTap.Checked = !_draft.Hold;
            _rbHold.Checked = _draft.Hold;
            RefreshTexts();
        }

        private void RefreshTexts()
        {
            _txtTrigger.Text = _draft.TrigVk == 0 ? "" : _draft.TriggerDisplay;
            _txtTarget.Text = _draft.TargetVk == 0 ? "" : _draft.TargetDisplay;
        }

        private void CaptureInto(bool trigger)
        {
            using (var dlg = new CaptureDialog(trigger ? "录制触发组合键" : "录制要发送的按键",
                trigger ? "请按下你想使用的组合键" : "请按下要代替它发送的按键"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || !dlg.Captured) return;
                if (trigger)
                {
                    _draft.TrigCtrl = dlg.Ctrl;
                    _draft.TrigAlt = dlg.Alt;
                    _draft.TrigShift = dlg.Shift;
                    _draft.TrigVk = dlg.CapturedVk;
                }
                else
                {
                    _draft.TargetCtrl = dlg.Ctrl;
                    _draft.TargetAlt = dlg.Alt;
                    _draft.TargetShift = dlg.Shift;
                    _draft.TargetVk = dlg.CapturedVk;
                }
                RefreshTexts();
                _lblWarn.Text = "";
            }
        }

        private void OnSave()
        {
            if (_draft.TrigVk == 0)
            {
                _lblWarn.Text = "请先点「录制…」设置触发组合键。";
                return;
            }
            if (_draft.TargetVk == 0)
            {
                _lblWarn.Text = "请先点「录制…」设置要发送的按键。";
                return;
            }

            for (int i = 0; i < _profile.Rules.Count; i++)
            {
                RuleSpec r = _profile.Rules[i];
                if (ReferenceEquals(r, _original)) continue;
                if (r.TrigVk == _draft.TrigVk && r.TrigCtrl == _draft.TrigCtrl &&
                    r.TrigAlt == _draft.TrigAlt && r.TrigShift == _draft.TrigShift)
                {
                    _lblWarn.Text = "方案里已有同样触发键的映射（" + r.Name + "），请先改掉那条。";
                    return;
                }
            }

            if (!_draft.TrigCtrl && !_draft.TrigAlt && !_draft.TrigShift)
            {
                DialogResult dr = MessageBox.Show(this,
                    "这条映射没有 Ctrl / Alt / Shift 修饰键，它会接管这个按键在系统里的所有输入。\n" +
                    "例如把 F8 映射成别的键之后，F8 原来的功能就按不出来了（除非暂停映射）。\n\n确定继续吗？",
                    "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes) return;
            }

            _draft.Enabled = _chkEnabled.Checked;
            _draft.Hold = _rbHold.Checked;
            string name = _txtName.Text.Trim();
            if (name.Length == 0) name = _draft.AutoName();
            _draft.Name = name;

            Result = _draft;
            DialogResult = DialogResult.OK;
        }
    }

    // =====================================================================
    // 组合键录制对话框
    // =====================================================================
    internal sealed class CaptureDialog : Form
    {
        private readonly Label _lblPreview;
        private readonly Label _lblHint;

        public bool Captured;
        public bool Ctrl, Alt, Shift;
        public ushort CapturedVk;

        public CaptureDialog(string title, string prompt)
        {
            Text = title;
            ClientSize = new Size(470, 226);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = Theme.UiFont();
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;

            var lbl = new Label();
            lbl.Text = prompt;
            lbl.AutoSize = true;
            lbl.ForeColor = Theme.Muted;
            lbl.Location = new Point(24, 22);
            Controls.Add(lbl);

            _lblPreview = new Label();
            _lblPreview.Text = "等待按键…";
            _lblPreview.AutoSize = false;
            _lblPreview.TextAlign = ContentAlignment.MiddleCenter;
            _lblPreview.Font = new Font(Theme.UiFont().FontFamily, 15F, FontStyle.Bold);
            _lblPreview.ForeColor = Theme.Text;
            _lblPreview.BackColor = Theme.PanelBg;
            _lblPreview.BorderStyle = BorderStyle.FixedSingle;
            _lblPreview.Location = new Point(24, 58);
            _lblPreview.Size = new Size(422, 74);
            Controls.Add(_lblPreview);

            _lblHint = new Label();
            _lblHint.Text = "按下组合键后会立即采用；按 Esc 取消。";
            _lblHint.AutoSize = true;
            _lblHint.ForeColor = Theme.Muted;
            _lblHint.Location = new Point(24, 144);
            Controls.Add(_lblHint);


            var btnCancel = Theme.MakeButton("取消", 96, false);
            btnCancel.Location = new Point(350, 176);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);
            CancelButton = btnCancel;

            Engine.Suspended = true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (TryCapture(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (TryCapture(keyData)) return true;
            return base.ProcessDialogKey(keyData);
        }

        private bool TryCapture(Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;

            if (code == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                return true;
            }
            if (code == Keys.None) return true;

            bool isModifier =
                code == Keys.ControlKey || code == Keys.LControlKey || code == Keys.RControlKey ||
                code == Keys.ShiftKey || code == Keys.LShiftKey || code == Keys.RShiftKey ||
                code == Keys.Menu || code == Keys.LMenu || code == Keys.RMenu ||
                code == Keys.LWin || code == Keys.RWin;

            Ctrl = (keyData & Keys.Control) != 0;
            Alt = (keyData & Keys.Alt) != 0;
            Shift = (keyData & Keys.Shift) != 0;

            if (isModifier)
            {
                _lblPreview.Text = KeyCodec.DisplayCombo(Ctrl, Alt, Shift, 0) + " + …";
                return true;
            }

            CapturedVk = (ushort)code;
            Captured = true;
            _lblPreview.Text = KeyCodec.DisplayCombo(Ctrl, Alt, Shift, CapturedVk);
            _lblHint.Text = "已采用，正在返回…";

            // 立即返回，避免焦点/输入法干扰
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (!IsDisposed) DialogResult = DialogResult.OK;
                }));
            }
            catch
            {
                DialogResult = DialogResult.OK;
            }
            return true;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Engine.Suspended = false;
            base.OnFormClosed(e);
        }
    }

    // =====================================================================
    // 简单输入框
    // =====================================================================
    internal sealed class PromptDialog : Form
    {
        private readonly TextBox _txt;

        private PromptDialog(string title, string label, string initial)
        {
            Text = title;
            ClientSize = new Size(420, 168);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = Theme.UiFont();
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi;

            var lbl = new Label();
            lbl.Text = label;
            lbl.AutoSize = true;
            lbl.ForeColor = Theme.Muted;
            lbl.Location = new Point(20, 20);
            Controls.Add(lbl);

            _txt = new TextBox();
            _txt.Text = initial;
            _txt.Location = new Point(20, 46);
            _txt.Width = 380;
            _txt.Font = new Font(Theme.UiFont().FontFamily, 10.5F);
            Controls.Add(_txt);

            var ok = Theme.MakeButton("确定", 88, true);
            ok.Location = new Point(204, 112);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            var cancel = Theme.MakeButton("取消", 88, false);
            cancel.Location = new Point(302, 112);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        public static string Ask(IWin32Window owner, string title, string label, string initial)
        {
            using (var dlg = new PromptDialog(title, label, initial))
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                return dlg._txt.Text;
            }
        }
    }

    // =====================================================================
    // 自检（不启动界面）
    // =====================================================================
    internal static class SelfTest
    {
        private static StringBuilder _log;
        private static int _fail;

        private static void Check(string name, bool ok)
        {
            if (!ok) _fail++;
            _log.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
        }

        public static int Run()
        {
            _log = new StringBuilder();
            _fail = 0;

            try
            {
                TestKeyCodec();
                TestConfigRoundTrip();
                TestMatching();
                TestHookBehavior();
                TestConfigFile();
                TestRobustness();
            }
            catch (Exception ex)
            {
                _fail++;
                _log.AppendLine("FAIL  异常：" + ex);
            }

            _log.AppendLine();
            _log.AppendLine(_fail == 0 ? "RESULT: ALL PASSED" : "RESULT: " + _fail + " FAILED");

            string text = _log.ToString();
            Console.WriteLine(text);
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log"), text, new UTF8Encoding(true));
            }
            catch { }
            return _fail == 0 ? 0 : 1;
        }

        private static void TestKeyCodec()
        {
            bool c, a, s;
            ushort vk;

            Check("parse Alt+OemComma", KeyCodec.TryParse("Alt+OemComma", out c, out a, out s, out vk) && vk == 0xBC && a && !c && !s);
            Check("parse Ctrl+Alt+F9", KeyCodec.TryParse("Ctrl+Alt+F9", out c, out a, out s, out vk) && vk == 0x78 && c && a && !s);
            Check("parse Shift+Left", KeyCodec.TryParse("Shift+Left", out c, out a, out s, out vk) && vk == 0x25 && s && !a);
            Check("parse OemPlus", KeyCodec.TryParse("Ctrl+OemPlus", out c, out a, out s, out vk) && vk == 0xBB && c);
            Check("parse invalid", !KeyCodec.TryParse("Ctrl+Bogus", out c, out a, out s, out vk));
            Check("parse empty", !KeyCodec.TryParse("", out c, out a, out s, out vk));

            Check("display Alt+Comma", KeyCodec.DisplayCombo(false, true, false, 0xBC) == "Alt + ,");
            Check("display Ctrl+Alt+F9", KeyCodec.DisplayCombo(true, true, false, 0x78) == "Ctrl + Alt + F9");
            Check("display Left", KeyCodec.DisplayOf(0x25) == "←");

            // 往返
            string[] samples = { "Alt+OemComma", "Ctrl+Shift+F5", "Left", "Alt+Numpad5", "Ctrl+Alt+Shift+OemPeriod" };
            for (int i = 0; i < samples.Length; i++)
            {
                bool ok = KeyCodec.TryParse(samples[i], out c, out a, out s, out vk);
                string round = KeyCodec.FormatCombo(c, a, s, vk);
                Check("roundtrip " + samples[i], ok && round == samples[i]);
            }
        }

        private static void TestConfigRoundTrip()
        {
            AppConfig cfg = AppConfig.CreateDefault();
            Check("default has 2 rules", cfg.Profiles.Count == 1 && cfg.Profiles[0].Rules.Count == 2);
            Check("default rule1 target", cfg.Profiles[0].Rules[0].TargetVk == 0x25);
            Check("default rule2 target", cfg.Profiles[0].Rules[1].TargetVk == 0x27);

            var p2 = new ProfileSpec("游戏 模式");
            p2.Rules.Add(new RuleSpec
            {
                Name = "Alt + , → Ctrl+←",
                TrigAlt = true,
                TrigVk = 0xBC,
                TargetCtrl = true,
                TargetVk = 0x25,
                Hold = true,
                Enabled = false
            });
            cfg.Profiles.Add(p2);

            string text = ConfigStore.Serialize(cfg);
            AppConfig back = ConfigStore.Parse(text);

            Check("roundtrip profiles", back != null && back.Profiles.Count == 2);
            Check("roundtrip active", back != null && back.ActiveProfile == "默认");
            Check("roundtrip chinese name", back != null && back.Profiles[1].Name == "游戏 模式");
            Check("roundtrip rule count", back != null && back.Profiles[0].Rules.Count == 2 && back.Profiles[1].Rules.Count == 1);

            RuleSpec r = back.Profiles[1].Rules[0];
            Check("roundtrip hold", r.Hold);
            Check("roundtrip disabled", !r.Enabled);
            Check("roundtrip target mod", r.TargetCtrl && r.TargetVk == 0x25);
            Check("roundtrip trigger", r.TrigAlt && r.TrigVk == 0xBC);
            Check("roundtrip name", r.Name == "Alt + , → Ctrl+←");
            Check("roundtrip toggle", back.ToggleVk == 0x78 && back.ToggleCtrl && back.ToggleAlt);
        }

        private static void TestMatching()
        {
            var rules = new RuleSpec[]
            {
                new RuleSpec { TrigVk = 0xBC, TrigAlt = true, TargetVk = 0x25 },
                new RuleSpec { TrigVk = 0xBE, TrigAlt = true, TargetVk = 0x27 },
                new RuleSpec { TrigVk = 0x41, TrigCtrl = true, TrigShift = true, TargetVk = 0x42 },
            };

            Check("match alt+comma", Engine.FindMatch(rules, 0xBC, false, true, false) == rules[0]);
            Check("match alt+period", Engine.FindMatch(rules, 0xBE, false, true, false) == rules[1]);
            Check("extra ctrl no match", Engine.FindMatch(rules, 0xBC, true, true, false) == null);
            Check("missing alt no match", Engine.FindMatch(rules, 0xBC, false, false, false) == null);
            Check("wrong key no match", Engine.FindMatch(rules, 0xBD, false, true, false) == null);
            Check("ctrl+shift+A", Engine.FindMatch(rules, 0x41, true, false, true) == rules[2]);
            Check("shift missing", Engine.FindMatch(rules, 0x41, true, false, false) == null);

            // 快照只包含启用的规则
            AppConfig cfg = AppConfig.CreateDefault();
            cfg.Profiles[0].Rules[1].Enabled = false;
            Engine.Rebuild(cfg);
            Check("snapshot enabled only", Engine.Current.Rules.Length == 1);
            cfg.Master = false;
            Engine.Rebuild(cfg);
            Check("snapshot master flag", !Engine.Current.Master);
            Check("snapshot toggle", Engine.Current.HasToggle && Engine.Current.ToggleVk == 0x78);

            // 恢复默认，避免影响后续
            cfg.Master = true;
            cfg.Profiles[0].Rules[1].Enabled = true;
            Engine.Rebuild(cfg);
        }

        private static void TestHookBehavior()
        {
            AppConfig cfg = AppConfig.CreateDefault();
            cfg.Master = true;
            Engine.Rebuild(cfg);

            var hook = new KeyboardHook();
            var sent = new List<string>();
            KeyboardHook.TestSink = delegate(ushort vk, bool up, bool ext)
            {
                sent.Add(vk.ToString("X2") + (up ? "u" : "d"));
            };

            try
            {
                hook.TestReset();
                KeyboardHook.TestKeyState = delegate(ushort vk) { return vk == 0xA4; }; // 只有 Alt 按住

                sent.Clear();
                IntPtr down = hook.TestHookEvent(0x0104, 0xBC, 0);   // WM_SYSKEYDOWN, Alt+,
                Check("hook suppresses trigger down", down == new IntPtr(1));
                Check("hook tap sequence", string.Join(",", sent.ToArray()) == "A2d,A2u,A4u,25d,25u,A4d");

                sent.Clear();
                hook.TestHookEvent(0x0104, 0xBC, 0);                 // 自动重复（按住不放）
                Check("hook repeat keeps alt released", string.Join(",", sent.ToArray()) == "A2d,A2u,A4u,25d,25u,A4d");

                sent.Clear();
                IntPtr up = hook.TestHookEvent(0x0105, 0xBC, 0);     // WM_SYSKEYUP, Alt+,
                Check("hook suppresses trigger up", up == new IntPtr(1));
                Check("hook no duplicate action on up", sent.Count == 0);

                sent.Clear();
                hook.TestHookEvent(0x0105, 0xA4, 0);                 // 物理 Alt 抬起
                Check("hook masks physical alt up", string.Join(",", sent.ToArray()) == "A2d,A2u");

                sent.Clear();
                KeyboardHook.TestKeyState = delegate(ushort vk) { return false; };
                IntPtr plain = hook.TestHookEvent(0x0100, 0xBC, 0);  // 无 Alt 的逗号
                Check("hook passes plain comma", plain == IntPtr.Zero);
                Check("hook plain comma no injection", sent.Count == 0);

                cfg.Master = false;
                Engine.Rebuild(cfg);
                KeyboardHook.TestKeyState = delegate(ushort vk) { return vk == 0xA4; };
                sent.Clear();
                IntPtr paused = hook.TestHookEvent(0x0104, 0xBC, 0);
                Check("hook passes when paused", paused == IntPtr.Zero);
                Check("hook paused no injection", sent.Count == 0);

                cfg.Master = true;
                Engine.Rebuild(cfg);

                // 按住模式 + 先松 Alt：不应把 Alt 卡在按下状态
                var hold = new ProfileSpec("hold");
                hold.Rules.Add(new RuleSpec
                {
                    Name = "hold test", TrigAlt = true, TrigVk = 0xBE, TargetVk = 0x27, Hold = true
                });
                cfg.Profiles.Clear();
                cfg.Profiles.Add(hold);
                cfg.ActiveProfile = "hold";
                Engine.Rebuild(cfg);

                hook.TestReset();
                KeyboardHook.TestKeyState = delegate(ushort vk) { return vk == 0xA4; };
                sent.Clear();
                hook.TestHookEvent(0x0104, 0xBE, 0);                 // 按下
                Check("hook hold presses target", string.Join(",", sent.ToArray()) == "A2d,A2u,A4u,27d");

                sent.Clear();
                hook.TestHookEvent(0x0105, 0xA4, 0);                 // 先松开 Alt
                Check("hook hold masks alt up", string.Join(",", sent.ToArray()) == "A2d,A2u");

                sent.Clear();
                hook.TestHookEvent(0x0105, 0xBE, 0);                 // 再松开触发键
                Check("hook hold releases without stuck alt", string.Join(",", sent.ToArray()) == "27u");
            }
            finally
            {
                KeyboardHook.TestKeyState = null;
                KeyboardHook.TestSink = null;
                hook.Dispose();
                Engine.Rebuild(AppConfig.CreateDefault());
            }
        }

        private static void TestConfigFile()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-tmp-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(dir, "config.ini");
            try
            {
                Directory.CreateDirectory(dir);
                AppConfig cfg = AppConfig.CreateDefault();
                cfg.MinimizeToTray = false;
                File.WriteAllText(file, ConfigStore.Serialize(cfg), new UTF8Encoding(true));

                AppConfig back = ConfigStore.Load(file);
                Check("file save/load", back.Profiles.Count == 1 && back.Profiles[0].Rules.Count == 2);
                Check("file keeps flags", !back.MinimizeToTray && back.Master);
                Check("file default mapping", back.Profiles[0].Rules[0].TrigVk == 0xBC && back.Profiles[0].Rules[0].TargetVk == 0x25);

                // 坏文件不应崩溃
                File.WriteAllText(file, "[profile=坏了\nrule=???", new UTF8Encoding(true));
                AppConfig broken = ConfigStore.Load(file);
                Check("corrupt file falls back", broken != null && broken.Profiles.Count == 1);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void TestRobustness()
        {
            Check("parse garbage returns null", ConfigStore.Parse("hello world\nnot a config") == null);
            Check("parse empty returns null", ConfigStore.Parse("") == null);

            AppConfig cfg = ConfigStore.Parse("version=1\nmaster=1\n[profile=测试]\nrule=1|坏行|Alt+Bogus|Left|tap\nrule=1|好的|Alt+OemComma|Left|tap");
            Check("skip bad rule", cfg != null && cfg.Profiles.Count == 1 && cfg.Profiles[0].Rules.Count == 1);
            Check("keep good rule", cfg != null && cfg.Profiles[0].Rules[0].Name == "好的");

            Check("pipe in name sanitized", !ConfigStore.Serialize(AppConfig.CreateDefault()).Contains("||"));
        }
    }

    // =====================================================================
    // 钩子自检：真实注入一次 Alt+, 观察钩子是否拦截
    // =====================================================================
    internal static class HookTest
    {
        public static int Run()
        {
            var log = new StringBuilder();
            int exit = 0;
            KeyboardHook hook = null;
            try
            {
                AppState.Initialize();

                // 确保配置里有一条 Alt+Comma → Left 且总开关打开
                AppConfig cfg = AppState.Config;
                cfg.Master = true;
                cfg.Profiles.Clear();
                var p = new ProfileSpec("测试");
                p.Rules.Add(new RuleSpec { Name = "test", TrigAlt = true, TrigVk = 0xBC, TargetVk = 0x25 });
                cfg.Profiles.Add(p);
                cfg.ActiveProfile = p.Name;
                Engine.Rebuild(cfg);

                KeyboardHook.Diagnostics = true;
                hook = new KeyboardHook();
                bool ok = hook.Start();
                log.AppendLine("hook installed: " + ok);
                log.AppendLine("snapshot: master=" + Engine.Current.Master + " rules=" + Engine.Current.Rules.Length +
                               " toggle=" + Engine.Current.ToggleVk);
                log.AppendLine("rule0: vk=" + Engine.Current.Rules[0].TrigVk + " alt=" + Engine.Current.Rules[0].TrigAlt +
                               " target=" + Engine.Current.Rules[0].TargetVk);
                if (!ok)
                {
                    log.AppendLine("RESULT: HOOK INSTALL FAILED (可能没有可交互桌面)");
                    Write(log.ToString());
                    return 2;
                }

                Thread.Sleep(300);
                int before = KeyboardHook.MatchCount;

                // 模拟真实的 Alt + , 物理按键（不带我们的魔数，钩子应该拦截）
                KeyboardHook.SimulatePhysicalKey(0xA4, false);
                Thread.Sleep(30);
                KeyboardHook.SimulatePhysicalKey(0xBC, false);
                Thread.Sleep(30);
                KeyboardHook.SimulatePhysicalKey(0xBC, true);
                Thread.Sleep(30);
                KeyboardHook.SimulatePhysicalKey(0xA4, true);
                Thread.Sleep(400);

                int matched = KeyboardHook.MatchCount - before;
                log.AppendLine("matched: " + matched);
                log.AppendLine("allEvents: " + KeyboardHook.AllEvents);
                log.AppendLine("lastEvent: " + KeyboardHook.LastEventInfo);
                log.AppendLine("injected: " + KeyboardHook.InjectCount);
                log.AppendLine("errors: " + KeyboardHook.ErrorCount);

                bool pass = matched >= 1 && KeyboardHook.InjectCount >= 2 && KeyboardHook.ErrorCount == 0;
                log.AppendLine(pass ? "RESULT: PASS" : "RESULT: FAIL");
                exit = pass ? 0 : 1;
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION " + ex);
                log.AppendLine("RESULT: FAIL");
                exit = 1;
            }
            finally
            {
                if (hook != null) { hook.Stop(); hook.Dispose(); }
            }

            Write(log.ToString());
            return exit;
        }

        private static void Write(string text)
        {
            Console.WriteLine(text);
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hooktest.log"), text, new UTF8Encoding(true)); }
            catch { }
        }
    }
}
