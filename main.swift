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
}

enum Sev {
    // Traffic-light thresholds; API "severity" only says normal/warning so we
    // derive finer bands from the percentage.
    static func color(_ pct: Double) -> Color {
        switch pct {
        case ..<50:  return Color(red: 0.22, green: 0.72, blue: 0.42)
        case ..<75:  return Color(red: 0.95, green: 0.77, blue: 0.06)
        case ..<90:  return Color(red: 0.96, green: 0.55, blue: 0.14)
        default:     return Color(red: 0.90, green: 0.26, blue: 0.21)
        }
    }
    static func nsColor(_ pct: Double) -> NSColor {
        switch pct {
        case ..<50:  return NSColor(red: 0.22, green: 0.72, blue: 0.42, alpha: 1)
        case ..<75:  return NSColor(red: 0.85, green: 0.68, blue: 0.00, alpha: 1)
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
    case noCredentials, refreshFailed(String), httpError(Int), badPayload
    var errorDescription: String? {
        switch self {
        case .noCredentials:        return "No Claude Code credentials in keychain — run `claude` and sign in once."
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

    private static func post(title: String, body: String) {
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

// MARK: - Observable model

@MainActor
final class UsageModel: ObservableObject {
    @Published var snapshot: UsageSnapshot?
    @Published var errorText: String?
    @Published var plan: String?
    @Published var refreshing = false
    let history = HistoryStore()
    private var timer: Timer?

    func start() {
        Task { await refresh() }
        timer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            Task { await self?.refresh() }
        }
        timer?.tolerance = 10
    }

    func refresh() async {
        if refreshing { return }
        refreshing = true
        defer { refreshing = false }
        do {
            let (snap, plan) = try await UsageAPI.fetchUsage()
            self.snapshot = snap
            self.plan = plan
            self.errorText = nil
            if let s = snap.session?.percent, let w = snap.weeklyAll?.percent {
                history.record(session: s, weekly: w)
            }
            Notifier.check(snap)
        } catch {
            self.errorText = error.localizedDescription
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

    var body: some View {
        VStack(spacing: 6) {
            ZStack {
                Circle()
                    .stroke(Color.primary.opacity(0.09), style: StrokeStyle(lineWidth: size * 0.1, lineCap: .round))
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
            Text(label).font(.system(size: 11, weight: .semibold))
            Text(sublabel)
                .font(.system(size: 9.5))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
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
                    Capsule().fill(Color.primary.opacity(0.09))
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
    @AppStorage("showFloating") private var showFloating = true
    @AppStorage("floatSquare") private var floatSquare = false
    @AppStorage("menuBarMetric") private var menuBarMetric = "worst"
    @AppStorage("menuBarShowPct") private var menuBarShowPct = true
    @AppStorage("warnThreshold") private var warnThreshold = 90.0

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
                    HStack(alignment: .top, spacing: 18) {
                        Spacer(minLength: 0)
                        if let s = snap.session {
                            RingGauge(percent: s.percent, label: "Session",
                                      sublabel: resetText(s.resetsAt))
                        }
                        if let w = snap.weeklyAll {
                            RingGauge(percent: w.percent, label: "Week (all)",
                                      sublabel: resetText(w.resetsAt))
                        }
                        Spacer(minLength: 0)
                    }
                }
                if !snap.scoped.isEmpty {
                    VStack(spacing: 8) {
                        ForEach(snap.scoped) { ScopedBar(entry: $0) }
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

                if let snap = model.snapshot {
                    Text("updated \(clockString(snap.fetchedAt, fmtTime))")
                        .font(.system(size: 9.5)).foregroundStyle(.tertiary)
                }
                Spacer()
                Menu {
                    Toggle("Desktop gauge", isOn: Binding(
                        get: { showFloating },
                        set: { _ in controller.toggleFloatingWindow() }))
                    Picker("Gauge style", selection: $floatSquare) {
                        Text("One line").tag(false)
                        Text("Square").tag(true)
                    }
                    Divider()
                    Picker("Menu bar shows", selection: $menuBarMetric) {
                        Text("Worst limit").tag("worst")
                        Text("Session (5 h)").tag("session")
                        Text("Week (all models)").tag("week")
                    }
                    Toggle("Percent in menu bar", isOn: $menuBarShowPct)
                    Picker("Warn at", selection: $warnThreshold) {
                        Text("Off").tag(0.0)
                        Text("80%").tag(80.0)
                        Text("90%").tag(90.0)
                        Text("95%").tag(95.0)
                    }
                    Divider()
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
        .padding(14)
        .frame(width: 292)
    }
}

struct FloatingView: View {
    @ObservedObject var model: UsageModel
    @AppStorage("floatSquare") private var square = false

    var body: some View {
        TimelineView(.periodic(from: .now, by: 30)) { _ in
            Group {
                if let snap = model.snapshot {
                    if square { squareLayout(snap) } else { wideLayout(snap) }
                } else {
                    Text(model.errorText ?? "Claude Meter…")
                        .font(.system(size: 10)).foregroundStyle(.secondary)
                        .frame(maxWidth: 180)
                }
            }
            .padding(.horizontal, 14).padding(.vertical, 10)
        }
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous)
            .strokeBorder(Color.primary.opacity(0.12)))
    }

    // One line: rings on the left, title + countdown on the right.
    private func wideLayout(_ snap: UsageSnapshot) -> some View {
        HStack(spacing: 14) {
            miniRing(snap.session, "5 h")
            miniRing(snap.weeklyAll, "week")
            VStack(alignment: .leading, spacing: 2) {
                Text("Claude").font(.system(size: 10, weight: .bold))
                    .foregroundStyle(.secondary)
                Text(resetText(snap.session?.resetsAt))
                    .font(.system(size: 9)).foregroundStyle(.secondary)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(width: 140, alignment: .leading)
            }
        }
    }

    // Square: gauges first row, reset countdown second row.
    private func squareLayout(_ snap: UsageSnapshot) -> some View {
        VStack(spacing: 7) {
            HStack(spacing: 16) {
                miniRing(snap.session, "5 h")
                miniRing(snap.weeklyAll, "week")
            }
            Text(resetText(snap.session?.resetsAt))
                .font(.system(size: 9)).foregroundStyle(.secondary)
                .lineLimit(1)
                .fixedSize()
        }
    }

    @ViewBuilder
    private func miniRing(_ entry: LimitEntry?, _ tag: String) -> some View {
        let pct = entry?.percent ?? 0
        VStack(spacing: 2) {
            ZStack {
                Circle().stroke(Color.primary.opacity(0.1), lineWidth: 3.5)
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

// MARK: - App controller (status item, popover, floating panel)

@MainActor
final class AppController: NSObject, NSApplicationDelegate, NSPopoverDelegate {
    let model = UsageModel()
    private var statusItem: NSStatusItem!
    private var popover: NSPopover!
    private var panel: NSPanel?
    private var lastSquare = UserDefaults.standard.bool(forKey: "floatSquare")
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
                let sq = UserDefaults.standard.bool(forKey: "floatSquare")
                if sq != self.lastSquare {
                    self.lastSquare = sq
                    DispatchQueue.main.async { self.sizeFloatingPanel() }
                }
            }
        }

        updateStatusButton()
        model.start()

        if UserDefaults.standard.object(forKey: "showFloating") == nil {
            UserDefaults.standard.set(true, forKey: "showFloating")
        }
        if UserDefaults.standard.bool(forKey: "showFloating") { showFloatingWindow() }
    }

    // Menu bar: colored ring icon + percent. Which limit it tracks ("worst",
    // session, or week) and whether the % text shows are gear-menu options.
    private func updateStatusButton() {
        guard let btn = statusItem.button else { return }
        let d = UserDefaults.standard
        let snap = model.snapshot
        let chosen: LimitEntry? = {
            guard let snap else { return nil }
            switch d.string(forKey: "menuBarMetric") ?? "worst" {
            case "session": return snap.session
            case "week":    return snap.weeklyAll
            default:        return snap.limits.max { $0.percent < $1.percent }
            }
        }()
        let pct = chosen?.percent
        btn.image = Self.ringImage(pct: pct)
        btn.imagePosition = .imageLeft
        let showPct = d.object(forKey: "menuBarShowPct") == nil
            ? true : d.bool(forKey: "menuBarShowPct")
        let text = showPct ? (pct.map { " \(Int($0.rounded()))%" } ?? " –") : ""
        btn.attributedTitle = NSAttributedString(string: text, attributes: [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium)
        ])
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
        let menu = NSMenu()
        menu.addItem(withTitle: "Refresh now", action: #selector(menuRefresh), keyEquivalent: "r").target = self
        let floatItem = NSMenuItem(title: "Show desktop gauge", action: #selector(menuToggleFloat), keyEquivalent: "")
        floatItem.target = self
        floatItem.state = (panel?.isVisible == true) ? .on : .off
        menu.addItem(floatItem)
        let squareItem = NSMenuItem(title: "Square gauge layout", action: #selector(menuToggleSquare), keyEquivalent: "")
        squareItem.target = self
        squareItem.state = UserDefaults.standard.bool(forKey: "floatSquare") ? .on : .off
        menu.addItem(squareItem)
        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit Claude Meter", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        statusItem.menu = menu
        statusItem.button?.performClick(nil)
        statusItem.menu = nil   // restore click handling
    }

    @objc private func menuRefresh() { Task { await model.refresh() } }
    @objc private func menuToggleFloat() { toggleFloatingWindow() }
    @objc private func menuToggleSquare() {
        let d = UserDefaults.standard
        d.set(!d.bool(forKey: "floatSquare"), forKey: "floatSquare")
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
            sz = UserDefaults.standard.bool(forKey: "floatSquare")
                ? NSSize(width: 190, height: 100)
                : NSSize(width: 290, height: 64)
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

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let controller = AppController()
    app.delegate = controller
    app.setActivationPolicy(.accessory)
    app.run()
}
