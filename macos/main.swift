// Claude Meter — menu bar + floating desktop gauge for Claude plan usage.
// Reads Claude Code's OAuth credentials from the login keychain, refreshes the
// token when expired (writing it back so Claude Code stays signed in), and
// polls the same endpoint the app's Settings → Usage screen uses.
//
// Build with app/build.sh. Single-file on purpose — same pattern as WFH Timer.

import AppKit
import SwiftUI
import Combine
import ServiceManagement
import UserNotifications

enum AppVersion {
    static var current: String {
        (Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String) ?? "0"
    }
}

// MARK: - Usage model

struct LimitEntry: Identifiable, Equatable {
    let id: String            // kind + label, stable across refreshes
    let kind: String          // session | weekly_all | weekly_scoped
    let label: String
    let percent: Double
    let resetsAt: Date?
    let isActive: Bool
}

struct UsageSnapshot: Equatable {
    let fetchedAt: Date
    let limits: [LimitEntry]
    var session: LimitEntry?   { limits.first { $0.kind == "session" } }
    var weeklyAll: LimitEntry? { limits.first { $0.kind == "weekly_all" } }
    var scoped: [LimitEntry]   { limits.filter { $0.kind == "weekly_scoped" } }
    // The per-model weekly limit shown as the third ring (today "Fable").
    var primaryModel: LimitEntry? { scoped.first }

    // "Week — Fable" -> "Fable". Shared with StatusExporter.
    static func modelName(_ e: LimitEntry) -> String {
        let prefix = "Week — "
        var name = e.label
        if name.hasPrefix(prefix) { name.removeFirst(prefix.count) }
        return name
    }
    func modelName(_ e: LimitEntry) -> String { Self.modelName(e) }
}

// Which of the three limits are drawn as gauges (popover ring row and the
// three floating styles). Preferences showSession / showWeek / showModel,
// absent = on. The menu-bar metric, status export, scoped bars and sparkline
// are not affected.
enum GaugeKind: Hashable { case session, week, model }

struct GaugeSelection: Equatable {
    var session = true
    var week = true
    var model = true

    static var current: GaugeSelection {
        let d = UserDefaults.standard
        return GaugeSelection(session: d.object(forKey: "showSession") as? Bool ?? true,
                              week: d.object(forKey: "showWeek") as? Bool ?? true,
                              model: d.object(forKey: "showModel") as? Bool ?? true)
    }

    // Preview helper: letters from "swm" name the ON set.
    init(letters: String) {
        session = letters.contains("s")
        week = letters.contains("w")
        model = letters.contains("m")
    }
    init(session: Bool = true, week: Bool = true, model: Bool = true) {
        self.session = session; self.week = week; self.model = model
    }

    var onCount: Int { [session, week, model].filter { $0 }.count }

    private func visible(_ snap: UsageSnapshot, _ order: [GaugeKind]) -> [GaugeKind] {
        let v = order.filter { k in
            switch k {
            case .session: return session
            case .week:    return week
            case .model:   return model && snap.primaryModel != nil
            }
        }
        return v.isEmpty ? [.session] : v   // nothing usable enabled -> session
    }
    // Outside-in order of the rings disc.
    func rings(_ snap: UsageSnapshot) -> [GaugeKind] { visible(snap, [.week, .model, .session]) }
    // Left-to-right order of the popover row and the mini rings.
    func row(_ snap: UsageSnapshot) -> [GaugeKind] { visible(snap, [.session, .week, .model]) }
}

extension UsageSnapshot {
    func entry(_ k: GaugeKind) -> LimitEntry? {
        switch k {
        case .session: return session
        case .week:    return weeklyAll
        case .model:   return primaryModel
        }
    }
}

// Floating-gauge layout preference: "line" | "square" | "rings". Stored under
// `floatStyle`; an absent key migrates from the older `floatSquare` bool.
enum FloatStyle {
    static var current: String {
        let d = UserDefaults.standard
        if let s = d.string(forKey: "floatStyle") { return s }
        return d.bool(forKey: "floatSquare") ? "square" : "line"
    }
}

// Floating-gauge background opacity, 0 (clear glass) ... 1 (solid). Stored under
// `gaugeOpacity`; absent = 0.6. FloatingView reads it through @AppStorage, the
// right-click slider and the previews through this helper.
enum GaugeOpacity {
    static let key = "gaugeOpacity"
    static let fallback = 0.6
    static func clamp(_ v: Double) -> Double { min(1, max(0, v)) }
    static var current: Double {
        clamp(UserDefaults.standard.object(forKey: key) as? Double ?? fallback)
    }
}

enum Sev {
    // Traffic-light thresholds; API "severity" only says normal/warning so we
    // derive finer bands from the percentage.
    static func color(_ pct: Double) -> Color {
        switch pct {
        case ..<50:  return Color(red: 0.22, green: 0.72, blue: 0.42)
        case ..<75:  return Color(red: 0.83, green: 0.62, blue: 0.02) // deep gold — bright yellow vanished on warm wallpapers
        case ..<90:  return Color(red: 0.96, green: 0.55, blue: 0.14)
        default:     return Color(red: 0.90, green: 0.26, blue: 0.21)
        }
    }
    static func nsColor(_ pct: Double) -> NSColor {
        switch pct {
        case ..<50:  return NSColor(red: 0.22, green: 0.72, blue: 0.42, alpha: 1)
        case ..<75:  return NSColor(red: 0.83, green: 0.62, blue: 0.02, alpha: 1)
        case ..<90:  return NSColor(red: 0.96, green: 0.55, blue: 0.14, alpha: 1)
        default:     return NSColor(red: 0.90, green: 0.26, blue: 0.21, alpha: 1)
        }
    }
}

// MARK: - Keychain credentials

struct Creds {
    var blob: [String: Any]
    var account: String
    private var oauth: [String: Any] { blob["claudeAiOauth"] as? [String: Any] ?? [:] }
    var accessToken: String?  { oauth["accessToken"] as? String }
    var refreshToken: String? { oauth["refreshToken"] as? String }
    var subscription: String? { oauth["subscriptionType"] as? String }
    var expiresAt: Date? {
        guard let ms = (oauth["expiresAt"] as? NSNumber)?.doubleValue else { return nil }
        return Date(timeIntervalSince1970: ms / 1000)
    }
    mutating func apply(accessToken: String, refreshToken: String?, expiresIn: Double) {
        var o = blob["claudeAiOauth"] as? [String: Any] ?? [:]
        o["accessToken"] = accessToken
        if let r = refreshToken { o["refreshToken"] = r }
        o["expiresAt"] = Int((Date().timeIntervalSince1970 + expiresIn) * 1000)
        blob["claudeAiOauth"] = o
    }
}

enum CredentialStore {
    static let service = "Claude Code-credentials"

    @discardableResult
    private static func security(_ args: [String]) -> (status: Int32, out: String) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = Pipe()
        do { try p.run() } catch { return (-1, "") }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, String(data: data, encoding: .utf8) ?? "")
    }

    private static func read(account: String) -> Creds? {
        let r = security(["find-generic-password", "-s", service, "-a", account, "-w"])
        guard r.status == 0,
              let data = r.out.trimmingCharacters(in: .whitespacesAndNewlines).data(using: .utf8),
              let blob = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              blob["claudeAiOauth"] != nil
        else { return nil }
        return Creds(blob: blob, account: account)
    }

    // Prefer whichever entry holds the freshest token. A stray empty-account
    // duplicate can exist; it gets folded back into the canonical entry on the
    // next write().
    static func read() -> Creds? {
        let candidates = [read(account: NSUserName()), read(account: "")].compactMap { $0 }
        return candidates.max { ($0.expiresAt ?? .distantPast) < ($1.expiresAt ?? .distantPast) }
    }

    static func write(_ creds: Creds) {
        guard let data = try? JSONSerialization.data(withJSONObject: creds.blob),
              let json = String(data: data, encoding: .utf8) else { return }
        security(["add-generic-password", "-U", "-s", service, "-a", NSUserName(), "-w", json])
        // Remove a stale empty-account duplicate if one exists (ignore failures).
        security(["delete-generic-password", "-s", service, "-a", ""])
    }
}

// MARK: - API

enum APIError: LocalizedError {
    case noCredentials, reauthNeeded, refreshFailed(String), httpError(Int), badPayload
    // Errors the user must fix by signing in to Claude Code again.
    var needsSignIn: Bool {
        switch self { case .noCredentials, .reauthNeeded: return true; default: return false }
    }
    var errorDescription: String? {
        switch self {
        case .noCredentials:        return "Claude Code isn't signed in on this Mac. Click Sign in below, or run `claude auth login` in Terminal."
        case .reauthNeeded:         return "Claude Code is signed out. Click Sign in below, or run `claude auth login` in Terminal."
        case .refreshFailed(let m): return "Token refresh failed: \(m)"
        case .httpError(let c):     return "Usage request failed (HTTP \(c))."
        case .badPayload:           return "Unexpected response from usage endpoint."
        }
    }
}

enum UsageAPI {
    static let userAgent = "claude-code/2.0.0 (external, cli)" // plain UAs get Cloudflare-1010'd
    static let clientID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e" // Claude Code's public OAuth client id

    private static func request(_ url: String, method: String = "GET",
                                headers: [String: String] = [:], body: Data? = nil)
        async throws -> (Data, Int) {
        var req = URLRequest(url: URL(string: url)!)
        req.httpMethod = method
        req.httpBody = body
        req.timeoutInterval = 20
        req.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        headers.forEach { req.setValue($1, forHTTPHeaderField: $0) }
        let (data, resp) = try await URLSession.shared.data(for: req)
        return (data, (resp as? HTTPURLResponse)?.statusCode ?? 0)
    }

    static func validToken() async throws -> (token: String, plan: String?) {
        guard var creds = CredentialStore.read() else { throw APIError.noCredentials }
        if let exp = creds.expiresAt, exp > Date().addingTimeInterval(120),
           let tok = creds.accessToken {
            return (tok, creds.subscription)
        }
        guard let refresh = creds.refreshToken else { throw APIError.noCredentials }
        let body = try JSONSerialization.data(withJSONObject: [
            "grant_type": "refresh_token", "refresh_token": refresh, "client_id": clientID])
        let (data, code) = try await request("https://platform.claude.com/v1/oauth/token",
                                             method: "POST",
                                             headers: ["Content-Type": "application/json"],
                                             body: body)
        guard code == 200,
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tok = obj["access_token"] as? String else {
            // 400/401 = invalid_grant: the refresh token was revoked or the
            // family rotated elsewhere. Only a fresh sign-in fixes that.
            if code == 400 || code == 401 { throw APIError.reauthNeeded }
            throw APIError.refreshFailed("HTTP \(code)")
        }
        creds.apply(accessToken: tok,
                    refreshToken: obj["refresh_token"] as? String,
                    expiresIn: (obj["expires_in"] as? NSNumber)?.doubleValue ?? 3600)
        CredentialStore.write(creds)
        return (tok, creds.subscription)
    }

    static func fetchUsage() async throws -> (UsageSnapshot, String?) {
        let (token, plan) = try await validToken()
        let (data, code) = try await request("https://api.anthropic.com/api/oauth/usage",
                                             headers: ["Authorization": "Bearer \(token)",
                                                       "anthropic-beta": "oauth-2025-04-20"])
        guard code == 200 else { throw APIError.httpError(code) }
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rawLimits = obj["limits"] as? [[String: Any]] else { throw APIError.badPayload }

        let iso = ISO8601DateFormatter()
        iso.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let isoPlain = ISO8601DateFormatter()

        var limits: [LimitEntry] = []
        for l in rawLimits {
            guard let kind = l["kind"] as? String,
                  let pct = (l["percent"] as? NSNumber)?.doubleValue else { continue }
            var label: String
            switch kind {
            case "session":    label = "Session (5 h)"
            case "weekly_all": label = "Week — all models"
            default:
                let scope = l["scope"] as? [String: Any]
                let model = (scope?["model"] as? [String: Any])?["display_name"] as? String
                label = "Week — \(model ?? "scoped")"
            }
            var resets: Date? = nil
            if let s = l["resets_at"] as? String { resets = iso.date(from: s) ?? isoPlain.date(from: s) }
            limits.append(LimitEntry(id: kind + label, kind: kind, label: label,
                                     percent: pct, resetsAt: resets,
                                     isActive: (l["is_active"] as? Bool) ?? false))
        }
        guard !limits.isEmpty else { throw APIError.badPayload }
        return (UsageSnapshot(fetchedAt: Date(), limits: limits), plan)
    }
}

// MARK: - History (for the sparkline)

struct HistoryPoint: Codable {
    let t: Date
    let s: Double   // session %
    let w: Double   // weekly all-models %
}

final class HistoryStore {
    private let url: URL
    private(set) var points: [HistoryPoint] = []

    init() {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Claude Meter", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        url = dir.appendingPathComponent("history.json")
        if let data = try? Data(contentsOf: url),
           let pts = try? JSONDecoder().decode([HistoryPoint].self, from: data) {
            points = pts
        }
    }

    func record(session: Double, weekly: Double) {
        if let last = points.last, Date().timeIntervalSince(last.t) < 270 { return }
        points.append(HistoryPoint(t: Date(), s: session, w: weekly))
        let cutoff = Date().addingTimeInterval(-7 * 24 * 3600)
        points.removeAll { $0.t < cutoff }
        if let data = try? JSONEncoder().encode(points) { try? data.write(to: url) }
    }

    func last24h() -> [HistoryPoint] {
        let cutoff = Date().addingTimeInterval(-24 * 3600)
        return points.filter { $0.t >= cutoff }
    }
}

// MARK: - Usage warnings

// Posts a notification when a limit crosses the configured threshold
// (default 90%, "Warn at" in the gear menu, 0 = off); re-arms once it drops
// 5 points below (i.e. after a reset), so each approach warns once.
enum Notifier {
    static func check(_ snap: UsageSnapshot) {
        let d = UserDefaults.standard
        let threshold = d.object(forKey: "warnThreshold") == nil
            ? 90.0 : d.double(forKey: "warnThreshold")
        guard threshold > 0 else { return }
        for l in snap.limits {
            let key = "warned-\(l.id)"
            if l.percent >= threshold, !d.bool(forKey: key) {
                d.set(true, forKey: key)
                post(title: "Claude usage at \(Int(l.percent.rounded()))%",
                     body: "\(l.label) — \(resetText(l.resetsAt))")
            } else if l.percent < threshold - 5, d.bool(forKey: key) {
                d.set(false, forKey: key)
            }
        }
    }

    static func post(title: String, body: String) {
        let center = UNUserNotificationCenter.current()
        center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
            if granted {
                let c = UNMutableNotificationContent()
                c.title = title
                c.body = body
                c.sound = .default
                center.add(UNNotificationRequest(identifier: UUID().uuidString,
                                                 content: c, trigger: nil))
            } else {
                // Not authorised (or UN framework unhappy with the ad-hoc
                // bundle) — fall back to an osascript banner.
                let esc = { (s: String) in s.replacingOccurrences(of: "\"", with: "\\\"") }
                let p = Process()
                p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
                p.arguments = ["-e",
                    "display notification \"\(esc(body))\" with title \"\(esc(title))\""]
                try? p.run()
            }
        }
    }
}

// MARK: - Status export

// Writes the current usage snapshot to a JSON file on every refresh attempt
// so other local tooling can read live numbers without touching the keychain
// or Anthropic's endpoint itself. Never surfaces errors to the UI.
enum StatusExporter {
    private static let iso: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.timeZone = TimeZone(identifier: "UTC")
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    private static func isoString(_ date: Date?) -> Any {
        guard let date else { return NSNull() }
        return iso.string(from: date)
    }

    private static var enabled: Bool {
        let d = UserDefaults.standard
        return d.object(forKey: "statusExportEnabled") == nil
            ? true : d.bool(forKey: "statusExportEnabled")
    }

    private static var exportPath: String {
        let raw = UserDefaults.standard.string(forKey: "statusExportPath")
            ?? "~/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/status/current.json"
        return (raw as NSString).expandingTildeInPath
    }

    // A machine without the Synology Drive folder mounted must behave
    // exactly as today: no export, no error, no directory creation beyond
    // the status/ leaf.
    private static func destinationURL() -> URL? {
        let dest = URL(fileURLWithPath: exportPath)
        let statusDir = dest.deletingLastPathComponent()
        let projectDir = statusDir.deletingLastPathComponent()
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: projectDir.path, isDirectory: &isDir),
              isDir.boolValue else { return nil }
        try? FileManager.default.createDirectory(at: statusDir, withIntermediateDirectories: true)
        return dest
    }

    private static func limitDict(_ entry: LimitEntry?) -> Any {
        guard let entry else { return NSNull() }
        return ["percent": entry.percent, "resets_at": isoString(entry.resetsAt)]
    }

    private static func modelsArray(_ entries: [LimitEntry]) -> [[String: Any]] {
        return entries.map { e in
            ["name": UsageSnapshot.modelName(e), "percent": e.percent, "resets_at": isoString(e.resetsAt)]
        }
    }

    private static func buildDocument(snap: UsageSnapshot?, plan: String?, error: String?) -> [String: Any] {
        [
            "fetched_at": snap.map { isoString($0.fetchedAt) } ?? NSNull(),
            "checked_at": isoString(Date()),
            "plan": plan ?? NSNull(),
            "session": limitDict(snap?.session),
            "weekly_all": limitDict(snap?.weeklyAll),
            "models": snap.map { modelsArray($0.scoped) } ?? [],
            "error": error ?? NSNull(),
        ]
    }

    static func documentForSuccess(_ snap: UsageSnapshot, plan: String?) -> [String: Any] {
        buildDocument(snap: snap, plan: plan, error: nil)
    }

    // Keeps every prior field on failure; only error/checked_at change. Falls
    // back to the full schema (NSNull fields, empty models) if no prior file
    // is readable.
    static func documentForFailure(_ message: String) -> [String: Any] {
        if let data = try? Data(contentsOf: URL(fileURLWithPath: exportPath)),
           var doc = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            doc["error"] = message
            doc["checked_at"] = isoString(Date())
            return doc
        }
        return buildDocument(snap: nil, plan: nil, error: message)
    }

    static func serialize(_ doc: [String: Any]) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: doc, options: [.prettyPrinted, .sortedKeys]),
              let str = String(data: data, encoding: .utf8) else { return "{}" }
        return str
    }

    // Not private: the `--status` CLI block calls this directly, bypassing
    // the `enabled` gate (explicit invocation), while still respecting the
    // missing-projectDir skip rule inside destinationURL().
    static func writeToFile(_ doc: [String: Any]) {
        guard let dest = destinationURL() else { return }
        guard let data = try? JSONSerialization.data(withJSONObject: doc, options: [.prettyPrinted, .sortedKeys])
        else { return }
        let tmp = dest.deletingLastPathComponent().appendingPathComponent("current.json.tmp")
        do {
            try data.write(to: tmp, options: .atomic)
            do {
                _ = try FileManager.default.replaceItemAt(dest, withItemAt: tmp)
            } catch {
                try? FileManager.default.removeItem(at: dest)
                try FileManager.default.moveItem(at: tmp, to: dest)
            }
        } catch {
            // Swallowed — the exporter must never crash or surface errors.
        }
    }

    static func exportSuccess(_ snap: UsageSnapshot, plan: String?) {
        guard enabled else { return }
        writeToFile(documentForSuccess(snap, plan: plan))
    }

    static func exportFailure(_ message: String) {
        guard enabled else { return }
        writeToFile(documentForFailure(message))
    }
}

// MARK: - Sign-in launcher

// Opens Terminal on a small .command script that runs `claude auth login`.
// Terminal (not a hidden process) because the flow may ask for a pasted code;
// NSWorkspace.open needs no Apple Events permission.
enum SignInLauncher {
    static func scriptURL() -> URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Claude Meter", isDirectory: true)
            .appendingPathComponent("sign-in.command")
    }

    static let scriptBody = """
    #!/bin/zsh -l
    echo "Signing in to Claude Code for Claude Meter…"
    claude auth login
    echo
    echo "Done. You can close this window."

    """

    @discardableResult
    static func writeScript() throws -> URL {
        let url = scriptURL()
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
        try scriptBody.write(to: url, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        return url
    }

    static func launch() {
        guard let url = try? writeScript() else { return }
        NSWorkspace.shared.open(url)
    }
}

// MARK: - Update check

// Asks GitHub Releases for the latest release. No releases yet (404) counts
// as up to date.
enum UpdateChecker {
    struct Release {
        let tag: String
        let version: String
        let url: URL
        let notes: String
    }

    static func latest() async throws -> Release? {
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/bernmc/claude-meter/releases/latest")!)
        req.timeoutInterval = 15
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("ClaudeMeter/\(AppVersion.current)", forHTTPHeaderField: "User-Agent")
        let (data, resp) = try await URLSession.shared.data(for: req)
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if code == 404 { return nil }
        guard code == 200 else { throw APIError.httpError(code) }
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = obj["tag_name"] as? String,
              let html = obj["html_url"] as? String,
              let url = URL(string: html) else { throw APIError.badPayload }
        let version = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        return Release(tag: tag, version: version, url: url, notes: (obj["body"] as? String) ?? "")
    }

    // Numeric, component by component; a leading "v" is ignored and missing
    // components count as 0 ("1.1" == "1.1.0").
    static func isNewer(_ a: String, than b: String) -> Bool {
        func parts(_ v: String) -> [Int] {
            let t = v.hasPrefix("v") ? String(v.dropFirst()) : v
            return t.split(separator: ".", omittingEmptySubsequences: false)
                .map { Int($0.prefix(while: \.isNumber)) ?? 0 }
        }
        let x = parts(a), y = parts(b)
        for i in 0..<max(x.count, y.count) {
            let p = i < x.count ? x[i] : 0, q = i < y.count ? y[i] : 0
            if p != q { return p > q }
        }
        return false
    }
}

// "Update…": with a usable repo checkout, opens Terminal on a script that
// pulls and rebuilds (same NSWorkspace .command pattern as SignInLauncher);
// otherwise opens the release page.
enum UpdateLauncher {
    static let defaultRepo = "~/SynologyDrive/AI_Context/01-Projects/Claude_Toolkit/Claude_Meter/claude-meter"

    static func scriptURL() -> URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Claude Meter", isDirectory: true)
            .appendingPathComponent("update.command")
    }

    // First launch only: adopt Bernard's checkout when it exists.
    static func seedRepoPath() {
        let d = UserDefaults.standard
        guard d.string(forKey: "updateRepoPath") == nil else { return }
        let path = (defaultRepo as NSString).expandingTildeInPath
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: path, isDirectory: &isDir), isDir.boolValue {
            d.set(path, forKey: "updateRepoPath")
        }
    }

    // The only remotes Update may pull from. The generated script repeats the
    // same four forms in shell.
    static let acceptedRemotes = [
        "https://github.com/bernmc/claude-meter",
        "https://github.com/bernmc/claude-meter.git",
        "git@github.com:bernmc/claude-meter",
        "git@github.com:bernmc/claude-meter.git",
    ]

    static func isAcceptedRemote(_ url: String) -> Bool {
        acceptedRemotes.contains(url.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    // `git -C <path> remote get-url origin`, 5 s timeout. nil on any failure.
    static func originURL(of path: String) -> String? {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/git")
        p.arguments = ["-C", path, "remote", "get-url", "origin"]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        let done = DispatchSemaphore(value: 0)
        p.terminationHandler = { _ in done.signal() }
        do { try p.run() } catch { return nil }
        if done.wait(timeout: .now() + 5) == .timedOut {
            p.terminate()
            return nil
        }
        guard p.terminationStatus == 0 else { return nil }
        let data = out.fileHandleForReading.readDataToEndOfFile()
        return String(data: data, encoding: .utf8)
    }

    private static let remoteLock = NSLock()
    private static var remoteCache: [String: Bool] = [:]

    // Cached per launch, per path.
    static func originIsAccepted(at path: String) -> Bool {
        remoteLock.lock()
        if let hit = remoteCache[path] { remoteLock.unlock(); return hit }
        remoteLock.unlock()
        let ok = originURL(of: path).map(isAcceptedRemote) ?? false
        remoteLock.lock()
        remoteCache[path] = ok
        remoteLock.unlock()
        return ok
    }

    // The configured checkout, only if it contains macos/build.sh and its
    // origin is this project's GitHub repository.
    static func repoPath() -> String? {
        guard let raw = UserDefaults.standard.string(forKey: "updateRepoPath"), !raw.isEmpty else { return nil }
        let path = (raw as NSString).expandingTildeInPath
        let build = (path as NSString).appendingPathComponent("macos/build.sh")
        guard FileManager.default.fileExists(atPath: build) else { return nil }
        return originIsAccepted(at: path) ? path : nil
    }

    static func scriptBody(for path: String) -> String {
        var q = ""
        for ch in path {
            if "\\\"$`".contains(ch) { q.append("\\") }
            q.append(ch)
        }
        return """
        #!/bin/zsh -l
        cd "\(q)" || exit 1
        expected='https://github.com/bernmc/claude-meter'
        remote="$(git remote get-url origin 2>/dev/null)"
        case "$remote" in
          "$expected"|"$expected.git"|"git@github.com:bernmc/claude-meter"|"git@github.com:bernmc/claude-meter.git") ;;
          *) echo "Refusing to update: this checkout's origin is '$remote', not $expected."; echo "Open https://github.com/bernmc/claude-meter/releases instead."; exit 2 ;;
        esac
        echo "Updating Claude Meter from $expected…"
        git pull --ff-only origin main || { echo "git pull failed; nothing was built."; exit 3; }
        cd macos && ./build.sh --install
        echo
        echo "Done. You can close this window."

        """
    }

    @discardableResult
    static func writeScript(for path: String) throws -> URL {
        let url = scriptURL()
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
        try scriptBody(for: path).write(to: url, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        return url
    }

    static func launch(releaseURL: URL?) {
        if let path = repoPath() {
            guard let url = try? writeScript(for: path) else { return }
            NSWorkspace.shared.open(url)
        } else if let releaseURL {
            NSWorkspace.shared.open(releaseURL)
        }
    }
}

// MARK: - Observable model

@MainActor
final class UsageModel: ObservableObject {
    @Published var snapshot: UsageSnapshot?
    @Published var errorText: String?
    @Published var plan: String?
    @Published var refreshing = false
    @Published var needsSignIn = false
    @Published var signInLaunchedAt: Date?
    @Published var availableUpdate: UpdateChecker.Release?
    @Published var updateStatus: String?
    let history = HistoryStore()
    private var timer: Timer?
    private var fastTimer: Timer?
    private var updateTimer: Timer?
    private var updateKickoff: Task<Void, Never>?
    private var statusClear: Task<Void, Never>?

    // Opens the sign-in Terminal window, then polls every 5 s until the fetch
    // recovers or 3 minutes pass. The 60 s timer is unaffected.
    func startSignIn() {
        SignInLauncher.launch()
        let started = Date()
        signInLaunchedAt = started
        fastTimer?.invalidate()
        fastTimer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                if !self.needsSignIn || Date().timeIntervalSince(started) >= 180 {
                    self.stopFastPoll()
                    return
                }
                await self.refresh()
                if !self.needsSignIn { self.stopFastPoll() }
            }
        }
    }

    private func stopFastPoll() {
        fastTimer?.invalidate()
        fastTimer = nil
        signInLaunchedAt = nil
    }

    func start() {
        Task { await refresh() }
        timer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            Task { await self?.refresh() }
        }
        timer?.tolerance = 10
        applyUpdatePreference(launch: true)
    }

    // Starts or stops the 24 h update timer to match "autoUpdateCheck"
    // (absent = on). Idempotent: AppController calls it from the
    // UserDefaults.didChangeNotification observer on every preference change.
    // `launch` adds the one-shot check 10 s after startup.
    func applyUpdatePreference(launch: Bool = false) {
        let d = UserDefaults.standard
        let on = d.object(forKey: "autoUpdateCheck") == nil ? true : d.bool(forKey: "autoUpdateCheck")
        if on, updateTimer == nil {
            if launch {
                updateKickoff = Task { [weak self] in
                    try? await Task.sleep(nanoseconds: 10_000_000_000)
                    guard !Task.isCancelled else { return }
                    await self?.checkForUpdates(manual: false)
                }
            }
            updateTimer = Timer.scheduledTimer(withTimeInterval: 24 * 3600, repeats: true) { [weak self] _ in
                Task { await self?.checkForUpdates(manual: false) }
            }
            updateTimer?.tolerance = 600
        } else if !on, updateTimer != nil {
            updateTimer?.invalidate()
            updateTimer = nil
            updateKickoff?.cancel()
            updateKickoff = nil
        }
    }

    private func setUpdateStatus(_ text: String) {
        updateStatus = text
        statusClear?.cancel()
        statusClear = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 6_000_000_000)
            guard !Task.isCancelled else { return }
            self?.updateStatus = nil
        }
    }

    func checkForUpdates(manual: Bool) async {
        do {
            let latest = try await UpdateChecker.latest()
            if let r = latest, UpdateChecker.isNewer(r.tag, than: AppVersion.current) {
                availableUpdate = r
                let d = UserDefaults.standard
                if manual || d.string(forKey: "lastNotifiedUpdate") != r.tag {
                    Notifier.post(title: "Claude Meter \(r.version) is available",
                                  body: "You have \(AppVersion.current). Open the gauge menu to update.")
                    d.set(r.tag, forKey: "lastNotifiedUpdate")
                }
            } else {
                availableUpdate = nil
                if manual { setUpdateStatus("Up to date (v\(AppVersion.current))") }
            }
        } catch {
            if manual { setUpdateStatus("Couldn't check: \(error.localizedDescription)") }
        }
    }

    private var notifiedSignIn = false

    func refresh() async {
        if refreshing { return }
        refreshing = true
        defer { refreshing = false }
        do {
            let (snap, plan) = try await UsageAPI.fetchUsage()
            self.snapshot = snap
            self.plan = plan
            self.errorText = nil
            self.needsSignIn = false
            self.notifiedSignIn = false
            if let s = snap.session?.percent, let w = snap.weeklyAll?.percent {
                history.record(session: s, weekly: w)
            }
            StatusExporter.exportSuccess(snap, plan: plan)
            Notifier.check(snap)
        } catch {
            self.errorText = error.localizedDescription
            self.needsSignIn = (error as? APIError)?.needsSignIn ?? false
            StatusExporter.exportFailure(error.localizedDescription)
            // Sign-in problems don't fix themselves — say so once, loudly,
            // instead of failing silently in the popover.
            if let api = error as? APIError, api.needsSignIn, !notifiedSignIn {
                notifiedSignIn = true
                Notifier.post(title: "Claude Meter can't fetch usage",
                              body: error.localizedDescription)
            }
        }
    }
}

// MARK: - Formatting helpers

// System-locale formats (day-before-month for AU, month-first for US, …).
let fmtTime: DateFormatter = {
    let f = DateFormatter()
    f.locale = .current
    f.setLocalizedDateFormatFromTemplate("jmm")
    return f
}()
let fmtDayTime: DateFormatter = {
    let f = DateFormatter()
    f.locale = .current
    f.setLocalizedDateFormatFromTemplate("EEE d/M jmm")
    return f
}()

func clockString(_ date: Date, _ f: DateFormatter) -> String {
    // Prefer lowercase am/pm where the locale uses them.
    f.string(from: date)
        .replacingOccurrences(of: " AM", with: " am")
        .replacingOccurrences(of: " PM", with: " pm")
}

// Two-line form of resetText for narrow ring columns: break at the "·".
func twoLine(_ text: String) -> String {
    text.replacingOccurrences(of: " · ", with: "\n")
}

func resetText(_ date: Date?) -> String {
    guard let date else { return "—" }
    let secs = date.timeIntervalSinceNow
    guard secs > 0 else { return "resetting…" }
    let h = Int(secs) / 3600, m = (Int(secs) % 3600) / 60
    let rel = h > 0 ? "\(h) h \(m) m" : "\(m) m"
    let clock = Calendar.current.isDateInToday(date)
        ? clockString(date, fmtTime)
        : clockString(date, fmtDayTime)
    return "resets in \(rel) · \(clock)"
}

// MARK: - SwiftUI views

struct RingGauge: View {
    let percent: Double
    let label: String
    let sublabel: String
    var size: CGFloat = 84
    // Set in the three-ring row: fixes the text column width so the label stays
    // on one line and the sublabel wraps (never ellipsises).
    var textWidth: CGFloat? = nil

    var body: some View {
        VStack(spacing: 6) {
            ZStack {
                Circle()
                    .stroke(Color.primary.opacity(0.16), style: StrokeStyle(lineWidth: size * 0.1, lineCap: .round))
                // Slightly wider dark arc under the colored one — a crisp rim
                // that separates it from whatever shows through the material.
                Circle()
                    .trim(from: 0, to: max(0.003, min(percent, 100) / 100))
                    .stroke(Color.black.opacity(0.32),
                            style: StrokeStyle(lineWidth: size * 0.1 + 2.5, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .animation(.easeOut(duration: 0.6), value: percent)
                Circle()
                    .trim(from: 0, to: max(0.003, min(percent, 100) / 100))
                    .stroke(
                        AngularGradient(colors: [Sev.color(percent).opacity(0.55), Sev.color(percent)],
                                        center: .center,
                                        startAngle: .degrees(0),
                                        endAngle: .degrees(360 * min(percent, 100) / 100)),
                        style: StrokeStyle(lineWidth: size * 0.1, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .animation(.easeOut(duration: 0.6), value: percent)
                VStack(spacing: 0) {
                    Text("\(Int(percent.rounded()))")
                        .font(.system(size: size * 0.3, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                    Text("%")
                        .font(.system(size: size * 0.14, weight: .medium))
                        .foregroundStyle(.secondary)
                }
            }
            .frame(width: size, height: size)
            if let textWidth {
                Text(label).font(.system(size: 11, weight: .semibold))
                    .lineLimit(1).minimumScaleFactor(0.75)
                    .frame(width: textWidth)
                // One Text per line, each shrinks slightly rather than ellipsising.
                VStack(spacing: 1) {
                    ForEach(Array(sublabel.components(separatedBy: "\n").enumerated()), id: \.offset) { _, line in
                        Text(line)
                            .font(.system(size: 9.5))
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .minimumScaleFactor(0.8)
                    }
                }
                .frame(width: textWidth)
            } else {
                Text(label).font(.system(size: 11, weight: .semibold))
                Text(sublabel)
                    .font(.system(size: 9.5))
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
        }
    }
}

struct ScopedBar: View {
    let entry: LimitEntry
    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack {
                Text(entry.label).font(.system(size: 11, weight: .medium))
                Spacer()
                Text("\(Int(entry.percent.rounded()))%")
                    .font(.system(size: 11, weight: .semibold)).monospacedDigit()
                    .foregroundStyle(Sev.color(entry.percent))
            }
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.primary.opacity(0.14))
                    Capsule()
                        .fill(LinearGradient(colors: [Sev.color(entry.percent).opacity(0.6), Sev.color(entry.percent)],
                                             startPoint: .leading, endPoint: .trailing))
                        .frame(width: max(4, geo.size.width * min(entry.percent, 100) / 100))
                        .animation(.easeOut(duration: 0.6), value: entry.percent)
                }
            }
            .frame(height: 6)
        }
    }
}

struct Sparkline: View {
    let points: [HistoryPoint]
    var body: some View {
        Canvas { ctx, size in
            guard points.count >= 2 else { return }
            let t0 = points.first!.t.timeIntervalSince1970
            let t1 = points.last!.t.timeIntervalSince1970
            let span = max(t1 - t0, 1)
            func pos(_ p: HistoryPoint) -> CGPoint {
                CGPoint(x: (p.t.timeIntervalSince1970 - t0) / span * size.width,
                        y: size.height - (min(p.s, 100) / 100) * (size.height - 2) - 1)
            }
            var line = Path(); var area = Path()
            let first = pos(points[0])
            line.move(to: first)
            area.move(to: CGPoint(x: first.x, y: size.height))
            area.addLine(to: first)
            for p in points.dropFirst() {
                let pt = pos(p)
                line.addLine(to: pt); area.addLine(to: pt)
            }
            area.addLine(to: CGPoint(x: pos(points.last!).x, y: size.height))
            area.closeSubpath()
            let cur = points.last!.s
            ctx.fill(area, with: .linearGradient(
                Gradient(colors: [Sev.color(cur).opacity(0.25), .clear]),
                startPoint: .zero, endPoint: CGPoint(x: 0, y: size.height)))
            ctx.stroke(line, with: .color(Sev.color(cur)), lineWidth: 1.5)
            let dot = pos(points.last!)
            ctx.fill(Path(ellipseIn: CGRect(x: dot.x - 2.5, y: dot.y - 2.5, width: 5, height: 5)),
                     with: .color(Sev.color(cur)))
        }
    }
}

struct PopoverView: View {
    @ObservedObject var model: UsageModel
    let controller: AppController
    var staticPreview = false   // ImageRenderer can't draw the AppKit-backed gear Menu
    @AppStorage("showFloating") private var showFloating = true
    @AppStorage("floatStyle") private var floatStyle = FloatStyle.current
    @AppStorage("ringsCentre") private var ringsCentre = "week"
    @AppStorage("menuBarMetric") private var menuBarMetric = "worst"
    @AppStorage("menuBarShowPct") private var menuBarShowPct = true
    @AppStorage("warnThreshold") private var warnThreshold = 90.0
    @AppStorage("statusExportEnabled") private var statusExportEnabled = true
    @AppStorage("autoUpdateCheck") private var autoUpdateCheck = true
    @AppStorage("showSession") private var showSession = true
    @AppStorage("showWeek") private var showWeek = true
    @AppStorage("showModel") private var showModel = true
    @AppStorage("gaugeOpacity") private var gaugeOpacity = GaugeOpacity.fallback
    var forceSel: GaugeSelection? = nil   // previews: don't touch user defaults
    private var sel: GaugeSelection {
        forceSel ?? GaugeSelection(session: showSession, week: showWeek, model: showModel)
    }

    // Waiting state lasts 3 minutes; the model clears signInLaunchedAt when its
    // fast poll ends, the date check covers any lag.
    @ViewBuilder
    private var signInButton: some View {
        let waiting = model.signInLaunchedAt.map { Date().timeIntervalSince($0) < 180 } ?? false
        Button(waiting ? "Waiting for sign-in…" : "Sign in to Claude Code…") {
            model.startSignIn()
        }
        .buttonStyle(.borderedProminent)
        .controlSize(.small)
        .disabled(waiting)
    }

    // Only the selected gauges, left to right: session, week, model. Fewer
    // gauges grow (72 / 84 / 96) and stay centred; sublabels break at the "·".
    private func ringRow(_ snap: UsageSnapshot) -> some View {
        let kinds = sel.row(snap)
        let size: CGFloat = kinds.count >= 3 ? 72 : (kinds.count == 2 ? 84 : 96)
        let textWidth: CGFloat = kinds.count >= 3 ? 92 : (kinds.count == 2 ? 110 : 130)
        return HStack(alignment: .top, spacing: 14) {
            ForEach(kinds, id: \.self) { k in
                if let e = snap.entry(k) {
                    RingGauge(percent: e.percent, label: ringLabel(k, e, snap),
                              sublabel: twoLine(resetText(e.resetsAt)),
                              size: size, textWidth: textWidth)
                }
            }
        }
        .frame(maxWidth: .infinity)
    }

    private func ringLabel(_ k: GaugeKind, _ e: LimitEntry, _ snap: UsageSnapshot) -> String {
        switch k {
        case .session: return "Session"
        case .week:    return "Week (all)"
        case .model:   return "Week (\(snap.modelName(e)))"
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Claude usage").font(.system(size: 13, weight: .bold))
                Spacer()
                if let plan = model.plan {
                    Text(plan.uppercased())
                        .font(.system(size: 9, weight: .heavy))
                        .padding(.horizontal, 7).padding(.vertical, 2.5)
                        .background(Capsule().fill(Color.accentColor.opacity(0.16)))
                        .foregroundStyle(Color.accentColor)
                }
            }

            if let snap = model.snapshot {
                TimelineView(.periodic(from: .now, by: 30)) { _ in
                    ringRow(snap)
                }
                // The first model is a ring now; bars cover any further ones.
                if snap.scoped.count > 1 {
                    VStack(spacing: 8) {
                        ForEach(Array(snap.scoped.dropFirst())) { ScopedBar(entry: $0) }
                    }
                }
                let hist = model.history.last24h()
                if hist.count >= 2 {
                    VStack(alignment: .leading, spacing: 3) {
                        Sparkline(points: hist).frame(height: 34)
                        Text("session · last 24 h")
                            .font(.system(size: 9)).foregroundStyle(.tertiary)
                    }
                }
            } else if model.errorText == nil {
                HStack { Spacer(); ProgressView().controlSize(.small); Spacer() }
                    .frame(height: 80)
            }

            if let err = model.errorText {
                Text(err)
                    .font(.system(size: 10.5))
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                if model.needsSignIn {
                    signInButton
                        .padding(.top, -4)   // 12 pt stack spacing - 4 = 8 pt gap under the text
                }
            }

            if let up = model.availableUpdate {
                HStack {
                    Text("Update available: v\(up.version)")
                        .font(.system(size: 11, weight: .semibold))
                    Spacer()
                    Button(UpdateLauncher.repoPath() != nil ? "Update…" : "Open release page…") {
                        UpdateLauncher.launch(releaseURL: up.url)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.small)
                }
            }

            Divider()

            HStack(spacing: 10) {
                Button {
                    Task { await model.refresh() }
                } label: {
                    Image(systemName: "arrow.clockwise").font(.system(size: 11))
                }
                .buttonStyle(.plain)
                .help("Refresh now")
                .opacity(model.refreshing ? 0.4 : 1)

                Button {
                    controller.toggleFloatingWindow()
                } label: {
                    Image(systemName: "macwindow.on.rectangle").font(.system(size: 11))
                }
                .buttonStyle(.plain)
                .help("Show/hide desktop gauge")

                if let status = model.updateStatus {
                    Text(status)
                        .font(.system(size: 9.5)).foregroundStyle(.tertiary)
                } else if let snap = model.snapshot {
                    Text("updated \(clockString(snap.fetchedAt, fmtTime))")
                        .font(.system(size: 9.5)).foregroundStyle(.tertiary)
                }
                Spacer()
                if staticPreview {
                    Image(systemName: "gearshape").font(.system(size: 11)).frame(width: 24)
                } else {
                Menu {
                    Toggle("Desktop gauge", isOn: Binding(
                        get: { showFloating },
                        set: { _ in controller.toggleFloatingWindow() }))
                    Menu("Gauges") {
                        Toggle("Session (5 h)", isOn: $showSession)
                            .disabled(showSession && !showWeek && !showModel)
                        Toggle("Week (all models)", isOn: $showWeek)
                            .disabled(showWeek && !showSession && !showModel)
                        Toggle("Model week", isOn: $showModel)
                            .disabled(showModel && !showSession && !showWeek)
                    }
                    Picker("Gauge style", selection: $floatStyle) {
                        Text("One line").tag("line")
                        Text("Square").tag("square")
                        Text("Rings").tag("rings")
                    }
                    Picker("Rings centre", selection: $ringsCentre) {
                        Text("Week largest").tag("week")
                        Text("Session largest").tag("session")
                    }
                    Picker("Gauge opacity", selection: $gaugeOpacity) {
                        Text("Clear").tag(0.0)
                        Text("25 %").tag(0.25)
                        Text("50 %").tag(0.5)
                        Text("75 %").tag(0.75)
                        Text("Solid").tag(1.0)
                    }
                    Divider()
                    Picker("Menu bar shows", selection: $menuBarMetric) {
                        Text("Worst limit").tag("worst")
                        Text("Session (5 h)").tag("session")
                        Text("Week (all models)").tag("week")
                        Text("Model week").tag("model")
                    }
                    Toggle("Percent in menu bar", isOn: $menuBarShowPct)
                    Picker("Warn at", selection: $warnThreshold) {
                        Text("Off").tag(0.0)
                        Text("80%").tag(80.0)
                        Text("90%").tag(90.0)
                        Text("95%").tag(95.0)
                    }
                    Toggle("Status file", isOn: $statusExportEnabled)
                    Divider()
                    Toggle("Check for updates automatically", isOn: $autoUpdateCheck)
                    Button("Check for updates…") {
                        Task { await model.checkForUpdates(manual: true) }
                    }
                    Toggle("Launch at login", isOn: Binding(
                        get: { SMAppService.mainApp.status == .enabled },
                        set: { on in
                            try? on ? SMAppService.mainApp.register()
                                    : SMAppService.mainApp.unregister()
                        }))
                    Divider()
                    Button("Quit Claude Meter") { NSApp.terminate(nil) }
                } label: {
                    Image(systemName: "gearshape").font(.system(size: 11))
                }
                .menuStyle(.borderlessButton)
                .frame(width: 24)
                }
            }
        }
        .padding(14)
        .frame(width: 336)
    }
}

// Background of the floating gauge. macOS 26+: Liquid Glass tinted with the
// window colour at `opacity` (0 = clear glass, 1 = near-solid). Older macOS:
// the frosted material with a window-colour fill over it. `.background(_, in:)`
// (not a background view) keeps the vibrancy-aware secondary text of the cards.
struct GaugeBackground<S: InsettableShape>: ViewModifier {
    let shape: S
    let opacity: Double
    var frosted = false   // previews: force the pre-26 path (glass can't be drawn off-screen)

    @ViewBuilder
    func body(content: Content) -> some View {
        let tint = Color(nsColor: .windowBackgroundColor).opacity(opacity)
        if #available(macOS 26.0, *), !frosted {
            // Glass tint alone never gets opaque (and darkens the colour), so a
            // window-colour fill under the glass takes over towards 1 (squared:
            // little at low values) while the tint fades out.
            let solid = opacity * opacity
            content
                .glassEffect(.regular.tint(tint.opacity(1 - solid)), in: shape)
                .background(shape.fill(Color(nsColor: .windowBackgroundColor).opacity(solid)))
                .overlay(shape.strokeBorder(Color.primary.opacity(0.12)))
        } else {
            content
                .background(shape.fill(tint))
                .background(.regularMaterial, in: shape)
                .overlay(shape.strokeBorder(Color.primary.opacity(0.12)))
        }
    }
}

struct FloatingView: View {
    @ObservedObject var model: UsageModel
    @AppStorage("floatStyle") private var storedStyle = FloatStyle.current
    @AppStorage("ringsCentre") private var storedCentre = "week"
    @AppStorage("showSession") private var showSession = true
    @AppStorage("showWeek") private var showWeek = true
    @AppStorage("showModel") private var showModel = true
    @AppStorage("gaugeOpacity") private var storedOpacity = GaugeOpacity.fallback
    var forceSel: GaugeSelection? = nil
    private var sel: GaugeSelection {
        forceSel ?? GaugeSelection(session: showSession, week: showWeek, model: showModel)
    }
    var forceOpacity: Double? = nil
    var forceFrosted = false
    private var opacity: Double { GaugeOpacity.clamp(forceOpacity ?? storedOpacity) }
    var forceStyle: String? = nil   // previews: don't depend on (or touch) user defaults
    var forceCentre: String? = nil
    private var centre: String { forceCentre ?? storedCentre }
    private var style: String { forceStyle ?? storedStyle }
    // Rings sit on a circular disc; the "no data" text keeps the rounded card.
    private var disc: Bool { style == "rings" && model.snapshot != nil }

    private var content: some View {
        TimelineView(.periodic(from: .now, by: 30)) { _ in
            Group {
                if let snap = model.snapshot {
                    switch style {
                    case "square": squareLayout(snap)
                    case "rings":  ringsLayout(snap)
                    default:       wideLayout(snap)
                    }
                } else {
                    Text(model.errorText ?? "Claude Meter…")
                        .font(.system(size: 10)).foregroundStyle(.secondary)
                        .frame(maxWidth: 180)
                }
            }
            .padding(.horizontal, disc ? 10 : 14).padding(.vertical, 10)
        }
    }

    @ViewBuilder
    var body: some View {
        if disc {
            content.modifier(GaugeBackground(shape: Circle(), opacity: opacity, frosted: forceFrosted))
        } else {
            content.modifier(GaugeBackground(shape: RoundedRectangle(cornerRadius: 14, style: .continuous),
                                             opacity: opacity, frosted: forceFrosted))
        }
    }

    // Rings: Apple-Watch-style concentric arcs. Outer = week (all models),
    // middle = primary per-model week, inner = session; only the selected
    // gauges (and the model one only if it exists). Centre stacks the
    // percentages in the same order.
    private func ringsLayout(_ snap: UsageSnapshot) -> some View {
        let week = snap.weeklyAll?.percent ?? 0
        let sess = snap.session?.percent ?? 0
        let m = snap.primaryModel
        var tip = ["Session \(Int(sess.rounded()))% · \(resetText(snap.session?.resetsAt))",
                   "Week \(Int(week.rounded()))% · \(resetText(snap.weeklyAll?.resetsAt))"]
        if let m { tip.append("\(snap.modelName(m)) \(Int(m.percent.rounded()))% · \(resetText(m.resetsAt))") }
        // Visible rings from outside in (week, model, session): 108 / 84 / 60.
        let kinds = sel.rings(snap)
        let diameters: [CGFloat] = [108, 84, 60]
        func pct(_ k: GaugeKind) -> Double { snap.entry(k)?.percent ?? 0 }
        // "week" (default): outermost on top, innermost at the bottom;
        // "session" reverses it. Lines are cap-height tight; sizes follow
        // position and are scaled together so the stack fits the hole.
        let ordered = centre == "session" ? Array(kinds.reversed()) : kinds
        let fonts: [CGFloat] = kinds.count >= 3 ? [19, 15, 12] : (kinds.count == 2 ? [19, 13] : [22])
        let scale = Self.centreScale(texts: ordered.map { "\(Int(pct($0).rounded()))" },
                                     fonts: fonts, hole: diameters[kinds.count - 1] - 18)
        let labels: [(String, CGFloat)] = kinds.enumerated().map { i, k in
            (bandLabel(k, snap), (diameters[i] - 9) / 2)
        }
        return ZStack {
            ForEach(Array(kinds.enumerated()), id: \.element) { i, k in
                ringArc(pct(k), diameter: diameters[i])
            }
            Canvas { ctx, size in
                let c = CGPoint(x: size.width / 2, y: size.height / 2)
                for (text, r) in labels { Self.drawCurved(text, radius: r, centre: c, ctx: ctx) }
            }
            .frame(width: 108, height: 108)
            .allowsHitTesting(false)
            VStack(spacing: scale) {
                ForEach(Array(ordered.enumerated()), id: \.element) { i, k in
                    ringNumber(pct(k), fonts[i] * scale)
                }
            }
        }
        .frame(width: 108, height: 108)
        .help(tip.joined(separator: "\n"))
    }

    // `diameter` is the ring's outer edge; the stroked path sits half a line
    // width inside it so nothing overhangs the 108 pt frame.
    private func ringArc(_ pct: Double, diameter: CGFloat) -> some View {
        let t = max(0.003, min(pct, 100) / 100)
        let d = diameter - 9
        return ZStack {
            Circle().stroke(Color.primary.opacity(0.11), lineWidth: 9)
            // Hard near-black hairline (0.8 pt each side, caps included) so
            // amber/orange arcs don't melt into the grey disc.
            Circle().trim(from: 0, to: t)
                .stroke(Color.black.opacity(0.85), style: StrokeStyle(lineWidth: 10.6, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(.easeOut(duration: 0.6), value: pct)
            Circle().trim(from: 0, to: t)
                .stroke(Sev.color(pct), style: StrokeStyle(lineWidth: 9, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(.easeOut(duration: 0.6), value: pct)
        }
        .frame(width: d, height: d)
    }

    // Largest scale (<= 1) at which the whole number stack fits the hole:
    // inside a square of 0.8 x hole on both axes, and with every row's outer
    // corner within 0.45 x hole of the centre (a square alone lets the top and
    // bottom rows touch a round hole). Row height is 0.70 x font size, 1 pt
    // spacing; hole is the clear diameter inside the innermost stroke.
    static func centreScale(texts: [String], fonts: [CGFloat], hole: CGFloat) -> CGFloat {
        let n = min(texts.count, fonts.count)
        let widths = (0..<n).map { OutlinedNumber.textWidth(texts[$0], size: fonts[$0]) }
        let heights = (0..<n).map { fonts[$0] * 0.70 }
        let total = heights.reduce(0, +) + CGFloat(max(n - 1, 0))
        var top = -total / 2, corner: CGFloat = 0
        for i in 0..<n {
            let bottom = top + heights[i]
            corner = max(corner, hypot(widths[i] / 2, max(abs(top), abs(bottom))))
            top = bottom + 1
        }
        let box = 0.8 * hole
        return min(1, box / max(widths.max() ?? 1, 1), box / max(total, 1), 0.45 * hole / max(corner, 1))
    }

    private func bandLabel(_ k: GaugeKind, _ snap: UsageSnapshot) -> String {
        switch k {
        case .week:    return "total"
        case .session: return "session"
        case .model:
            guard let m = snap.primaryModel else { return "model" }
            let n = snap.modelName(m).lowercased()
            return n.isEmpty ? "model" : n
        }
    }

    // Draws `text` glyph by glyph along the circle of `radius` around `centre`,
    // starting at 12 o'clock and running clockwise; each glyph is rotated to the
    // tangent with its cap-height centred on the circle. Thin weight, cap height
    // 60 % of the 9 pt band; shrunk if the label would pass a quarter turn.
    static func drawCurved(_ text: String, radius r: CGFloat, centre c: CGPoint, ctx: GraphicsContext) {
        func font(_ s: CGFloat) -> NSFont { .systemFont(ofSize: s, weight: .light) }
        func advances(_ s: CGFloat) -> [CGFloat] {
            text.map { NSAttributedString(string: String($0), attributes: [.font: font(s)]).size().width }
        }
        var size = 9 * 0.6 / (font(100).capHeight / 100)
        var adv = advances(size)
        let total = adv.reduce(0, +), quarter = (CGFloat.pi / 2) * r
        if total > quarter { size *= quarter / total; adv = advances(size) }
        let f = font(size)
        // SwiftUI centres the line box; move the glyph so the cap-height centre sits on the circle.
        let lift = (f.ascender + f.descender) / 2 - f.capHeight / 2
        var s: CGFloat = 0
        for (ch, a) in zip(text, adv) {
            let theta = (s + a / 2) / r
            s += a
            var g = ctx
            g.translateBy(x: c.x + r * sin(theta), y: c.y - r * cos(theta))
            g.rotate(by: .radians(theta))
            let t = Text(String(ch)).font(.system(size: size, weight: .light))
                .foregroundColor(Color.primary.opacity(0.8))
            g.draw(t, at: CGPoint(x: 0, y: -lift), anchor: .center)
        }
    }

    private func ringNumber(_ pct: Double, _ size: CGFloat) -> some View {
        OutlinedNumber(text: "\(Int(pct.rounded()))", size: size, color: Sev.nsColor(pct))
            .frame(height: size * 0.70)
    }

    // One line: rings on the left, a centred text column on the right: title,
    // "resets in …", clock (the reset text split at its " · "; without one it
    // stays a single line). The column is as wide as its widest line.
    private func wideLayout(_ snap: UsageSnapshot) -> some View {
        let reset = resetText(snap.session?.resetsAt)
        let parts: [String]
        if let r = reset.range(of: " · ") {
            parts = [String(reset[..<r.lowerBound]), String(reset[r.upperBound...])]
        } else {
            parts = [reset]
        }
        return HStack(spacing: 14) {
            miniRings(snap)
            VStack(alignment: .center, spacing: 2) {
                Text("Claude").font(.system(size: 10, weight: .bold))
                    .foregroundStyle(.secondary)
                ForEach(Array(parts.enumerated()), id: \.offset) { _, line in
                    Text(line).font(.system(size: 9)).foregroundStyle(.secondary)
                }
            }
            .lineLimit(1)
            .fixedSize()
        }
    }

    // Square: gauges first row, reset countdown second row.
    private func squareLayout(_ snap: UsageSnapshot) -> some View {
        VStack(spacing: 7) {
            HStack(spacing: 16) {
                miniRings(snap)
            }
            Text(resetText(snap.session?.resetsAt))
                .font(.system(size: 9)).foregroundStyle(.secondary)
                .lineLimit(1)
                .fixedSize()
        }
    }

    // Selected mini rings in the popover's order: session, week, model.
    @ViewBuilder
    private func miniRings(_ snap: UsageSnapshot) -> some View {
        ForEach(sel.row(snap), id: \.self) { k in
            switch k {
            case .session: miniRing(snap.session, "5 h")
            case .week:    miniRing(snap.weeklyAll, "week")
            case .model:   if let m = snap.primaryModel { miniRing(m, snap.modelName(m).lowercased()) }
            }
        }
    }

    @ViewBuilder
    private func miniRing(_ entry: LimitEntry?, _ tag: String) -> some View {
        let pct = entry?.percent ?? 0
        VStack(spacing: 2) {
            ZStack {
                Circle().stroke(Color.primary.opacity(0.18), lineWidth: 3.5)
                Circle()
                    .trim(from: 0, to: max(0.003, min(pct, 100) / 100))
                    .stroke(Color.black.opacity(0.32), style: StrokeStyle(lineWidth: 5.2, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                Circle()
                    .trim(from: 0, to: max(0.003, min(pct, 100) / 100))
                    .stroke(Sev.color(pct), style: StrokeStyle(lineWidth: 3.5, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                Text("\(Int(pct.rounded()))")
                    .font(.system(size: 11, weight: .semibold, design: .rounded))
                    .monospacedDigit()
            }
            .frame(width: 34, height: 34)
            Text(tag).font(.system(size: 8)).foregroundStyle(.tertiary)
        }
    }
}

// Centre number of the rings disc: filled in the ring colour with a fine dark-grey
// outline. SwiftUI Text can't stroke glyphs, so this wraps NSTextFields with
// attributed strings: a stroke-only layer (strokeWidth +6 = 6 % of the point
// size, dark grey) under a fill-only layer in the ring colour. A single fill+stroke
// string (negative strokeWidth) centres the stroke on the glyph edge and eats
// into the digit; stacking the layers leaves the full bold fill with the
// outline hugging it from outside.
struct OutlinedNumber: NSViewRepresentable {
    let text: String
    let size: CGFloat
    let color: NSColor

    // Dark-grey outline (#3A3A3A) in both appearances.
    static let outlineColor = NSColor(red: 0.227, green: 0.227, blue: 0.227, alpha: 1)

    static func font(size: CGFloat) -> NSFont {
        var desc = NSFont.systemFont(ofSize: size, weight: .bold).fontDescriptor
        if let rounded = desc.withDesign(.rounded) { desc = rounded }
        desc = desc.addingAttributes([.featureSettings: [[
            NSFontDescriptor.FeatureKey.typeIdentifier: kNumberSpacingType,
            NSFontDescriptor.FeatureKey.selectorIdentifier: kMonospacedNumbersSelector]]])
        return NSFont(descriptor: desc, size: size) ?? .systemFont(ofSize: size, weight: .bold)
    }

    static func textWidth(_ text: String, size: CGFloat) -> CGFloat {
        NSAttributedString(string: text, attributes: [.font: font(size: size)]).size().width
    }

    private func attributed(stroke: Bool) -> NSAttributedString {
        let font = Self.font(size: size)
        if stroke {
            return NSAttributedString(string: text, attributes: [
                .font: font,
                .foregroundColor: Self.outlineColor,
                .strokeColor: Self.outlineColor,
                .strokeWidth: 6,
            ])
        }
        return NSAttributedString(string: text, attributes: [.font: font, .foregroundColor: color])
    }

    func makeNSView(context: Context) -> OutlinedNumberHost {
        OutlinedNumberHost(back: Self.label(), front: Self.label())
    }

    private static func label() -> NSTextField {
        let f = NSTextField(labelWithString: "")
        f.cell = ExactTextCell()
        f.isEditable = false
        f.isSelectable = false
        f.isBezeled = false
        f.isBordered = false
        f.drawsBackground = false
        f.alignment = .center
        f.lineBreakMode = .byClipping
        f.usesSingleLineMode = false   // single-line mode shifts the baseline up
        f.cell?.wraps = false
        return f
    }

    func updateNSView(_ host: OutlinedNumberHost, context: Context) {
        host.back.attributedStringValue = attributed(stroke: true)
        host.front.attributedStringValue = attributed(stroke: false)
        host.needsLayout = true
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: OutlinedNumberHost, context: Context) -> CGSize? {
        CGSize(width: ceil(attributed(stroke: false).size().width) + 4, height: size * 0.70)
    }
}

final class ExactTextCell: NSTextFieldCell {
    override func drawingRect(forBounds rect: NSRect) -> NSRect { rect }
}

// Both labels sit at their intrinsic size, centred in whatever frame the stack
// gives the view (SwiftUI Text centres its line box the same way), so the
// 0.70 × font-height rows keep their metrics.
final class OutlinedNumberHost: NSView {
    let back: NSTextField
    let front: NSTextField
    init(back: NSTextField, front: NSTextField) {
        self.back = back
        self.front = front
        super.init(frame: .zero)
        addSubview(back)
        addSubview(front)
    }
    required init?(coder: NSCoder) { fatalError() }
    override func layout() {
        super.layout()
        let s = front.cell!.cellSize
        let r = NSRect(x: (bounds.width - ceil(s.width)) / 2, y: (bounds.height - ceil(s.height)) / 2,
                       width: ceil(s.width), height: ceil(s.height))
        back.frame = r
        front.frame = r
    }
}

// Window-background dragging is decided by the deepest view under the click,
// and SwiftUI's internal views can refuse it. The gauge has no interactive
// controls, so a transparent overlay catches every click and drives the drag
// explicitly — no hit-testing heuristics involved.
final class DragOverlayView: NSView {
    // Builds the same menu as the status item's right-click.
    var menuProvider: (() -> NSMenu)?
    override var mouseDownCanMoveWindow: Bool { true }
    override func mouseDown(with event: NSEvent) {
        window?.performDrag(with: event)
    }
    func makeContextMenu() -> NSMenu? { menuProvider?() }
    override func rightMouseDown(with event: NSEvent) {
        guard let menu = makeContextMenu() else { return }
        menu.popUp(positioning: nil, at: convert(event.locationInWindow, from: nil), in: self)
    }
}

// MARK: - App controller (status item, popover, floating panel)

@MainActor
final class AppController: NSObject, NSApplicationDelegate, NSPopoverDelegate {
    let model = UsageModel()
    private var statusItem: NSStatusItem!
    private var popover: NSPopover!
    private var panel: NSPanel?
    private var lastStyle = FloatStyle.current
    private var lastSel = GaugeSelection.current
    private var cancellables = Set<AnyCancellable>()

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let btn = statusItem.button {
            btn.target = self
            btn.action = #selector(statusClicked(_:))
            btn.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }

        popover = NSPopover()
        popover.behavior = .transient
        popover.delegate = self
        popover.contentViewController = NSHostingController(
            rootView: PopoverView(model: model, controller: self))

        model.$snapshot
            .combineLatest(model.$errorText)
            .receive(on: RunLoop.main)
            .sink { [weak self] _, _ in self?.updateStatusButton() }
            .store(in: &cancellables)

        // React to preference changes (menu-bar metric/percent, gauge layout)
        // regardless of whether the floating panel exists.
        NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.updateStatusButton()
                self.model.applyUpdatePreference()
                let st = FloatStyle.current
                let gs = GaugeSelection.current
                if st != self.lastStyle || gs != self.lastSel {
                    self.lastStyle = st
                    self.lastSel = gs
                    DispatchQueue.main.async { self.sizeFloatingPanel() }
                }
            }
        }

        // The one-line panel's width follows its text, which changes with every
        // snapshot and as the countdown ticks.
        model.$snapshot
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in DispatchQueue.main.async { self?.sizeFloatingPanel() } }
            .store(in: &cancellables)
        Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.sizeFloatingPanel() }
        }

        updateStatusButton()
        UpdateLauncher.seedRepoPath()
        model.start()

        if UserDefaults.standard.object(forKey: "showFloating") == nil {
            UserDefaults.standard.set(true, forKey: "showFloating")
        }
        if UserDefaults.standard.bool(forKey: "showFloating") { showFloatingWindow() }
    }

    // Menu bar: colored ring icon + percent. Which limit it tracks ("worst",
    // session, week, or model) and whether the % text shows are gear-menu options.
    private func updateStatusButton() {
        guard let btn = statusItem.button else { return }
        let d = UserDefaults.standard
        let snap = model.snapshot
        let chosen: LimitEntry? = {
            guard let snap else { return nil }
            switch d.string(forKey: "menuBarMetric") ?? "worst" {
            case "session": return snap.session
            case "week":    return snap.weeklyAll
            case "model":   return snap.primaryModel ?? snap.limits.max { $0.percent < $1.percent }
            default:        return snap.limits.max { $0.percent < $1.percent }
            }
        }()
        let pct = chosen?.percent
        btn.image = Self.ringImage(pct: pct)
        btn.imagePosition = .imageLeft
        let showPct = d.object(forKey: "menuBarShowPct") == nil
            ? true : d.bool(forKey: "menuBarShowPct")
        // A broken data path (auth expired, endpoint down) shows a red "!"
        // even with the percent hidden — errors shouldn't be invisible.
        if model.errorText != nil, model.snapshot == nil {
            btn.attributedTitle = NSAttributedString(string: " !", attributes: [
                .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .bold),
                .foregroundColor: NSColor.systemRed,
            ])
        } else {
            let text = showPct ? (pct.map { " \(Int($0.rounded()))%" } ?? " –") : ""
            btn.attributedTitle = NSAttributedString(string: text, attributes: [
                .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium)
            ])
        }
        btn.toolTip = snap.map { s in
            s.limits.map { "\($0.label): \(Int($0.percent.rounded()))%" }
                .joined(separator: "\n")
        } ?? model.errorText
    }

    private static func ringImage(pct: Double?) -> NSImage {
        let side: CGFloat = 16
        let img = NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            let center = CGPoint(x: rect.midX, y: rect.midY)
            let radius = side / 2 - 1.6
            let track = NSBezierPath()
            track.appendArc(withCenter: center, radius: radius, startAngle: 0, endAngle: 360)
            track.lineWidth = 2.6
            NSColor.secondaryLabelColor.withAlphaComponent(0.35).setStroke()
            track.stroke()
            if let pct {
                let frac = max(0.02, min(pct, 100) / 100)
                let arc = NSBezierPath()
                arc.appendArc(withCenter: center, radius: radius,
                              startAngle: 90, endAngle: 90 - 360 * frac, clockwise: true)
                arc.lineWidth = 2.6
                arc.lineCapStyle = .round
                Sev.nsColor(pct).setStroke()
                arc.stroke()
            }
            return true
        }
        img.isTemplate = false
        return img
    }

    @objc private func statusClicked(_ sender: NSStatusBarButton) {
        if NSApp.currentEvent?.type == .rightMouseUp {
            showContextMenu()
            return
        }
        if popover.isShown {
            popover.performClose(nil)
        } else {
            if let snap = model.snapshot, Date().timeIntervalSince(snap.fetchedAt) > 30 {
                Task { await model.refresh() }
            } else if model.snapshot == nil {
                Task { await model.refresh() }
            }
            popover.show(relativeTo: sender.bounds, of: sender, preferredEdge: .minY)
            popover.contentViewController?.view.window?.makeKey()
        }
    }

    private func showContextMenu() {
        statusItem.menu = buildContextMenu()
        statusItem.button?.performClick(nil)
        statusItem.menu = nil   // restore click handling
    }

    // Shared by the status item's right-click and the floating gauge's.
    func buildContextMenu() -> NSMenu {
        let menu = NSMenu()
        if model.needsSignIn {
            menu.addItem(withTitle: "Sign in to Claude Code…", action: #selector(menuSignIn), keyEquivalent: "").target = self
            menu.addItem(.separator())
        }
        menu.addItem(withTitle: "Refresh now", action: #selector(menuRefresh), keyEquivalent: "r").target = self
        let floatItem = NSMenuItem(title: "Show desktop gauge", action: #selector(menuToggleFloat), keyEquivalent: "")
        floatItem.target = self
        floatItem.state = (panel?.isVisible == true) ? .on : .off
        menu.addItem(floatItem)
        menu.addItem(Self.makeGaugesItem(sel: .current, target: self))
        let styleItem = NSMenuItem(title: "Gauge style", action: nil, keyEquivalent: "")
        let styleMenu = NSMenu(title: "Gauge style")
        let current = FloatStyle.current
        for (title, key) in [("One line", "line"), ("Square", "square"), ("Rings", "rings")] {
            let item = NSMenuItem(title: title, action: #selector(menuSetStyle(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = key
            item.state = (current == key) ? .on : .off
            styleMenu.addItem(item)
        }
        styleItem.submenu = styleMenu
        menu.addItem(styleItem)
        let centreItem = NSMenuItem(title: "Rings centre", action: nil, keyEquivalent: "")
        let centreMenu = NSMenu(title: "Rings centre")
        let centreNow = UserDefaults.standard.string(forKey: "ringsCentre") ?? "week"
        for (title, key) in [("Week largest", "week"), ("Session largest", "session")] {
            let item = NSMenuItem(title: title, action: #selector(menuSetCentre(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = key
            item.state = (centreNow == key) ? .on : .off
            centreMenu.addItem(item)
        }
        centreItem.submenu = centreMenu
        menu.addItem(centreItem)
        menu.addItem(makeOpacityItem())
        menu.addItem(.separator())
        if let up = model.availableUpdate {
            menu.addItem(withTitle: "Update to v\(up.version)…", action: #selector(menuUpdate), keyEquivalent: "").target = self
        }
        menu.addItem(withTitle: "Check for updates…", action: #selector(menuCheckUpdates), keyEquivalent: "").target = self
        menu.addItem(withTitle: "Quit Claude Meter", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        return menu
    }

    // "Gauge opacity": a label over a continuous 0...1 slider (160 pt), written to
    // `gaugeOpacity` on every change so the float follows live.
    func makeOpacityItem() -> NSMenuItem {
        let item = NSMenuItem(title: "Gauge opacity", action: nil, keyEquivalent: "")
        let pad: CGFloat = 14, sliderWidth: CGFloat = 160
        let view = NSView(frame: NSRect(x: 0, y: 0, width: sliderWidth + 2 * pad, height: 44))
        let label = NSTextField(labelWithString: "Gauge opacity")
        label.font = .menuFont(ofSize: 0)
        label.sizeToFit()
        label.frame.origin = NSPoint(x: pad, y: 24)
        let slider = NSSlider(value: GaugeOpacity.current, minValue: 0, maxValue: 1,
                              target: self, action: #selector(menuSetOpacity(_:)))
        slider.isContinuous = true
        slider.frame = NSRect(x: pad, y: 4, width: sliderWidth, height: 18)
        slider.toolTip = "Left: clear glass, right: solid"
        view.addSubview(label)
        view.addSubview(slider)
        item.view = view
        return item
    }

    @objc private func menuSetOpacity(_ sender: NSSlider) {
        UserDefaults.standard.set(GaugeOpacity.clamp(sender.doubleValue), forKey: GaugeOpacity.key)
    }

    @objc private func menuSignIn() { model.startSignIn() }
    @objc private func menuRefresh() { Task { await model.refresh() } }
    @objc private func menuToggleFloat() { toggleFloatingWindow() }
    @objc private func menuUpdate() { UpdateLauncher.launch(releaseURL: model.availableUpdate?.url) }
    @objc private func menuCheckUpdates() { Task { await model.checkForUpdates(manual: true) } }
    @objc private func menuSetCentre(_ sender: NSMenuItem) {
        guard let key = sender.representedObject as? String else { return }
        UserDefaults.standard.set(key, forKey: "ringsCentre")
    }
    // "Gauges" submenu of the right-click menu: one check item per limit; the
    // last one on is disabled so at least one gauge always stays.
    static func makeGaugesItem(sel gs: GaugeSelection, target: AnyObject?) -> NSMenuItem {
        let gaugesItem = NSMenuItem(title: "Gauges", action: nil, keyEquivalent: "")
        let gaugesMenu = NSMenu(title: "Gauges")
        gaugesMenu.autoenablesItems = false
        for (title, key, on) in [("Session (5 h)", "showSession", gs.session),
                                 ("Week (all models)", "showWeek", gs.week),
                                 ("Model week", "showModel", gs.model)] {
            let item = NSMenuItem(title: title, action: #selector(menuToggleGauge(_:)), keyEquivalent: "")
            item.target = target
            item.representedObject = key
            item.state = on ? .on : .off
            item.isEnabled = !(on && gs.onCount == 1)
            gaugesMenu.addItem(item)
        }
        gaugesItem.submenu = gaugesMenu
        return gaugesItem
    }

    @objc fileprivate func menuToggleGauge(_ sender: NSMenuItem) {
        guard let key = sender.representedObject as? String else { return }
        let gs = GaugeSelection.current
        let on = UserDefaults.standard.object(forKey: key) as? Bool ?? true
        if on && gs.onCount == 1 { return }
        UserDefaults.standard.set(!on, forKey: key)
    }
    @objc private func menuSetStyle(_ sender: NSMenuItem) {
        guard let key = sender.representedObject as? String else { return }
        UserDefaults.standard.set(key, forKey: "floatStyle")
    }

    func toggleFloatingWindow() {
        if panel?.isVisible == true {
            panel?.orderOut(nil)
            UserDefaults.standard.set(false, forKey: "showFloating")
        } else {
            showFloatingWindow()
            UserDefaults.standard.set(true, forKey: "showFloating")
        }
    }

    private func showFloatingWindow() {
        if panel == nil {
            let hosting = NSHostingView(rootView: FloatingView(model: model))
            let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 280, height: 64),
                            styleMask: [.borderless, .nonactivatingPanel],
                            backing: .buffered, defer: false)
            p.contentView = hosting
            let overlay = DragOverlayView(frame: hosting.bounds)
            overlay.autoresizingMask = [.width, .height]
            overlay.menuProvider = { [weak self] in self?.buildContextMenu() ?? NSMenu() }
            hosting.addSubview(overlay)
            p.isOpaque = false
            p.backgroundColor = .clear
            p.level = .floating
            p.hasShadow = true
            p.isMovableByWindowBackground = true
            p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            p.hidesOnDeactivate = false
            panel = p

            // Restore position only (an earlier build autosaved a 0×0 frame —
            // never persist the size).
            if let s = UserDefaults.standard.string(forKey: "floatOrigin") {
                p.setFrameOrigin(NSPointFromString(s))
            } else if let screen = NSScreen.main {
                p.setFrameOrigin(NSPoint(x: screen.visibleFrame.maxX - 320,
                                         y: screen.visibleFrame.maxY - 100))
            }
            NotificationCenter.default.addObserver(
                forName: NSWindow.didMoveNotification, object: p, queue: .main) { _ in
                MainActor.assumeIsolated {
                    if let f = self.panel?.frame {
                        UserDefaults.standard.set(NSStringFromPoint(f.origin),
                                                  forKey: "floatOrigin")
                    }
                }
            }
        }
        sizeFloatingPanel()
        panel?.orderFrontRegardless()
    }

    private func sizeFloatingPanel() {
        guard let p = panel, let hosting = p.contentView else { return }
        hosting.layoutSubtreeIfNeeded()
        var sz = hosting.fittingSize
        // fittingSize of a hosted TimelineView can come back 0 — never let the
        // panel collapse; fall back to known-good sizes per layout.
        if sz.width < 60 || sz.height < 30 {
            switch FloatStyle.current {
            case "square": sz = NSSize(width: 240, height: 100)
            case "rings":  sz = NSSize(width: 128, height: 128)   // 108 + 2×10
            default:       sz = NSSize(width: 340, height: 64)
            }
        }
        let topLeft = NSPoint(x: p.frame.minX, y: p.frame.maxY)
        p.setContentSize(sz)
        p.setFrameTopLeftPoint(topLeft)
        if let screen = p.screen ?? NSScreen.main {
            var o = p.frame.origin
            o.x = min(max(o.x, screen.visibleFrame.minX), screen.visibleFrame.maxX - p.frame.width)
            o.y = min(max(o.y, screen.visibleFrame.minY), screen.visibleFrame.maxY - p.frame.height)
            p.setFrameOrigin(o)
        }
    }

    func popoverDidClose(_ notification: Notification) {}
}

// MARK: - Entry point

// `--once`: headless smoke test — fetch and print, no UI. Used by build/test.
if CommandLine.arguments.contains("--once") {
    let sem = DispatchSemaphore(value: 0)
    Task {
        do {
            let (snap, plan) = try await UsageAPI.fetchUsage()
            print("plan: \(plan ?? "?")")
            for l in snap.limits {
                let reset = l.resetsAt.map { clockString($0, fmtDayTime) } ?? "—"
                print("\(l.label.padding(toLength: 22, withPad: " ", startingAt: 0)) \(String(format: "%5.1f", l.percent))%  resets \(reset)")
            }
        } catch {
            print("ERROR: \(error.localizedDescription)")
        }
        sem.signal()
    }
    sem.wait()
    exit(0)
}

// `--preview-signin <out.png>` / `--preview-signin-waiting <out.png>`: render the
// signed-out popover to a PNG for visual checks. No network, no keychain.
for (flag, waiting) in [("--preview-signin", false), ("--preview-signin-waiting", true)] {
    let args = CommandLine.arguments
    guard let i = args.firstIndex(of: flag) else { continue }
    guard i + 1 < args.count else { print("usage: \(flag) <out.png>"); exit(2) }
    let out = args[i + 1]
    MainActor.assumeIsolated {
        _ = NSApplication.shared
        let model = UsageModel()
        model.errorText = APIError.reauthNeeded.errorDescription
        model.needsSignIn = true
        if waiting { model.signInLaunchedAt = Date() }
        var pv = PopoverView(model: model, controller: AppController())
        pv.staticPreview = true
        let view = pv
            .background(Color(nsColor: .windowBackgroundColor))
        let renderer = ImageRenderer(content: view)
        renderer.scale = 2
        guard let cg = renderer.cgImage,
              let png = NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])
        else { print("render failed"); exit(1) }
        do { try png.write(to: URL(fileURLWithPath: out)) } catch { print("write failed: \(error)"); exit(1) }
        exit(0)
    }
}

// `--preview-popover` / `--preview-float-wide` / `--preview-float-square` /
// `--preview-float-rings` (+ `-noscoped`, `-session`)
// (+ `--preview-popover-noscoped`, the two-ring fallback) <out.png>: render the
// views from one shared fake snapshot. No network, no keychain.
func fakeSnapshot(withScoped: Bool = true) -> UsageSnapshot {
    let now = Date()
    var limits = [
        LimitEntry(id: "sessionSession (5 h)", kind: "session", label: "Session (5 h)",
                   percent: 52, resetsAt: now.addingTimeInterval(33 * 60), isActive: true),
        LimitEntry(id: "weekly_allWeek — all models", kind: "weekly_all", label: "Week — all models",
                   percent: 26, resetsAt: now.addingTimeInterval(117 * 3600), isActive: false),
    ]
    if withScoped {
        limits.append(LimitEntry(id: "weekly_scopedWeek — Fable", kind: "weekly_scoped",
                                 label: "Week — Fable", percent: 39,
                                 resetsAt: now.addingTimeInterval(117 * 3600), isActive: false))
    }
    return UsageSnapshot(fetchedAt: now, limits: limits)
}

// ImageRenderer can't draw AppKit-backed views (the rings' outlined numbers),
// so rings previews go through an off-screen NSHostingView at 2x.
@MainActor
func renderHosted<V: View>(_ view: V) -> CGImage? {
    let host = NSHostingView(rootView: view)
    let win = NSWindow(contentRect: NSRect(origin: .zero, size: host.fittingSize),
                       styleMask: [.borderless], backing: .buffered, defer: false)
    win.contentView = host
    host.frame = NSRect(origin: .zero, size: host.fittingSize)
    host.layoutSubtreeIfNeeded()
    let b = host.bounds
    guard b.width > 0, b.height > 0,
          let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(b.width * 2),
                                     pixelsHigh: Int(b.height * 2), bitsPerSample: 8,
                                     samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                     colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
    else { return nil }
    rep.size = b.size
    host.cacheDisplay(in: b, to: rep)
    return rep.cgImage
}

// Liquid Glass is composited by the window server, so cacheDisplay/ImageRenderer
// draw it blank. This puts the view in a real borderless window on screen for a
// moment and grabs that window with screencapture(1) (2x, no shadow).
@MainActor
func renderOnScreen<V: View>(_ view: V, to out: String) -> Bool {
    NSApp.setActivationPolicy(.accessory)
    let host = NSHostingView(rootView: view)
    host.frame = NSRect(origin: .zero, size: host.fittingSize)
    let win = NSWindow(contentRect: NSRect(origin: NSPoint(x: 240, y: 240), size: host.fittingSize),
                       styleMask: [.borderless], backing: .buffered, defer: false)
    win.isOpaque = true
    win.hasShadow = false
    win.level = .floating
    win.contentView = host
    win.orderFrontRegardless()
    RunLoop.current.run(until: Date().addingTimeInterval(1.5))
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    p.arguments = ["-x", "-o", "-l", String(win.windowNumber), out]
    do { try p.run() } catch { return false }
    p.waitUntilExit()
    win.orderOut(nil)
    return p.terminationStatus == 0
}

for (flag, kind) in [("--preview-popover", "popover"), ("--preview-popover-noscoped", "noscoped"),
                     ("--preview-float-wide", "wide"), ("--preview-float-square", "square"),
                     ("--preview-float-rings", "rings"), ("--preview-float-rings-noscoped", "rings-noscoped"),
                     ("--preview-float-rings-session", "rings-session"),
                     ("--preview-float-rings-dark", "rings-dark")] {
    let args = CommandLine.arguments
    guard let i = args.firstIndex(of: flag) else { continue }
    guard i + 1 < args.count else { print("usage: \(flag) <out.png>"); exit(2) }
    let out = args[i + 1]
    MainActor.assumeIsolated {
        _ = NSApplication.shared
        if kind == "rings-dark" { NSApp.appearance = NSAppearance(named: .darkAqua) }
        let model = UsageModel()
        model.snapshot = fakeSnapshot(withScoped: kind != "noscoped" && kind != "rings-noscoped")
        model.plan = "max"
        let bg = Color(nsColor: .windowBackgroundColor)
        let image: CGImage?
        switch kind {
        case "popover", "noscoped":
            var pv = PopoverView(model: model, controller: AppController())
            pv.staticPreview = true
            let r = ImageRenderer(content: pv.background(bg))
            r.scale = 2
            image = r.cgImage
        default:
            var fv = FloatingView(model: model)
            fv.forceFrosted = true
            fv.forceCentre = (kind == "rings-session") ? "session" : "week"
            fv.forceStyle = kind.hasPrefix("rings") ? "rings" : (kind == "square" ? "square" : "line")
            if kind.hasPrefix("rings") {
                image = renderHosted(fv.padding(12).background(bg))
            } else {
                let r = ImageRenderer(content: fv.padding(12).background(bg))
                r.scale = 2
                image = r.cgImage
            }
        }
        guard let cg = image,
              let png = NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])
        else { print("render failed"); exit(1) }
        do { try png.write(to: URL(fileURLWithPath: out)) } catch { print("write failed: \(error)"); exit(1) }
        exit(0)
    }
}

// `--preview-float-rings-opacity <out.png> <0..1>` / `--preview-float-wide-opacity
// <out.png> <0..1>`: the float at the given gauge opacity over a busy backdrop (a
// hard-edged diagonal two-colour gradient, preview only) so the glass shows.
for (flag, rings) in [("--preview-float-rings-opacity", true), ("--preview-float-wide-opacity", false)] {
    let args = CommandLine.arguments
    guard let i = args.firstIndex(of: flag) else { continue }
    guard i + 2 < args.count, let value = Double(args[i + 2]) else {
        print("usage: \(flag) <out.png> <opacity 0..1>"); exit(2)
    }
    let out = args[i + 1]
    MainActor.assumeIsolated {
        _ = NSApplication.shared
        let model = UsageModel()
        model.snapshot = fakeSnapshot()
        model.plan = "max"
        var fv = FloatingView(model: model)
        fv.forceCentre = "week"
        fv.forceStyle = rings ? "rings" : "line"
        fv.forceOpacity = value
        let backdrop = LinearGradient(
            stops: [.init(color: Color(red: 0.98, green: 0.62, blue: 0.10), location: 0),
                    .init(color: Color(red: 0.98, green: 0.62, blue: 0.10), location: 0.46),
                    .init(color: Color(red: 0.10, green: 0.25, blue: 0.80), location: 0.54),
                    .init(color: Color(red: 0.10, green: 0.25, blue: 0.80), location: 1)],
            startPoint: .topLeading, endPoint: .bottomTrailing)
        // Optional 4th argument "frosted" renders the pre-macOS-26 path for comparison.
        fv.forceFrosted = i + 3 < args.count && args[i + 3] == "frosted"
        guard renderOnScreen(fv.padding(28).background(backdrop), to: out) else { print("render failed"); exit(1) }
        exit(0)
    }
}

// `--preview-popover-sel <out.png> <flags>` / `--preview-float-rings-sel <out.png> <flags>`:
// like the previews above, with <flags> a string from "swm" (session, week,
// model) naming the gauges that are ON. User defaults are not read or written.
for (flag, rings) in [("--preview-popover-sel", false), ("--preview-float-rings-sel", true)] {
    let args = CommandLine.arguments
    guard let i = args.firstIndex(of: flag) else { continue }
    guard i + 2 < args.count else { print("usage: \(flag) <out.png> <flags swm>"); exit(2) }
    let out = args[i + 1]
    let sel = GaugeSelection(letters: args[i + 2])
    MainActor.assumeIsolated {
        _ = NSApplication.shared
        let model = UsageModel()
        model.snapshot = fakeSnapshot()
        model.plan = "max"
        let bg = Color(nsColor: .windowBackgroundColor)
        let image: CGImage?
        if rings {
            var fv = FloatingView(model: model)
            fv.forceFrosted = true
            fv.forceCentre = "week"
            fv.forceStyle = "rings"
            fv.forceSel = sel
            image = renderHosted(fv.padding(12).background(bg))
        } else {
            var pv = PopoverView(model: model, controller: AppController())
            pv.staticPreview = true
            pv.forceSel = sel
            let r = ImageRenderer(content: pv.background(bg))
            r.scale = 2
            image = r.cgImage
        }
        guard let cg = image,
              let png = NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])
        else { print("render failed"); exit(1) }
        do { try png.write(to: URL(fileURLWithPath: out)) } catch { print("write failed: \(error)"); exit(1) }
        exit(0)
    }
}

// `--selftest-float-menu`: build the menu the floating gauge's right-click pops
// up (same overlay wiring as showFloatingWindow) and print its item titles. No UI.
if CommandLine.arguments.contains("--selftest-float-menu") {
    MainActor.assumeIsolated {
        _ = NSApplication.shared
        let controller = AppController()
        let overlay = DragOverlayView(frame: NSRect(x: 0, y: 0, width: 100, height: 100))
        overlay.menuProvider = { controller.buildContextMenu() }
        guard let menu = overlay.makeContextMenu() else { print("no menu"); exit(1) }
        for item in menu.items { print(item.isSeparatorItem ? "---" : item.title) }
        exit(0)
    }
}

// `--selftest-centre-fit`: for 1, 2 and 3 rings print the number stack's size
// (worst case "100") against the clear hole inside the innermost stroke.
if CommandLine.arguments.contains("--selftest-centre-fit") {
    for (count, fonts, hole) in [(1, [CGFloat(22)], CGFloat(90)), (2, [19, 13], 66), (3, [19, 15, 12], 42)] {
        let texts = Array(repeating: "100", count: count)
        let scale = FloatingView.centreScale(texts: texts, fonts: fonts, hole: hole)
        let w = (0..<count).map { OutlinedNumber.textWidth("100", size: fonts[$0] * scale) }.max() ?? 0
        let h = fonts.reduce(0) { $0 + $1 * 0.70 * scale } + CGFloat(count - 1) * scale
        print(String(format: "rings=%d hole=%.0f scale=%.3f stack=%.1f x %.1f limit=%.1f fits=%@",
                     count, hole, scale, w, h, 0.8 * hole, (w <= 0.8 * hole && h <= 0.8 * hole) ? "yes" : "no"))
    }
    exit(0)
}

// `--selftest-gauges-menu <flags>`: print the right-click "Gauges" submenu
// (title, checked, enabled) for the ON set <flags> ("swm" letters). No UI.
if let i = CommandLine.arguments.firstIndex(of: "--selftest-gauges-menu") {
    let args = CommandLine.arguments
    guard i + 1 < args.count else { print("usage: --selftest-gauges-menu <flags swm>"); exit(2) }
    MainActor.assumeIsolated {
        let item = AppController.makeGaugesItem(sel: GaugeSelection(letters: args[i + 1]), target: nil)
        print("submenu \(item.title)")
        for m in item.submenu!.items {
            print("\(m.title) | checked=\(m.state == .on) | enabled=\(m.isEnabled)")
        }
        exit(0)
    }
}

// `--preview-update <out.png>`: the popover with the fake snapshot plus a fake
// available update. No network, no keychain.
if let i = CommandLine.arguments.firstIndex(of: "--preview-update") {
    let args = CommandLine.arguments
    guard i + 1 < args.count else { print("usage: --preview-update <out.png>"); exit(2) }
    let out = args[i + 1]
    MainActor.assumeIsolated {
        _ = NSApplication.shared
        let model = UsageModel()
        model.snapshot = fakeSnapshot()
        model.plan = "max"
        model.availableUpdate = UpdateChecker.Release(
            tag: "v9.9", version: "9.9",
            url: URL(string: "https://github.com/bernmc/claude-meter/releases")!, notes: "")
        var pv = PopoverView(model: model, controller: AppController())
        pv.staticPreview = true
        let r = ImageRenderer(content: pv.background(Color(nsColor: .windowBackgroundColor)))
        r.scale = 2
        guard let cg = r.cgImage,
              let png = NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])
        else { print("render failed"); exit(1) }
        do { try png.write(to: URL(fileURLWithPath: out)) } catch { print("write failed: \(error)"); exit(1) }
        exit(0)
    }
}

// `--selftest-update-script`: print the generated update script to stdout
// (placeholder checkout path), write and open nothing, exit 0.
if CommandLine.arguments.contains("--selftest-update-script") {
    print(UpdateLauncher.scriptBody(for: "/path/to/claude-meter"), terminator: "")
    exit(0)
}

// `--selftest-remote-check <url>`: print accept/reject for an origin URL using
// the same matcher the app uses before offering Update. Exit 0.
if let i = CommandLine.arguments.firstIndex(of: "--selftest-remote-check") {
    let args = CommandLine.arguments
    guard i + 1 < args.count else { print("usage: --selftest-remote-check <url>"); exit(2) }
    print(UpdateLauncher.isAcceptedRemote(args[i + 1]) ? "accept" : "reject")
    exit(0)
}

// `--check-update`: one GitHub Releases lookup, no UI, no state written.
// Exit 0 on success (including "no releases yet"), 2 on network/API error.
if CommandLine.arguments.contains("--check-update") {
    let sem = DispatchSemaphore(value: 0)
    var exitCode: Int32 = 0
    Task {
        do {
            let r = try await UpdateChecker.latest()
            let newer = r.map { UpdateChecker.isNewer($0.tag, than: AppVersion.current) } ?? false
            print("current \(AppVersion.current), latest \(r?.tag ?? "none"), newer: \(newer ? "yes" : "no")")
        } catch {
            print("ERROR: \(error.localizedDescription)")
            exitCode = 2
        }
        sem.signal()
    }
    sem.wait()
    exit(exitCode)
}

// `--signin-script-dryrun`: write the sign-in script, print path + contents,
// never open it.
if CommandLine.arguments.contains("--signin-script-dryrun") {
    do {
        let url = try SignInLauncher.writeScript()
        print(url.path)
        print(try String(contentsOf: url, encoding: .utf8), terminator: "")
        exit(0)
    } catch {
        print("ERROR: \(error.localizedDescription)")
        exit(1)
    }
}

// `--status`: one fetch, write current.json (ignoring the enabled toggle —
// explicit invocation), print the same document to stdout so file and
// stdout can never diverge, exit 0/1 on success/failure.
if CommandLine.arguments.contains("--status") {
    let sem = DispatchSemaphore(value: 0)
    var exitCode: Int32 = 0
    Task {
        do {
            let (snap, plan) = try await UsageAPI.fetchUsage()
            let doc = StatusExporter.documentForSuccess(snap, plan: plan)
            StatusExporter.writeToFile(doc)
            print(StatusExporter.serialize(doc))
            exitCode = 0
        } catch {
            let doc = StatusExporter.documentForFailure(error.localizedDescription)
            StatusExporter.writeToFile(doc)
            print(StatusExporter.serialize(doc))
            exitCode = 1
        }
        sem.signal()
    }
    sem.wait()
    exit(exitCode)
}

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let controller = AppController()
    app.delegate = controller
    app.setActivationPolicy(.accessory)
    app.run()
}
