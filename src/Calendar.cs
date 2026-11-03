using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Cascade
{
    public class CalEvent
    {
        public string id, cal, title, start, end, location, desc, link, meet, color;
        public bool allDay;
    }

    // Reads private iCal feeds (any calendar app) and expands events for a date range.
    public static class Calendar
    {
        class Ev
        {
            public string Uid, Summary, Rrule, Location, Desc;
            public DateTime Start, End;
            public bool AllDay, Cancelled;
            public DateTime? RecurrenceId;
            public readonly List<DateTime> Ex = new List<DateTime>();
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Dictionary<string, (string text, DateTime at)> cache = new Dictionary<string, (string, DateTime)>();
        public static readonly Dictionary<int, string> Names = new Dictionary<int, string>();

        static string Fetch(string url)
        {
            lock (cache) if (cache.TryGetValue(url, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(10)) return c.text;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string text;
            using (var wc = new WebClient { Encoding = Encoding.UTF8 }) text = wc.DownloadString(url.Replace("webcal://", "https://"));
            lock (cache) cache[url] = (text, DateTime.UtcNow);
            return text;
        }

        public static void Flush() { lock (cache) cache.Clear(); }

        public static (List<CalEvent> list, string error) Load(List<string> urls, DateTime from, DateTime to, Func<string, bool> visible = null)
        {
            var all = new List<CalEvent>();
            string error = null;
            for (int ci = 0; ci < urls.Count; ci++)
            {
                var calId = "ics:" + ci;
                if (visible != null && !visible(calId)) continue;
                try
                {
                    var text = Fetch(urls[ci]);
                    var evs = Parse(text, ci);
                    foreach (var o in evs.Where(e => e.RecurrenceId != null))
                        evs.FirstOrDefault(e => e.Uid == o.Uid && e.RecurrenceId == null)?.Ex.Add(o.RecurrenceId.Value);
                    foreach (var e in evs.Where(e => !e.Cancelled))
                        foreach (var (s, en) in Occurrences(e, from, to))
                            all.Add(new CalEvent
                            {
                                id = e.Uid + s.Ticks, cal = calId, title = string.IsNullOrWhiteSpace(e.Summary) ? "Busy" : e.Summary,
                                start = s.ToString("s"), end = en.ToString("s"), allDay = e.AllDay, location = e.Location, desc = e.Desc
                            });
                }
                catch (Exception ex) { error = ex.Message; Log.Write("ical " + ci + ": " + ex.Message); }
            }
            return (all, error);
        }

        static string Unescape(string v) => v.Replace("\\,", ",").Replace("\\;", ";").Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\\\", "\\");

        static List<Ev> Parse(string text, int ci)
        {
            var lines = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if ((raw.StartsWith(" ") || raw.StartsWith("\t")) && lines.Count > 0) lines[lines.Count - 1] += raw.Substring(1);
                else lines.Add(raw);
            }
            var list = new List<Ev>();
            Ev cur = null;
            foreach (var l in lines)
            {
                if (cur == null && l.StartsWith("X-WR-CALNAME:")) { lock (Names) Names[ci] = Unescape(l.Substring(13)); continue; }
                if (l == "BEGIN:VEVENT") { cur = new Ev(); continue; }
                if (l == "END:VEVENT")
                {
                    if (cur != null && cur.Start != default(DateTime))
                    {
                        if (cur.End == default(DateTime)) cur.End = cur.AllDay ? cur.Start.AddDays(1) : cur.Start.AddHours(1);
                        list.Add(cur);
                    }
                    cur = null;
                    continue;
                }
                if (cur == null) continue;
                int c = l.IndexOf(':');
                if (c < 0) continue;
                string head = l.Substring(0, c), val = l.Substring(c + 1), name = head.Split(';')[0].ToUpperInvariant();
                try
                {
                    switch (name)
                    {
                        case "UID": cur.Uid = val; break;
                        case "SUMMARY": cur.Summary = Unescape(val).Replace('\n', ' '); break;
                        case "LOCATION": cur.Location = Unescape(val); break;
                        case "DESCRIPTION": cur.Desc = Unescape(val); if (cur.Desc.Length > 3000) cur.Desc = cur.Desc.Substring(0, 3000); break;
                        case "DTSTART": cur.Start = ParseDt(val, out cur.AllDay); break;
                        case "DTEND": cur.End = ParseDt(val, out _); break;
                        case "RRULE": cur.Rrule = val; break;
                        case "EXDATE": foreach (var part in val.Split(',')) cur.Ex.Add(ParseDt(part, out _)); break;
                        case "RECURRENCE-ID": cur.RecurrenceId = ParseDt(val, out _); break;
                        case "STATUS": cur.Cancelled = val.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
                catch { }
            }
            return list;
        }

        // UTC times become local; times with a TZID are treated as local wall-clock time.
        static DateTime ParseDt(string v, out bool allDay)
        {
            v = v.Trim();
            allDay = v.Length == 8;
            if (allDay) return DateTime.ParseExact(v, "yyyyMMdd", Inv);
            bool utc = v.EndsWith("Z");
            var d = DateTime.ParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", Inv);
            return utc ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime() : d;
        }

        static DayOfWeek Dow(string s)
        {
            switch (s) { case "MO": return DayOfWeek.Monday; case "TU": return DayOfWeek.Tuesday; case "WE": return DayOfWeek.Wednesday; case "TH": return DayOfWeek.Thursday; case "FR": return DayOfWeek.Friday; case "SA": return DayOfWeek.Saturday; default: return DayOfWeek.Sunday; }
        }

        static IEnumerable<(DateTime, DateTime)> Occurrences(Ev e, DateTime from, DateTime to)
        {
            var dur = e.End - e.Start;
            if (string.IsNullOrEmpty(e.Rrule))
            {
                if (e.End > from && e.Start < to) yield return (e.Start, e.End);
                yield break;
            }
            var r = e.Rrule.Split(';').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1]);
            string freq = r.TryGetValue("FREQ", out var f) ? f : "DAILY";
            int interval = r.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out var ivn) ? Math.Max(1, ivn) : 1;
            int count = r.TryGetValue("COUNT", out var cv) && int.TryParse(cv, out var cn) ? cn : int.MaxValue;
            DateTime until = r.TryGetValue("UNTIL", out var uv) ? ParseDt(uv, out _) : DateTime.MaxValue;
            if (uv != null && uv.Length == 8) until = until.AddDays(1).AddTicks(-1);
            var byDay = freq == "WEEKLY" && r.TryGetValue("BYDAY", out var bd)
                ? bd.Split(',').Select(x => Dow(x.Length > 2 ? x.Substring(x.Length - 2) : x)).ToList() : null;

            int n = 0;
            var cur = e.Start;
            for (int i = 0; i < 20000; i++)
            {
                IEnumerable<DateTime> batch;
                if (byDay != null)
                {
                    var weekStart = cur.Date.AddDays(-(((int)cur.DayOfWeek + 6) % 7));
                    batch = byDay.Select(d => weekStart.AddDays(((int)d + 6) % 7) + e.Start.TimeOfDay).Where(x => x >= e.Start).OrderBy(x => x).ToList();
                }
                else batch = new[] { cur };
                foreach (var s in batch)
                {
                    if (s > until || n >= count || s >= to) yield break;
                    n++;
                    if (!e.Ex.Contains(s) && s + dur > from) yield return (s, s + dur);
                }
                switch (freq)
                {
                    case "DAILY": cur = cur.AddDays(interval); break;
                    case "WEEKLY": cur = cur.AddDays(7 * interval); break;
                    case "MONTHLY": cur = e.Start.AddMonths(interval * (i + 1)); break;
                    case "YEARLY": cur = e.Start.AddYears(interval * (i + 1)); break;
                    default: yield break;
                }
            }
        }
    }

    // Merges Google calendars and iCal feeds into one list of calendars and events.
    public static class CalendarHub
    {
        static readonly string[] IcsColors = { "#a8c7af", "#d8b07a", "#a8b9c7", "#c7a8c0", "#c7c1a8" };
        static List<CalInfo> googleCals;
        static DateTime googleAt;
        public static string Error;

        public static List<CalInfo> Sources(List<string> ics, bool refresh)
        {
            var list = new List<CalInfo>();
            Error = null;
            if (Google.Connected)
            {
                try
                {
                    if (refresh || googleCals == null || DateTime.UtcNow - googleAt > TimeSpan.FromMinutes(30)) { googleCals = Google.Calendars(); googleAt = DateTime.UtcNow; }
                    list.AddRange(googleCals);
                }
                catch (Exception e) { Error = "Google: " + e.Message; Log.Write("google calendars: " + e.Message); if (googleCals != null) list.AddRange(googleCals); }
            }
            for (int i = 0; i < ics.Count; i++)
            {
                string name;
                lock (Calendar.Names) Calendar.Names.TryGetValue(i, out name);
                list.Add(new CalInfo { id = "ics:" + i, name = name ?? "iCal calendar " + (i + 1), color = IcsColors[i % IcsColors.Length], source = "ics", access = "owner" });
            }
            return list;
        }

        public static (List<CalEvent> list, string error) Fetch(List<CalInfo> cals, List<string> ics, DateTime from, DateTime to, HashSet<string> hidden)
        {
            var all = new List<CalEvent>();
            string error = null;
            var google = cals.Where(c => c.source == "google" && !hidden.Contains(c.id)).ToList();
            Parallel.ForEach(google, new ParallelOptions { MaxDegreeOfParallelism = 6 }, c =>
            {
                try { var evs = Google.Events(c, from, to); lock (all) all.AddRange(evs); }
                catch (Exception e) { error = c.name + ": " + e.Message; Log.Write("google events " + c.name + ": " + e.Message); }
            });
            if (ics.Count > 0)
            {
                var (list, err) = Calendar.Load(ics, from, to, id => !hidden.Contains(id));
                all.AddRange(list);
                error = error ?? err;
            }
            return (all.OrderBy(e => e.start, StringComparer.Ordinal).ToList(), error);
        }
    }
}
