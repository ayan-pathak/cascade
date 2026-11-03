using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Cascade
{
    public static class Stats
    {
        static long lastIdle, lastTotal;

        public static object Read()
        {
            int cpu = 0;
            if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                long total = kernel + user, dt = total - lastTotal, di = idle - lastIdle;
                if (lastTotal > 0 && dt > 0) cpu = (int)Math.Round(100.0 * (1 - (double)di / dt));
                lastIdle = idle; lastTotal = total;
            }
            var mem = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            Native.GlobalMemoryStatusEx(ref mem);
            double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;

            int disk = 0;
            try { var d = new DriveInfo("C"); disk = (int)Math.Round(100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize); } catch { }

            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            bool hasBattery = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1;
            int battery = hasBattery ? (int)Math.Round(ps.BatteryLifePercent * 100) : -1;
            bool charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

            string net = "none";
            try
            {
                var up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToList();
                if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)) net = "wifi";
                else if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)) net = "ethernet";
                if (!NetworkInterface.GetIsNetworkAvailable()) net = "none";
            }
            catch { }

            var up_ = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            return new
            {
                type = "stats",
                cpu = Math.Max(0, Math.Min(100, cpu)),
                mem = (int)mem.dwMemoryLoad,
                memGb = Math.Round(usedGb, 1),
                disk,
                battery,
                charging,
                net,
                uptime = (up_.Days > 0 ? up_.Days + "d " : "") + up_.Hours.ToString("00") + "h " + up_.Minutes.ToString("00") + "m"
            };
        }
    }

    public static class Media
    {
        static string lastTitle, lastArtist, lastSource;

        // Reads the now-playing track from Spotify or YouTube window titles.
        public static object Read()
        {
            bool playing = false;
            foreach (var w in WindowTracker.AllOf("Spotify"))
            {
                if (w.Title.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase)) { if (lastSource == "Spotify") playing = false; continue; }
                var i = w.Title.IndexOf(" - ", StringComparison.Ordinal);
                if (i <= 0) continue;
                lastArtist = w.Title.Substring(0, i); lastTitle = w.Title.Substring(i + 3); lastSource = "Spotify"; playing = true;
                break;
            }
            if (!playing)
            {
                foreach (var w in WindowTracker.AllOf("chrome", "msedge", "firefox"))
                {
                    var m = Regex.Match(w.Title, @"^(?:\(\d+\)\s*)?(.+?) - YouTube( Music)?\b");
                    if (!m.Success) continue;
                    lastTitle = m.Groups[1].Value; lastArtist = m.Groups[2].Success ? "YouTube Music" : "YouTube"; lastSource = w.Proc; playing = true;
                    break;
                }
            }
            return new { type = "media", title = lastTitle, artist = lastArtist, source = lastSource, playing };
        }

        public static void Control(string action)
        {
            byte vk = action == "prev" ? (byte)0xB1 : action == "next" ? (byte)0xB0 : (byte)0xB3;
            Native.Keys(vk);
        }
    }

    public static class Sessions
    {
        class Entry { public long Length; public string Title, Cwd; public DateTime Last; }
        static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex titleRx = new Regex("\"customTitle\":\"((?:[^\"\\\\]|\\\\.)*)\"");
        static readonly Regex cwdRx = new Regex("\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"");
        static readonly Regex timeRx = new Regex("\"timestamp\":\"(\\d{4}-\\d\\d-\\d\\dT[^\"]+)\"");

        static string Unescape(string s) { try { return Regex.Unescape(s); } catch { return s; } }

        // Recent Claude Code sessions from ~/.claude/projects, newest first.
        public static object Read(int count = 5)
        {
            var list = new List<object>();
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
                if (!Directory.Exists(root)) return new { type = "sessions", list };
                var files = Directory.GetDirectories(root)
                    .SelectMany(d => { try { return Directory.GetFiles(d, "*.jsonl"); } catch { return new string[0]; } })
                    .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Take(count * 4)
                    // The app appends untimestamped bookkeeping lines to many files at once, so file
                    // times bunch together; order by the last real message instead.
                    .Select(f => (f, e: Scan(f)))
                    .OrderByDescending(x => x.e.Last == DateTime.MinValue ? x.f.LastWriteTimeUtc : x.e.Last).Take(count);
                foreach (var (f, e) in files)
                {
                    var ago = DateTime.UtcNow - (e.Last == DateTime.MinValue ? f.LastWriteTimeUtc : e.Last);
                    var folder = string.IsNullOrEmpty(e.Cwd) ? "" : Path.GetFileName(e.Cwd.TrimEnd('\\'));
                    if (folder.StartsWith("scratch-", StringComparison.OrdinalIgnoreCase)) folder = "No folder";
                    list.Add(new
                    {
                        title = string.IsNullOrEmpty(e.Title) ? (folder.Length > 0 ? folder : "Untitled session") : e.Title,
                        folder,
                        working = ago.TotalSeconds < 90,
                        ago = ago.TotalMinutes < 1 ? "now" : ago.TotalHours < 1 ? (int)ago.TotalMinutes + "m" : ago.TotalDays < 1 ? (int)ago.TotalHours + "h" : (int)ago.TotalDays + "d"
                    });
                }
            }
            catch (Exception ex) { Log.Write("sessions: " + ex.Message); }
            return new { type = "sessions", list };
        }

        // Reads only the bytes appended since the last scan.
        static Entry Scan(FileInfo f)
        {
            if (!cache.TryGetValue(f.FullName, out var e)) cache[f.FullName] = e = new Entry();
            if (f.Length == e.Length) return e;
            try
            {
                using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = e.Length > f.Length ? 0 : e.Length;
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[1 << 20];
                    var carry = "";
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        var text = carry + Encoding.UTF8.GetString(buf, 0, n);
                        foreach (Match m in titleRx.Matches(text)) e.Title = Unescape(m.Groups[1].Value);
                        if (e.Cwd == null) { var c = cwdRx.Match(text); if (c.Success) e.Cwd = Unescape(c.Groups[1].Value); }
                        var ts = timeRx.Matches(text);
                        if (ts.Count > 0 && DateTime.TryParse(ts[ts.Count - 1].Groups[1].Value, null,
                                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) && t > e.Last)
                            e.Last = t;
                        carry = text.Length > 400 ? text.Substring(text.Length - 400) : text;
                    }
                }
                e.Length = f.Length;
            }
            catch { }
            return e;
        }
    }

    public static class Desktops
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

        public static (int count, int current) Read()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    var ids = k?.GetValue("VirtualDesktopIDs") as byte[];
                    var cur = k?.GetValue("CurrentVirtualDesktop") as byte[];
                    if (ids == null || ids.Length < 16) return (1, 0);
                    int count = ids.Length / 16, current = 0;
                    if (cur != null && cur.Length == 16)
                        for (int i = 0; i < count; i++)
                            if (ids.Skip(i * 16).Take(16).SequenceEqual(cur)) { current = i; break; }
                    return (count, current);
                }
            }
            catch { return (1, 0); }
        }

        public static async void Switch(int target)
        {
            var (count, current) = Read();
            if (target < 0 || target >= count) return;
            int diff = target - current;
            byte arrow = diff > 0 ? (byte)0x27 : (byte)0x25;
            for (int i = 0; i < Math.Abs(diff); i++)
            {
                Native.Keys(0x11, 0x5B, arrow);
                await System.Threading.Tasks.Task.Delay(120);
            }
        }
    }

    public static class StartApps
    {
        // Every Start-menu app (classic and Store) as name + AppsFolder id.
        public static List<object> Read()
        {
            var list = new List<object>();
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name, path = item.Path;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path) || !seen.Add(name)) continue;
                    if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || name.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    list.Add(new { name, id = path });
                }
            }
            catch (Exception e) { Log.Write("startapps: " + e.Message); }
            return list;
        }
    }
}
