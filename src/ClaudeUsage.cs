using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Cascade
{
    // Claude plan limits and recent usage for the desktop tile.
    // Limits: Anthropic's usage endpoint, called with Claude Code's sign-in from
    // ~/.claude/.credentials.json. When it expires Cascade renews it the way Claude Code does and
    // writes it back to that file. Tokens are never logged and only go to Anthropic.
    // Usage: token counts from the session transcripts in ~/.claude/projects.
    public static class ClaudeUsage
    {
        static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

        static object limits;
        static string limitsNote;
        static DateTime limitsAt = DateTime.MinValue;

        public static void Invalidate() => limitsAt = DateTime.MinValue;

        static DateTime credsSeen;

        public static object Read()
        {
            // A fresh sign-in rewrites the credentials file; pick it up straight away.
            try
            {
                var t = File.GetLastWriteTimeUtc(Path.Combine(Home, ".credentials.json"));
                if (t != credsSeen) { credsSeen = t; limitsAt = DateTime.MinValue; }
            }
            catch { }
            if ((DateTime.UtcNow - limitsAt).TotalMinutes >= 5) FetchLimits();
            var (h5, wk) = Local();
            return new
            {
                type = "usage",
                limits,
                note = limitsNote,
                local = new { hour5 = h5, week = wk }
            };
        }

        static string CredsPath => Path.Combine(Home, ".credentials.json");
        const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
        const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"; // Claude Code's own OAuth client

        static void FetchLimits()
        {
            limitsAt = DateTime.UtcNow;
            string token = CurrentToken(out var expired);
            if (token == null) { limits = null; limitsNote = "signin"; return; }
            if (expired)
            {
                token = Renew();
                if (token == null) { limitsNote = "expired"; return; } // keep the last good numbers
            }
            try { limits = Query(token); limitsNote = null; }
            catch (WebException e) when ((e.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.Unauthorized)
            {
                // revoked or expired early: renew once and retry
                token = Renew();
                if (token == null) { limitsNote = "expired"; return; }
                try { limits = Query(token); limitsNote = null; }
                catch (Exception e2) { Log.Write("claude usage: " + e2.Message); limitsNote = "expired"; }
            }
            catch (Exception e)
            {
                Log.Write("claude usage: " + e.Message);
                limitsNote = "offline";
            }
        }

        static Dictionary<string, object> OAuth(Dictionary<string, object> creds) =>
            creds.TryGetValue("claudeAiOauth", out var o) ? o as Dictionary<string, object> : null;

        static string CurrentToken(out bool expired)
        {
            expired = false;
            try
            {
                var oauth = OAuth(Json.Obj(File.ReadAllText(CredsPath)));
                var token = oauth?.Str("accessToken");
                if (string.IsNullOrEmpty(token))
                {
                    expired = true;
                    return string.IsNullOrEmpty(oauth?.Str("refreshToken")) ? null : "";
                }
                long exp = oauth.Long("expiresAt");
                expired = DateTimeOffset.FromUnixTimeMilliseconds(exp).UtcDateTime < DateTime.UtcNow.AddMinutes(2);
                return token;
            }
            catch { return null; }
        }

        // Renews the sign-in exactly as Claude Code does and saves the result back to its credentials
        // file, so both keep working. Skipped if Claude Code changed the file while we were renewing.
        static string Renew()
        {
            try
            {
                var before = File.ReadAllText(CredsPath);
                var creds = Json.Obj(before);
                var oauth = OAuth(creds);
                var refresh = oauth?.Str("refreshToken");
                if (string.IsNullOrEmpty(refresh)) return null;
                var scopes = oauth.TryGetValue("scopes", out var sc) && sc is System.Collections.IEnumerable se && !(sc is string)
                    ? string.Join(" ", se.Cast<object>()) : "user:profile user:inference user:sessions:claude_code";

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(TokenUrl);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Accept = "application/json";
                req.UserAgent = "Cascade";
                req.Timeout = 30000;
                var payload = Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refresh,
                    ["client_id"] = ClientId,
                    ["scope"] = scopes
                }));
                using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
                string body;
                using (var res = req.GetResponse())
                using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd();
                var d = Json.Obj(body);
                var access = d.Str("access_token");
                if (string.IsNullOrEmpty(access)) return null;

                // Claude Code may have renewed at the same moment; its result wins.
                if (File.ReadAllText(CredsPath) != before)
                {
                    var now = CurrentToken(out var stale);
                    return string.IsNullOrEmpty(now) || stale ? null : now;
                }
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                oauth["accessToken"] = access;
                var newRefresh = d.Str("refresh_token");
                if (!string.IsNullOrEmpty(newRefresh)) oauth["refreshToken"] = newRefresh;
                oauth["expiresAt"] = nowMs + (long)(d.Num("expires_in") * 1000);
                if (d.ContainsKey("refresh_token_expires_in") && d["refresh_token_expires_in"] != null)
                    oauth["refreshTokenExpiresAt"] = nowMs + (long)(d.Num("refresh_token_expires_in") * 1000);
                var granted = d.Str("scope");
                if (!string.IsNullOrEmpty(granted)) oauth["scopes"] = granted.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                var tmp = CredsPath + ".cascade.tmp";
                File.WriteAllText(tmp, Json.Write(creds), new UTF8Encoding(false));
                File.Replace(tmp, CredsPath, null);
                credsSeen = File.GetLastWriteTimeUtc(CredsPath);
                Log.Write("claude usage: renewed Claude Code sign-in");
                return access;
            }
            catch (Exception e)
            {
                Log.Write("claude usage: renew failed: " + ((e as WebException)?.Response is HttpWebResponse r ? (int)r.StatusCode + " " : "") + e.Message);
                return null;
            }
        }

        static List<object> Query(string token)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(Endpoint);
            req.Headers["Authorization"] = "Bearer " + token;
            req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            req.Accept = "application/json";
            req.UserAgent = "Cascade";
            req.Timeout = 10000;
            string body;
            using (var res = req.GetResponse())
            using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd();
            var d = Json.Obj(body);
            var list = new List<object>();
            foreach (var (key, label) in new[] { ("five_hour", "5-hour"), ("seven_day", "Week"), ("seven_day_opus", "Week · Opus"), ("seven_day_sonnet", "Week · Sonnet") })
            {
                if (!(d.TryGetValue(key, out var v) && v is Dictionary<string, object> l)) continue;
                list.Add(new { key, label, pct = Math.Round(l.Num("utilization"), 1), resets = l.Str("resets_at") });
            }
            return list;
        }

        // ---- local transcripts ----

        class FileState { public long Offset; }
        struct Req { public DateTime At; public long Tokens; }

        static readonly Dictionary<string, FileState> files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Req> reqs = new Dictionary<string, Req>();
        static readonly Regex tsRx = new Regex("\"timestamp\":\"(\\d{4}-[^\"]+)\"");
        static readonly Regex reqRx = new Regex("\"requestId\":\"([^\"]+)\"");
        static readonly Regex tokRx = new Regex("\"(input_tokens|output_tokens|cache_creation_input_tokens)\":(\\d+)");

        static (object, object) Local()
        {
            var now = DateTime.UtcNow;
            try
            {
                var root = Path.Combine(Home, "projects");
                if (Directory.Exists(root))
                    foreach (var dir in Directory.GetDirectories(root))
                        foreach (var path in Directory.GetFiles(dir, "*.jsonl"))
                        {
                            var fi = new FileInfo(path);
                            if (fi.LastWriteTimeUtc < now.AddDays(-7)) continue;
                            Scan(fi);
                        }
            }
            catch (Exception e) { Log.Write("usage scan: " + e.Message); }

            foreach (var k in reqs.Where(r => r.Value.At < now.AddDays(-8)).Select(r => r.Key).ToList()) reqs.Remove(k);
            object Sum(DateTime since)
            {
                var r = reqs.Values.Where(x => x.At >= since).ToList();
                return new { messages = r.Count, tokens = r.Sum(x => x.Tokens) };
            }
            return (Sum(now.AddHours(-5)), Sum(now.AddDays(-7)));
        }

        // Reads only lines appended since the last pass; each API response is counted once.
        static void Scan(FileInfo f)
        {
            if (!files.TryGetValue(f.FullName, out var s)) files[f.FullName] = s = new FileState();
            if (f.Length < s.Offset) s.Offset = 0;
            if (f.Length == s.Offset) return;
            try
            {
                using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(s.Offset, SeekOrigin.Begin);
                    var sr = new StreamReader(fs, Encoding.UTF8);
                    long pos = s.Offset;
                    string line;
                    var sb = new StringBuilder();
                    // Read complete lines only, so a line being written is picked up next time.
                    int c;
                    while ((c = sr.Read()) >= 0)
                    {
                        if (c != '\n') { sb.Append((char)c); continue; }
                        line = sb.ToString();
                        sb.Clear();
                        pos += Encoding.UTF8.GetByteCount(line) + 1;
                        Take(line);
                    }
                    s.Offset = pos;
                }
            }
            catch { }
        }

        static void Take(string line)
        {
            if (line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) < 0) return;
            int u = line.LastIndexOf("\"usage\":{", StringComparison.Ordinal);
            if (u < 0) return;
            var rq = reqRx.Match(line);
            var ts = tsRx.Match(line);
            if (!rq.Success || !ts.Success) return;
            if (!DateTime.TryParse(ts.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return;
            // The usage object can nest small objects, so take each counter's first value after it.
            var usage = line.Substring(u, Math.Min(700, line.Length - u));
            var seen = new HashSet<string>();
            long tokens = 0;
            foreach (Match m in tokRx.Matches(usage))
                if (seen.Add(m.Groups[1].Value)) tokens += long.Parse(m.Groups[2].Value);
            reqs[rq.Groups[1].Value] = new Req { At = at, Tokens = tokens };
        }
    }
}
