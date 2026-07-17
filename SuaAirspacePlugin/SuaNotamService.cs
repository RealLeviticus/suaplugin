using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using vatsys;

namespace SuaAirspacePlugin;

/// <summary>
/// Scans VATPAC NOTAMs, extracts the activated SUA designators and their
/// activation time windows, and matches them to the dataset's areas so they
/// can be activated in one click — immediately, or scheduled to the NOTAM
/// times. The vatpac.org NOTAM page is a client-rendered SPA; the underlying
/// Strapi CMS API at cms.vatpac.org serves the NOTAM content as JSON.
/// </summary>
public sealed class SuaNotamService
{
    private const string NotamApiUrl =
        "https://cms.vatpac.org/api/notams?pagination%5BpageSize%5D=50&sort%5B1%5D=createdAt%3Adesc";

    private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = CreateClient();

    // Designators appear as e.g. "R232", "R249A", compressed runs "R225ABCDEF"
    // / "M278BFGH", or ranges "R276B-D". The trailing \b in the range group
    // rejects things like "- A095" after a plain designator.
    private static readonly Regex DesignatorRegex =
        new(@"\b([RDM])(\d{2,3})([A-Z]{0,12})(?:\s*-\s*([A-Z])\b)?", RegexOptions.Compiled);

    // Activation times appear as "202607 17 1800z" (yyyyMM dd HHmm, spacing
    // varies) listed in from-to pairs.
    private static readonly Regex TimeRegex =
        new(@"\b(\d{6})\s*(\d{2})\s*(\d{4})\s*[zZ]", RegexOptions.Compiled);

    private readonly SuaAirspaceService _sua;
    private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    private readonly object _lock = new object();

    private List<Notam>? _cache;
    private DateTime _cacheTimeUtc;

    public SuaNotamService(SuaAirspaceService sua)
    {
        _sua = sua;
    }

    public object GetNotams()
    {
        try
        {
            var now = DateTime.UtcNow;
            var notams = FetchAirspaceNotams();
            return new
            {
                Success = true,
                Notams = notams.Select(n => new
                {
                    n.Id,
                    n.Title,
                    n.Start,
                    n.End,
                    Status = n.StartUtc is not null && n.StartUtc > now ? "UPCOMING" : "CURRENT",
                    n.Designators,
                    Matched = n.MatchedAreas,
                    n.Unmatched,
                    Windows = n.Windows.Select(w =>
                        w.From.ToString("dd/MM HHmm", CultureInfo.InvariantCulture) + "-" +
                        w.To.ToString("HHmm", CultureInfo.InvariantCulture) + "Z").ToList(),
                }).ToList(),
            };
        }
        catch (Exception ex)
        {
            return new { Success = false, Error = "NOTAM fetch failed: " + ex.Message };
        }
    }

    /// <summary>mode "now": activate immediately (H24, ignore NOTAM times); "schedule": use the NOTAM's time windows.</summary>
    public object ActivateNotam(int id, string mode)
    {
        List<Notam> notams;
        try
        {
            notams = FetchAirspaceNotams();
        }
        catch (Exception ex)
        {
            return new { Success = false, Error = "NOTAM fetch failed: " + ex.Message };
        }

        var notam = notams.FirstOrDefault(n => n.Id == id);
        if (notam is null) return new { Success = false, Error = "Unknown NOTAM id " + id };

        var useSchedule = string.Equals(mode, "schedule", StringComparison.OrdinalIgnoreCase)
                          && notam.Windows.Count > 0;
        var windows = notam.Windows.Select(w => (w.From, w.To)).ToList();

        var activated = new List<string>();
        foreach (var areaName in notam.MatchedAreas)
        {
            var ok = useSchedule
                ? _sua.TryActivateWindows(areaName, windows)
                : _sua.TryActivate(areaName, 0);
            if (ok) activated.Add(areaName);
        }

        return new
        {
            Success = true,
            notam.Id,
            notam.Title,
            Mode = useSchedule ? "schedule" : "now",
            Activated = activated,
            notam.Unmatched,
        };
    }

    private List<Notam> FetchAirspaceNotams()
    {
        lock (_lock)
        {
            if (_cache is not null && DateTime.UtcNow - _cacheTimeUtc < CacheAge) return _cache;
        }

        var raw = Http.GetStringAsync(NotamApiUrl).GetAwaiter().GetResult();
        var parsed = ParseNotams(raw);

        lock (_lock)
        {
            _cache = parsed;
            _cacheTimeUtc = DateTime.UtcNow;
        }

        return parsed;
    }

    private List<Notam> ParseNotams(string raw)
    {
        var result = new List<Notam>();
        if (_json.DeserializeObject(raw) is not Dictionary<string, object> root ||
            root.TryGetValue("data", out var dataObj) is false || dataObj is not object[] data)
            return result;

        var now = DateTime.UtcNow;
        foreach (var item in data.OfType<Dictionary<string, object>>())
        {
            if (!item.TryGetValue("attributes", out var attrObj) || attrObj is not Dictionary<string, object> attrs)
                continue;

            var type = GetString(attrs, "type");
            if (type.IndexOf("airspace", StringComparison.OrdinalIgnoreCase) < 0) continue;

            // Upcoming NOTAMs are listed too (status marks them); only expired
            // ones are dropped.
            var end = GetDate(attrs, "end");
            if (end is not null && end < now) continue;

            var content = StripHtml(GetString(attrs, "content"));
            var designators = ExtractDesignators(content);
            if (designators.Count == 0) continue;

            var (matched, unmatched) = MatchAreas(designators);
            result.Add(new Notam
            {
                Id = item.TryGetValue("id", out var idObj) ? Convert.ToInt32(idObj, CultureInfo.InvariantCulture) : 0,
                Title = GetString(attrs, "title"),
                Start = GetString(attrs, "start"),
                End = GetString(attrs, "end"),
                StartUtc = GetDate(attrs, "start"),
                Designators = designators,
                MatchedAreas = matched,
                Unmatched = unmatched,
                Windows = ExtractTimeWindows(content),
            });
        }

        return result;
    }

    internal static List<string> ExtractDesignators(string text)
    {
        var result = new List<string>();
        void Add(string d) { if (!result.Contains(d)) result.Add(d); }

        foreach (Match m in DesignatorRegex.Matches(text))
        {
            var prefix = m.Groups[1].Value;
            var number = m.Groups[2].Value;
            var suffixes = m.Groups[3].Value;
            var rangeEnd = m.Groups[4].Value;

            if (suffixes.Length == 0 && rangeEnd.Length == 0)
            {
                Add(prefix + number);
            }
            else if (rangeEnd.Length == 1)
            {
                // "R276B-D": letters before the range start are individual
                // suffixes; the last listed letter starts the range.
                for (var i = 0; i < suffixes.Length - 1; i++) Add(prefix + number + suffixes[i]);
                var from = suffixes.Length > 0 ? suffixes[suffixes.Length - 1] : 'A';
                for (var c = from; c <= rangeEnd[0]; c++) Add(prefix + number + c);
            }
            else
            {
                // "R225ABCDEF": each trailing letter is a separate sub-area.
                foreach (var c in suffixes) Add(prefix + number + c);
            }
        }

        return result;
    }

    /// <summary>
    /// Times are listed as sequential from-to pairs ("202607 17 1800z -
    /// 202607 17 2200z ..."), so datetime tokens are paired in document order.
    /// </summary>
    internal static List<(DateTime From, DateTime To)> ExtractTimeWindows(string text)
    {
        var stamps = new List<DateTime>();
        foreach (Match m in TimeRegex.Matches(text))
        {
            if (DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value,
                    "yyyyMMddHHmm", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                stamps.Add(dt);
        }

        var windows = new List<(DateTime, DateTime)>();
        for (var i = 0; i + 1 < stamps.Count; i += 2)
        {
            if (stamps[i + 1] > stamps[i]) windows.Add((stamps[i], stamps[i + 1]));
        }

        return windows;
    }

    /// <summary>
    /// Areas are named "&lt;designator&gt; &lt;place&gt;" (e.g. "D101 NORTHAM"), so match
    /// on the first token. A plain designator whose exact token is absent falls
    /// back to all lettered sub-areas ("R232" -> "R232A", "R232B", ...).
    /// </summary>
    private static (List<string> Matched, List<string> Unmatched) MatchAreas(List<string> designators)
    {
        var matched = new List<string>();
        var unmatched = new List<string>();

        var instance = RestrictedAreas.Instance;
        if (instance is null) return (matched, new List<string>(designators));

        var byToken = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in instance.Areas)
        {
            if (string.IsNullOrWhiteSpace(area.Name)) continue;
            var token = area.Name.Split(' ')[0];
            if (!byToken.TryGetValue(token, out var names)) byToken[token] = names = new List<string>();
            names.Add(area.Name);
        }

        foreach (var designator in designators)
        {
            if (byToken.TryGetValue(designator, out var exact))
            {
                matched.AddRange(exact.Where(n => !matched.Contains(n)));
                continue;
            }

            var prefixed = byToken
                .Where(kv => kv.Key.Length > designator.Length
                             && kv.Key.StartsWith(designator, StringComparison.OrdinalIgnoreCase)
                             && kv.Key.Skip(designator.Length).All(char.IsLetter))
                .SelectMany(kv => kv.Value)
                .ToList();

            if (prefixed.Count > 0) matched.AddRange(prefixed.Where(n => !matched.Contains(n)));
            else unmatched.Add(designator);
        }

        return (matched, unmatched);
    }

    private static string StripHtml(string html)
    {
        var text = Regex.Replace(html, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(text);
    }

    private static string GetString(Dictionary<string, object> dict, string key) =>
        dict.TryGetValue(key, out var value) && value is string s ? s : "";

    private static DateTime? GetDate(Dictionary<string, object> dict, string key)
    {
        var s = GetString(dict, key);
        if (s.Length == 0) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
    }

    private static HttpClient CreateClient()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SuaAirspacePlugin/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return client;
    }

    private sealed class Notam
    {
        public int Id;
        public string Title = "";
        public string Start = "";
        public string End = "";
        public DateTime? StartUtc;
        public List<string> Designators = new();
        public List<string> MatchedAreas = new();
        public List<string> Unmatched = new();
        public List<(DateTime From, DateTime To)> Windows = new();
    }
}
