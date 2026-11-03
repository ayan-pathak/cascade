using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cascade
{
    public class WinInfo
    {
        public IntPtr Hwnd;
        public string Title, Proc, Exe, Class;
        public uint Pid;
    }

    public static class WindowTracker
    {
        static readonly Dictionary<uint, string> exeCache = new Dictionary<uint, string>();
        static readonly uint selfPid = (uint)Process.GetCurrentProcess().Id;
        static readonly HashSet<string> skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
            "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow"
        };

        public static bool IsOwn(IntPtr h) { Native.GetWindowThreadProcessId(h, out var pid); return pid == selfPid; }

        // Alt-Tab style list in z-order (most recently active first).
        public static List<WinInfo> Enumerate()
        {
            var list = new List<WinInfo>();
            Native.EnumWindows((h, l) => { var w = Inspect(h); if (w != null) list.Add(w); return true; }, IntPtr.Zero);
            return list;
        }

        // Every titled top-level window of a process, visible or not (used for media titles).
        public static List<WinInfo> AllOf(params string[] procs)
        {
            var list = new List<WinInfo>();
            Native.EnumWindows((h, l) =>
            {
                Native.GetWindowThreadProcessId(h, out var pid);
                var exe = ExePath(pid);
                var proc = exe == null ? "" : Path.GetFileNameWithoutExtension(exe);
                if (procs.Any(p => string.Equals(p, proc, StringComparison.OrdinalIgnoreCase)))
                {
                    var t = Native.Title(h);
                    if (!string.IsNullOrWhiteSpace(t)) list.Add(new WinInfo { Hwnd = h, Title = t, Proc = proc, Exe = exe, Pid = pid });
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static WinInfo Inspect(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return null;
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            bool appWin = (ex & Native.WS_EX_APPWINDOW) != 0;
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWin) return null;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero && !appWin) return null;
            if (Native.DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return null;
            var title = Native.Title(h);
            if (string.IsNullOrWhiteSpace(title)) return null;
            var cls = Native.Class(h);
            if (skip.Contains(cls)) return null;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == selfPid) return null;
            var exe = ExePath(pid);
            if (exe != null && exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                uint inner = 0;
                Native.EnumChildWindows(h, (c, l) =>
                {
                    Native.GetWindowThreadProcessId(c, out var p);
                    if (p != pid) { inner = p; return false; }
                    return true;
                }, IntPtr.Zero);
                if (inner != 0) { pid = inner; exe = ExePath(pid); }
            }
            return new WinInfo { Hwnd = h, Title = title, Pid = pid, Exe = exe, Class = cls, Proc = exe == null ? "" : Path.GetFileNameWithoutExtension(exe) };
        }

        public static string ExePath(uint pid)
        {
            if (exeCache.TryGetValue(pid, out var s)) return s;
            var hp = Native.OpenProcess(0x1000, false, pid);
            if (hp != IntPtr.Zero)
            {
                var sb = new StringBuilder(1024);
                int n = sb.Capacity;
                if (Native.QueryFullProcessImageName(hp, 0, sb, ref n)) s = sb.ToString();
                Native.CloseHandle(hp);
            }
            exeCache[pid] = s;
            return s;
        }

        public static void FlushCache() => exeCache.Clear();

        public static bool IsFullscreen(IntPtr h)
        {
            if (h == IntPtr.Zero || IsOwn(h)) return false;
            var cls = Native.Class(h);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false;
            if (!Native.GetWindowRect(h, out var r)) return false;
            var m = Native.Monitor(h).rcMonitor;
            return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        }
    }

    public static class Icons
    {
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = Environment.ExpandEnvironmentVariables(path);
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(path)) return path;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), path);
                    if (File.Exists(p)) return p;
                    if (File.Exists(p + ".exe")) return p + ".exe";
                }
                catch { }
            }
            return path;
        }

        // Large shell icon for any parsable path (exe, AppsFolder id, folder) as a PNG data URL.
        public static string DataUrl(string path, int size = 96)
        {
            path = Resolve(path);
            if (path == null) return null;
            if (cache.TryGetValue(path, out var hit)) return hit;
            string result = null;
            try
            {
                var bs = Load(path, size);
                if (bs != null)
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bs));
                    using (var ms = new MemoryStream()) { enc.Save(ms); result = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray()); }
                }
            }
            catch (Exception e) { Log.Write("icon " + path + ": " + e.Message); }
            cache[path] = result;
            return result;
        }

        static BitmapSource Load(string path, int size)
        {
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Native.SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var f);
                if (f.GetImage(new SIZE { cx = size, cy = size }, 0x4, out hbm) != 0 || hbm == IntPtr.Zero) return null;
                if (Native.GetObject(hbm, Marshal.SizeOf(typeof(DIBSECTION)), out var ds) != 0 && ds.dsBm.bmBits != IntPtr.Zero && ds.dsBm.bmBitsPixel == 32)
                {
                    int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
                    var buf = new byte[stride * h];
                    Marshal.Copy(ds.dsBm.bmBits, buf, 0, buf.Length);
                    if (ds.dsBmih.biHeight > 0)
                    {
                        var row = new byte[stride];
                        for (int y = 0; y < h / 2; y++)
                        {
                            Buffer.BlockCopy(buf, y * stride, row, 0, stride);
                            Buffer.BlockCopy(buf, (h - 1 - y) * stride, buf, y * stride, stride);
                            Buffer.BlockCopy(row, 0, buf, (h - 1 - y) * stride, stride);
                        }
                    }
                    bool anyAlpha = false;
                    for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { anyAlpha = true; break; }
                    if (!anyAlpha) for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
                    var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
                    bs.Freeze();
                    return bs;
                }
                var b = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                b.Freeze();
                return b;
            }
            catch { return null; }
            finally { if (hbm != IntPtr.Zero) Native.DeleteObject(hbm); }
        }
    }

    public static class Layouts
    {
        public static double Dpi = 1;

        public static void Launch(AppEntry a, bool newWindow = false)
        {
            if (a == null) return;
            LaunchTarget(a.launch, a.args);
        }

        public static void LaunchTarget(string target, string args = null)
        {
            try
            {
                target = Environment.ExpandEnvironmentVariables(target ?? "");
                args = Environment.ExpandEnvironmentVariables(args ?? "");
                if (target.Length == 0) return;
                if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) Process.Start("explorer.exe", "\"" + target + "\"");
                else Process.Start(new ProcessStartInfo(target, args) { UseShellExecute = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
            }
            catch (Exception e) { Log.Write("launch " + target + ": " + e.Message); }
        }

        public static void Place(IntPtr h, double[] r, int gapDip)
        {
            if (h == IntPtr.Zero || r == null || r.Length < 4) return;
            if (Native.IsIconic(h) || Native.IsZoomed(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            var wa = Native.Monitor(h).rcWork;
            double W = wa.Width, H = wa.Height;
            int g = (int)Math.Round(gapDip * Dpi), hg = g / 2;
            int x0 = wa.Left + (int)Math.Round(r[0] * W), y0 = wa.Top + (int)Math.Round(r[1] * H);
            int x1 = wa.Left + (int)Math.Round((r[0] + r[2]) * W), y1 = wa.Top + (int)Math.Round((r[1] + r[3]) * H);
            x0 += r[0] <= 0.001 ? g : hg;
            y0 += r[1] <= 0.001 ? g : hg;
            x1 -= r[0] + r[2] >= 0.999 ? g : hg;
            y1 -= r[1] + r[3] >= 0.999 ? g : hg;
            Native.GetWindowRect(h, out var wr);
            int l = 0, t = 0, rr = 0, b = 0;
            if (Native.DwmGetWindowAttribute(h, 9, out RECT fr, Marshal.SizeOf(typeof(RECT))) == 0)
            {
                l = fr.Left - wr.Left; t = fr.Top - wr.Top; rr = wr.Right - fr.Right; b = wr.Bottom - fr.Bottom;
            }
            Native.SetWindowPos(h, IntPtr.Zero, x0 - l, y0 - t, x1 - x0 + l + rr, y1 - y0 + t + b, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        public static int Busy;

        public static async void Apply(Host host, LayoutEntry layout)
        {
            if (layout?.slots == null) return;
            Busy++;
            try { await ApplyInner(host, layout); } finally { Busy--; }
        }

        static async Task ApplyInner(Host host, LayoutEntry layout)
        {
            var used = new HashSet<IntPtr>();
            var active = host.LastForeground;
            var tasks = layout.slots.Select(s => RunSlot(host, s, active, used)).ToList();
            var handles = await Task.WhenAll(tasks);
            for (int i = handles.Length - 1; i >= 0; i--) if (handles[i] != IntPtr.Zero) Native.Activate(handles[i]);
        }

        static async Task<IntPtr> RunSlot(Host host, SlotEntry slot, IntPtr active, HashSet<IntPtr> used)
        {
            IntPtr h = IntPtr.Zero;
            if (slot.app == "@active") h = active;
            var app = host.Cfg.App(slot.app);
            if (h == IntPtr.Zero && app != null)
            {
                var wins = WindowTracker.Enumerate();
                var w = wins.FirstOrDefault(x => host.OwnerOf(x) == app && !used.Contains(x.Hwnd));
                if (w != null) h = w.Hwnd;
                used.Add(h);
                if (h == IntPtr.Zero)
                {
                    var before = new HashSet<IntPtr>(wins.Select(x => x.Hwnd));
                    Launch(app);
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 20000 && h == IntPtr.Zero)
                    {
                        await Task.Delay(300);
                        var n = WindowTracker.Enumerate().FirstOrDefault(x => host.OwnerOf(x) == app && !before.Contains(x.Hwnd) && !used.Contains(x.Hwnd));
                        if (n != null) { h = n.Hwnd; used.Add(h); }
                    }
                    if (h != IntPtr.Zero)
                    {
                        Place(h, slot.rect, host.Cfg.gap);
                        await Task.Delay(900);
                    }
                }
            }
            Place(h, slot.rect, host.Cfg.gap);
            return h;
        }

        public static void Snap(IntPtr h, double[] rect, int gap)
        {
            if (h == IntPtr.Zero || WindowTracker.IsOwn(h)) return;
            Place(h, rect, gap);
        }
    }

    public static class AppBar
    {
        public static readonly uint Message = Native.RegisterWindowMessage("CascadeAppBar");

        static APPBARDATA New(IntPtr h) => new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = h };

        public static bool Register(IntPtr h)
        {
            var d = New(h);
            d.uCallbackMessage = Message;
            return Native.SHAppBarMessage(Native.ABM_NEW, ref d) != 0;
        }

        public static RECT SetPos(IntPtr h, uint edge, int thickness)
        {
            int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
            var d = New(h);
            d.uEdge = edge;
            d.rc = edge == Native.ABE_TOP ? new RECT(0, 0, sw, thickness) : new RECT(0, sh - thickness, sw, sh);
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref d);
            if (edge == Native.ABE_TOP) d.rc.Bottom = d.rc.Top + thickness; else d.rc.Top = d.rc.Bottom - thickness;
            Native.SHAppBarMessage(Native.ABM_SETPOS, ref d);
            return d.rc;
        }

        public static void Remove(IntPtr h)
        {
            var d = New(h);
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref d);
        }
    }

    public static class Taskbar
    {
        public static bool Hidden;

        static List<IntPtr> Trays()
        {
            var l = new List<IntPtr>();
            var p = Native.FindWindow("Shell_TrayWnd", null);
            if (p != IntPtr.Zero) l.Add(p);
            IntPtr s = IntPtr.Zero;
            while ((s = Native.FindWindowEx(IntPtr.Zero, s, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) l.Add(s);
            return l;
        }

        static APPBARDATA Abd() => new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = Native.FindWindow("Shell_TrayWnd", null) };

        public static void Hide(State st)
        {
            var d = Abd();
            if (d.hWnd == IntPtr.Zero) return;
            if (st.taskbarState < 0) { st.taskbarState = (int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref d); st.Save(); }
            d.lParam = (IntPtr)Native.ABS_AUTOHIDE;
            Native.SHAppBarMessage(Native.ABM_SETSTATE, ref d);
            foreach (var t in Trays()) Native.ShowWindow(t, Native.SW_HIDE);
            Hidden = true;
        }

        // Shows the real taskbar but leaves auto-hide on, so it slides up from the bottom edge.
        public static void Peek()
        {
            foreach (var t in Trays()) Native.ShowWindow(t, Native.SW_SHOW);
            Hidden = false;
        }

        public static void Restore(State st)
        {
            foreach (var t in Trays()) Native.ShowWindow(t, Native.SW_SHOW);
            if (st.taskbarState >= 0)
            {
                var d = Abd();
                d.lParam = (IntPtr)st.taskbarState;
                Native.SHAppBarMessage(Native.ABM_SETSTATE, ref d);
                st.taskbarState = -1;
                st.Save();
            }
            Hidden = false;
        }

        public static void Enforce(State st)
        {
            if (!Hidden) return;
            var fg = Native.GetForegroundWindow();
            if (Native.Class(fg) == "Windows.UI.Core.CoreWindow") return;
            foreach (var t in Trays()) if (Native.IsWindowVisible(t)) Native.ShowWindow(t, Native.SW_HIDE);
        }
    }

    public static class WallpaperSync
    {
        const string Key = @"HKEY_CURRENT_USER\Control Panel\Desktop";

        public static void Apply(State st, string file)
        {
            try
            {
                var current = Microsoft.Win32.Registry.GetValue(Key, "Wallpaper", "") as string ?? "";
                if (st.originalWallpaper == null && !current.StartsWith(Paths.Walls, StringComparison.OrdinalIgnoreCase))
                {
                    st.originalWallpaper = current;
                    st.Save();
                }
                if (string.Equals(current, file, StringComparison.OrdinalIgnoreCase)) return;
                Microsoft.Win32.Registry.SetValue(Key, "WallpaperStyle", "10");
                Microsoft.Win32.Registry.SetValue(Key, "TileWallpaper", "0");
                Native.SystemParametersInfo(0x14, 0, file, 3);
                st.syncedWallpaper = file;
                st.Save();
            }
            catch (Exception e) { Log.Write("wallpaper: " + e.Message); }
        }

        public static void RestoreOriginal(State st)
        {
            if (st.originalWallpaper == null) return;
            Native.SystemParametersInfo(0x14, 0, st.originalWallpaper, 3);
            st.originalWallpaper = null;
            st.Save();
        }
    }
}
