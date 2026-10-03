// Claude Meter (Windows) — system tray + floating desktop gauge for Claude plan
// usage. Reads Claude Code's OAuth credentials from %USERPROFILE%\.claude\
// .credentials.json, refreshes the token when expired (writing it back so
// Claude Code stays signed in), and polls the same endpoint the Claude app's
// Settings → Usage screen uses.
//
// Build with build.ps1. Single-file on purpose — same pattern as the macOS
// version (macos/main.swift), which this mirrors section by section.

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ClaudeMeter;

// ───────────────────────────── Usage model ─────────────────────────────

record LimitEntry(string Id, string Kind, string Label, double Percent,
                  DateTimeOffset? ResetsAt, bool IsActive);

class UsageSnapshot
{
    public DateTimeOffset FetchedAt;
    public List<LimitEntry> Limits = new();
    public LimitEntry? Session   => Limits.FirstOrDefault(l => l.Kind == "session");
    public LimitEntry? WeeklyAll => Limits.FirstOrDefault(l => l.Kind == "weekly_all");
    public List<LimitEntry> Scoped => Limits.Where(l => l.Kind == "weekly_scoped").ToList();
    public LimitEntry? PrimaryModel => Scoped.FirstOrDefault();
    public static string ModelName(LimitEntry e) =>
        e.Label.StartsWith("Week — ") ? e.Label.Substring("Week — ".Length) : e.Label;
}

static class Sev
{
    // Traffic-light thresholds; API "severity" only says normal/warning so we
    // derive finer bands from the percentage. Same RGB values as the mac app.
    public static Color Of(double pct) => pct switch
    {
        < 50 => Color.FromArgb(56, 184, 107),
        < 75 => Color.FromArgb(212, 158, 5), // deep gold — bright yellow vanished on warm wallpapers
        < 90 => Color.FromArgb(245, 140, 36),
        _    => Color.FromArgb(230, 66, 54),
    };
}

// ───────────────────────────── Gauge selection ─────────────────────────────
// Which of the three limits are drawn as gauges. Menu-bar/tray metric, tooltip,
// scoped bars and the sparkline do not use this.

enum Gauge { Session, Week, Model }

static class Gauges
{
    public static bool IsOn(Gauge k) => k switch
    {
        Gauge.Session => S.ShowSession,
        Gauge.Week => S.ShowWeek,
        _ => S.ShowModel,
    };

    public static int OnCount =>
        (S.ShowSession ? 1 : 0) + (S.ShowWeek ? 1 : 0) + (S.ShowModel ? 1 : 0);

    // Canonical order (Rings, outside in): week, model, session. Model needs a
    // primary model; nothing left -> session.
    public static List<Gauge> Canonical(UsageSnapshot snap)
    {
        var list = new List<Gauge>();
        if (S.ShowWeek) list.Add(Gauge.Week);
        if (S.ShowModel && snap.PrimaryModel != null) list.Add(Gauge.Model);
        if (S.ShowSession) list.Add(Gauge.Session);
        if (list.Count == 0) list.Add(Gauge.Session);
        return list;
    }

    // Display order (popover, one line, square; left to right): session, week, model.
    public static List<Gauge> Display(UsageSnapshot snap) =>
        Canonical(snap).OrderBy(k => (int)k).ToList();

    public static LimitEntry? Entry(UsageSnapshot snap, Gauge k) => k switch
    {
        Gauge.Session => snap.Session,
        Gauge.Week => snap.WeeklyAll,
        _ => snap.PrimaryModel,
    };

    // Popover ring diameter (logical px) by number of visible gauges.
    public static int PopoverRing(int n) => n >= 3 ? 72 : n == 2 ? 84 : 96;
}

// ───────────────────────────── Settings ─────────────────────────────
// Windows counterpart of UserDefaults: a transparent JSON file in
// %APPDATA%\Claude Meter\settings.json.

static class S
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude Meter");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static JsonObject data = new();
    public static event Action? Changed;

    static S()
    {
        try
        {
            if (File.Exists(FilePath) &&
                JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o) data = o;
        }
        catch { /* corrupt settings → defaults */ }
    }

    static T Get<T>(string key, T fallback)
    {
        try { return data[key] is JsonNode n ? n.GetValue<T>() : fallback; }
        catch { return fallback; }
    }

    static void Set<T>(string key, T value)
    {
        data[key] = JsonValue.Create(value);
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        Changed?.Invoke();
    }

    public static bool ShowFloating   { get => Get("showFloating", true);  set => Set("showFloating", value); }
    // "line" | "square" | "rings". Migrates the old boolean floatSquare when floatStyle was never written.
    public static string FloatStyle
    {
        get => data["floatStyle"] == null
            ? (Get("floatSquare", false) ? "square" : "line")
            : Get("floatStyle", "line");
        set => Set("floatStyle", value);
    }
    // "week" | "session": which number is largest (top) in the Rings centre.
    public static string RingsCentre  { get => Get("ringsCentre", "week"); set => Set("ringsCentre", value); }
    public static string TrayMetric   { get => Get("trayMetric", "worst"); set => Set("trayMetric", value); }
    public static bool TrayShowPct    { get => Get("trayShowPct", true);   set => Set("trayShowPct", value); }
    // Which limits are drawn as gauges (popover rings, all float styles). Absent = on.
    public static bool ShowSession    { get => Get("showSession", true);   set => Set("showSession", value); }
    public static bool ShowWeek       { get => Get("showWeek", true);      set => Set("showWeek", value); }
    public static bool ShowModel      { get => Get("showModel", true);     set => Set("showModel", value); }
    public static double WarnThreshold{ get => Get("warnThreshold", 90.0); set => Set("warnThreshold", value); }
    public static int FloatX          { get => Get("floatX", int.MinValue);set => Set("floatX", value); }
    public static int FloatY          { get => Get("floatY", int.MinValue);set => Set("floatY", value); }

    public static bool AutoUpdateCheck { get => Get("autoUpdateCheck", true); set => Set("autoUpdateCheck", value); }
    public static string LastNotifiedUpdate { get => Get("lastNotifiedUpdate", ""); set => Set("lastNotifiedUpdate", value); }

    public static bool GetWarned(string id) => Get("warned-" + id, false);
    public static void SetWarned(string id, bool v) => Set("warned-" + id, v);
}

// ───────────────────────────── Credentials ─────────────────────────────
// Claude Code on Windows keeps the same claudeAiOauth JSON the mac keychain
// holds, in a plain file. We preserve every field we don't understand.

class Creds
{
    public JsonObject Blob;
    Creds(JsonObject blob) => Blob = blob;
    JsonObject? OAuth => Blob["claudeAiOauth"] as JsonObject;
    public string? AccessToken  => (string?)OAuth?["accessToken"];
    public string? RefreshToken => (string?)OAuth?["refreshToken"];
    public string? Subscription => (string?)OAuth?["subscriptionType"];
    public DateTimeOffset? ExpiresAt
    {
        get
        {
            try
            {
                return OAuth?["expiresAt"] is JsonNode n
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)n.GetValue<double>())
                    : null;
            }
            catch { return null; }
        }
    }

    public void Apply(string accessToken, string? refreshToken, double expiresIn)
    {
        var o = OAuth ?? new JsonObject();
        o["accessToken"] = accessToken;
        if (refreshToken != null) o["refreshToken"] = refreshToken;
        o["expiresAt"] = DateTimeOffset.Now.AddSeconds(expiresIn).ToUnixTimeMilliseconds();
        Blob["claudeAiOauth"] = o;
    }

    // CLAUDE_METER_CREDS_PATH (testing only) replaces the default for read and write-back.
    public static string CredsPath =
        Environment.GetEnvironmentVariable("CLAUDE_METER_CREDS_PATH") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", ".credentials.json");

    // True only when the file is missing or parses without a claudeAiOauth
    // object. A locked / half-written file is not "absent".
    public static bool IsAbsent()
    {
        try
        {
            if (!File.Exists(CredsPath)) return true;
            return JsonNode.Parse(File.ReadAllText(CredsPath)) is JsonObject blob &&
                   blob["claudeAiOauth"] is not JsonObject;
        }
        catch { return false; }
    }

    public static Creds? Read()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(CredsPath)) is JsonObject blob &&
                blob["claudeAiOauth"] is JsonObject) return new Creds(blob);
        }
        catch { }
        return null;
    }

    public void Write()
    {
        try { File.WriteAllText(CredsPath, Blob.ToJsonString()); } catch { }
    }
}

// ───────────────────────────── API ─────────────────────────────

class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

// Credentials are missing, have no refresh token, or the refresh token was
// rejected (HTTP 400/401) — only a fresh `claude auth login` fixes this.
class AuthRequiredException : ApiException
{
    public AuthRequiredException(string message) : base(message) { }
}

static class UsageAPI
{
    public const string UserAgent = "claude-code/2.0.0 (external, cli)"; // plain UAs get Cloudflare-1010'd
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"; // Claude Code's public OAuth client id

    static readonly HttpClient http = MakeClient();
    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return c;
    }

    static async Task<(string body, int status)> Request(string url, HttpMethod? method = null,
        Dictionary<string, string>? headers = null, string? jsonBody = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (jsonBody != null)
            req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        if (headers != null)
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        using var resp = await http.SendAsync(req);
        return (await resp.Content.ReadAsStringAsync(), (int)resp.StatusCode);
    }

    public static async Task<(string token, string? plan)> ValidToken(bool forceRefresh = false)
    {
        var noCreds = $"No Claude Code credentials at {Creds.CredsPath} — install Claude Code on this machine and sign in once (run `claude`).";
        var creds = Creds.Read() ?? throw (Creds.IsAbsent()
            ? new AuthRequiredException(noCreds)
            : new ApiException(noCreds));
        if (!forceRefresh && creds.ExpiresAt is DateTimeOffset exp && exp > DateTimeOffset.Now.AddSeconds(120) &&
            creds.AccessToken is string tok)
            return (tok, creds.Subscription);

        var refresh = creds.RefreshToken ?? throw new AuthRequiredException(
            "Credentials file has no refresh token — sign in to Claude Code again.");
        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = ClientId,
        });
        var (text, code) = await Request("https://platform.claude.com/v1/oauth/token",
                                         HttpMethod.Post, jsonBody: body);
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(text) as JsonObject; } catch { }
        if (code == 400 || code == 401)
        {
            string? err = null, desc = null;
            try { err = (string?)obj?["error"]; } catch { }
            try { desc = (string?)obj?["error_description"]; } catch { }
            var detail = string.Join(" — ", new[] { err, desc }.Where(s => !string.IsNullOrEmpty(s)));
            throw new AuthRequiredException($"Token refresh failed: HTTP {code}" +
                                            (detail.Length > 0 ? $" ({detail})" : ""));
        }
        if (code != 200 || obj?["access_token"] is not JsonNode tokNode)
            throw new ApiException($"Token refresh failed: HTTP {code}");
        var newTok = tokNode.GetValue<string>();
        double expiresIn = 3600;
        try { if (obj["expires_in"] is JsonNode e) expiresIn = e.GetValue<double>(); } catch { }
        creds.Apply(newTok, (string?)obj["refresh_token"], expiresIn);
        creds.Write();
        return (newTok, creds.Subscription);
    }

    static Task<(string body, int status)> GetUsage(string token) =>
        Request("https://api.anthropic.com/api/oauth/usage",
            headers: new()
            {
                ["Authorization"] = "Bearer " + token,
                ["anthropic-beta"] = "oauth-2025-04-20",
            });

    public static async Task<(UsageSnapshot snap, string? plan)> FetchUsage()
    {
        var (token, plan) = await ValidToken();
        var (text, code) = await GetUsage(token);
        if (code == 401) // possibly revoked while the access token is unexpired: refresh once, retry once
        {
            (token, plan) = await ValidToken(forceRefresh: true);
            (text, code) = await GetUsage(token);
            if (code == 401)
                throw new AuthRequiredException("Usage request rejected (HTTP 401) after token refresh.");
        }
        if (code != 200) throw new ApiException($"Usage request failed (HTTP {code}).");

        JsonObject? obj;
        try { obj = JsonNode.Parse(text) as JsonObject; }
        catch { throw new ApiException("Unexpected response from usage endpoint."); }
        if (obj?["limits"] is not JsonArray rawLimits)
            throw new ApiException("Unexpected response from usage endpoint.");

        var snap = new UsageSnapshot { FetchedAt = DateTimeOffset.Now };
        foreach (var node in rawLimits)
        {
            if (node is not JsonObject l) continue;
            if (l["kind"] is not JsonNode kindNode || l["percent"] is not JsonNode pctNode) continue;
            var kind = kindNode.GetValue<string>();
            double pct;
            try { pct = pctNode.GetValue<double>(); } catch { continue; }
            string label = kind switch
            {
                "session" => "Session (5 h)",
                "weekly_all" => "Week — all models",
                _ => "Week — " + ((string?)((l["scope"] as JsonObject)?["model"] as JsonObject)?["display_name"] ?? "scoped"),
            };
            DateTimeOffset? resets = null;
            if ((string?)l["resets_at"] is string s &&
                DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                resets = dt;
            bool active = false;
            try { if (l["is_active"] is JsonNode a) active = a.GetValue<bool>(); } catch { }
            snap.Limits.Add(new LimitEntry(kind + label, kind, label, pct, resets, active));
        }
        if (snap.Limits.Count == 0) throw new ApiException("Unexpected response from usage endpoint.");
        return (snap, plan);
    }
}

// ───────────────────────────── History (sparkline) ─────────────────────────────

record HistoryPoint(DateTimeOffset T, double S, double W);

class HistoryStore
{
    readonly string path;
    public List<HistoryPoint> Points = new();

    public HistoryStore()
    {
        Directory.CreateDirectory(S.Dir);
        path = Path.Combine(S.Dir, "history.json");
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<List<HistoryPoint>>(File.ReadAllText(path)) is List<HistoryPoint> pts)
                Points = pts;
        }
        catch { }
    }

    public void Record(double session, double weekly)
    {
        if (Points.Count > 0 && (DateTimeOffset.Now - Points[^1].T).TotalSeconds < 270) return;
        Points.Add(new HistoryPoint(DateTimeOffset.Now, session, weekly));
        var cutoff = DateTimeOffset.Now.AddDays(-7);
        Points.RemoveAll(p => p.T < cutoff);
        try { File.WriteAllText(path, JsonSerializer.Serialize(Points)); } catch { }
    }

    public List<HistoryPoint> Last24h()
    {
        var cutoff = DateTimeOffset.Now.AddHours(-24);
        return Points.Where(p => p.T >= cutoff).ToList();
    }
}

// ───────────────────────────── Formatting helpers ─────────────────────────────

static class Fmt
{
    // System-locale short time ("21:40" or "9:40 pm" depending on settings).
    public static string Clock(DateTimeOffset d) =>
        d.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)
            .Replace(" AM", " am").Replace(" PM", " pm");

    // Day-before-month for locales that write it that way (AU), month-first otherwise.
    public static string DayMonth(DateTimeOffset d)
    {
        var p = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
        bool dayFirst = p.IndexOf('d') < p.IndexOf('M');
        return d.ToLocalTime().ToString(dayFirst ? "ddd d/M" : "ddd M/d", CultureInfo.CurrentCulture);
    }

    public static string ResetText(DateTimeOffset? at)
    {
        if (at is not DateTimeOffset a) return "—";
        var secs = (a - DateTimeOffset.Now).TotalSeconds;
        if (secs <= 0) return "resetting…";
        int h = (int)secs / 3600, m = ((int)secs % 3600) / 60;
        var rel = h > 0 ? $"{h} h {m} m" : $"{m} m";
        var clock = a.ToLocalTime().Date == DateTime.Today
            ? Clock(a)
            : DayMonth(a) + " " + Clock(a);
        return $"resets in {rel} · {clock}";
    }
}

// ───────────────────────────── Theme ─────────────────────────────

static class Theme
{
    public static bool Dark { get; private set; }
    public static bool TaskbarDark { get; private set; } = true;

    public static void Refresh()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            Dark = Equals(k?.GetValue("AppsUseLightTheme"), 0);
            TaskbarDark = Equals(k?.GetValue("SystemUsesLightTheme") ?? 0, 0);
        }
        catch { Dark = false; }
    }

    public static Color Bg      => Dark ? Color.FromArgb(34, 34, 36)   : Color.FromArgb(248, 248, 248);
    public static Color Fg      => Dark ? Color.FromArgb(240, 240, 240): Color.FromArgb(25, 25, 25);
    public static Color Fg2     => Dark ? Color.FromArgb(160, 160, 165): Color.FromArgb(110, 110, 115);
    public static Color Fg3     => Dark ? Color.FromArgb(110, 110, 115): Color.FromArgb(160, 160, 165);
    public static Color Track   => Dark ? Color.FromArgb(62, 62, 66)   : Color.FromArgb(228, 228, 230);
    public static Color Border  => Dark ? Color.FromArgb(70, 70, 74)   : Color.FromArgb(210, 210, 214);
    public static Color Accent  => Color.FromArgb(217, 119, 87); // Claude terracotta
    public static Color ErrorFg => Color.FromArgb(230, 66, 54);
}

// ───────────────────────────── Usage warnings ─────────────────────────────
// Balloon tip when a limit crosses the configured threshold (default 90%,
// 0 = off); re-arms once it drops 5 points below, so each approach warns once.

enum BalloonKind { SignIn, Update, UsageWarning, Other }

static class Notifier
{
    // Returns true when at least one warning balloon was shown.
    public static bool Check(UsageSnapshot snap, NotifyIcon tray)
    {
        bool shown = false;
        var threshold = S.WarnThreshold;
        if (threshold <= 0) return false;
        foreach (var l in snap.Limits)
        {
            if (l.Percent >= threshold && !S.GetWarned(l.Id))
            {
                S.SetWarned(l.Id, true);
                tray.ShowBalloonTip(10000, $"Claude usage at {Math.Round(l.Percent)}%",
                                    $"{l.Label} — {Fmt.ResetText(l.ResetsAt)}", ToolTipIcon.Warning);
                shown = true;
            }
            else if (l.Percent < threshold - 5 && S.GetWarned(l.Id))
            {
                S.SetWarned(l.Id, false);
            }
        }
        return shown;
    }
}

// ───────────────────────────── Update check ─────────────────────────────
// Looks at the latest GitHub Release; never downloads or installs anything.

static class AppVersion
{
    // Assembly informational/file version trimmed to "major.minor[.patch]".
    public static readonly string Current = Compute();
    static string Compute()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
            var info = (Attribute.GetCustomAttribute(asm,
                typeof(System.Reflection.AssemblyInformationalVersionAttribute))
                as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion;
            var m = System.Text.RegularExpressions.Regex.Match(info ?? "", @"^\d+(\.\d+){0,2}");
            if (m.Success) return m.Value;
            return asm.GetName().Version?.ToString(3) ?? "0";
        }
        catch { return "0"; }
    }
}

record Release(string Tag, string Version, string Url, string Notes);

static class UpdateChecker
{
    const string Api = "https://api.github.com/repos/bernmc/claude-meter/releases/latest";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // null = no releases yet (404), i.e. up to date.
    public static async Task<Release?> Latest()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("User-Agent", "ClaudeMeter/" + AppVersion.Current);
        using var resp = await http.SendAsync(req);
        int code = (int)resp.StatusCode;
        if (code == 404) return null;
        if (code != 200) throw new ApiException($"GitHub returned HTTP {code}.");
        var text = await resp.Content.ReadAsStringAsync();
        JsonObject? obj;
        try { obj = JsonNode.Parse(text) as JsonObject; }
        catch { throw new ApiException("Unexpected response from GitHub."); }
        if ((string?)obj?["tag_name"] is not string tag || (string?)obj["html_url"] is not string url)
            throw new ApiException("Unexpected response from GitHub.");
        return new Release(tag, tag.TrimStart('v', 'V'), url, (string?)obj["body"] ?? "");
    }

    // True when version a is newer than b: leading "v" stripped, numeric per
    // component, missing components = 0.
    public static bool IsNewer(string a, string b)
    {
        static int[] Parts(string v) => v.Trim().TrimStart('v', 'V').Split('.')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .ToArray();
        var x = Parts(a); var y = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int xi = i < x.Length ? x[i] : 0, yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi > yi;
        }
        return false;
    }
}

// ───────────────────────────── Shared drawing ─────────────────────────────

static class Draw
{
    // outlineExtra > 0 (Rings float style): hard near-black outline arc, penW + outlineExtra wide,
    // drawn under the coloured arc in place of the soft halo.
    public static void Ring(Graphics g, RectangleF rect, float penW, double pct, float outlineExtra = 0)
    {
        using var track = new Pen(Theme.Track, penW);
        var r = rect; r.Inflate(-penW / 2, -penW / 2);
        g.DrawEllipse(track, r);
        float sweep = (float)(360 * Math.Min(pct, 100) / 100);
        if (sweep < 1.5f) sweep = 1.5f;
        if (outlineExtra > 0)
        {
            using var outline = new Pen(Color.FromArgb(217, 0, 0, 0), penW + outlineExtra)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(outline, r, -90, sweep);
        }
        else
        {
            // Faint dark halo behind the colored arc so it separates from the
            // background whatever's behind the window.
            using var halo = new Pen(Color.FromArgb(80, 0, 0, 0), penW + 2.5f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(halo, r, -90, sweep);
        }
        using var pen = new Pen(Sev.Of(pct), penW)
        { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, r, -90, sweep);
    }

    public static void Centered(Graphics g, string text, Font font, Color color, float cx, float cy)
    {
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, cx, cy, sf);
    }

    // Filled text in `fill` with a black stroke of strokeW px centred on the glyph edge.
    // `font` must be created in GraphicsUnit.Pixel (Size is then the em size in px).
    public static void CenteredOutlined(Graphics g, string text, Font font, Color fill,
                                        float strokeW, float cx, float cy)
    {
        float boxW = font.Size * text.Length * 2 + 40, boxH = font.Size * 3;
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size,
                       new RectangleF(cx - boxW / 2, cy - boxH / 2, boxW, boxH), sf);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(Color.Black, strokeW) { LineJoin = LineJoin.Round };
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }
}

// ───────────────────────────── Tray icon rendering ─────────────────────────────
// Windows can't put text next to a tray icon the way the mac menu bar can,
// so the percentage (optionally) lives inside the ring.

static class TrayIconRenderer
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    public static Icon Make(double? pct, bool showNumber, bool error = false)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var fg = Theme.TaskbarDark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(40, 40, 40);
            float penW = showNumber && pct != null ? 4f : 5.5f;
            var rect = new RectangleF(0.5f, 0.5f, size - 1, size - 1);
            using (var track = new Pen(Color.FromArgb(90, fg), penW))
            {
                var r = rect; r.Inflate(-penW / 2, -penW / 2);
                g.DrawEllipse(track, r);
                if (pct is double p)
                {
                    float sweep = (float)(360 * Math.Min(p, 100) / 100);
                    if (sweep < 6f) sweep = 6f;
                    using var pen = new Pen(Sev.Of(p), penW)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawArc(pen, r, -90, sweep);
                }
            }
            if (error)
            {
                // A broken data path shows a red "!" even with the number
                // hidden — errors shouldn't be invisible.
                using var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
                Draw.Centered(g, "!", font, Color.FromArgb(230, 66, 54), size / 2f, size / 2f + 0.5f);
            }
            else if (showNumber)
            {
                string text = pct is double pp ? Math.Min(Math.Round(pp), 99).ToString() : "–";
                using var font = new Font("Segoe UI", text.Length > 1 ? 13f : 15f,
                                          FontStyle.Bold, GraphicsUnit.Pixel);
                Draw.Centered(g, text, font, fg, size / 2f, size / 2f + 0.5f);
            }
        }
        IntPtr h = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
        finally { DestroyIcon(h); }
    }
}

// ───────────────────────────── Flyout (popover equivalent) ─────────────────────────────

class FlyoutForm : Form
{
    readonly App app;
    Rectangle refreshRect, gaugeRect, gearRect, signInRect, updateRect;
    bool gearMenuOpen;
    bool ShowSignIn => app.AuthRequired && app.ErrorText != null;

    public FlyoutForm(App app)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        Win32.RoundCorners(this);
    }

    float F => DeviceDpi / 96f;
    int L(double logical) => (int)Math.Round(logical * F);
    int VisibleGauges => app.Snap is UsageSnapshot sn ? Gauges.Canonical(sn).Count : 0;
    int FlyoutW() => L(VisibleGauges == 3 ? 336 : 300);

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= Win32.WS_EX_TOOLWINDOW; return p; }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!gearMenuOpen) Hide();
    }

    public void ShowNearTray()
    {
        var h = Relayout();
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = FlyoutW();
        Bounds = new Rectangle(
            Math.Clamp(Cursor.Position.X - w / 2, screen.Left + L(8), screen.Right - w - L(8)),
            Math.Clamp(Cursor.Position.Y - h - L(12), screen.Top + L(8), screen.Bottom - h - L(8)),
            w, h);
        Show();
        Activate();
        Invalidate();
    }

    // Compute total height (device px) from current content; mirrors the
    // popover's self-sizing.
    int Relayout()
    {
        using var g = CreateGraphics();
        int pad = L(14), y = pad;
        y += L(18) + L(12);                                   // header
        if (app.Snap is UsageSnapshot snap)
        {
            y += L(Gauges.PopoverRing(Gauges.Canonical(snap).Count) + 6 + 15 + 3) + L(28) + L(12);   // rings + labels + 2-line sublabels
            int extra = Math.Max(0, snap.Scoped.Count - 1);   // first scoped entry is the third ring
            if (extra > 0)
                y += extra * L(28) + (extra - 1) * L(8) + L(12);
            if (app.History.Last24h().Count >= 2)
                y += L(34 + 3 + 12) + L(12);
        }
        else if (app.ErrorText == null)
        {
            y += L(80) + L(12);                               // "loading" block
        }
        if (app.ErrorText is string err)
        {
            using var f = Fnt(10.5, FontStyle.Regular);
            var sz = g.MeasureString(err, f, FlyoutW() - 2 * pad);
            y += (int)Math.Ceiling(sz.Height) + L(ShowSignIn ? 8 : 12);
            if (ShowSignIn) y += L(28) + L(12);               // sign-in button
        }
        if (app.AvailableUpdate != null) y += L(20) + L(12);  // update row
        y += 1 + L(12);                                       // divider
        y += L(20) + pad;                                     // footer row
        return y;
    }

    Font Fnt(double px, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", (float)(px * F), style, GraphicsUnit.Pixel);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Bg);
        using (var border = new Pen(Theme.Border))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        signInRect = Rectangle.Empty;
        updateRect = Rectangle.Empty;

        int pad = L(14), y = pad, w = Width;

        // Header: title + plan badge
        using (var f = Fnt(13, FontStyle.Bold))
        using (var b = new SolidBrush(Theme.Fg))
            g.DrawString("Claude usage", f, b, pad, y);
        if (app.Plan is string plan)
        {
            using var f = Fnt(9, FontStyle.Bold);
            var text = plan.ToUpperInvariant();
            var sz = g.MeasureString(text, f);
            var rect = new RectangleF(w - pad - sz.Width - L(14), y, sz.Width + L(14), L(16));
            using var path = Win32.Rounded(rect, L(8));
            using (var bg = new SolidBrush(Color.FromArgb(40, Theme.Accent))) g.FillPath(bg, path);
            using (var fgb = new SolidBrush(Theme.Accent))
                g.DrawString(text, f, fgb, rect.X + L(7), rect.Y + L(2.5));
        }
        y += L(18) + L(12);

        if (app.Snap is UsageSnapshot snap)
        {
            // Big ring gauges: the selected limits (Session, Week (all), first per-model limit)
            var vis = Gauges.Display(snap);
            int n = vis.Count;
            int ring = L(Gauges.PopoverRing(n));
            float pitch = L(n == 3 ? 104 : 150);
            int textW = L(n == 3 ? 100 : 144);
            for (int i = 0; i < n; i++)
            {
                float cxi = w / 2f + (i - (n - 1) / 2f) * pitch;
                if (Gauges.Entry(snap, vis[i]) is not LimitEntry ent) continue;
                var label = vis[i] switch
                {
                    Gauge.Session => "Session",
                    Gauge.Week => "Week (all)",
                    _ => "Week (" + UsageSnapshot.ModelName(ent) + ")",
                };
                RingGauge(g, ent, cxi, y, ring, label, textW);
            }
            y += ring + L(6 + 15 + 3 + 28) + L(12);

            // Remaining scoped per-model bars (the first one is the third ring)
            var barEntries = snap.Scoped.Skip(1).ToList();
            foreach (var sc in barEntries)
            {
                using (var f = Fnt(11, FontStyle.Regular))
                using (var b = new SolidBrush(Theme.Fg))
                    g.DrawString(sc.Label, f, b, pad, y);
                using (var f = Fnt(11, FontStyle.Bold))
                using (var b = new SolidBrush(Sev.Of(sc.Percent)))
                {
                    var t = Math.Round(sc.Percent) + "%";
                    var sz = g.MeasureString(t, f);
                    g.DrawString(t, f, b, w - pad - sz.Width, y);
                }
                var barY = y + L(17);
                var track = new RectangleF(pad, barY, w - 2 * pad, L(6));
                using (var path = Win32.Rounded(track, L(3)))
                using (var tb = new SolidBrush(Theme.Track)) g.FillPath(tb, path);
                var fillW = Math.Max(L(4), (float)((w - 2 * pad) * Math.Min(sc.Percent, 100) / 100));
                using (var path = Win32.Rounded(new RectangleF(pad, barY, fillW, L(6)), L(3)))
                using (var fb = new SolidBrush(Sev.Of(sc.Percent))) g.FillPath(fb, path);
                y += L(28) + L(8);
            }
            if (barEntries.Count > 0) y += L(12) - L(8);

            // Sparkline
            var hist = app.History.Last24h();
            if (hist.Count >= 2)
            {
                Sparkline(g, hist, new RectangleF(pad, y, w - 2 * pad, L(34)));
                using (var f = Fnt(9))
                using (var b = new SolidBrush(Theme.Fg3))
                    g.DrawString("session · last 24 h", f, b, pad, y + L(34 + 3));
                y += L(34 + 3 + 12) + L(12);
            }
        }
        else if (app.ErrorText == null)
        {
            using var f = Fnt(10.5);
            Draw.Centered(g, "Loading…", f, Theme.Fg2, w / 2f, y + L(40));
            y += L(80) + L(12);
        }

        if (app.ErrorText is string err)
        {
            using var f = Fnt(10.5);
            using var b = new SolidBrush(Theme.ErrorFg);
            var rect = new RectangleF(pad, y, w - 2 * pad, Height);
            var sz = g.MeasureString(err, f, w - 2 * pad);
            g.DrawString(err, f, b, rect);
            y += (int)Math.Ceiling(sz.Height) + L(ShowSignIn ? 8 : 12);
            if (ShowSignIn)
            {
                signInRect = new Rectangle(pad, y, w - 2 * pad, L(28));
                using (var path = Win32.Rounded(signInRect, L(6)))
                using (var bg = new SolidBrush(Theme.Accent)) g.FillPath(bg, path);
                using (var bf = Fnt(10.5, FontStyle.Bold))
                    Draw.Centered(g, "Sign in to Claude Code…", bf, Color.White,
                                  signInRect.X + signInRect.Width / 2f, signInRect.Y + signInRect.Height / 2f);
                y += L(28) + L(12);
            }
        }

        // Update row: only while a newer release is known
        if (app.AvailableUpdate is Release upd)
        {
            using (var f = Fnt(11, FontStyle.Bold))
            using (var b = new SolidBrush(Theme.Fg))
                g.DrawString("Update available: v" + upd.Version, f, b, pad, y + L(2));
            using (var f = Fnt(11, FontStyle.Regular))
            using (var b = new SolidBrush(Theme.Accent))
            {
                const string link = "Open release page…";
                var sz = g.MeasureString(link, f);
                updateRect = new Rectangle(w - pad - (int)Math.Ceiling(sz.Width), y, (int)Math.Ceiling(sz.Width), L(20));
                g.DrawString(link, f, b, updateRect.X, y + L(2));
            }
            y += L(20) + L(12);
        }

        // Divider
        using (var p = new Pen(Theme.Border)) g.DrawLine(p, pad, y, w - pad, y);
        y += 1 + L(12);

        // Footer: refresh ⟳, gauge toggle, updated text, gear ⚙
        using (var f = Fnt(14))
        {
            refreshRect = new Rectangle(pad, y - L(2), L(22), L(22));
            Draw.Centered(g, "⟳", f, app.Refreshing ? Theme.Fg3 : Theme.Fg2,
                          refreshRect.X + L(11), refreshRect.Y + L(11));
            gaugeRect = new Rectangle(pad + L(30), y - L(2), L(22), L(22));
            Draw.Centered(g, "▣", f, Theme.Fg2, gaugeRect.X + L(11), gaugeRect.Y + L(11));
            gearRect = new Rectangle(w - pad - L(22), y - L(2), L(22), L(22));
            Draw.Centered(g, "⚙", f, Theme.Fg2, gearRect.X + L(11), gearRect.Y + L(11));
        }
        string? footerText = app.UpdateStatus ??
            (app.Snap is UsageSnapshot sn ? "updated " + Fmt.Clock(sn.FetchedAt) : null);
        if (footerText != null)
        {
            using var f = Fnt(9.5);
            using var b = new SolidBrush(Theme.Fg3);
            using var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            int tx = pad + L(62);
            g.DrawString(footerText, f, b, new RectangleF(tx, y + L(3), gearRect.Left - L(6) - tx, L(16)), sf);
        }
    }

    void RingGauge(Graphics g, LimitEntry entry, float cx, int top, int ring, string label, int textW)
    {
        Draw.Ring(g, new RectangleF(cx - ring / 2f, top, ring, ring), ring * 0.1f, entry.Percent);
        using (var f = Fnt(25, FontStyle.Bold))
            Draw.Centered(g, Math.Round(entry.Percent).ToString(), f, Theme.Fg, cx, top + ring / 2f - L(5));
        using (var f = Fnt(11))
            Draw.Centered(g, "%", f, Theme.Fg2, cx, top + ring / 2f + L(14));
        using (var f = Fnt(11, FontStyle.Bold))
            Draw.Centered(g, label, f, Theme.Fg, cx, top + ring + L(6 + 7));
        using (var f = Fnt(9.5))
        using (var b = new SolidBrush(Theme.Fg2))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            g.DrawString(Fmt.ResetText(entry.ResetsAt), f, b,
                new RectangleF(cx - textW / 2f, top + ring + L(6 + 15 + 3), textW, L(28)), sf);
    }

    void Sparkline(Graphics g, List<HistoryPoint> pts, RectangleF rect)
    {
        double t0 = pts[0].T.ToUnixTimeSeconds(), t1 = pts[^1].T.ToUnixTimeSeconds();
        double span = Math.Max(t1 - t0, 1);
        PointF Pos(HistoryPoint p) => new(
            (float)(rect.X + (p.T.ToUnixTimeSeconds() - t0) / span * rect.Width),
            (float)(rect.Bottom - Math.Min(p.S, 100) / 100 * (rect.Height - 2) - 1));

        var line = pts.Select(Pos).ToArray();
        var color = Sev.Of(pts[^1].S);
        using (var area = new GraphicsPath())
        {
            area.AddLine(line[0].X, rect.Bottom, line[0].X, line[0].Y);
            area.AddLines(line);
            area.AddLine(line[^1].X, line[^1].Y, line[^1].X, rect.Bottom);
            using var grad = new LinearGradientBrush(rect, Color.FromArgb(64, color),
                Color.FromArgb(0, color), LinearGradientMode.Vertical);
            g.FillPath(grad, area);
        }
        using (var pen = new Pen(color, 1.5f)) g.DrawLines(pen, line);
        using (var b = new SolidBrush(color))
            g.FillEllipse(b, line[^1].X - 2.5f, line[^1].Y - 2.5f, 5, 5);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = refreshRect.Contains(e.Location) || gaugeRect.Contains(e.Location) ||
                 gearRect.Contains(e.Location) || signInRect.Contains(e.Location) ||
                 updateRect.Contains(e.Location)
                 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (refreshRect.Contains(e.Location)) app.RefreshNow();
        else if (signInRect.Contains(e.Location)) BeginInvoke(app.SignIn);
        else if (updateRect.Contains(e.Location)) app.OpenReleasePage();
        else if (gaugeRect.Contains(e.Location)) app.ToggleFloating();
        else if (gearRect.Contains(e.Location))
        {
            gearMenuOpen = true;
            var menu = app.BuildMenu(includeRefresh: false);
            menu.Closed += (_, _) =>
            {
                gearMenuOpen = false;
                if (!ContainsFocus) Hide();
                BeginInvoke(menu.Dispose);   // built per click — don't leak it
            };
            menu.Show(this, gearRect.Left, gearRect.Bottom);
        }
    }

    public void Refresh(bool resize)
    {
        if (!Visible) return;
        if (resize)
        {
            var top = Top; var h = Relayout();
            var wa = Screen.FromControl(this).WorkingArea;
            int w = FlyoutW();
            Bounds = new Rectangle(Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - w)),
                                   Math.Max(wa.Top, Top + Height - h), w, h);
        }
        Invalidate();
    }
}

// ───────────────────────────── Floating desktop gauge ─────────────────────────────

class FloatForm : Form
{
    readonly App app;
    readonly ToolTip tip = new();

    public FloatForm(App app)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        Opacity = 0.94;
        // Dragging starts from OnMouseDown (WM_NCLBUTTONDOWN) so the client area
        // stays HTCLIENT and the tooltip sees mouse moves; no caption means a
        // double-click can't maximize, but keep these off anyway.
        MaximizeBox = false;
        MinimizeBox = false;
        Win32.RoundCorners(this);
    }

    float F => DeviceDpi / 96f;
    int L(double logical) => (int)Math.Round(logical * F);
    Font Fnt(double px, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", (float)(px * F), style, GraphicsUnit.Pixel);

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            return p;
        }
    }

    // Drag anywhere: hand the left-button press to the native move loop as a
    // caption press. WM_EXITSIZEMOVE still fires when it ends, so the position saves.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Win32.ReleaseCapture();
        Win32.SendMessage(Handle, Win32.WM_NCLBUTTONDOWN, Win32.HTCAPTION, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_EXITSIZEMOVE) { S.FloatX = Left; S.FloatY = Top; }
        base.WndProc(ref m);
    }

    public void ShowAtSavedSpot()
    {
        Relayout();
        int x = S.FloatX, y = S.FloatY;
        // Clamp against the monitor the gauge was saved on, not the primary —
        // otherwise a gauge parked on a second screen snaps back on restart.
        var screen = (x == int.MinValue ? Screen.PrimaryScreen!
                                        : Screen.FromPoint(new Point(x, y))).WorkingArea;
        if (x == int.MinValue) { x = screen.Right - Width - L(30); y = screen.Top + L(36); }
        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - Width));
        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - Height));
        Location = new Point(x, y);
        Show();
    }

    public void Relayout()
    {
        using var g = CreateGraphics();
        using var f9 = Fnt(9);
        var reset = Fmt.ResetText(app.Snap?.Session?.ResetsAt);
        int rings = app.Snap is UsageSnapshot rs ? Gauges.Canonical(rs).Count : 2;
        var style = S.FloatStyle;
        if (style == "rings")
        {
            Size = new Size(L(128), L(128));
            using var disc = new GraphicsPath();
            disc.AddEllipse(0, 0, Width, Height);
            var old = Region;
            Region = new Region(disc);
            old?.Dispose();
        }
        else if (style == "square")
        {
            var old = Region;
            Region = null;
            old?.Dispose();
            var textW = (int)Math.Ceiling(g.MeasureString(reset, f9).Width);
            int w = Math.Max(L(34 * rings + 16 * (rings - 1)), textW) + L(28);
            Size = new Size(w, L(10 + 34 + 12 + 7 + 12 + 10));
        }
        else
        {
            var old = Region;
            Region = null;
            old?.Dispose();
            Size = new Size(L(14 + (34 + 14) * rings + 140 + 14), L(66));
        }
        tip.SetToolTip(this, TooltipText());
        Invalidate();
    }

    // One line per limit, shown as the form's hover tooltip in every style.
    string TooltipText()
    {
        var snap = app.Snap;
        if (snap == null) return "";
        var lines = new List<string>();
        if (snap.Session is LimitEntry s)
            lines.Add($"Session {Math.Round(s.Percent)}% · {Fmt.ResetText(s.ResetsAt)}");
        if (snap.WeeklyAll is LimitEntry w)
            lines.Add($"Week {Math.Round(w.Percent)}% · {Fmt.ResetText(w.ResetsAt)}");
        if (snap.PrimaryModel is LimitEntry m)
            lines.Add($"{UsageSnapshot.ModelName(m)} {Math.Round(m.Percent)}% · {Fmt.ResetText(m.ResetsAt)}");
        return string.Join("\n", lines);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Bg);
        var style = S.FloatStyle;
        using (var border = new Pen(Theme.Border))
        {
            if (style == "rings") g.DrawEllipse(border, 0.5f, 0.5f, Width - 1f, Height - 1f);
            else g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        var snap = app.Snap;
        if (snap == null)
        {
            using var f = Fnt(10);
            Draw.Centered(g, app.ErrorText ?? "Claude Meter…", f, Theme.Fg2, Width / 2f, Height / 2f);
            return;
        }

        var reset = Fmt.ResetText(snap.Session?.ResetsAt);
        if (style == "rings")
        {
            float cx = Width / 2f, cy = Height / 2f;
            float pen = L(9);
            void R(double dia, double pct)
            {
                float d = L(dia);
                Draw.Ring(g, new RectangleF(cx - d / 2f, cy - d / 2f, d, d), pen, pct, 1.6f * F);
            }
            // Visible gauges outside in (week, model, session); diameters 108 / 84 / 60 by position.
            var order = Gauges.Canonical(snap);
            double Pct(Gauge k) => Gauges.Entry(snap, k)?.Percent ?? 0;
            double[] dias = { 108, 84, 60 };
            for (int i = 0; i < order.Count; i++) R(dias[i], Pct(order[i]));
            // Numbers top to bottom: outer to inner by default, reversed when ringsCentre = "session".
            var numbers = new List<Gauge>(order);
            if (S.RingsCentre == "session") numbers.Reverse();
            double[] sizes = numbers.Count switch { 3 => new[] { 19.0, 15, 12 }, 2 => new[] { 19.0, 13 }, _ => new[] { 22.0 } };
            double[] offs = numbers.Count switch { 3 => new[] { -14.0, 1, 14 }, 2 => new[] { -7.0, 8 }, _ => new[] { 0.0 } };
            for (int i = 0; i < numbers.Count; i++)
            {
                double v = Pct(numbers[i]);
                using var fi = Fnt(sizes[i], FontStyle.Bold);
                Draw.CenteredOutlined(g, Math.Round(v).ToString(), fi, Sev.Of(v), L(1), cx, cy + L(offs[i]));
            }
        }
        else if (style == "square")
        {
            var vis = Gauges.Display(snap);
            for (int i = 0; i < vis.Count; i++)
                MiniRing(g, vis[i], snap, Width / 2f + (i - (vis.Count - 1) / 2f) * L(50), L(10));
            using var f = Fnt(9);
            Draw.Centered(g, reset, f, Theme.Fg2, Width / 2f, Height - L(16));
        }
        else
        {
            var vis = Gauges.Display(snap);
            for (int i = 0; i < vis.Count; i++)
                MiniRing(g, vis[i], snap, L(14 + (34 + 14) * i + 17), L(10));
            float tx = L(14 + (34 + 14) * vis.Count);
            using (var f = Fnt(10, FontStyle.Bold))
            using (var b = new SolidBrush(Theme.Fg2))
                g.DrawString("Claude", f, b, tx, L(12));
            using (var f = Fnt(9))
            using (var b = new SolidBrush(Theme.Fg2))
                g.DrawString(reset, f, b, new RectangleF(tx, L(27), L(140), L(28)));
        }
    }

    void MiniRing(Graphics g, Gauge kind, UsageSnapshot snap, float cx, int top)
    {
        var entry = Gauges.Entry(snap, kind);
        string tag = kind switch
        {
            Gauge.Session => "5 h",
            Gauge.Week => "week",
            _ => entry != null ? UsageSnapshot.ModelName(entry).ToLowerInvariant() : "",
        };
        double pct = entry?.Percent ?? 0;
        int size = L(34);
        Draw.Ring(g, new RectangleF(cx - size / 2f, top, size, size), L(3.5), pct);
        using (var f = Fnt(11, FontStyle.Bold))
            Draw.Centered(g, Math.Round(pct).ToString(), f, Theme.Fg, cx, top + size / 2f);
        using (var f = Fnt(8))
            Draw.Centered(g, tag, f, Theme.Fg3, cx, top + size + L(6));
    }
}

// ───────────────────────────── Win32 helpers ─────────────────────────────

static class Win32
{
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_NCHITTEST = 0x84;
    public const int WM_EXITSIZEMOVE = 0x232;
    public const int WM_NCLBUTTONDOWN = 0xA1;
    public static readonly IntPtr HTCAPTION = 2;

    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] public static extern bool FreeConsole();

    // Win11 rounded corners; harmless no-op on Win10.
    public static void RoundCorners(Form f)
    {
        try
        {
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(f.Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch { }
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// ───────────────────────────── Launch at login ─────────────────────────────

static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Claude Meter") != null;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Claude Meter", $"\"{Application.ExecutablePath}\"");
            else k.DeleteValue("Claude Meter", false);
        }
    }
}

// ───────────────────────────── Claude Code sign-in ─────────────────────────────
// Recovery from a revoked refresh token: launch Claude Code's own
// `claude auth login` in a visible console and refresh once it writes new
// credentials. The meter never implements OAuth itself.

static class ClaudeCli
{
    public const string InstallCommand = "winget install Anthropic.ClaudeCode";
    const string InstallPage = "https://code.claude.com/docs/en/setup";

    static bool busy;   // a sign-in watch or the not-found dialog is active

    public static string? FindClaude()
    {
        var over = Environment.GetEnvironmentVariable("CLAUDE_METER_CLAUDE_EXE");
        if (!string.IsNullOrEmpty(over)) return File.Exists(over) ? over : null;

        try
        {
            var psi = new ProcessStartInfo("where.exe", "claude")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (p.WaitForExit(5000) && outTask.Wait(2000))
                {
                    foreach (var raw in outTask.Result.Split('\n'))
                    {
                        var line = raw.Trim();
                        if ((line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                             line.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) &&
                            File.Exists(line)) return line;
                    }
                }
                else { try { p.Kill(); } catch { } }
            }
        }
        catch { }

        string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var candidates = new[]
        {
            Path.Combine(Env(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "claude.exe"),
            Path.Combine(Env(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
            Path.Combine(Env(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void SignIn(Action refresh)
    {
        if (busy) return;
        var exe = FindClaude();
        if (exe == null)
        {
            busy = true;
            try { using var d = new NotFoundDialog(InstallCommand, InstallPage); d.ShowDialog(); }
            finally { busy = false; }
            return;
        }

        Process? proc;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                UseShellExecute = true, FileName = exe, Arguments = "auth login",
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Claude Meter");
            return;
        }

        busy = true;
        var started = DateTime.UtcNow;
        var lastWrite = File.GetLastWriteTimeUtc(Creds.CredsPath);
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        void Finish()
        {
            timer.Stop();
            timer.Dispose();
            proc?.Dispose();
            busy = false;
        }
        timer.Tick += (_, _) =>
        {
            var now = File.GetLastWriteTimeUtc(Creds.CredsPath);
            if (now != lastWrite) { lastWrite = now; refresh(); }

            bool exited = false;
            try { exited = proc == null || proc.HasExited; } catch { exited = true; }
            if (exited)
            {
                int code = -1;
                try { code = proc?.ExitCode ?? -1; } catch { }
                Finish();
                if (code == 0) refresh();
            }
            else if (DateTime.UtcNow - started > TimeSpan.FromMinutes(10)) Finish();
        };
        timer.Start();
    }
}

class NotFoundDialog : Form
{
    public NotFoundDialog(string installCommand, string installPage)
    {
        Text = "Claude Code not found";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = Theme.Bg; ForeColor = Theme.Fg;
        Font = new Font("Segoe UI", 9.5f);
        // Size from content, not fixed pixels, so it is right at any DPI.
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        int px(int logical) => (int)Math.Round(logical * DeviceDpi / 96f);
        Padding = new Padding(px(16));

        var body = new Label
        {
            Text = "Claude Meter reads Claude Code's sign-in, but the claude command isn't installed on this PC.\n\n" +
                   "Install it from PowerShell:\n" + installCommand + "\n\n" +
                   "Then click \"Sign in to Claude Code…\" again.",
            AutoSize = true, MaximumSize = new Size(px(420), 0),
            Margin = new Padding(0, 0, 0, px(14)),
        };
        Button Btn(string text, EventHandler click)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(px(6), px(2), px(6), px(2)),
                Margin = new Padding(0, 0, px(10), 0),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Track, ForeColor = Theme.Fg,
            };
            b.FlatAppearance.BorderColor = Theme.Border;
            b.Click += click;
            return b;
        }
        var copy = Btn("Copy command", (_, _) =>
        {
            try { Clipboard.SetText(installCommand); } catch { }
        });
        var open = Btn("Open install page", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(installPage) { UseShellExecute = true }); } catch { }
        });
        var close = Btn("Close", (_, _) => Close());
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Margin = Padding.Empty,
        };
        buttons.Controls.AddRange(new Control[] { copy, open, close });
        var layout = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Dock = DockStyle.Fill,
        };
        layout.Controls.Add(body);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        CancelButton = close;
    }
}

// The default check glyph is an unscaled bitmap that is clipped to a fragment at >100% DPI in the
// top-level tray strip. Draw a DPI-scaled box instead (same size on every strip), and give every
// menu a check column of matching width.
sealed class CheckMarginRenderer : ToolStripProfessionalRenderer
{
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();

    public static void Apply(ToolStripDropDownMenu menu)
    {
        int h = (int)Math.Round(16 * GetDpiForSystem() / 96.0);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        menu.ImageScalingSize = new Size(h, h);
        // The top-level strip sizes its check-only column from a 16px bitmap, narrower than submenus; an
        // empty image column widens the text indent to match.
        if (menu is ContextMenuStrip) menu.ShowImageMargin = true;
        menu.Renderer = new CheckMarginRenderer();
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        int side = (int)Math.Round(15 * GetDpiForSystem() / 96.0);
        var box = new Rectangle(e.ImageRectangle.X, (e.Item.Height - side) / 2, side - 1, side - 1);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(ColorTable.CheckBackground)) g.FillRectangle(fill, box);
        using (var border = new Pen(ColorTable.ButtonSelectedBorder)) g.DrawRectangle(border, box);
        using var tick = new Pen(e.Item.ForeColor, Math.Max(1.5f, side / 10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(tick, new[]
        {
            new PointF(box.X + box.Width * 0.24f, box.Y + box.Height * 0.52f),
            new PointF(box.X + box.Width * 0.44f, box.Y + box.Height * 0.72f),
            new PointF(box.X + box.Width * 0.78f, box.Y + box.Height * 0.30f),
        });
    }
}

// ───────────────────────────── App controller ─────────────────────────────

class App : ApplicationContext
{
    public const string AuthRequiredText = "Claude Code sign-in has expired or been revoked.";

    public UsageSnapshot? Snap;
    public string? Plan;
    public string? ErrorText;
    public bool AuthRequired { get; private set; }
    public bool Refreshing;
    public readonly HistoryStore History = new();
    public Release? AvailableUpdate;
    public string? UpdateStatus;      // transient text for manual checks

    BalloonKind lastBalloon = BalloonKind.Other;
    readonly NotifyIcon tray;
    readonly FlyoutForm flyout;
    readonly FloatForm floatForm;
    readonly System.Windows.Forms.Timer pollTimer;
    readonly System.Windows.Forms.Timer tickTimer;
    readonly System.Windows.Forms.Timer updateTimer;        // every 24 h
    readonly System.Windows.Forms.Timer updateStartTimer;   // one-shot, 10 s after launch
    readonly System.Windows.Forms.Timer statusTimer;        // clears UpdateStatus after 6 s

    public App()
    {
        Theme.Refresh();
        tray = new NotifyIcon { Visible = true, Text = "Claude Meter" };
        tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
        // Route a balloon click by the kind of the balloon shown last.
        tray.BalloonTipClicked += (_, _) =>
        {
            switch (lastBalloon)
            {
                case BalloonKind.SignIn: SignIn(); break;
                case BalloonKind.Update: OpenReleasePage(); break;
            }
        };
        tray.ContextMenuStrip = BuildMenu(includeRefresh: true);

        flyout = new FlyoutForm(this);
        floatForm = new FloatForm(this);

        pollTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        pollTimer.Tick += (_, _) => RefreshNow();
        pollTimer.Start();

        // Countdown texts drift; repaint visible surfaces every 30 s.
        tickTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        tickTimer.Tick += (_, _) =>
        {
            if (flyout.Visible) flyout.Invalidate();
            if (floatForm.Visible) floatForm.Invalidate();
        };
        tickTimer.Start();

        updateStartTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        updateStartTimer.Tick += (_, _) =>
        {
            updateStartTimer.Stop();
            if (S.AutoUpdateCheck) CheckForUpdates(manual: false);
        };
        updateTimer = new System.Windows.Forms.Timer { Interval = 24 * 60 * 60 * 1000 };
        updateTimer.Tick += (_, _) => { if (S.AutoUpdateCheck) CheckForUpdates(manual: false); };
        statusTimer = new System.Windows.Forms.Timer { Interval = 6_000 };
        statusTimer.Tick += (_, _) =>
        {
            statusTimer.Stop();
            UpdateStatus = null;
            flyout.Refresh(resize: false);
        };
        if (S.AutoUpdateCheck) { updateStartTimer.Start(); updateTimer.Start(); }

        S.Changed += OnSettingsChanged;
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            Theme.Refresh();
            UpdateTray();
            flyout.Invalidate();
            floatForm.Invalidate();
        };

        UpdateTray();
        RefreshNow();
        if (S.ShowFloating) floatForm.ShowAtSavedSpot();
    }

    string lastStyle = S.FloatStyle;
    string lastSel = SelKey();
    static string SelKey() => $"{S.ShowSession}{S.ShowWeek}{S.ShowModel}";
    void OnSettingsChanged()
    {
        if (S.AutoUpdateCheck) { if (!updateTimer.Enabled) updateTimer.Start(); }
        else { updateTimer.Stop(); updateStartTimer.Stop(); }
        UpdateTray();
        if (SelKey() != lastSel)
        {
            lastSel = SelKey();
            lastStyle = S.FloatStyle;
            if (floatForm.Visible) floatForm.Relayout();
            flyout.Refresh(resize: true);
        }
        else if (S.FloatStyle != lastStyle)
        {
            lastStyle = S.FloatStyle;
            if (floatForm.Visible) floatForm.Relayout();
        }
        else if (floatForm.Visible) floatForm.Invalidate();   // e.g. rings-centre change
    }

    public async void RefreshNow()
    {
        if (Refreshing) return;
        Refreshing = true;
        flyout.Invalidate();
        try
        {
            var (snap, plan) = await UsageAPI.FetchUsage();
            Snap = snap; Plan = plan; ErrorText = null; AuthRequired = false;
            if (snap.Session is LimitEntry s && snap.WeeklyAll is LimitEntry w)
                History.Record(s.Percent, w.Percent);
            if (Notifier.Check(snap, tray)) lastBalloon = BalloonKind.UsageWarning;
        }
        catch (AuthRequiredException)
        {
            ErrorText = AuthRequiredText;
            if (!AuthRequired)
            {
                lastBalloon = BalloonKind.SignIn;
                tray.ShowBalloonTip(10000, "Claude Meter can't sign in",
                                    "Click to sign in to Claude Code.", ToolTipIcon.Warning);
            }
            AuthRequired = true;
        }
        catch (ApiException ex) { ErrorText = ex.Message; }
        catch (Exception ex) { ErrorText = "Usage request failed: " + ex.Message; }
        finally { Refreshing = false; }
        UpdateTray();
        flyout.Refresh(resize: true);
        if (floatForm.Visible) { floatForm.Relayout(); }
    }

    public async void CheckForUpdates(bool manual)
    {
        string? status = null;
        try
        {
            var r = await UpdateChecker.Latest();
            if (r != null && UpdateChecker.IsNewer(r.Version, AppVersion.Current))
            {
                AvailableUpdate = r;
                if (manual || S.LastNotifiedUpdate != r.Tag)
                {
                    lastBalloon = BalloonKind.Update;
                    tray.ShowBalloonTip(10000, $"Claude Meter {r.Version} is available",
                        $"You have {AppVersion.Current}. Open the tray menu to update.", ToolTipIcon.Info);
                    S.LastNotifiedUpdate = r.Tag;
                }
            }
            else
            {
                AvailableUpdate = null;
                if (manual) status = $"Up to date (v{AppVersion.Current})";
            }
        }
        catch (Exception ex)
        {
            if (manual) status = "Couldn't check: " + ex.Message;
        }
        if (status != null)
        {
            UpdateStatus = status;
            statusTimer.Stop();
            statusTimer.Start();
        }
        flyout.Refresh(resize: true);
    }

    public void OpenReleasePage()
    {
        if (AvailableUpdate is Release r && Uri.TryCreate(r.Url, UriKind.Absolute, out var u) &&
            u.Scheme == Uri.UriSchemeHttps)
        {
            try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
        }
    }

    LimitEntry? ChosenLimit() => S.TrayMetric switch
    {
        "session" => Snap?.Session,
        "week" => Snap?.WeeklyAll,
        "model" => Snap?.PrimaryModel ?? Snap?.Limits.OrderByDescending(l => l.Percent).FirstOrDefault(),
        _ => Snap?.Limits.OrderByDescending(l => l.Percent).FirstOrDefault(),
    };

    void UpdateTray()
    {
        var old = tray.Icon;
        tray.Icon = TrayIconRenderer.Make(ChosenLimit()?.Percent, S.TrayShowPct,
                                          error: Snap == null && ErrorText != null);
        old?.Dispose();
        var tip = Snap != null
            ? string.Join("\n", Snap.Limits.Select(l => $"{l.Label}: {Math.Round(l.Percent)}%"))
            : (ErrorText ?? "Claude Meter");
        tray.Text = tip.Length <= 127 ? tip : tip[..126] + "…";
    }

    void ToggleFlyout()
    {
        if (flyout.Visible) { flyout.Hide(); return; }
        if (Snap == null || (DateTimeOffset.Now - Snap.FetchedAt).TotalSeconds > 30) RefreshNow();
        flyout.ShowNearTray();
    }

    public void SignIn() => ClaudeCli.SignIn(RefreshNow);

    public void ToggleFloating()
    {
        if (floatForm.Visible) { floatForm.Hide(); S.ShowFloating = false; }
        else { floatForm.ShowAtSavedSpot(); S.ShowFloating = true; }
    }

    // One menu serves both the tray right-click and the flyout's gear button.
    public ContextMenuStrip BuildMenu(bool includeRefresh)
    {
        var menu = new ContextMenuStrip();
        CheckMarginRenderer.Apply(menu);
        if (includeRefresh)
        {
            menu.Items.Add("Refresh now", null, (_, _) => RefreshNow());
            menu.Items.Add("Sign in to Claude Code…", null, (_, _) => SignIn());
        }

        var floatItem = new ToolStripMenuItem("Desktop gauge") { CheckOnClick = false };
        floatItem.Click += (_, _) => ToggleFloating();

        // Gauges: which limits are drawn. The last one on cannot be switched off.
        var gauges = new ToolStripMenuItem("Gauges");
        ToolStripMenuItem GaugeItem(string text, Gauge k) => new(text, null, (_, _) =>
        {
            bool on = Gauges.IsOn(k);
            if (on && Gauges.OnCount <= 1) return;
            switch (k)
            {
                case Gauge.Session: S.ShowSession = !on; break;
                case Gauge.Week: S.ShowWeek = !on; break;
                default: S.ShowModel = !on; break;
            }
        });
        var gSession = GaugeItem("Session (5 h)", Gauge.Session);
        var gWeek = GaugeItem("Week (all models)", Gauge.Week);
        var gModel = GaugeItem("Model week", Gauge.Model);
        gauges.DropDownItems.AddRange(new ToolStripItem[] { gSession, gWeek, gModel });

        var style = new ToolStripMenuItem("Gauge style");
        var oneLine = new ToolStripMenuItem("One line", null, (_, _) => S.FloatStyle = "line");
        var square = new ToolStripMenuItem("Square", null, (_, _) => S.FloatStyle = "square");
        var rings = new ToolStripMenuItem("Rings", null, (_, _) => S.FloatStyle = "rings");
        style.DropDownItems.AddRange(new ToolStripItem[] { oneLine, square, rings });

        var centre = new ToolStripMenuItem("Rings centre");
        var cWeek = new ToolStripMenuItem("Week largest", null, (_, _) => S.RingsCentre = "week");
        var cSession = new ToolStripMenuItem("Session largest", null, (_, _) => S.RingsCentre = "session");
        centre.DropDownItems.AddRange(new ToolStripItem[] { cWeek, cSession });

        var metric = new ToolStripMenuItem("Tray icon shows");
        var mWorst = new ToolStripMenuItem("Worst limit", null, (_, _) => S.TrayMetric = "worst");
        var mSession = new ToolStripMenuItem("Session (5 h)", null, (_, _) => S.TrayMetric = "session");
        var mWeek = new ToolStripMenuItem("Week (all models)", null, (_, _) => S.TrayMetric = "week");
        var mModel = new ToolStripMenuItem("Model week", null, (_, _) => S.TrayMetric = "model");
        metric.DropDownItems.AddRange(new ToolStripItem[] { mWorst, mSession, mWeek, mModel });

        var pctItem = new ToolStripMenuItem("Number in tray icon");
        pctItem.Click += (_, _) => S.TrayShowPct = !S.TrayShowPct;

        var warn = new ToolStripMenuItem("Warn at");
        var wOff = new ToolStripMenuItem("Off", null, (_, _) => S.WarnThreshold = 0);
        var w80 = new ToolStripMenuItem("80%", null, (_, _) => S.WarnThreshold = 80);
        var w90 = new ToolStripMenuItem("90%", null, (_, _) => S.WarnThreshold = 90);
        var w95 = new ToolStripMenuItem("95%", null, (_, _) => S.WarnThreshold = 95);
        warn.DropDownItems.AddRange(new ToolStripItem[] { wOff, w80, w90, w95 });

        var autoUpd = new ToolStripMenuItem("Check for updates automatically");
        autoUpd.Click += (_, _) => S.AutoUpdateCheck = !S.AutoUpdateCheck;
        var checkNow = new ToolStripMenuItem("Check for updates…", null, (_, _) => CheckForUpdates(manual: true));
        var updateItem = new ToolStripMenuItem("Update…", null, (_, _) => OpenReleasePage()) { Visible = false };

        var login = new ToolStripMenuItem("Launch at login");
        login.Click += (_, _) => LoginItem.Enabled = !LoginItem.Enabled;

        menu.Items.AddRange(new ToolStripItem[]
        {
            floatItem, gauges, style, centre, new ToolStripSeparator(),
            metric, pctItem, warn, new ToolStripSeparator(),
            autoUpd, checkNow, login, new ToolStripSeparator(),
            updateItem,
            new ToolStripMenuItem("Quit Claude Meter", null, (_, _) => Quit()),
        });

        // Submenus are separate drop-downs; give them the same dedicated check column.
        foreach (var top in menu.Items.OfType<ToolStripMenuItem>())
            if (top.DropDown is ToolStripDropDownMenu dd) CheckMarginRenderer.Apply(dd);

        menu.Opening += (_, _) =>
        {
            floatItem.Checked = floatForm.Visible;
            foreach (var (item, k) in new[] { (gSession, Gauge.Session), (gWeek, Gauge.Week), (gModel, Gauge.Model) })
            {
                item.Checked = Gauges.IsOn(k);
                item.Enabled = !(item.Checked && Gauges.OnCount <= 1);
            }
            var fs = S.FloatStyle;
            oneLine.Checked = fs != "square" && fs != "rings";
            square.Checked = fs == "square";
            rings.Checked = fs == "rings";
            cWeek.Checked = S.RingsCentre != "session";
            cSession.Checked = S.RingsCentre == "session";
            mWorst.Checked = S.TrayMetric == "worst";
            mSession.Checked = S.TrayMetric == "session";
            mWeek.Checked = S.TrayMetric == "week";
            mModel.Checked = S.TrayMetric == "model";
            pctItem.Checked = S.TrayShowPct;
            wOff.Checked = S.WarnThreshold <= 0;
            w80.Checked = S.WarnThreshold == 80;
            w90.Checked = S.WarnThreshold == 90;
            w95.Checked = S.WarnThreshold == 95;
            login.Checked = LoginItem.Enabled;
            autoUpd.Checked = S.AutoUpdateCheck;
            updateItem.Visible = AvailableUpdate != null;
            if (AvailableUpdate is Release ur) updateItem.Text = $"Update to v{ur.Version}…";
        };
        return menu;
    }

    void Quit()
    {
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }
}

// ───────────────────────────── Entry point ─────────────────────────────

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // `--once`: headless smoke test — fetch and print, no UI.
        if (args.Contains("--once"))
        {
            Win32.AttachConsole(-1);
            try
            {
                var (snap, plan) = UsageAPI.FetchUsage().GetAwaiter().GetResult();
                Console.WriteLine();
                Console.WriteLine($"plan: {plan ?? "?"}");
                foreach (var l in snap.Limits)
                {
                    var reset = l.ResetsAt is DateTimeOffset r
                        ? Fmt.DayMonth(r) + " " + Fmt.Clock(r) : "—";
                    Console.WriteLine($"{l.Label,-22} {l.Percent,5:F1}%  resets {reset}");
                }
            }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); }
            Win32.FreeConsole();
            return;
        }

        // `--check-update`: print current/latest version, no UI, no mutex. Exit 2 on error.
        if (args.Contains("--check-update"))
        {
            Win32.AttachConsole(-1);
            try
            {
                var r = UpdateChecker.Latest().GetAwaiter().GetResult();
                bool newer = r != null && UpdateChecker.IsNewer(r.Version, AppVersion.Current);
                Console.WriteLine();
                Console.WriteLine($"current {AppVersion.Current}, latest {r?.Tag ?? "none"}, newer: {(newer ? "yes" : "no")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                Environment.ExitCode = 2;
            }
            Win32.FreeConsole();
            return;
        }

        // `--find-claude`: print the resolved claude path (or "not found"), no UI, no mutex.
        if (args.Contains("--find-claude"))
        {
            Win32.AttachConsole(-1);
            Console.WriteLine(ClaudeCli.FindClaude() ?? "not found");
            Win32.FreeConsole();
            return;
        }

        using var mutex = new Mutex(true, "ClaudeMeterSingleInstance", out bool isNew);
        if (!isNew) return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new App());
    }
}
