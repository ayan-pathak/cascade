using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Cascade
{
    public class CalInfo { public string id, name, color, source, access; public bool primary; }

    public class GoogleStore { public string clientId, clientSecret, refresh, email; }

    // Read-only Google Calendar access with the user's own OAuth desktop client (loopback + PKCE).
    public static class Google
    {
        const string Scope = "https://www.googleapis.com/auth/calendar.readonly";
        const string Api = "https://www.googleapis.com/calendar/v3/";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly object gate = new object();
        static GoogleStore store;
        static string access;
        static DateTime accessExp;

        static string FilePath => Path.Combine(Paths.Data, "google.json");

        public static GoogleStore Store => store ?? (store = Load());
        public static bool HasClient => !string.IsNullOrEmpty(Store.clientId);
        public static bool Connected => HasClient && !string.IsNullOrEmpty(Store.refresh);

        static GoogleStore Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = Json.Read<GoogleStore>(File.ReadAllText(FilePath)) ?? new GoogleStore();
                    if (!string.IsNullOrEmpty(s.refresh)) s.refresh = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.refresh), null, DataProtectionScope.CurrentUser));
                    return s;
                }
            }
            catch (Exception e) { Log.Write("google load: " + e.Message); }
            return new GoogleStore();
        }

        // The refresh token is encrypted with Windows DPAPI so only this Windows account can read it.
        static void Save()
        {
            var copy = new GoogleStore
            {
                clientId = Store.clientId,
                clientSecret = Store.clientSecret,
                email = Store.email,
                refresh = string.IsNullOrEmpty(Store.refresh) ? null
                    : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Store.refresh), null, DataProtectionScope.CurrentUser))
            };
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(FilePath, Json.Pretty(Json.Write(copy)));
        }

        public static void SetClientFile(string path)
        {
            var d = Json.Obj(File.ReadAllText(path));
            var inner = (d.ContainsKey("installed") ? d["installed"] : d.ContainsKey("web") ? d["web"] : null) as Dictionary<string, object>;
            if (inner == null || string.IsNullOrEmpty(inner.Str("client_id"))) throw new Exception("That file isn't a Google OAuth client file.");
            Store.clientId = inner.Str("client_id");
            Store.clientSecret = inner.Str("client_secret");
            Save();
        }

        public static void Disconnect()
        {
            lock (gate) { Store.refresh = null; Store.email = null; access = null; }
            Save();
        }

        static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        static string E(string s) => Uri.EscapeDataString(s ?? "");

        static Dictionary<string, string> Query(string target)
        {
            var q = new Dictionary<string, string>();
            int i = target.IndexOf('?');
            if (i < 0) return q;
            foreach (var part in target.Substring(i + 1).Split('&'))
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length == 2) q[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
            }
            return q;
        }

        public static async Task Connect()
        {
            if (!HasClient) throw new Exception("Choose your OAuth client file first.");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirect = "http://127.0.0.1:" + port + "/";
            var rng = RandomNumberGenerator.Create();
            var vb = new byte[32]; rng.GetBytes(vb);
            var sb = new byte[16]; rng.GetBytes(sb);
            string verifier = B64Url(vb), state = B64Url(sb);
            string challenge;
            using (var sha = SHA256.Create()) challenge = B64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            Process.Start("https://accounts.google.com/o/oauth2/v2/auth?client_id=" + E(Store.clientId) + "&redirect_uri=" + E(redirect) +
                "&response_type=code&scope=" + E(Scope + " email") + "&code_challenge=" + challenge + "&code_challenge_method=S256" +
                "&access_type=offline&prompt=consent&state=" + state);

            string code = null, error = null;
            var deadline = DateTime.UtcNow.AddMinutes(5);
            try
            {
                while (code == null && error == null)
                {
                    var accept = listener.AcceptTcpClientAsync();
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || await Task.WhenAny(accept, Task.Delay(left)) != accept) throw new Exception("Timed out waiting for Google sign-in.");
                    using (var client = accept.Result)
                    using (var ns = client.GetStream())
                    {
                        var line = await new StreamReader(ns).ReadLineAsync();
                        var parts = (line ?? "").Split(' ');
                        var q = parts.Length > 1 ? Query(parts[1]) : new Dictionary<string, string>();
                        bool ours = q.ContainsKey("code") || q.ContainsKey("error");
                        if (ours)
                        {
                            if (!q.TryGetValue("state", out var st) || st != state) error = "Sign-in response didn't match.";
                            else if (q.TryGetValue("code", out var c)) code = c;
                            else error = q["error"];
                        }
                        var body = !ours ? "" : "<html><body style=\"font-family:Segoe UI,sans-serif;background:#0c100e;color:#e4ebe2;display:grid;place-items:center;height:100vh;margin:0\"><div style=\"text-align:center\"><h2 style=\"font-weight:500\">" +
                            (code != null ? "Cascade can now read your Google calendars." : "Sign-in didn't finish.") + "</h2><p style=\"color:#9aa79a\">You can close this tab.</p></div></body></html>";
                        var head = (ours ? "HTTP/1.1 200 OK" : "HTTP/1.1 404 Not Found") + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n";
                        var bytes = Encoding.UTF8.GetBytes(head + body);
                        await ns.WriteAsync(bytes, 0, bytes.Length);
                    }
                }
            }
            finally { listener.Stop(); }
            if (code == null) throw new Exception(error);

            var tok = await Task.Run(() => Post(new NameValueCollection
            {
                { "code", code }, { "client_id", Store.clientId }, { "client_secret", Store.clientSecret ?? "" },
                { "redirect_uri", redirect }, { "grant_type", "authorization_code" }, { "code_verifier", verifier }
            }));
            lock (gate)
            {
                Store.refresh = tok.Str("refresh_token");
                access = tok.Str("access_token");
                accessExp = DateTime.UtcNow.AddSeconds(tok.Num("expires_in") - 60);
            }
            try { Store.email = (await Task.Run(() => Get(Api + "calendars/primary"))).Str("id"); } catch { }
            Save();
        }

        static Dictionary<string, object> Post(NameValueCollection form)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                try { return Json.Obj(Encoding.UTF8.GetString(wc.UploadValues("https://oauth2.googleapis.com/token", "POST", form))); }
                catch (WebException we) { throw new Exception(ReadError(we)); }
            }
        }

        static string Token()
        {
            lock (gate)
            {
                if (access != null && DateTime.UtcNow < accessExp) return access;
                if (string.IsNullOrEmpty(Store.refresh)) throw new Exception("Not connected to Google.");
                var t = Post(new NameValueCollection
                {
                    { "client_id", Store.clientId }, { "client_secret", Store.clientSecret ?? "" },
                    { "refresh_token", Store.refresh }, { "grant_type", "refresh_token" }
                });
                access = t.Str("access_token");
                accessExp = DateTime.UtcNow.AddSeconds(t.Num("expires_in") - 60);
                return access;
            }
        }

        static Dictionary<string, object> Get(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient { Encoding = Encoding.UTF8 })
            {
                wc.Headers[HttpRequestHeader.Authorization] = "Bearer " + Token();
                try { return Json.Obj(wc.DownloadString(url)); }
                catch (WebException we) { throw new Exception(ReadError(we)); }
            }
        }

        static string ReadError(WebException we)
        {
            try
            {
                using (var r = new StreamReader(we.Response.GetResponseStream()))
                {
                    var text = r.ReadToEnd();
                    try
                    {
                        var d = Json.Obj(text);
                        if (d.TryGetValue("error", out var e) && e is Dictionary<string, object> ed) return ed.Str("message") ?? text;
                        return d.Str("error_description") ?? d.Str("error") ?? text;
                    }
                    catch { return text; }
                }
            }
            catch { return we.Message; }
        }

        static IEnumerable<Dictionary<string, object>> Items(Dictionary<string, object> d) =>
            d.TryGetValue("items", out var v) && v is IEnumerable e ? e.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();

        // Every calendar in the user's list: their own plus shared and subscribed ones.
        public static List<CalInfo> Calendars()
        {
            var list = new List<CalInfo>();
            string page = null;
            do
            {
                var d = Get(Api + "users/me/calendarList?maxResults=250" + (page != null ? "&pageToken=" + E(page) : ""));
                foreach (var it in Items(d))
                    list.Add(new CalInfo
                    {
                        id = "g:" + it.Str("id"),
                        name = it.Str("summaryOverride") ?? it.Str("summary") ?? it.Str("id"),
                        color = it.Str("backgroundColor") ?? "#7986cb",
                        primary = string.Equals(it.Str("primary"), "true", StringComparison.OrdinalIgnoreCase),
                        access = it.Str("accessRole"),
                        source = "google"
                    });
                page = d.Str("nextPageToken");
            } while (page != null);
            return list.OrderByDescending(c => c.primary).ThenBy(c => c.access == "owner" ? 0 : 1).ThenBy(c => c.name).ToList();
        }

        static readonly Dictionary<string, string> EventColors = new Dictionary<string, string>
        {
            { "1", "#7986cb" }, { "2", "#33b679" }, { "3", "#8e24aa" }, { "4", "#e67c73" }, { "5", "#f6bf26" }, { "6", "#f4511e" },
            { "7", "#039be5" }, { "8", "#616161" }, { "9", "#3f51b5" }, { "10", "#0b8043" }, { "11", "#d50000" }
        };

        static DateTime When(Dictionary<string, object> t, out bool allDay)
        {
            allDay = t.ContainsKey("date");
            return allDay ? DateTime.ParseExact(t.Str("date"), "yyyy-MM-dd", Inv) : DateTimeOffset.Parse(t.Str("dateTime"), Inv).LocalDateTime;
        }

        public static List<CalEvent> Events(CalInfo cal, DateTime from, DateTime to)
        {
            var list = new List<CalEvent>();
            string page = null, id = cal.id.Substring(2);
            do
            {
                var d = Get(Api + "calendars/" + E(id) + "/events?singleEvents=true&orderBy=startTime&maxResults=2500" +
                    "&timeMin=" + E(from.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv)) +
                    "&timeMax=" + E(to.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv)) +
                    (page != null ? "&pageToken=" + E(page) : ""));
                foreach (var it in Items(d))
                {
                    if (it.Str("status") == "cancelled") continue;
                    if (!(it.TryGetValue("start", out var so) && so is Dictionary<string, object> s)) continue;
                    if (!(it.TryGetValue("end", out var eo) && eo is Dictionary<string, object> e)) continue;
                    var start = When(s, out bool allDay);
                    var end = When(e, out _);
                    var desc = it.Str("description");
                    list.Add(new CalEvent
                    {
                        id = it.Str("id"), cal = cal.id, title = it.Str("summary") ?? (cal.access == "freeBusyReader" ? "Busy" : "(No title)"),
                        start = start.ToString("s"), end = end.ToString("s"), allDay = allDay,
                        location = it.Str("location"), desc = desc != null && desc.Length > 3000 ? desc.Substring(0, 3000) : desc,
                        link = it.Str("htmlLink"), meet = it.Str("hangoutLink"),
                        color = it.Str("colorId") != null && EventColors.TryGetValue(it.Str("colorId"), out var c) ? c : null
                    });
                }
                page = d.Str("nextPageToken");
            } while (page != null);
            return list;
        }
    }
}
