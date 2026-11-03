using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Cascade
{
    public static class Paths
    {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static string Data => Path.Combine(Root, "data");
        public static string Walls => Path.Combine(Data, "wallpapers");
        public static string WebData => Path.Combine(Data, "webview");
        public static string Config => Path.Combine(Root, "config.json");
        public static string Settings => Path.Combine(Data, "settings.json");
        public static string State => Path.Combine(Data, "state.json");
        public const string Origin = "https://cascade.local/";

        // Maps a cascade.local URL back to a file on disk, or null for remote URLs.
        public static string LocalFile(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return null;
            var rel = Uri.UnescapeDataString(url.Substring(Origin.Length)).Replace('/', '\\');
            var full = Path.GetFullPath(Path.Combine(Root, rel));
            return full.StartsWith(Root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }

        public static string UrlFor(string file) =>
            Origin + string.Join("/", file.Substring(Root.Length + 1).Split('\\').Select(Uri.EscapeDataString));
    }

    public static class Log
    {
        static readonly object gate = new object();
        public static void Write(string s)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Paths.Data);
                    var path = Path.Combine(Paths.Data, "log.txt");
                    if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    public static class Json
    {
        static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        public static string Write(object o) => js.Serialize(o);
        public static T Read<T>(string s) => js.Deserialize<T>(s);
        public static Dictionary<string, object> Obj(string s) => js.Deserialize<Dictionary<string, object>>(s);

        public static string Str(this Dictionary<string, object> d, string k) =>
            d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        public static long Long(this Dictionary<string, object> d, string k) =>
            long.TryParse(d.Str(k), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0;

        public static double Num(this Dictionary<string, object> d, string k) =>
            double.TryParse(d.Str(k), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0;

        // Indents serializer output so config.json stays hand-editable; number arrays stay on one line.
        public static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int ind = 0;
            bool q = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (q)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                    else if (c == '"') q = false;
                    continue;
                }
                switch (c)
                {
                    case '"': q = true; sb.Append(c); break;
                    case '[' when i + 1 < json.Length && (char.IsDigit(json[i + 1]) || json[i + 1] == '-'):
                        int end = json.IndexOf(']', i);
                        sb.Append(json.Substring(i, end - i + 1).Replace(",", ", "));
                        i = end;
                        break;
                    case '{': case '[':
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c).Append(json[++i]); break; }
                        sb.Append(c).Append('\n').Append(' ', ++ind * 2);
                        break;
                    case '}': case ']': sb.Append('\n').Append(' ', --ind * 2).Append(c); break;
                    case ',': sb.Append(",\n").Append(' ', ind * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    public class AppEntry
    {
        public string id, name, match, title, launch, args, icon;
        public bool pinned = true;

        public bool Matches(WinInfo w)
        {
            bool hasProc = !string.IsNullOrEmpty(match), hasTitle = !string.IsNullOrEmpty(title);
            if (!hasProc && !hasTitle) return false;
            if (hasProc && !match.Split(',', '|').Any(p => string.Equals(p.Trim(), w.Proc, StringComparison.OrdinalIgnoreCase))) return false;
            if (!hasTitle) return true;
            try { return Regex.IsMatch(w.Title ?? "", title, RegexOptions.IgnoreCase); }
            catch { return false; }
        }
    }

    public class SlotEntry { public string app; public double[] rect; }

    public class LayoutEntry { public string name, hotkey; public List<SlotEntry> slots = new List<SlotEntry>(); }

    public class Config
    {
        public bool hideWindowsTaskbar = true;
        public int gap = 10;
        public Dictionary<string, string> hotkeys = new Dictionary<string, string>();
        public List<AppEntry> apps = new List<AppEntry>();
        public List<LayoutEntry> layouts = new List<LayoutEntry>();

        public static Config Load()
        {
            try { return Json.Read<Config>(File.ReadAllText(Paths.Config)) ?? new Config(); }
            catch (Exception e) { Log.Write("config: " + e.Message); return new Config(); }
        }

        public void Save()
        {
            try { File.WriteAllText(Paths.Config, Json.Pretty(Json.Write(this))); }
            catch (Exception e) { Log.Write("config save: " + e.Message); }
        }

        public AppEntry App(string id) => apps.FirstOrDefault(a => string.Equals(a.id, id, StringComparison.OrdinalIgnoreCase));

        public string Hotkey(string name, string fallback) =>
            hotkeys != null && hotkeys.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
    }

    public class State
    {
        public int taskbarState = -1;
        public string originalWallpaper;
        public string syncedWallpaper;

        public static State Load()
        {
            try { if (File.Exists(Paths.State)) return Json.Read<State>(File.ReadAllText(Paths.State)) ?? new State(); }
            catch { }
            return new State();
        }

        public void Save()
        {
            try { Directory.CreateDirectory(Paths.Data); File.WriteAllText(Paths.State, Json.Pretty(Json.Write(this))); }
            catch (Exception e) { Log.Write("state: " + e.Message); }
        }
    }
}
