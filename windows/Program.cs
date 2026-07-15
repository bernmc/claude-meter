// Claude Meter (Windows) — system tray + floating desktop gauge for Claude plan
// usage. Reads Claude Code's OAuth credentials from %USERPROFILE%\.claude\
// .credentials.json, refreshes the token when expired (writing it back so
// Claude Code stays signed in), and polls the same endpoint the Claude app's
// Settings → Usage screen uses.
//
// Build with build.ps1. Single-file on purpose — same pattern as the macOS
// version (macos/main.swift), which this mirrors section by section.

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
}

static class Sev
{
    // Traffic-light thresholds; API "severity" only says normal/warning so we
    // derive finer bands from the percentage. Same RGB values as the mac app.
    public static Color Of(double pct) => pct switch
    {
        < 50 => Color.FromArgb(56, 184, 107),
        < 75 => Color.FromArgb(224, 178, 8),
        < 90 => Color.FromArgb(245, 140, 36),
        _    => Color.FromArgb(230, 66, 54),
    };
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
    public static bool FloatSquare    { get => Get("floatSquare", false);  set => Set("floatSquare", value); }
    public static string TrayMetric   { get => Get("trayMetric", "worst"); set => Set("trayMetric", value); }
    public static bool TrayShowPct    { get => Get("trayShowPct", true);   set => Set("trayShowPct", value); }
    public static double WarnThreshold{ get => Get("warnThreshold", 90.0); set => Set("warnThreshold", value); }
    public static int FloatX          { get => Get("floatX", int.MinValue);set => Set("floatX", value); }
    public static int FloatY          { get => Get("floatY", int.MinValue);set => Set("floatY", value); }

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

    public static string CredsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

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

    public static async Task<(string token, string? plan)> ValidToken()
    {
        var creds = Creds.Read() ?? throw new ApiException(
            $"No Claude Code credentials at {Creds.CredsPath} — install Claude Code on this machine and sign in once (run `claude`).");
        if (creds.ExpiresAt is DateTimeOffset exp && exp > DateTimeOffset.Now.AddSeconds(120) &&
            creds.AccessToken is string tok)
            return (tok, creds.Subscription);

        var refresh = creds.RefreshToken ?? throw new ApiException(
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
        if (code != 200 || obj?["access_token"] is not JsonNode tokNode)
            throw new ApiException($"Token refresh failed: HTTP {code}");
        var newTok = tokNode.GetValue<string>();
        double expiresIn = 3600;
        try { if (obj["expires_in"] is JsonNode e) expiresIn = e.GetValue<double>(); } catch { }
        creds.Apply(newTok, (string?)obj["refresh_token"], expiresIn);
        creds.Write();
        return (newTok, creds.Subscription);
    }

    public static async Task<(UsageSnapshot snap, string? plan)> FetchUsage()
    {
        var (token, plan) = await ValidToken();
        var (text, code) = await Request("https://api.anthropic.com/api/oauth/usage",
            headers: new()
            {
                ["Authorization"] = "Bearer " + token,
                ["anthropic-beta"] = "oauth-2025-04-20",
            });
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

static class Notifier
{
    public static void Check(UsageSnapshot snap, NotifyIcon tray)
    {
        var threshold = S.WarnThreshold;
        if (threshold <= 0) return;
        foreach (var l in snap.Limits)
        {
            if (l.Percent >= threshold && !S.GetWarned(l.Id))
            {
                S.SetWarned(l.Id, true);
                tray.ShowBalloonTip(10000, $"Claude usage at {Math.Round(l.Percent)}%",
                                    $"{l.Label} — {Fmt.ResetText(l.ResetsAt)}", ToolTipIcon.Warning);
            }
            else if (l.Percent < threshold - 5 && S.GetWarned(l.Id))
            {
                S.SetWarned(l.Id, false);
            }
        }
    }
}

// ───────────────────────────── Shared drawing ─────────────────────────────

static class Draw
{
    public static void Ring(Graphics g, RectangleF rect, float penW, double pct)
    {
        using var track = new Pen(Theme.Track, penW);
        var r = rect; r.Inflate(-penW / 2, -penW / 2);
        g.DrawEllipse(track, r);
        float sweep = (float)(360 * Math.Min(pct, 100) / 100);
        if (sweep < 1.5f) sweep = 1.5f;
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
}

// ───────────────────────────── Tray icon rendering ─────────────────────────────
// Windows can't put text next to a tray icon the way the mac menu bar can,
// so the percentage (optionally) lives inside the ring.

static class TrayIconRenderer
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    public static Icon Make(double? pct, bool showNumber)
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
            if (showNumber)
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
    Rectangle refreshRect, gaugeRect, gearRect;
    bool gearMenuOpen;

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
        int w = L(300);
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
            y += L(84 + 6 + 15 + 3) + L(28) + L(12);          // rings + labels + 2-line sublabels
            if (snap.Scoped.Count > 0)
                y += snap.Scoped.Count * L(28) + (snap.Scoped.Count - 1) * L(8) + L(12);
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
            var sz = g.MeasureString(err, f, L(300) - 2 * pad);
            y += (int)Math.Ceiling(sz.Height) + L(12);
        }
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
            // Two big ring gauges
            int ring = L(84);
            float cxL = w / 2f - L(75), cxR = w / 2f + L(75);
            if (snap.Session is LimitEntry s) RingGauge(g, s, cxL, y, ring, "Session");
            if (snap.WeeklyAll is LimitEntry wk) RingGauge(g, wk, cxR, y, ring, "Week (all)");
            y += ring + L(6 + 15 + 3 + 28) + L(12);

            // Scoped per-model bars
            foreach (var sc in snap.Scoped)
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
            if (snap.Scoped.Count > 0) y += L(12) - L(8);

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
            y += (int)Math.Ceiling(sz.Height) + L(12);
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
        if (app.Snap is UsageSnapshot sn)
        {
            using var f = Fnt(9.5);
            using var b = new SolidBrush(Theme.Fg3);
            g.DrawString("updated " + Fmt.Clock(sn.FetchedAt), f, b, pad + L(62), y + L(3));
        }
    }

    void RingGauge(Graphics g, LimitEntry entry, float cx, int top, int ring, string label)
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
                new RectangleF(cx - L(72), top + ring + L(6 + 15 + 3), L(144), L(28)), sf);
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
                 gearRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (refreshRect.Contains(e.Location)) app.RefreshNow();
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
            Bounds = new Rectangle(Left, Math.Max(Screen.FromControl(this).WorkingArea.Top, Top + Height - h), Width, h);
        }
        Invalidate();
    }
}

// ───────────────────────────── Floating desktop gauge ─────────────────────────────

class FloatForm : Form
{
    readonly App app;

    public FloatForm(App app)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        Opacity = 0.94;
        // The whole surface reports HTCAPTION for dragging — without this a
        // double-click would maximize the gauge to full screen.
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

    // Drag anywhere; save position when the drag ends.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_NCHITTEST) { m.Result = Win32.HTCAPTION; return; }
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
        if (S.FloatSquare)
        {
            var textW = (int)Math.Ceiling(g.MeasureString(reset, f9).Width);
            int w = Math.Max(L(34 + 16 + 34), textW) + L(28);
            Size = new Size(w, L(10 + 34 + 12 + 7 + 12 + 10));
        }
        else
        {
            Size = new Size(L(14 + 34 + 14 + 34 + 14 + 140 + 14), L(66));
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Bg);
        using (var border = new Pen(Theme.Border)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var snap = app.Snap;
        if (snap == null)
        {
            using var f = Fnt(10);
            Draw.Centered(g, app.ErrorText ?? "Claude Meter…", f, Theme.Fg2, Width / 2f, Height / 2f);
            return;
        }

        var reset = Fmt.ResetText(snap.Session?.ResetsAt);
        if (S.FloatSquare)
        {
            float cx1 = Width / 2f - L(25), cx2 = Width / 2f + L(25);
            MiniRing(g, snap.Session, "5 h", cx1, L(10));
            MiniRing(g, snap.WeeklyAll, "week", cx2, L(10));
            using var f = Fnt(9);
            Draw.Centered(g, reset, f, Theme.Fg2, Width / 2f, Height - L(16));
        }
        else
        {
            MiniRing(g, snap.Session, "5 h", L(14 + 17), L(10));
            MiniRing(g, snap.WeeklyAll, "week", L(14 + 34 + 14 + 17), L(10));
            float tx = L(14 + 34 + 14 + 34 + 14);
            using (var f = Fnt(10, FontStyle.Bold))
            using (var b = new SolidBrush(Theme.Fg2))
                g.DrawString("Claude", f, b, tx, L(12));
            using (var f = Fnt(9))
            using (var b = new SolidBrush(Theme.Fg2))
                g.DrawString(reset, f, b, new RectangleF(tx, L(27), L(140), L(28)));
        }
    }

    void MiniRing(Graphics g, LimitEntry? entry, string tag, float cx, int top)
    {
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
    public static readonly IntPtr HTCAPTION = 2;

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

// ───────────────────────────── App controller ─────────────────────────────

class App : ApplicationContext
{
    public UsageSnapshot? Snap;
    public string? Plan;
    public string? ErrorText;
    public bool Refreshing;
    public readonly HistoryStore History = new();

    readonly NotifyIcon tray;
    readonly FlyoutForm flyout;
    readonly FloatForm floatForm;
    readonly System.Windows.Forms.Timer pollTimer;
    readonly System.Windows.Forms.Timer tickTimer;

    public App()
    {
        Theme.Refresh();
        tray = new NotifyIcon { Visible = true, Text = "Claude Meter" };
        tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
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

    bool lastSquare = S.FloatSquare;
    void OnSettingsChanged()
    {
        UpdateTray();
        if (S.FloatSquare != lastSquare)
        {
            lastSquare = S.FloatSquare;
            if (floatForm.Visible) floatForm.Relayout();
        }
    }

    public async void RefreshNow()
    {
        if (Refreshing) return;
        Refreshing = true;
        flyout.Invalidate();
        try
        {
            var (snap, plan) = await UsageAPI.FetchUsage();
            Snap = snap; Plan = plan; ErrorText = null;
            if (snap.Session is LimitEntry s && snap.WeeklyAll is LimitEntry w)
                History.Record(s.Percent, w.Percent);
            Notifier.Check(snap, tray);
        }
        catch (ApiException ex) { ErrorText = ex.Message; }
        catch (Exception ex) { ErrorText = "Usage request failed: " + ex.Message; }
        finally { Refreshing = false; }
        UpdateTray();
        flyout.Refresh(resize: true);
        if (floatForm.Visible) { floatForm.Relayout(); }
    }

    LimitEntry? ChosenLimit() => S.TrayMetric switch
    {
        "session" => Snap?.Session,
        "week" => Snap?.WeeklyAll,
        _ => Snap?.Limits.OrderByDescending(l => l.Percent).FirstOrDefault(),
    };

    void UpdateTray()
    {
        var old = tray.Icon;
        tray.Icon = TrayIconRenderer.Make(ChosenLimit()?.Percent, S.TrayShowPct);
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

    public void ToggleFloating()
    {
        if (floatForm.Visible) { floatForm.Hide(); S.ShowFloating = false; }
        else { floatForm.ShowAtSavedSpot(); S.ShowFloating = true; }
    }

    // One menu serves both the tray right-click and the flyout's gear button.
    public ContextMenuStrip BuildMenu(bool includeRefresh)
    {
        var menu = new ContextMenuStrip();
        if (includeRefresh) menu.Items.Add("Refresh now", null, (_, _) => RefreshNow());

        var floatItem = new ToolStripMenuItem("Desktop gauge") { CheckOnClick = false };
        floatItem.Click += (_, _) => ToggleFloating();

        var style = new ToolStripMenuItem("Gauge style");
        var oneLine = new ToolStripMenuItem("One line", null, (_, _) => S.FloatSquare = false);
        var square = new ToolStripMenuItem("Square", null, (_, _) => S.FloatSquare = true);
        style.DropDownItems.AddRange(new ToolStripItem[] { oneLine, square });

        var metric = new ToolStripMenuItem("Tray icon shows");
        var mWorst = new ToolStripMenuItem("Worst limit", null, (_, _) => S.TrayMetric = "worst");
        var mSession = new ToolStripMenuItem("Session (5 h)", null, (_, _) => S.TrayMetric = "session");
        var mWeek = new ToolStripMenuItem("Week (all models)", null, (_, _) => S.TrayMetric = "week");
        metric.DropDownItems.AddRange(new ToolStripItem[] { mWorst, mSession, mWeek });

        var pctItem = new ToolStripMenuItem("Number in tray icon");
        pctItem.Click += (_, _) => S.TrayShowPct = !S.TrayShowPct;

        var warn = new ToolStripMenuItem("Warn at");
        var wOff = new ToolStripMenuItem("Off", null, (_, _) => S.WarnThreshold = 0);
        var w80 = new ToolStripMenuItem("80%", null, (_, _) => S.WarnThreshold = 80);
        var w90 = new ToolStripMenuItem("90%", null, (_, _) => S.WarnThreshold = 90);
        var w95 = new ToolStripMenuItem("95%", null, (_, _) => S.WarnThreshold = 95);
        warn.DropDownItems.AddRange(new ToolStripItem[] { wOff, w80, w90, w95 });

        var login = new ToolStripMenuItem("Launch at login");
        login.Click += (_, _) => LoginItem.Enabled = !LoginItem.Enabled;

        menu.Items.AddRange(new ToolStripItem[]
        {
            floatItem, style, new ToolStripSeparator(),
            metric, pctItem, warn, new ToolStripSeparator(),
            login, new ToolStripSeparator(),
            new ToolStripMenuItem("Quit Claude Meter", null, (_, _) => Quit()),
        });

        menu.Opening += (_, _) =>
        {
            floatItem.Checked = floatForm.Visible;
            oneLine.Checked = !S.FloatSquare; square.Checked = S.FloatSquare;
            mWorst.Checked = S.TrayMetric == "worst";
            mSession.Checked = S.TrayMetric == "session";
            mWeek.Checked = S.TrayMetric == "week";
            pctItem.Checked = S.TrayShowPct;
            wOff.Checked = S.WarnThreshold <= 0;
            w80.Checked = S.WarnThreshold == 80;
            w90.Checked = S.WarnThreshold == 90;
            w95.Checked = S.WarnThreshold == 95;
            login.Checked = LoginItem.Enabled;
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

        using var mutex = new Mutex(true, "ClaudeMeterSingleInstance", out bool isNew);
        if (!isNew) return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new App());
    }
}
