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
using System.Text.Encodings.Web;
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
    // .NET 8: JsonNode.ToJsonString(options) with a custom options instance throws unless a TypeInfoResolver is set.
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
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
            File.WriteAllText(FilePath, data.ToJsonString(JsonOpts));
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
    // Floating gauge opacity, 0.2..1.0 (default 0.94). Applied to all three styles.
    public static double GaugeOpacity
    {
        get { double v = Get("gaugeOpacity", 0.94); return double.IsNaN(v) ? 0.94 : Math.Clamp(v, 0.2, 1.0); }
        set => Set("gaugeOpacity", double.IsNaN(value) ? 0.94 : Math.Clamp(value, 0.2, 1.0));
    }
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
    // Status file export. Path: full-path override ("" = default resolution). Enabled: absent = on.
    public static string StatusExportPath { get => Get("statusExportPath", ""); set => Set("statusExportPath", value); }
    public static bool StatusExportEnabled { get => Get("statusExportEnabled", true); set => Set("statusExportEnabled", value); }

    public static bool GetWarned(string id) => Get("warned-" + id, false);
    public static void SetWarned(string id, bool v) => Set("warned-" + id, v);
}

// ───────────────────────────── Status export ─────────────────────────────
// Writes the current usage snapshot to a JSON file on every refresh attempt so
// other local tooling can read live numbers without touching the credentials
// or Anthropic's endpoint. Same document as the macOS app (StatusExporter in
// macos/main.swift): sorted keys, ISO-8601 UTC timestamps with a trailing Z.
// Never surfaces errors to the UI.

static class StatusExporter
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    // Seconds precision, rounded to the nearest second like Foundation's ISO8601DateFormatter
    // (the API sends e.g. 01:59:59.9996, which the Mac writes as 02:00:00Z).
    static JsonNode? Iso(DateTimeOffset? d)
    {
        if (d is not DateTimeOffset v) return null;
        const long sec = TimeSpan.TicksPerSecond;
        var utc = new DateTime((v.UtcDateTime.Ticks + sec / 2) / sec * sec, DateTimeKind.Utc);
        return JsonValue.Create(utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }

    static JsonNode? LimitNode(LimitEntry? e) => e == null ? null : new JsonObject
    {
        ["percent"] = e.Percent,
        ["resets_at"] = Iso(e.ResetsAt),
    };

    static JsonObject Build(UsageSnapshot? snap, string? plan, string? error) => new()
    {
        ["checked_at"] = Iso(DateTimeOffset.UtcNow),
        ["error"] = error,
        ["fetched_at"] = snap == null ? null : Iso(snap.FetchedAt),
        ["models"] = new JsonArray((snap?.Scoped ?? new List<LimitEntry>()).Select(e => (JsonNode?)new JsonObject
        {
            ["name"] = UsageSnapshot.ModelName(e),
            ["percent"] = e.Percent,
            ["resets_at"] = Iso(e.ResetsAt),
        }).ToArray()),
        ["plan"] = plan,
        ["session"] = LimitNode(snap?.Session),
        ["weekly_all"] = LimitNode(snap?.WeeklyAll),
    };

    public static JsonObject ForSuccess(UsageSnapshot snap, string? plan) => Build(snap, plan, null);

    // Keeps every prior field; only error and checked_at change. Falls back to
    // the full schema (null fields, empty models) when no prior file is readable.
    public static JsonObject ForFailure(string message)
    {
        try
        {
            var path = ResolvePath();
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject prior)
            {
                prior["error"] = message;
                prior["checked_at"] = Iso(DateTimeOffset.UtcNow);
                return prior;
            }
        }
        catch { }
        return Build(null, null, message);
    }

    public static string Serialize(JsonObject doc) => doc.ToJsonString(Opts);

    // This PC's name lower-cased, anything but a-z0-9 replaced with '-'.
    public static string Hostname()
    {
        var chars = Environment.MachineName.ToLowerInvariant()
            .Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-').ToArray();
        return new string(chars);
    }

    // Environment first so a test can point the exporter at another profile;
    // GetFolderPath otherwise (same values in normal use).
    static string Env(string name, Environment.SpecialFolder folder) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : Environment.GetFolderPath(folder);

    // statusExportPath override -> synced folder (when the Claude_Meter project directory
    // exists; status\ is created by Write) -> %APPDATA%\Claude Meter\status. One file per machine so file sync never
    // sees two writers on one file.
    public static string ResolvePath()
    {
        if (S.StatusExportPath is { Length: > 0 } custom) return custom;
        var name = $"current-{Hostname()}.json";
        var project = Path.Combine(Env("USERPROFILE", Environment.SpecialFolder.UserProfile),
            "SynologyDrive", "AI_Context", "01-Projects", "Claude_Toolkit", "Claude_Meter");
        if (Directory.Exists(project)) return Path.Combine(project, "status", name);
        return Path.Combine(Env("APPDATA", Environment.SpecialFolder.ApplicationData),
            "Claude Meter", "status", name);
    }

    // Atomic: temp file in the same directory, then move over the destination.
    public static void Write(JsonObject doc)
    {
        try
        {
            var path = ResolvePath();
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(doc), new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* swallowed — the exporter must never crash or surface errors */ }
    }
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
    // Stroke colour of the Rings centre numbers (dark grey, full alpha).
    public static readonly Color NumberOutline = Color.FromArgb(255, 0x3A, 0x3A, 0x3A);

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

    // Filled text in `fill` with a dark-grey (NumberOutline) stroke of strokeW px centred on the glyph edge.
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
        using var pen = new Pen(NumberOutline, strokeW) { LineJoin = LineJoin.Round };
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }

    // Rings centre numbers: the stack (outer to inner = top to bottom) is laid out by ink bounds and
    // scaled so every line's ink box (plus the outline) stays inside a circle of radius fitR about (cx, cy).
    // rel = relative em size of each line (keeps the 19 : 15 : 12 proportions).
    public static void FitStack(Graphics g, IList<(string Text, Color Color, double Rel)> lines,
                                float cx, float cy, float fitR, float stroke, float maxEm)
    {
        const float BaseEm = 100f;
        using var fam = new FontFamily("Segoe UI");
        using var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
        var paths = new List<GraphicsPath>();
        var bounds = new List<RectangleF>();
        foreach (var l in lines)
        {
            var p = new GraphicsPath();
            p.AddString(l.Text, fam, (int)FontStyle.Bold, (float)(BaseEm * l.Rel), PointF.Empty, sf);
            paths.Add(p);
            bounds.Add(p.GetBounds());
        }
        double relMax = lines.Max(l => l.Rel);

        // Ink rectangles (relative to the stack centre) at scale k.
        (float x, float y, float w, float h)[] Layout(float k)
        {
            var r = new (float x, float y, float w, float h)[lines.Count];
            float gap = 0.2f * k * bounds.Max(b => b.Height);
            float total = -gap;
            for (int i = 0; i < r.Length; i++) total += bounds[i].Height * k + stroke + gap;
            float y = -total / 2f;
            for (int i = 0; i < r.Length; i++)
            {
                float w = bounds[i].Width * k + stroke, h = bounds[i].Height * k + stroke;
                r[i] = (-w / 2f, y, w, h);
                y += h + gap;
            }
            return r;
        }
        bool Fits(float k)
        {
            foreach (var r in Layout(k))
            {
                float dx = r.w / 2f, dy = Math.Max(Math.Abs(r.y), Math.Abs(r.y + r.h));
                if (dx * dx + dy * dy > fitR * fitR) return false;
            }
            return true;
        }
        float lo = 0f, hi = (float)(maxEm / (BaseEm * relMax));
        if (Fits(hi)) lo = hi;
        else for (int it = 0; it < 28; it++) { float mid = (lo + hi) / 2f; if (Fits(mid)) lo = mid; else hi = mid; }
        float scale = lo;

        var rects = Layout(scale);
        for (int i = 0; i < paths.Count; i++)
        {
            var b = bounds[i];
            using var m = new Matrix();
            m.Translate(cx, cy + rects[i].y + rects[i].h / 2f);
            m.Scale(scale, scale);
            m.Translate(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
            paths[i].Transform(m);
            using var brush = new SolidBrush(lines[i].Color);
            using var pen = new Pen(NumberOutline, stroke) { LineJoin = LineJoin.Round };
            g.FillPath(brush, paths[i]);
            g.DrawPath(pen, paths[i]);
            paths[i].Dispose();
        }
    }

    // Thin text along the centreline (radius rMid) of a ring band of width `band`: starts at 12 o'clock
    // and runs clockwise, glyph by glyph rotated to the tangent, upright on the outside of the circle.
    // Cap height = 60 % of the band; shrunk if the text would not fit in a quarter turn.
    public static void RingLabel(Graphics g, string text, float cx, float cy, float rMid, float band, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        FontFamily fam;
        fam = new FontFamily("Segoe UI");
        using (fam)
        {
            const FontStyle style = FontStyle.Regular;
            float design = fam.GetEmHeight(style);
            float capRatio = 0.70f;   // Segoe UI cap height / em
            float em = 0.60f * band / capRatio;
            using var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;

            float[] Advances(float e)
            {
                using var f = new Font(fam, e, style, GraphicsUnit.Pixel);
                var a = new float[text.Length + 1];
                for (int i = 1; i <= text.Length; i++)
                    a[i] = g.MeasureString(text.Substring(0, i), f, PointF.Empty, sf).Width;
                return a;
            }
            var adv = Advances(em);
            float cap = em * capRatio;
            float rBase = rMid - cap / 2f;
            float quarter = (float)(Math.PI / 2 * rBase) - em * 0.1f;
            if (adv[text.Length] > quarter)
            {
                float k = quarter / adv[text.Length];
                em *= k; cap = em * capRatio; rBase = rMid - cap / 2f;
                adv = Advances(em);
            }
            float baseline = em * fam.GetCellAscent(style) / design;
            using var brush = new SolidBrush(color);
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i])) continue;
                float w = adv[i + 1] - adv[i];
                float theta = (adv[i] + w / 2f) / rBase;           // radians clockwise from 12 o'clock
                using var path = new GraphicsPath();
                path.AddString(text[i].ToString(), fam, (int)style, em, PointF.Empty, sf);
                using var m = new Matrix();
                m.Translate(cx + rBase * (float)Math.Sin(theta), cy - rBase * (float)Math.Cos(theta));
                m.Rotate(theta * 180f / (float)Math.PI);
                m.Translate(-w / 2f, -baseline);
                path.Transform(m);
                g.FillPath(brush, path);
            }
        }
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
    // ShowAlways: the form is WS_EX_NOACTIVATE and never active, so the default (active-only) tooltip rarely showed.
    readonly ToolTip tip = new() { ShowAlways = true };

    public FloatForm(App app)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        // S.GaugeOpacity is applied in ApplyLayering / ApplyOpacity (line, square) and PushLayered (Rings disc).
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

    // Layering: 1 = Rings disc (UpdateLayeredWindow, per-pixel alpha), 2 = other styles (whole-window alpha = S.GaugeOpacity).
    // The layered bit is cleared and set again on every switch, so UpdateLayeredWindow stays legal after
    // SetLayeredWindowAttributes and vice versa.
    int layerMode;
    bool ringsMode;
    static byte Alpha => (byte)Math.Round(255 * S.GaugeOpacity);

    protected override void OnHandleCreated(EventArgs e)
    {
        layerMode = 0;
        base.OnHandleCreated(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Redraw();
    }

    void ApplyLayering(bool rings)
    {
        int want = rings ? 1 : 2;
        ringsMode = rings;
        if (!IsHandleCreated || layerMode == want) return;
        int ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, ex & ~Win32.WS_EX_LAYERED);
        Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_LAYERED);
        if (!rings) Win32.SetLayeredWindowAttributes(Handle, 0, Alpha, Win32.LWA_ALPHA);
        Win32.DwmFrame(this, rings);
        layerMode = want;
    }

    // Whole-window alpha for the line/square styles (the Rings disc takes it in PushLayered).
    void ApplyOpacity()
    {
        if (IsHandleCreated && layerMode == 2)
            Win32.SetLayeredWindowAttributes(Handle, 0, Alpha, Win32.LWA_ALPHA);
    }

    // Repaint whatever the current style needs.
    public void Redraw()
    {
        if (ringsMode) PushLayered();
        else { ApplyOpacity(); Invalidate(); }
    }

    // Render the Rings disc into a 32-bpp premultiplied-ARGB DIB and hand it to the window manager.
    void PushLayered()
    {
        if (!IsHandleCreated || layerMode != 1 || Width <= 0 || Height <= 0) return;
        int w = Width, h = Height;
        var bi = new Win32.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h,
            biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
        IntPtr hbm = Win32.CreateDIBSection(memDc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = Win32.SelectObject(memDc, hbm);
        try
        {
            using (var bmp = new Bitmap(w, h, w * 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, bits))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                DrawRings(g);
                g.Flush();
            }
            var size = new Win32.SIZE { cx = w, cy = h };
            var src = new Win32.POINT();
            var blend = new Win32.BLENDFUNCTION
            { BlendOp = 0 /* AC_SRC_OVER */, SourceConstantAlpha = Alpha, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            Win32.UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend,
                                      Win32.ULW_ALPHA);
        }
        finally
        {
            Win32.SelectObject(memDc, old);
            Win32.DeleteObject(hbm);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
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

    // Right-click: the same menu as the tray icon, at the cursor. The form is no-activate, so take the
    // foreground first or the menu would not close on an outside click.
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Right) return;
        var menu = app.BuildMenu(includeRefresh: true);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);   // built per click — don't leak it
        Win32.SetForegroundWindow(Handle);
        menu.Show(Cursor.Position);
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
        ApplyLayering(style == "rings");
        if (style == "rings")
        {
            Size = new Size(L(128), L(128));
        }
        else if (style == "square")
        {
            var textW = (int)Math.Ceiling(TextW(g, reset, f9));
            int w = Math.Max(L(34 * rings + 16 * (rings - 1)), textW) + L(28);
            Size = new Size(w, L(10 + 34 + 12 + 7 + 12 + 10));
        }
        else
        {
            // left pad + rings block + gap + widest text line + right pad (= left pad)
            var lines = OneLineText();
            using var f10 = Fnt(10, FontStyle.Bold);
            float textW = 0, blockH = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                var f = i == 0 ? f10 : f9;
                textW = Math.Max(textW, TextW(g, lines[i], f));
                blockH += f.GetHeight(g);
            }
            Size = new Size(L(14 + 48 * rings) + (int)Math.Ceiling(textW) + L(14),
                            Math.Max(L(66), (int)Math.Ceiling(blockH) + L(16)));
        }
        KeepOnScreen();
        tip.SetToolTip(this, TooltipText());
        Redraw();
    }

    // Tight text width (no GDI+ side bearings), so left and right padding are exact.
    static float TextW(Graphics g, string text, Font f)
    {
        using var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
        return g.MeasureString(text, f, PointF.Empty, sf).Width;
    }

    // One-line style text block: "Claude", then the reset line split into "resets in …" and the clock time.
    string[] OneLineText()
    {
        var reset = Fmt.ResetText(app.Snap?.Session?.ResetsAt);
        int i = reset.IndexOf(" · ", StringComparison.Ordinal);
        return i < 0 ? new[] { "Claude", reset }
                     : new[] { "Claude", reset.Substring(0, i), reset.Substring(i + 3) };
    }

    // A width change (selection, reset text, first snapshot) keeps the left edge; pull the form
    // back inside the working area if it now overhangs.
    void KeepOnScreen()
    {
        if (!Visible) return;
        var wa = Screen.FromControl(this).WorkingArea;
        int x = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        int y = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        if (x != Left || y != Top) Location = new Point(x, y);
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

    // Rings disc: drawn by DrawRings into the layered bitmap (PushLayered), never through WM_PAINT.
    protected override void OnPaint(PaintEventArgs e)
    {
        if (ringsMode) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Bg);
        var style = S.FloatStyle;
        using (var border = new Pen(Theme.Border))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var snap = app.Snap;
        if (snap == null)
        {
            using var f = Fnt(10);
            Draw.Centered(g, app.ErrorText ?? "Claude Meter…", f, Theme.Fg2, Width / 2f, Height / 2f);
            return;
        }

        var reset = Fmt.ResetText(snap.Session?.ResetsAt);
        if (style == "square")
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
            // Text lines stacked, vertically centred in the panel, each centred within the column
            // (column = widest line, left edge at tx).
            var lines = OneLineText();
            var fonts = lines.Select((_, i) => i == 0 ? Fnt(10, FontStyle.Bold) : Fnt(9)).ToArray();
            float blockH = fonts.Sum(f => f.GetHeight(g));
            float ty = (Height - blockH) / 2f;
            float[] lw = lines.Select((s, i) => TextW(g, s, fonts[i])).ToArray();
            float colW = lw.Max();
            using var tb = new SolidBrush(Theme.Fg2);
            using var tsf = (StringFormat)StringFormat.GenericTypographic.Clone();
            for (int i = 0; i < lines.Length; i++)
            {
                g.DrawString(lines[i], fonts[i], tb, tx + (colW - lw[i]) / 2f, ty, tsf);
                ty += fonts[i].GetHeight(g);
                fonts[i].Dispose();
            }
        }
    }

    // The Rings disc on a transparent surface: antialiased disc, rings, band labels, centre numbers.
    void DrawRings(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var bg = new SolidBrush(Theme.Bg))
            g.FillEllipse(bg, 0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var border = new Pen(Theme.Border))
            g.DrawEllipse(border, 0.5f, 0.5f, Width - 1f, Height - 1f);

        var snap = app.Snap;
        if (snap == null)
        {
            using var f = Fnt(10);
            Draw.Centered(g, app.ErrorText ?? "Claude Meter…", f, Theme.Fg2, Width / 2f, Height / 2f);
            return;
        }

        float cx = Width / 2f, cy = Height / 2f;
        float pen = L(9);
        // Visible gauges outside in (week, model, session); diameters 108 / 84 / 60 by position.
        var order = Gauges.Canonical(snap);
        double Pct(Gauge k) => Gauges.Entry(snap, k)?.Percent ?? 0;
        double[] dias = { 108, 84, 60 };
        for (int i = 0; i < order.Count; i++)
        {
            float d = L(dias[i]);
            Draw.Ring(g, new RectangleF(cx - d / 2f, cy - d / 2f, d, d), pen, Pct(order[i]), 1.6f * F);
        }
        // Band labels along each band centreline: total / model name / session.
        for (int i = 0; i < order.Count; i++)
        {
            float d = L(dias[i]);
            string label = order[i] switch
            {
                Gauge.Week => "total",
                Gauge.Session => "session",
                _ => snap.PrimaryModel is LimitEntry m && UsageSnapshot.ModelName(m).Trim().Length > 0
                         ? UsageSnapshot.ModelName(m).Trim().ToLowerInvariant() : "model",
            };
            Draw.RingLabel(g, label, cx, cy, d / 2f - pen / 2f, pen, Color.Black);
        }
        // Numbers top to bottom: outer to inner by default, reversed when ringsCentre = "session".
        // Sized to fit the innermost ring's hole with a margin of 1/10 of the hole diameter on every side.
        var numbers = new List<Gauge>(order);
        if (S.RingsCentre == "session") numbers.Reverse();
        double[] rel = numbers.Count switch { 3 => new[] { 19.0, 15, 12 }, 2 => new[] { 19.0, 13 }, _ => new[] { 22.0 } };
        var lines = numbers.Select((k, i) => (Math.Round(Pct(k)).ToString(), Sev.Of(Pct(k)), rel[i])).ToList();
        float hole = L(dias[order.Count - 1]) - 2 * pen - 2 * 0.8f * F;
        Draw.FitStack(g, lines, cx, cy, 0.4f * hole, L(1), L(46));
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
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
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

    // DWM still frames a layered window's rectangle (rounded border + shadow) around the disc.
    // off = true: no non-client rendering (frame, shadow), square corners, no border colour.
    public static void DwmFrame(Form f, bool off)
    {
        try
        {
            int policy = off ? 1 /* DWMNCRP_DISABLED */ : 0 /* DWMNCRP_USEWINDOWSTYLE */;
            DwmSetWindowAttribute(f.Handle, 2 /* DWMWA_NCRENDERING_POLICY */, ref policy, sizeof(int));
            int pref = off ? 1 /* DWMWCP_DONOTROUND */ : 2 /* DWMWCP_ROUND */;
            DwmSetWindowAttribute(f.Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
            int border = unchecked((int)(off ? 0xFFFFFFFE /* DWMWA_COLOR_NONE */ : 0xFFFFFFFF /* DWMWA_COLOR_DEFAULT */));
            DwmSetWindowAttribute(f.Handle, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
        }
        catch { }
    }

    // Layered window (Rings disc): per-pixel-alpha bitmap via UpdateLayeredWindow.
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x80000;
    public const uint LWA_ALPHA = 2;
    public const uint ULW_ALPHA = 2;
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")]
    public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE size,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
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
            if (floatForm.Visible) floatForm.Relayout();   // reset text length changes -> width follows
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
            floatForm.Redraw();
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
        else if (floatForm.Visible) floatForm.Redraw();   // e.g. rings-centre change
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
        if (S.StatusExportEnabled) ExportCurrent();
        UpdateTray();
        flyout.Refresh(resize: true);
        if (floatForm.Visible) { floatForm.Relayout(); }
    }

    // Writes the status file for the latest refresh outcome; nothing before the first one.
    void ExportCurrent()
    {
        if (ErrorText != null) StatusExporter.Write(StatusExporter.ForFailure(ErrorText));
        else if (Snap != null) StatusExporter.Write(StatusExporter.ForSuccess(Snap, Plan));
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
        ToolStripMenuItem? signInItem = null;
        if (includeRefresh)
        {
            menu.Items.Add("Refresh now", null, (_, _) => RefreshNow());
            signInItem = new ToolStripMenuItem("Sign in to Claude Code…", null, (_, _) => SignIn()) { Visible = false };
            menu.Items.Add(signInItem);
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

        var opacity = new ToolStripMenuItem("Gauge opacity");
        var opacityTrack = new TrackBar
        {
            Minimum = 20, Maximum = 100, TickFrequency = 20, SmallChange = 5, LargeChange = 20,
            Value = 94, AutoSize = false, Width = 160, Height = 32,
        };
        bool opacitySyncing = false;
        opacityTrack.ValueChanged += (_, _) =>
        {
            if (!opacitySyncing) S.GaugeOpacity = opacityTrack.Value / 100.0;
        };
        opacity.DropDownItems.Add(new ToolStripControlHost(opacityTrack) { AutoSize = false, Size = new Size(160, 32) });

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

        var statusItem = new ToolStripMenuItem("Status file");
        statusItem.Click += (_, _) =>
        {
            S.StatusExportEnabled = !S.StatusExportEnabled;
            if (S.StatusExportEnabled) ExportCurrent();
        };
        var autoUpd = new ToolStripMenuItem("Check for updates automatically");
        autoUpd.Click += (_, _) => S.AutoUpdateCheck = !S.AutoUpdateCheck;
        var checkNow = new ToolStripMenuItem("Check for updates…", null, (_, _) => CheckForUpdates(manual: true));
        var updateItem = new ToolStripMenuItem("Update…", null, (_, _) => OpenReleasePage()) { Visible = false };

        var login = new ToolStripMenuItem("Launch at login");
        login.Click += (_, _) => LoginItem.Enabled = !LoginItem.Enabled;

        menu.Items.AddRange(new ToolStripItem[]
        {
            floatItem, gauges, style, centre, opacity, new ToolStripSeparator(),
            metric, pctItem, warn, new ToolStripSeparator(),
            statusItem, autoUpd, checkNow, login, new ToolStripSeparator(),
            updateItem,
            new ToolStripMenuItem("Quit Claude Meter", null, (_, _) => Quit()),
        });

        // Submenus are separate drop-downs; give them the same dedicated check column.
        foreach (var top in menu.Items.OfType<ToolStripMenuItem>())
            if (top.DropDown is ToolStripDropDownMenu dd) CheckMarginRenderer.Apply(dd);

        menu.Opening += (_, _) =>
        {
            floatItem.Checked = floatForm.Visible;
            if (signInItem != null) signInItem.Visible = AuthRequired;   // only when sign-in is needed
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
            opacitySyncing = true;
            opacityTrack.Value = Math.Clamp((int)Math.Round(S.GaugeOpacity * 100), 20, 100);
            opacitySyncing = false;
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
            statusItem.Checked = S.StatusExportEnabled;
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

        // `--status`: one fetch, write the status file (ignores the enabled toggle), print the same
        // JSON to stdout, no UI, no mutex. Exit 1 on fetch failure.
        if (args.Contains("--status"))
        {
            Win32.AttachConsole(-1);
            JsonObject doc;
            try
            {
                var (snap, plan) = UsageAPI.FetchUsage().GetAwaiter().GetResult();
                doc = StatusExporter.ForSuccess(snap, plan);
            }
            catch (Exception ex)
            {
                doc = StatusExporter.ForFailure(ex is AuthRequiredException ? App.AuthRequiredText
                    : ex is ApiException ? ex.Message : "Usage request failed: " + ex.Message);
                Environment.ExitCode = 1;
            }
            StatusExporter.Write(doc);
            if (!Console.IsOutputRedirected) Console.WriteLine();
            Console.WriteLine(StatusExporter.Serialize(doc));
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
