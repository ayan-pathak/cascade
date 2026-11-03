using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Cascade
{
    class DockGroup
    {
        public string Key, Name, IconPath, Exe;
        public AppEntry App;
        public bool Pinned;
        public List<IntPtr> Wins = new List<IntPtr>();
    }

    public class Host
    {
        public const string MsgTitle = "CascadeHost";
        public const int WM_QUIT_CASCADE = Native.WM_APP + 77;
        const double TopCss = 40, DockCss = 80;
        static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

        public Config Cfg;
        public State St;
        public IntPtr LastForeground;
        public double Dpi = 1, Scale = 1;
        int sw, sh;

        WebWindow desktop, topbar, dock, launcher, personalize, ambient, calendar, preview;
        Thumbnails previewThumbs, liveThumbs;
        string previewKey;
        int previewHold;
        double previewX, previewTop;
        DispatcherTimer previewTimer;
        Dictionary<string, object> liveBox;
        string liveSig = "";
        object lastUsage;
        bool usageBusy;
        List<WebWindow> all = new List<WebWindow>();
        readonly HashSet<WebWindow> registered = new HashSet<WebWindow>();
        TipWindow tip;
        HwndSource msgWin;
        readonly Dictionary<int, Action> hotkeys = new Dictionary<int, Action>();
        int nextHotkey = 1;

        string settingsJson;
        Dictionary<string, object> settings;
        DispatcherTimer fast, slow, saveTimer, wallTimer;
        int slowTicks;

        Dictionary<string, DockGroup> groups = new Dictionary<string, DockGroup>();
        readonly List<string> runningOrder = new List<string>();
        readonly Dictionary<string, string> iconMap = new Dictionary<string, string>();
        readonly HashSet<string> iconTried = new HashSet<string>();
        readonly Dictionary<string, string> friendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string winSig = "";
        object lastWindows, lastStats, lastMedia, lastSessions;
        List<object> startApps;
        bool restored, keepSystem, ending, suppressed, dialogOpen, reflowing;
        Native.WinEventProc fgHook, showHook;
        bool dockAuto, dockModeSet, dockShown, menuOpen;
        int dockW, dockH, edgeHold, leaveTicks;
        DispatcherTimer edgeTimer, slideTimer;
        readonly HashSet<IntPtr> seen = new HashSet<IntPtr>();
        object lastEvents;
        string lastIcs;

        public async void Start()
        {
            try
            {
                Cfg = Config.Load();
                St = State.Load();
                Directory.CreateDirectory(Paths.Walls);
                Directory.CreateDirectory(Paths.WebData);
                if (File.Exists(Paths.Settings))
                {
                    try { settingsJson = File.ReadAllText(Paths.Settings); settings = Json.Obj(settingsJson); }
                    catch { settingsJson = null; settings = null; }
                }
                Measure();
                if (Cfg.hideWindowsTaskbar) Taskbar.Hide(St);

                msgWin = new HwndSource(new HwndSourceParameters(MsgTitle) { Width = 0, Height = 0, WindowStyle = 0 });
                msgWin.AddHook(MsgProc);

                var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebData);
                desktop = new WebWindow(this, "desktop") { BottomMost = true };
                topbar = new WebWindow(this, "topbar") { Topmost = true, IsAppBar = true, Edge = Native.ABE_TOP };
                dock = new WebWindow(this, "dock") { Topmost = true, IsAppBar = true, Edge = Native.ABE_BOTTOM };
                launcher = new WebWindow(this, "launcher") { Topmost = true };
                personalize = new WebWindow(this, "personalize") { Topmost = true };
                ambient = new WebWindow(this, "ambient") { Topmost = true };
                calendar = new WebWindow(this, "calendar") { Topmost = true };
                preview = new WebWindow(this, "preview") { Topmost = true, Left = -30000, Top = -30000, Width = 400, Height = 200 };
                all = new List<WebWindow> { desktop, topbar, dock, launcher, personalize, ambient, calendar, preview };

                foreach (var p in new[] { launcher, personalize, ambient, calendar })
                {
                    p.Left = -30000; p.Top = -30000; p.Width = 400; p.Height = 400;
                    p.Deactivated += (s, e) => { if (!dialogOpen) ((Window)s).Hide(); };
                }
                // Clicking the raised desktop layer reorders the topmost band; keep the bars above it.
                desktop.Activated += (s, e) => { if (!desktop.BottomMost) RaiseBars(); };
                foreach (var w in all) w.Show();
                foreach (var p in new[] { launcher, personalize, calendar, preview }) Native.RoundCorners(p.Handle, 0x00303A33);
                Native.AddExStyle(preview.Handle, Native.WS_EX_NOACTIVATE);
                previewThumbs = new Thumbnails(preview.Handle);
                liveThumbs = new Thumbnails(desktop.Handle);
                Reflow();

                await Task.WhenAll(all.Select(w => w.InitAsync(env, Scale)));
                preview.Hide();
                launcher.Hide();
                personalize.Hide();
                ambient.Hide();
                calendar.Hide();

                tip = new TipWindow();
                UpdateTipColors();
                RegisterHotkeys();

                fast = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                fast.Tick += (s, e) => Guard(FastTick);
                fast.Start();
                slow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                slow.Tick += (s, e) => Guard(SlowTick);
                slow.Start();
                saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                saveTimer.Tick += (s, e) => { saveTimer.Stop(); try { File.WriteAllText(Paths.Settings, settingsJson ?? "{}"); } catch (Exception ex) { Log.Write("save: " + ex.Message); } };
                wallTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                wallTimer.Tick += (s, e) => { wallTimer.Stop(); SyncWindowsWallpaper(); };

                SystemEvents.DisplaySettingsChanged += (s, e) => desktop.Dispatcher.BeginInvoke(new Action(OnDisplayChanged));
                fgHook = (hk, ev, hwnd, o, c, t, tm) => Guard(() => DesktopMode(hwnd));
                Native.SetWinEventHook(3, 3, IntPtr.Zero, fgHook, 0, 0, 0);
                foreach (var w in WindowTracker.Enumerate()) seen.Add(w.Hwnd);
                showHook = (hk, ev, hwnd, o, c, t, tm) => { if (o == 0 && c == 0) OnWindowShown(hwnd); };
                Native.SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, showHook, 0, 0, 0);
                edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                edgeTimer.Tick += (s, e) => Guard(EdgeTick);
                previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                previewTimer.Tick += (s, e) => Guard(PreviewTick);
                ApplyDockMode();
                FetchCalendar();
                Guard(FastTick);
                Guard(SlowTick);
                Log.Write("started at " + sw + "x" + sh + " dpi " + Dpi.ToString("0.00") + " scale " + Scale.ToString("0.00"));
            }
            catch (Exception e)
            {
                Log.Write("start failed: " + e);
                RestoreSystem();
                MessageBox.Show("Cascade couldn't start:\n\n" + e.Message + "\n\nDetails are in data\\log.txt.", "Cascade");
                Application.Current.Shutdown();
            }
        }

        static void Guard(Action a) { try { a(); } catch (Exception e) { Log.Write("tick: " + e); } }

        void Measure()
        {
            sw = Native.GetSystemMetrics(0);
            sh = Native.GetSystemMetrics(1);
            double dipW = SystemParameters.PrimaryScreenWidth;
            Dpi = sw / dipW;
            Layouts.Dpi = Dpi;
            Scale = Math.Max(0.85, Math.Min(1.5, dipW / 1440.0));
        }

        int Px(double css) => (int)Math.Round(css * Scale * Dpi);

        object RectDip(WebWindow w)
        {
            Native.GetWindowRect(w.Handle, out var r);
            return new { x = r.Left / Dpi, y = r.Top / Dpi, w = r.Width / Dpi, h = r.Height / Dpi };
        }

        object ScreenDip() => new { w = sw / Dpi, h = sh / Dpi };

        public void Reflow()
        {
            if (reflowing || desktop == null) return;
            reflowing = true;
            try
            {
                desktop.PlacePhysical(new RECT(0, 0, sw, sh));
                foreach (var bar in new[] { topbar, dock })
                {
                    if (bar == dock && dockAuto) continue;
                    if (!registered.Contains(bar) && AppBar.Register(bar.Handle)) registered.Add(bar);
                    var rc = AppBar.SetPos(bar.Handle, bar.Edge, Px(bar == topbar ? TopCss : DockCss));
                    bar.PlacePhysical(rc);
                }
                foreach (var w in new[] { desktop, topbar, dock })
                    w.Send(new { type = "rect", rect = RectDip(w), screen = ScreenDip(), scale = Scale });
            }
            finally { reflowing = false; }
        }

        void OnDisplayChanged()
        {
            Measure();
            foreach (var w in all) w.Web.ZoomFactor = Scale;
            Reflow();
        }

        object InitFor(WebWindow w) => new
        {
            type = "init",
            view = w.View,
            scale = Scale,
            screen = ScreenDip(),
            rect = RectDip(w),
            settings,
            config = ConfigPayload(),
            wallpapers = Wallpapers()
        };

        object ConfigPayload() => new { apps = Cfg.apps, layouts = Cfg.layouts, hotkeys = Cfg.hotkeys };

        List<string> Wallpapers()
        {
            try
            {
                return new DirectoryInfo(Paths.Walls).GetFiles()
                    .Where(f => ImageExts.Contains(f.Extension.ToLowerInvariant()))
                    .OrderBy(f => f.CreationTimeUtc).Select(f => Paths.UrlFor(f.FullName)).ToList();
            }
            catch { return new List<string>(); }
        }

        static void Broadcast(object msg, params WebWindow[] targets)
        {
            foreach (var t in targets) t?.Send(msg);
        }

        // ---------------- messages from the web UI ----------------

        public void OnMessage(WebWindow w, string json)
        {
            Dictionary<string, object> m;
            try { m = Json.Obj(json); } catch { return; }
            try { Handle(w, m); }
            catch (Exception e) { Log.Write("msg " + m.Str("type") + ": " + e); }
        }

        void Handle(WebWindow w, Dictionary<string, object> m)
        {
            switch (m.Str("type"))
            {
                case "ready":
                    w.Ready(InitFor(w));
                    if (iconMap.Count > 0) w.Send(new { type = "icons", map = iconMap });
                    foreach (var o in new[] { lastWindows, lastStats, lastMedia, lastSessions, lastEvents, lastUsage }) if (o != null) w.Send(o);
                    if (w == desktop) liveSig = "";
                    if (w == dock) w.Send(new { type = "dockMode", parked = dockAuto });
                    break;
                case "dockSize":
                    int nw = Px(m.Num("w")), nh = Px(m.Num("h"));
                    if (nw != dockW || nh != dockH)
                    {
                        dockW = nw; dockH = nh;
                        if (dockAuto) PlaceDock(dockShown, false);
                    }
                    break;
                case "settings":
                    settings = m["settings"] as Dictionary<string, object>;
                    settingsJson = Json.Write(settings);
                    foreach (var o in all) if (o != w) o.Send(new { type = "settings", settings });
                    saveTimer.Stop(); saveTimer.Start();
                    wallTimer.Stop(); wallTimer.Start();
                    UpdateTipColors();
                    ApplyDockMode();
                    if ((settings.Str("icsUrl") ?? "") != (lastIcs ?? "") || string.Join("|", HiddenCalendars().OrderBy(x => x)) != lastHidden) FetchCalendar();
                    break;
                case "gClientFile": GoogleClientFile(); break;
                case "gConnect": GoogleConnect(); break;
                case "gDisconnect": Google.Disconnect(); FetchCalendar(true); SendCalSources("Disconnected from Google."); break;
                case "calRefresh": Calendar.Flush(); FetchCalendar(true); break;
                case "calSources": SendCalSources(null); break;
                case "calRange":
                    CalRange(w, m.Str("key"), DateTime.Parse(m.Str("from"), System.Globalization.CultureInfo.InvariantCulture),
                        DateTime.Parse(m.Str("to"), System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "openGoogleCalendar": OpenGoogleCalendar(); break;
                case "openUrl":
                    var url = m.Str("url") ?? "";
                    if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Process.Start(url);
                    break;
                case "dockClick": HidePreview(); DockClick(m.Str("key")); break;
                case "dockNew": if (groups.TryGetValue(m.Str("key") ?? "", out var gn)) LaunchGroup(gn); break;
                case "dockMenu": DockMenu(m.Str("key")); break;
                case "preview":
                    ShowPreview(m.Str("key"), m.Num("x"), m.Num("top"));
                    break;
                case "thumbRects":
                    if (w != preview || !preview.IsVisible) break;
                    previewThumbs.Set(Boxes(m));
                    break;
                case "previewActivate":
                    HidePreview();
                    Native.Activate(new IntPtr(m.Long("hwnd")));
                    break;
                case "previewClose":
                    Native.PostMessage(new IntPtr(m.Long("hwnd")), Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    RefreshPreviewSoon();
                    break;
                case "liveRect":
                    liveBox = m.TryGetValue("rect", out var lr) ? lr as Dictionary<string, object> : null;
                    liveSig = "";
                    UpdateLive();
                    break;
                case "usageRefresh": RefreshUsage(true); break;
                case "openClaudeUsage":
                    try { Process.Start("claude://claude.ai/settings/usage"); } catch (Exception e) { Log.Write("usage page: " + e.Message); }
                    break;
                case "tip":
                    var text = m.Str("text");
                    if (string.IsNullOrEmpty(text) || suppressed || preview.IsVisible) { tip?.Hide(); break; }
                    Native.GetWindowRect(dock.Handle, out var dr);
                    tip?.ShowAt(text, dr.Left / Dpi + m.Num("x") * Scale, dr.Top / Dpi + m.Num("top") * Scale - 4);
                    break;
                case "layout":
                    int li = (int)m.Long("index");
                    if (li >= 0 && li < Cfg.layouts.Count) Layouts.Apply(this, Cfg.layouts[li]);
                    break;
                case "launch": Layouts.Launch(Cfg.App(m.Str("id"))); break;
                case "launchStart": Layouts.LaunchTarget("shell:AppsFolder\\" + m.Str("id")); break;
                case "activate": Native.Activate(new IntPtr(m.Long("hwnd"))); break;
                case "open":
                    var v = m.Str("view");
                    ShowPopup(v == "personalize" ? personalize : v == "ambient" ? ambient : v == "calendar" ? calendar : launcher, false);
                    break;
                case "hide": if (w == launcher || w == personalize || w == ambient || w == calendar) w.Hide(); break;
                case "addFont": AddFont(); break;
                case "keys":
                    switch (m.Str("name"))
                    {
                        case "quick": Native.Keys(0x5B, 0x41); break;
                        case "notify": Native.Keys(0x5B, 0x4E); break;
                        case "start": Native.Keys(0x5B); break;
                    }
                    break;
                case "media": Media.Control(m.Str("action")); break;
                case "cmd": Command(m.Str("name")); break;
                case "addPhoto": AddPhoto(); break;
                case "removePhoto":
                    var file = Paths.LocalFile(m.Str("url"));
                    if (file != null && file.StartsWith(Paths.Walls, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
                    Broadcast(new { type = "wallpapers", list = Wallpapers() }, all.ToArray());
                    break;
                case "desktop": Desktops.Switch((int)m.Long("index")); break;
                case "icon":
                    var key = m.Str("key");
                    if (!iconMap.ContainsKey(key)) iconMap[key] = Icons.DataUrl(m.Str("path"));
                    w.Send(new { type = "icons", map = new Dictionary<string, string> { { key, iconMap[key] } } });
                    break;
                case "startApps":
                    if (startApps == null) startApps = StartApps.Read();
                    w.Send(new { type = "startApps", list = startApps });
                    break;
                case "openClaude": OpenApp("claude"); break;
                case "askClaude": AskClaude(); break;
                case "desktopMenu":
                    Menu(("Personalize", () => ShowPopup(personalize, false)),
                         ("Open launcher", () => ShowPopup(launcher, false)),
                         (Taskbar.Hidden ? "Show Windows taskbar" : "Hide Windows taskbar", ToggleTaskbar),
                         ("Reset widget layout", () => desktop.Send(new { type = "resetLayout" })),
                         ("Edit config", () => Command("editConfig")),
                         ("Reload Cascade", Reload),
                         ("Quit Cascade", Quit));
                    break;
                case "powerMenu":
                    Menu(("Lock", () => Command("lock")), ("Sleep", () => Command("sleep")), ("Sign out", () => Command("signout")),
                         ("Restart", () => Command("restart")), ("Shut down", () => Command("shutdown")));
                    break;
            }
        }

        void Command(string name)
        {
            switch (name)
            {
                case "terminal": Layouts.LaunchTarget("wt.exe"); break;
                case "files": Layouts.LaunchTarget("explorer.exe"); break;
                case "code": OpenApp("code"); break;
                case "settings": Layouts.LaunchTarget("ms-settings:"); break;
                case "taskManager": Layouts.LaunchTarget("taskmgr.exe"); break;
                case "home": HidePreview(); Native.Keys(0x5B, 0x44); break; // Win+D: show the home screen, again to bring windows back
                case "lock": Native.LockWorkStation(); break;
                case "sleep": System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false); break;
                case "signout": Process.Start("shutdown.exe", "/l"); break;
                case "restart": Process.Start("shutdown.exe", "/r /t 0"); break;
                case "shutdown": Process.Start("shutdown.exe", "/s /t 0"); break;
                case "editConfig": Process.Start("notepad.exe", "\"" + Paths.Config + "\""); break;
                case "toggleTaskbar": ToggleTaskbar(); break;
                case "personalize": ShowPopup(personalize, false); break;
                case "launcher": ShowPopup(launcher, false); break;
                case "reload": Reload(); break;
                case "quit": Quit(); break;
            }
        }

        void Menu(params (string label, Action act)[] items)
        {
            var cm = new ContextMenu { Placement = PlacementMode.MousePoint };
            menuOpen = true;
            cm.Closed += (s, e) => menuOpen = false;
            foreach (var (label, act) in items)
            {
                var mi = new MenuItem { Header = label };
                mi.Click += (s, e) => { try { act(); } catch (Exception ex) { Log.Write("menu: " + ex); } };
                cm.Items.Add(mi);
            }
            cm.IsOpen = true;
        }

        void OpenApp(string id)
        {
            var app = Cfg.App(id);
            if (app == null) return;
            var w = WindowTracker.Enumerate().FirstOrDefault(x => OwnerOf(x) == app);
            if (w != null) Native.Activate(w.Hwnd); else Layouts.Launch(app);
        }

        void ShowPopup(WebWindow w, bool toggle)
        {
            if (w == null) return;
            if (w.IsVisible && toggle) { w.Hide(); return; }
            Native.GetWindowRect(topbar.Handle, out var tr);
            Native.GetWindowRect(dock.Handle, out var dr);
            RECT r;
            if (w == calendar)
            {
                r = new RECT(Px(12), tr.Bottom + Px(8), sw - Px(12), sh - Px(12));
            }
            else if (w == ambient)
            {
                if (w != personalize && personalize.IsVisible) personalize.Hide();
                r = new RECT(0, 0, sw, sh);
            }
            else if (w == launcher)
            {
                int pw = Px(640), ph = Px(470);
                int x = (sw - pw) / 2, y = Math.Max(tr.Bottom + Px(40), (int)(sh * 0.16));
                r = new RECT(x, y, x + pw, y + ph);
            }
            else
            {
                int pw = Px(384), x = Px(10), y = tr.Bottom + Px(8);
                int ph = Math.Min(Px(720), dr.Top - Px(8) - y);
                r = new RECT(x, y, x + pw, y + ph);
            }
            if (!w.IsVisible) w.Show();
            w.PlacePhysical(r);
            w.Activate();
            Native.Activate(w.Handle);
            w.Web.Focus();
            w.Send(new { type = "show" });
        }

        void AddPhoto()
        {
            dialogOpen = true;
            string added = null;
            try
            {
                var dlg = new OpenFileDialog { Title = "Add wallpapers", Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp", Multiselect = true };
                if (dlg.ShowDialog(personalize.IsVisible ? personalize : null) == true)
                {
                    foreach (var f in dlg.FileNames)
                    {
                        var name = Path.GetFileNameWithoutExtension(f);
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        var dest = Path.Combine(Paths.Walls, name + ext);
                        for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(Paths.Walls, name + "-" + i + ext);
                        File.Copy(f, dest);
                        File.SetCreationTimeUtc(dest, DateTime.UtcNow);
                        added = Paths.UrlFor(dest);
                    }
                }
            }
            finally { dialogOpen = false; }
            if (personalize.IsVisible) personalize.Activate();
            Broadcast(new { type = "wallpapers", list = Wallpapers(), added }, all.ToArray());
        }

        // Copies a font file (e.g. Harmond) into data\fonts so the web UI can load it as the display font.
        void AddFont()
        {
            dialogOpen = true;
            try
            {
                var dlg = new OpenFileDialog { Title = "Import a display font", Filter = "Fonts|*.otf;*.ttf;*.woff;*.woff2" };
                if (dlg.ShowDialog(personalize.IsVisible ? personalize : null) != true) return;
                var dir = Path.Combine(Paths.Data, "fonts");
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, Path.GetFileName(dlg.FileName));
                File.Copy(dlg.FileName, dest, true);
                var name = Path.GetFileNameWithoutExtension(dest).Replace('-', ' ').Replace('_', ' ');
                Broadcast(new { type = "fontAdded", url = Paths.UrlFor(dest), name }, personalize);
            }
            finally
            {
                dialogOpen = false;
                if (personalize.IsVisible) personalize.Activate();
            }
        }

        void SyncWindowsWallpaper()
        {
            var file = Paths.LocalFile(settings.Str("wall"));
            if (file != null) WallpaperSync.Apply(St, file);
        }

        void UpdateTipColors()
        {
            if (tip == null) return;
            try
            {
                var acc = (Color)ColorConverter.ConvertFromString(settings.Str("acc") ?? "#a9c7a1");
                tip.SetColors(acc, Color.FromRgb(12, 16, 14));
            }
            catch { }
        }

        // ---------------- windows and dock ----------------

        public AppEntry OwnerOf(WinInfo w) =>
            Cfg.apps.FirstOrDefault(a => !string.IsNullOrEmpty(a.title) && a.Matches(w)) ??
            Cfg.apps.FirstOrDefault(a => string.IsNullOrEmpty(a.title) && a.Matches(w));

        string Friendly(WinInfo w)
        {
            var key = w.Exe ?? w.Proc;
            if (friendly.TryGetValue(key, out var n)) return n;
            n = w.Proc;
            try
            {
                if (w.Exe != null)
                {
                    var d = FileVersionInfo.GetVersionInfo(w.Exe).FileDescription;
                    if (!string.IsNullOrWhiteSpace(d) && d.Length < 40) n = d.Trim();
                }
            }
            catch { }
            friendly[key] = n;
            return n;
        }

        string KeyOf(WinInfo w)
        {
            var a = OwnerOf(w);
            return a != null && a.pinned ? "app:" + a.id : "exe:" + (w.Exe ?? w.Proc).ToLowerInvariant();
        }

        void FastTick()
        {
            var windows = WindowTracker.Enumerate();
            var fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && !WindowTracker.IsOwn(fg) && Native.Class(fg) != "Progman" && Native.Class(fg) != "WorkerW") LastForeground = fg;

            var map = new Dictionary<string, DockGroup>();
            var order = new List<DockGroup>();
            foreach (var a in Cfg.apps.Where(a => a.pinned))
            {
                var g = new DockGroup { Key = "app:" + a.id, Name = a.name, App = a, Pinned = true, IconPath = a.icon ?? a.launch };
                map[g.Key] = g;
                order.Add(g);
            }
            foreach (var w in windows)
            {
                var key = KeyOf(w);
                if (!map.TryGetValue(key, out var g))
                {
                    var a = OwnerOf(w);
                    map[key] = g = new DockGroup { Key = key, Name = a?.name ?? Friendly(w), App = a, Exe = w.Exe, IconPath = a?.icon ?? w.Exe };
                }
                if (g.Exe == null) g.Exe = w.Exe;
                g.Wins.Add(w.Hwnd);
            }
            runningOrder.RemoveAll(k => !map.ContainsKey(k) || map[k].Pinned);
            foreach (var k in map.Keys) if (!map[k].Pinned && !runningOrder.Contains(k)) runningOrder.Add(k);
            order.AddRange(runningOrder.Select(k => map[k]));
            foreach (var g in order) g.Wins.Sort((x, y) => x.ToInt64().CompareTo(y.ToInt64()));
            groups = map;

            var fresh = new Dictionary<string, string>();
            foreach (var g in order)
            {
                if (iconMap.TryGetValue(g.Key, out var have) && have != null) continue;
                var attempt = g.Key + "|" + g.Exe;
                if (!iconTried.Add(attempt)) continue;
                var d = Icons.DataUrl(g.IconPath) ?? (g.Exe != null ? Icons.DataUrl(g.Exe) : null);
                iconMap[g.Key] = d;
                if (d != null) fresh[g.Key] = d;
            }
            if (fresh.Count > 0) Broadcast(new { type = "icons", map = fresh }, dock, launcher);

            var fgInfo = windows.FirstOrDefault(x => x.Hwnd == LastForeground);
            var payload = new
            {
                type = "windows",
                groups = order.Select(g => new { key = g.Key, name = g.Name, pinned = g.Pinned, count = g.Wins.Count, active = g.Wins.Contains(LastForeground) }).ToList(),
                fg = fgInfo == null ? null : new { title = fgInfo.Title, app = OwnerOf(fgInfo)?.name ?? Friendly(fgInfo), key = KeyOf(fgInfo) },
                list = windows.Select(x => new { hwnd = x.Hwnd.ToInt64(), title = x.Title, key = KeyOf(x) }).ToList()
            };
            var sig = Json.Write(payload);
            if (sig != winSig)
            {
                winSig = sig;
                lastWindows = payload;
                Broadcast(payload, dock, topbar, launcher);
            }

            bool fs = WindowTracker.IsFullscreen(fg);
            if (fs != suppressed)
            {
                suppressed = fs;
                topbar.Visibility = dock.Visibility = fs ? Visibility.Hidden : Visibility.Visible;
                if (fs) { tip?.Hide(); if (dockAuto) PlaceDock(false, false); }
            }
            if (Cfg.hideWindowsTaskbar) Taskbar.Enforce(St);
            DesktopMode(fg);
            if (!desktop.BottomMost && fg == desktop.Handle) RaiseBars();
            if (!Native.IsWindowVisible(desktop.Handle) || Native.IsIconic(desktop.Handle)) Native.ShowWindow(desktop.Handle, Native.SW_SHOWNOACTIVATE);
            UpdateLive(windows);
            if (suppressed && preview.IsVisible) HidePreview();
        }

        // ---------------- live window previews ----------------

        // css-pixel boxes from a page -> physical pixels inside that window
        IEnumerable<(IntPtr, RECT)> Boxes(Dictionary<string, object> m)
        {
            if (!(m.TryGetValue("rects", out var v) && v is System.Collections.IEnumerable list)) yield break;
            double k = Scale * Dpi;
            foreach (var o in list)
            {
                if (!(o is Dictionary<string, object> r)) continue;
                int x = (int)(r.Num("x") * k), y = (int)(r.Num("y") * k);
                yield return (new IntPtr(r.Long("hwnd")), new RECT(x, y, x + (int)(r.Num("w") * k), y + (int)(r.Num("h") * k)));
            }
        }

        // Hovering a running app in the dock: a strip of live thumbnails of its windows above the icon.
        void ShowPreview(string key, double xCss, double topCss)
        {
            if (key == null || suppressed || !groups.TryGetValue(key, out var g)) { HidePreview(); return; }
            var wins = g.Wins.Where(Native.IsWindow).Take(6).ToList();
            if (wins.Count == 0) { HidePreview(); return; }
            previewKey = key;
            previewX = xCss;
            previewTop = topCss;
            previewHold = 0;
            const double card = 240, gap = 10, pad = 10, height = 190;
            int pw = Px(wins.Count * card + (wins.Count - 1) * gap + pad * 2), ph = Px(height);
            Native.GetWindowRect(dock.Handle, out var dr);
            int cx = dr.Left + (int)(xCss * Scale * Dpi), bottom = dr.Top + (int)(topCss * Scale * Dpi) - Px(10);
            int x = Math.Max(Px(8), Math.Min(sw - pw - Px(8), cx - pw / 2));
            tip?.Hide();
            preview.Send(new
            {
                type = "preview",
                name = g.Name,
                items = wins.Select(h => new { hwnd = h.ToInt64(), title = Native.Title(h), active = h == LastForeground, min = Native.IsIconic(h) }).ToList()
            });
            if (!preview.IsVisible) preview.Show();
            Native.SetWindowPos(preview.Handle, Native.HWND_TOPMOST, x, bottom - ph, pw, ph, Native.SWP_NOACTIVATE);
            previewTimer.Start();
        }

        void HidePreview()
        {
            previewTimer?.Stop();
            previewKey = null;
            previewThumbs?.Clear();
            if (preview != null && preview.IsVisible) preview.Hide();
        }

        // After closing a window from the preview, redraw the strip without it.
        async void RefreshPreviewSoon()
        {
            await Task.Delay(400);
            Guard(FastTick);
            if (previewKey != null) ShowPreview(previewKey, previewX, previewTop);
        }

        // Close once the pointer has left both the dock and the preview for a moment.
        void PreviewTick()
        {
            if (!preview.IsVisible) { previewTimer.Stop(); return; }
            Native.GetCursorPos(out var p);
            Native.GetWindowRect(preview.Handle, out var pr);
            Native.GetWindowRect(dock.Handle, out var dr);
            bool over = (p.X >= pr.Left && p.X <= pr.Right && p.Y >= pr.Top && p.Y <= pr.Bottom + Px(14)) ||
                        (p.X >= dr.Left && p.X <= dr.Right && p.Y >= dr.Top && p.Y <= dr.Bottom);
            if (over) previewHold = 0;
            else if (++previewHold > 5) HidePreview();
        }

        // Desktop tile with a live view of the Claude window.
        void UpdateLive(IEnumerable<WinInfo> windows = null)
        {
            if (liveThumbs == null) return;
            IntPtr src = IntPtr.Zero;
            var claude = Cfg.App("claude");
            if (liveBox != null && claude != null)
            {
                var wins = (windows ?? WindowTracker.Enumerate()).Where(x => OwnerOf(x) == claude).ToList();
                var pick = wins.FirstOrDefault(x => x.Hwnd == LastForeground) ?? wins.FirstOrDefault();
                if (pick != null) src = pick.Hwnd;
            }
            var sig = src + "|" + (liveBox == null ? "" : Json.Write(liveBox));
            if (sig == liveSig) return;
            liveSig = sig;
            if (src == IntPtr.Zero) liveThumbs.Clear();
            else
            {
                var b = new Dictionary<string, object>(liveBox) { ["hwnd"] = src.ToInt64() };
                liveThumbs.Set(Boxes(new Dictionary<string, object> { ["rects"] = new object[] { b } }));
            }
            desktop.Send(new { type = "live", has = src != IntPtr.Zero, title = src == IntPtr.Zero ? null : Native.Title(src) });
        }

        async void RefreshUsage(bool force = false)
        {
            if (usageBusy) return;
            usageBusy = true;
            try
            {
                if (force) ClaudeUsage.Invalidate();
                lastUsage = await Task.Run(() => ClaudeUsage.Read());
                desktop.Send(lastUsage);
            }
            catch (Exception e) { Log.Write("usage: " + e.Message); }
            finally { usageBusy = false; }
        }

        // "Show desktop" (Win+D, desktop click) raises the real desktop; lift the Cascade layer above it
        // while the desktop has focus, and send it back to the bottom once an app takes over.
        void DesktopMode(IntPtr fg)
        {
            if (desktop == null || fg == IntPtr.Zero || WindowTracker.IsOwn(fg)) return;
            var cls = Native.Class(fg);
            bool onDesktop = cls == "WorkerW" || cls == "Progman";
            if (onDesktop == !desktop.BottomMost) return;
            Log.Write("desktop mode " + onDesktop + " (" + cls + ")");
            desktop.BottomMost = !onDesktop;
            const uint f = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
            if (onDesktop)
            {
                // Explorer keeps the shell desktop above normal windows in this mode, so go topmost
                // and put the bar and dock back above us.
                Native.SetWindowPos(desktop.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, f);
                RaiseBars();
                if (dockAuto) PlaceDock(true, true);
            }
            else
            {
                Native.SetWindowPos(desktop.Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, f);
                Native.SetWindowPos(desktop.Handle, Native.AboveDesktop(desktop.Handle), 0, 0, 0, 0, f);
            }
        }

        void RaiseBars()
        {
            const uint f = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
            foreach (var w in new[] { dock, topbar, launcher, personalize, calendar })
                if (w.IsVisible) Native.SetWindowPos(w.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, f);
        }

        void SlowTick()
        {
            slowTicks++;
            lastStats = Stats.Read();
            Broadcast(lastStats, desktop, topbar);
            lastMedia = Media.Read();
            Broadcast(lastMedia, desktop, topbar);
            var (count, current) = Desktops.Read();
            topbar.Send(new { type = "desktops", count, current });
            if (slowTicks % 5 == 1)
            {
                lastSessions = Sessions.Read();
                Broadcast(lastSessions, desktop, launcher);
            }
            if (slowTicks % 15 == 1) RefreshUsage();
            if (slowTicks % 30 == 0) WindowTracker.FlushCache();
            if (slowTicks % 300 == 0) FetchCalendar();
        }

        bool Setting(string key, bool fallback)
        {
            var s = settings.Str(key);
            return s == null ? fallback : s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- parked dock ----------------

        // Auto-hide: the dock gives up its reserved strip, shrinks to the pill and waits below the screen edge.
        void ApplyDockMode()
        {
            bool auto = Setting("dockAutoHide", true);
            if (dockModeSet && auto == dockAuto) return;
            dockModeSet = true;
            dockAuto = auto;
            dock.IsAppBar = !auto;
            if (auto)
            {
                if (registered.Remove(dock)) AppBar.Remove(dock.Handle);
                dock.Send(new { type = "dockMode", parked = true });
                PlaceDock(false, false);
                edgeTimer.Start();
            }
            else
            {
                edgeTimer.Stop();
                Native.SetWindowRgn(dock.Handle, IntPtr.Zero, true);
                dock.Send(new { type = "dockMode", parked = false });
                dockShown = true;
                Reflow();
            }
        }

        void PlaceDock(bool show, bool animate)
        {
            if (!dockAuto || dockW <= 0 || dockH <= 0) return;
            if (show && suppressed) show = false;
            dockShown = show;
            if (!show) { tip?.Hide(); leaveTicks = 0; }
            int x = (sw - dockW) / 2, target = show ? sh - dockH - Px(10) : sh + 2;
            Native.SetWindowRgn(dock.Handle, Native.CreateRoundRectRgn(0, 0, dockW + 1, dockH + 1, Px(40), Px(40)), true);
            Native.GetWindowRect(dock.Handle, out var cur);
            int startY = cur.Top;
            slideTimer?.Stop();
            const uint f = Native.SWP_NOACTIVATE;
            void Done()
            {
                Native.SetWindowPos(dock.Handle, Native.HWND_TOPMOST, x, target, dockW, dockH, f);
                dock.Send(new { type = "rect", rect = RectDip(dock), screen = ScreenDip(), scale = Scale });
            }
            if (!animate || Math.Abs(startY - target) < 4) { Done(); return; }
            int step = 0;
            slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(14) };
            slideTimer.Tick += (s, e) =>
            {
                step++;
                double t = Math.Min(1, step / 9.0), ease = 1 - Math.Pow(1 - t, 3);
                Native.SetWindowPos(dock.Handle, Native.HWND_TOPMOST, x, (int)(startY + (target - startY) * ease), dockW, dockH, f);
                if (t >= 1) { slideTimer.Stop(); Done(); }
            };
            slideTimer.Start();
        }

        void EdgeTick()
        {
            if (!dockAuto || suppressed) return;
            Native.GetCursorPos(out var p);
            if (!dockShown)
            {
                if (p.Y >= sh - 2) { if (++edgeHold >= 2) { edgeHold = 0; PlaceDock(true, true); } }
                else edgeHold = 0;
                return;
            }
            if (!desktop.BottomMost) return; // keep it up while showing the desktop
            Native.GetWindowRect(dock.Handle, out var r);
            bool near = p.X >= r.Left - Px(40) && p.X <= r.Right + Px(40) && p.Y >= r.Top - Px(50);
            if (near || menuOpen || launcher.IsVisible || preview.IsVisible) leaveTicks = 0;
            else if (++leaveTicks > 10) PlaceDock(false, true);
        }

        // ---------------- new windows open maximized ----------------

        async void OnWindowShown(IntPtr h)
        {
            if (seen.Contains(h) || !Setting("maximizeNew", true)) return;
            if (Native.GetAncestor(h, 2) != h) return;
            await Task.Delay(150);
            if (seen.Contains(h)) return;
            var w = WindowTracker.Inspect(h);
            if (w == null) return;
            seen.Add(h);
            if (Layouts.Busy > 0 || Native.IsZoomed(h) || Native.IsIconic(h)) return;
            int style = Native.GetWindowLong(h, Native.GWL_STYLE);
            if ((style & Native.WS_MAXIMIZEBOX) == 0 || (style & Native.WS_THICKFRAME) == 0) return;
            Native.ShowWindow(h, Native.SW_MAXIMIZE);
        }

        // ---------------- calendar ----------------

        List<CalInfo> calSources = new List<CalInfo>();
        string lastHidden = "";

        List<string> IcsUrls() => (settings.Str("icsUrl") ?? "").Split(new[] { '\n', '\r', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase) || u.StartsWith("webcal", StringComparison.OrdinalIgnoreCase)).ToList();

        HashSet<string> HiddenCalendars()
        {
            var h = new HashSet<string>();
            if (settings != null && settings.TryGetValue("calHidden", out var v) && v is System.Collections.IEnumerable e && !(v is string))
                foreach (var o in e) h.Add(Convert.ToString(o));
            return h;
        }

        // Upcoming two weeks for the desktop tile and top bar, plus the calendar list for every view.
        async void FetchCalendar(bool refreshSources = false)
        {
            lastIcs = settings.Str("icsUrl") ?? "";
            var ics = IcsUrls();
            var hidden = HiddenCalendars();
            lastHidden = string.Join("|", hidden.OrderBy(x => x));
            if (Google.Connected || ics.Count > 0)
            {
                var res = await Task.Run(() =>
                {
                    var srcs = CalendarHub.Sources(ics, refreshSources);
                    var r = CalendarHub.Fetch(srcs, ics, DateTime.Today, DateTime.Today.AddDays(15), hidden);
                    return (srcs: CalendarHub.Sources(ics, false), r.list, err: r.error ?? CalendarHub.Error);
                });
                calSources = res.srcs;
                lastEvents = new { type = "events", configured = true, list = res.list.Take(80).ToList(), calendars = calSources, error = res.err };
            }
            else
            {
                calSources = new List<CalInfo>();
                lastEvents = new { type = "events", configured = false, list = new List<CalEvent>(), calendars = calSources, error = (string)null };
            }
            Broadcast(lastEvents, desktop, topbar, personalize, calendar);
            SendCalSources(null);
        }

        void SendCalSources(string message) =>
            Broadcast(new
            {
                type = "calSources",
                google = new { hasClient = Google.HasClient, connected = Google.Connected, email = Google.Store.email },
                calendars = calSources,
                error = CalendarHub.Error,
                message
            }, personalize, calendar);

        async void CalRange(WebWindow w, string key, DateTime from, DateTime to)
        {
            var ics = IcsUrls();
            var hidden = HiddenCalendars();
            var srcs = calSources;
            var r = await Task.Run(() => CalendarHub.Fetch(srcs, ics, from, to, hidden));
            w.Send(new { type = "calData", key, list = r.list, error = r.error });
        }

        // Google Calendar in Chrome (falls back to the default browser if Chrome isn't found).
        void OpenGoogleCalendar()
        {
            const string url = "https://calendar.google.com/calendar/r";
            var chrome = Icons.Resolve(Cfg.App("chrome")?.launch ?? @"C:\Program Files\Google\Chrome\Application\chrome.exe");
            try
            {
                if (chrome != null && File.Exists(chrome)) Process.Start(chrome, url);
                else Process.Start(url);
            }
            catch (Exception e) { Log.Write("google calendar: " + e.Message); }
        }

        // Opens a fresh chat in the Claude app with the message box focused.
        void AskClaude()
        {
            if (launcher.IsVisible) launcher.Hide();
            try { Process.Start("claude://claude.ai/new"); }
            catch (Exception e) { Log.Write("ask claude: " + e.Message); OpenApp("claude"); }
        }

        async void GoogleConnect()
        {
            try
            {
                SendCalSources("Finish signing in in your browser…");
                await Google.Connect();
                FetchCalendar(true);
                SendCalSources("Connected as " + (Google.Store.email ?? "your Google account"));
            }
            catch (Exception e) { Log.Write("google connect: " + e.Message); SendCalSources("Couldn't connect: " + e.Message); }
        }

        void GoogleClientFile()
        {
            dialogOpen = true;
            try
            {
                var dlg = new OpenFileDialog { Title = "Choose your Google OAuth client file", Filter = "Google client file|*.json" };
                if (dlg.ShowDialog(personalize.IsVisible ? personalize : null) != true) return;
                Google.SetClientFile(dlg.FileName);
                SendCalSources("Client file added. Now connect Google.");
            }
            catch (Exception e) { SendCalSources(e.Message); }
            finally { dialogOpen = false; if (personalize.IsVisible) personalize.Activate(); }
        }

        void DockClick(string key)
        {
            if (key == null || !groups.TryGetValue(key, out var g)) return;
            if (g.Wins.Count == 0) { LaunchGroup(g); return; }
            var fg = LastForeground;
            int idx = g.Wins.IndexOf(fg);
            if (idx >= 0 && Native.IsIconic(fg)) { Native.Activate(fg); return; }
            if (idx >= 0 && g.Wins.Count == 1) { Native.ShowWindow(fg, Native.SW_MINIMIZE); return; }
            var target = g.Wins[(idx + 1) % g.Wins.Count];
            Native.Activate(target);
            LastForeground = target;
        }

        void LaunchGroup(DockGroup g)
        {
            if (g.App != null) Layouts.Launch(g.App);
            else if (g.Exe != null) Layouts.LaunchTarget(g.Exe);
        }

        void DockMenu(string key)
        {
            if (key == null || !groups.TryGetValue(key, out var g)) return;
            var items = new List<(string, Action)> { ("New window", () => LaunchGroup(g)) };
            if (g.Wins.Count > 0)
                items.Add((g.Wins.Count > 1 ? "Close " + g.Wins.Count + " windows" : "Close window",
                    () => { foreach (var h in g.Wins) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }));
            if (g.Pinned) items.Add(("Unpin from dock", () => { g.App.pinned = false; ConfigChanged(); }));
            else if (g.App != null) items.Add(("Pin to dock", () => { g.App.pinned = true; ConfigChanged(); }));
            else if (g.Exe != null) items.Add(("Pin to dock", () => Pin(g)));
            Menu(items.ToArray());
        }

        void Pin(DockGroup g)
        {
            var proc = Path.GetFileNameWithoutExtension(g.Exe);
            var id = proc.ToLowerInvariant();
            for (int i = 2; Cfg.App(id) != null; i++) id = proc.ToLowerInvariant() + i;
            Cfg.apps.Add(new AppEntry { id = id, name = g.Name, match = proc, launch = g.Exe, pinned = true });
            iconMap.Remove("app:" + id);
            ConfigChanged();
        }

        void ConfigChanged()
        {
            Cfg.Save();
            winSig = "";
            Broadcast(new { type = "config", config = ConfigPayload() }, all.ToArray());
            FastTick();
        }

        void ToggleTaskbar()
        {
            if (Taskbar.Hidden) Taskbar.Peek(); else Taskbar.Hide(St);
        }

        // ---------------- hotkeys and lifetime ----------------

        void RegisterHotkeys()
        {
            Bind(Cfg.Hotkey("askClaude", "Alt+Space"), AskClaude);
            Bind(Cfg.Hotkey("launcher", "Ctrl+Alt+K"), () => ShowPopup(launcher, true));
            Bind(Cfg.Hotkey("personalize", "Ctrl+Alt+P"), () => ShowPopup(personalize, true));
            Bind(Cfg.Hotkey("calendar", "Ctrl+Alt+C"), OpenGoogleCalendar);
            Bind(Cfg.Hotkey("maximize", "Ctrl+Alt+F"), () =>
            {
                var h = LastForeground;
                if (h == IntPtr.Zero || WindowTracker.IsOwn(h)) return;
                Native.ShowWindow(h, Native.IsZoomed(h) ? Native.SW_RESTORE : Native.SW_MAXIMIZE);
            });
            Bind(Cfg.Hotkey("snapLeft", "Ctrl+Alt+Left"), () => Layouts.Snap(LastForeground, new[] { 0, 0, 0.5, 1 }, Cfg.gap));
            Bind(Cfg.Hotkey("snapRight", "Ctrl+Alt+Right"), () => Layouts.Snap(LastForeground, new[] { 0.5, 0, 0.5, 1 }, Cfg.gap));
            Bind(Cfg.Hotkey("fill", "Ctrl+Alt+Up"), () => Layouts.Snap(LastForeground, new[] { 0, 0, 1.0, 1 }, Cfg.gap));
            Bind(Cfg.Hotkey("center", "Ctrl+Alt+Down"), () => Layouts.Snap(LastForeground, new[] { 0.16, 0.04, 0.68, 0.92 }, Cfg.gap));
            Bind(Cfg.Hotkey("toggleTaskbar", "Ctrl+Alt+T"), ToggleTaskbar);
            Bind(Cfg.Hotkey("reload", "Ctrl+Alt+R"), Reload);
            foreach (var l in Cfg.layouts)
            {
                var layout = l;
                if (!string.IsNullOrWhiteSpace(l.hotkey)) Bind(l.hotkey, () => Layouts.Apply(this, layout));
            }
        }

        void Bind(string combo, Action act)
        {
            if (!ParseHotkey(combo, out var mods, out var vk)) { Log.Write("bad hotkey: " + combo); return; }
            int id = nextHotkey++;
            if (Native.RegisterHotKey(msgWin.Handle, id, mods | 0x4000, vk)) hotkeys[id] = act;
            else Log.Write("hotkey already in use: " + combo);
        }

        static bool ParseHotkey(string s, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            foreach (var raw in s.Split('+'))
            {
                var p = raw.Trim();
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= 2; break;
                    case "alt": mods |= 1; break;
                    case "shift": mods |= 4; break;
                    case "win": mods |= 8; break;
                    default:
                        if (p.Length == 1 && char.IsDigit(p[0])) p = "D" + p;
                        if (!Enum.TryParse(p, true, out Key k)) return false;
                        vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                        break;
                }
            }
            return vk != 0;
        }

        IntPtr MsgProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && hotkeys.TryGetValue(w.ToInt32(), out var act))
            {
                Guard(act);
                handled = true;
            }
            else if (msg == WM_QUIT_CASCADE) { Quit(); handled = true; }
            return IntPtr.Zero;
        }

        public void SessionEnding() => ending = true;

        public void RestoreSystem()
        {
            if (restored) return;
            restored = true;
            try
            {
                foreach (var id in hotkeys.Keys) Native.UnregisterHotKey(msgWin.Handle, id);
                foreach (var bar in registered) AppBar.Remove(bar.Handle);
                registered.Clear();
                if (!keepSystem && !ending) Taskbar.Restore(St);
            }
            catch (Exception e) { Log.Write("restore: " + e); }
        }

        public void Quit()
        {
            RestoreSystem();
            Application.Current.Shutdown();
        }

        public void Reload()
        {
            keepSystem = true;
            RestoreSystem();
            Process.Start(Process.GetCurrentProcess().MainModule.FileName, "--restart");
            Application.Current.Shutdown();
        }
    }
}

