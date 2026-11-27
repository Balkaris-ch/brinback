// Brinback for macOS - save a set of windows exactly as they sit, bring them all back with
// one click, and put them back by themselves when the app that owns them restarts.
//
// The Mac sibling of src/Brinback.cs. Same interface (src/ui/index.html, served the same way,
// same API), a different engine underneath:
//
//   Desk         screens from CoreGraphics; windows from the Accessibility API (titles, moving,
//                raising) matched to CoreGraphics' window list (stacking order, window ids)
//   Claude       the Claude desktop app's session pop-outs, so a window is remembered by session
//   Store        groups.json in ~/Library/Application Support/Brinback
//   Layouts      matches a saved group to the windows open now, finds layouts on each screen
//   AutoRestore  puts a group's windows back in place as they open
//   Server       a loopback-only HTTP server for the interface, which a WKWebView window shows
//   Updater      asks GitHub for the latest release; Update pulls the source and rebuilds here
//   App          the menu bar item, the window, start at login
//
// Brinback was called Window Groups up to 1.5; the groups saved under that name are carried over.
//
// One file, plain swiftc - see build.sh. Needs Accessibility access (System Settings > Privacy &
// Security > Accessibility): macOS lets no app read or move another app's windows without it.
//
// macOS has no snap groups. Windows' Snap Layouts form groups the taskbar knows about; the Mac's
// window tiling keeps nothing of the kind. So here every layout is "placed": put back at its exact
// position and size, which on a Mac is all there is.

import AppKit
import ApplicationServices
import Foundation
import Network
import ServiceManagement
import WebKit

let appVersion = "1.6.7"                 // must match the release tag (v + this): the updater compares them
let uiTitle = "Brinback"

// MARK: - the desktop

final class Mon {
    var index = 0
    var x = 0, y = 0, w = 0, h = 0          // whole screen, top-left origin, points
    var wx = 0, wy = 0, ww = 0, wh = 0      // work area: minus menu bar and Dock
    var primary = false
    var name = "Screen"
}

final class Win {
    var id: UInt32 = 0                      // CGWindowID - the handle the interface knows
    var pid: pid_t = 0
    var title = ""
    var process = ""
    var x = 0, y = 0, w = 0, h = 0
    var min = false
    var z = 0                               // 0 = front
    var mon = -1
    var isClaude: Bool { return process.lowercased() == "claude" }
}

// Private, but the only bridge from an Accessibility window to its CoreGraphics id. Every window
// manager on the Mac uses it (Rectangle, AltTab, yabai).
@_silgen_name("_AXUIElementGetWindow")
func _AXUIElementGetWindow(_ element: AXUIElement, _ id: UnsafeMutablePointer<CGWindowID>) -> AXError

enum Desk {
    static let selfPid = ProcessInfo.processInfo.processIdentifier
    static var ax: [UInt32: AXUIElement] = [:]          // last listing's windows, for moving them
    static let axGate = NSLock()

    static var trusted: Bool { return AXIsProcessTrusted() }

    static func monitors() -> [Mon] {
        var ids = [CGDirectDisplayID](repeating: 0, count: 32)
        var n: UInt32 = 0
        CGGetActiveDisplayList(32, &ids, &n)
        let main = CGMainDisplayID()
        let primaryH = CGDisplayBounds(main).height
        var work: [CGDirectDisplayID: CGRect] = [:]
        let screens: () -> Void = {
            for s in NSScreen.screens {
                guard let num = s.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { continue }
                let vf = s.visibleFrame               // bottom-left origin: flip to top-left
                work[CGDirectDisplayID(num.uint32Value)] = CGRect(x: vf.minX, y: primaryH - vf.maxY, width: vf.width, height: vf.height)
            }
        }
        if Thread.isMainThread { screens() } else { DispatchQueue.main.sync(execute: screens) }
        var list: [Mon] = []
        for i in 0..<Int(n) {
            let b = CGDisplayBounds(ids[i])
            let m = Mon()
            m.x = Int(b.minX); m.y = Int(b.minY); m.w = Int(b.width); m.h = Int(b.height)
            let wr = work[ids[i]] ?? b
            m.wx = Int(wr.minX); m.wy = Int(wr.minY); m.ww = Int(wr.width); m.wh = Int(wr.height)
            m.primary = ids[i] == main
            list.append(m)
        }
        list.sort { $0.x != $1.x ? $0.x < $1.x : $0.y < $1.y }
        for (i, m) in list.enumerated() { m.index = i }
        nameThem(list)
        return list
    }

    // "Top left", "Main", "Far right" - names a person uses, from where each screen sits
    // relative to the main one. Same rule as on Windows.
    static func nameThem(_ ms: [Mon]) {
        guard !ms.isEmpty else { return }
        let p = ms.first(where: { $0.primary }) ?? ms[0]
        let pcx = Double(p.x) + Double(p.w) / 2, pcy = Double(p.y) + Double(p.h) / 2
        for m in ms {
            if m === p { m.name = ms.count == 1 ? "Screen" : "Main"; continue }
            let dx = Double(m.x) + Double(m.w) / 2 - pcx, dy = Double(m.y) + Double(m.h) / 2 - pcy
            let v = dy < -Double(p.h) * 0.5 ? "Top" : dy > Double(p.h) * 0.5 ? "Bottom" : ""
            var hz = ""
            if dx < -Double(p.w) * 0.25 { hz = dx < -Double(p.w) * 1.5 ? "far left" : "left" }
            else if dx > Double(p.w) * 0.25 { hz = dx > Double(p.w) * 1.5 ? "far right" : "right" }
            if !v.isEmpty && !hz.isEmpty { m.name = v + " " + hz }
            else if !v.isEmpty { m.name = v }
            else if !hz.isEmpty { m.name = hz.prefix(1).uppercased() + hz.dropFirst() }
            else { m.name = "Screen" }
        }
        for (_, dup) in Dictionary(grouping: ms, by: { $0.name }) where dup.count > 1 {
            for (k, m) in dup.sorted(by: { $0.x != $1.x ? $0.x < $1.x : $0.y < $1.y }).enumerated() { m.name += " \(k + 1)" }
        }
    }

    static func monitorOf(_ x: Int, _ y: Int, _ w: Int, _ h: Int, _ mons: [Mon]) -> Int {
        var best = -1, bestArea = 0
        for m in mons {
            let ix = max(0, min(x + w, m.x + m.w) - max(x, m.x)), iy = max(0, min(y + h, m.y + m.h) - max(y, m.y))
            if ix * iy > bestArea { bestArea = ix * iy; best = m.index }
        }
        if best >= 0 || mons.isEmpty { return best }
        let cx = Double(x) + Double(w) / 2, cy = Double(y) + Double(h) / 2
        var bd = Double.greatestFiniteMagnitude
        for m in mons {
            let d = abs(Double(m.x) + Double(m.w) / 2 - cx) + abs(Double(m.y) + Double(m.h) / 2 - cy)
            if d < bd { bd = d; best = m.index }
        }
        return best
    }

    static func attr<T>(_ el: AXUIElement, _ name: String) -> T? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, name as CFString, &v) == .success else { return nil }
        return v as? T
    }

    static func point(_ el: AXUIElement, _ name: String) -> CGPoint? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, name as CFString, &v) == .success, let val = v, CFGetTypeID(val) == AXValueGetTypeID() else { return nil }
        var p = CGPoint.zero
        return AXValueGetValue(val as! AXValue, .cgPoint, &p) ? p : nil
    }

    static func size(_ el: AXUIElement) -> CGSize? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, kAXSizeAttribute as CFString, &v) == .success, let val = v, CFGetTypeID(val) == AXValueGetTypeID() else { return nil }
        var s = CGSize.zero
        return AXValueGetValue(val as! AXValue, .cgSize, &s) ? s : nil
    }

    // Every ordinary window: on-screen ones in stacking order from CoreGraphics, titles and
    // minimized windows from Accessibility.
    static func list(_ mons: [Mon]) -> [Win] {
        let cg = (CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]) ?? []
        var order: [UInt32: Int] = [:]
        var cgInfo: [UInt32: [String: Any]] = [:]
        var z = 0
        for d in cg {
            guard (d[kCGWindowLayer as String] as? Int) == 0, let id = d[kCGWindowNumber as String] as? UInt32 else { continue }
            if order[id] == nil { order[id] = z; z += 1 }
            cgInfo[id] = d
        }
        var res: [Win] = []
        var axNew: [UInt32: AXUIElement] = [:]
        var seen = Set<UInt32>()
        let apps = NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular && $0.processIdentifier != selfPid }
        for app in apps {
            let pid = app.processIdentifier
            let ae = AXUIElementCreateApplication(pid)
            AXUIElementSetMessagingTimeout(ae, 0.4)        // a hung app must not hang the listing
            guard let wins: [AXUIElement] = attr(ae, kAXWindowsAttribute) else { continue }
            for el in wins {
                var id: CGWindowID = 0
                guard _AXUIElementGetWindow(el, &id) == .success, id != 0, !seen.contains(id) else { continue }
                let sub: String? = attr(el, kAXSubroleAttribute)
                if let s = sub, s != (kAXStandardWindowSubrole as String) { continue }      // sheets, panels, palettes
                let minimized: Bool = attr(el, kAXMinimizedAttribute) ?? false
                if !minimized && order[id] == nil { continue }                           // on another Space, or hidden
                guard let p = point(el, kAXPositionAttribute), let s = size(el), s.width >= 60, s.height >= 40 else { continue }
                let w = Win()
                w.id = id; w.pid = pid
                w.title = (attr(el, kAXTitleAttribute) as String?) ?? ""
                if w.title.isEmpty { w.title = app.localizedName ?? "Window" }
                w.process = app.localizedName ?? ""
                w.x = Int(p.x); w.y = Int(p.y); w.w = Int(s.width); w.h = Int(s.height)
                w.min = minimized
                w.z = order[id] ?? (100000 + res.count)
                w.mon = monitorOf(w.x, w.y, w.w, w.h, mons)
                seen.insert(id)
                axNew[id] = el
                res.append(w)
            }
        }
        // Without Accessibility access there are no titles and nothing can be moved - still show
        // where windows are, from CoreGraphics alone, so the map is not empty.
        if !trusted {
            for (id, d) in cgInfo where !seen.contains(id) {
                guard let bd = d[kCGWindowBounds as String] as? [String: Any], let r = CGRect(dictionaryRepresentation: bd as CFDictionary),
                      r.width >= 60, r.height >= 40, (d[kCGWindowOwnerPID as String] as? Int32) != selfPid else { continue }
                let w = Win()
                w.id = id; w.pid = (d[kCGWindowOwnerPID as String] as? Int32) ?? 0
                w.process = (d[kCGWindowOwnerName as String] as? String) ?? ""
                w.title = (d[kCGWindowName as String] as? String) ?? w.process
                w.x = Int(r.minX); w.y = Int(r.minY); w.w = Int(r.width); w.h = Int(r.height)
                w.z = order[id] ?? 0
                w.mon = monitorOf(w.x, w.y, w.w, w.h, mons)
                res.append(w)
            }
        }
        res.sort { $0.z < $1.z }
        for (i, w) in res.enumerated() { w.z = i }
        axGate.lock(); ax = axNew; axGate.unlock()
        return res
    }

    static func element(_ id: UInt32) -> AXUIElement? {
        axGate.lock(); defer { axGate.unlock() }
        return ax[id]
    }

    @discardableResult
    static func place(_ id: UInt32, _ x: Int, _ y: Int, _ w: Int, _ h: Int) -> Bool {
        guard let el = element(id) else { return false }
        let minimized: Bool = attr(el, kAXMinimizedAttribute) ?? false
        if minimized { AXUIElementSetAttributeValue(el, kAXMinimizedAttribute as CFString, kCFBooleanFalse) }
        var p = CGPoint(x: x, y: y)
        var s = CGSize(width: w, height: h)
        guard let pv = AXValueCreate(.cgPoint, &p), let sv = AXValueCreate(.cgSize, &s) else { return false }
        // Position, size, position: moved onto a smaller screen first, a window cannot take its
        // full size; sized first, it can be pushed off the edge. The last call settles both.
        AXUIElementSetAttributeValue(el, kAXPositionAttribute as CFString, pv)
        let ok = AXUIElementSetAttributeValue(el, kAXSizeAttribute as CFString, sv) == .success
        AXUIElementSetAttributeValue(el, kAXPositionAttribute as CFString, pv)
        return ok
    }

    // Raise one window above the others. On a Mac a window only comes to the front with its app.
    static func raise(_ id: UInt32, pid: pid_t, activate: Bool) {
        guard let el = element(id) else { return }
        let minimized: Bool = attr(el, kAXMinimizedAttribute) ?? false
        if minimized { AXUIElementSetAttributeValue(el, kAXMinimizedAttribute as CFString, kCFBooleanFalse) }
        AXUIElementPerformAction(el, kAXRaiseAction as CFString)
        if activate, let app = NSRunningApplication(processIdentifier: pid) {
            DispatchQueue.main.async { app.activate(options: []) }
        }
    }

    // wins: bottom of the stack first; the last ends on top, with its app active.
    static func bring(_ wins: [Win]) {
        for (k, w) in wins.enumerated() { raise(w.id, pid: w.pid, activate: k == wins.count - 1) }
    }

    static func minimize(_ id: UInt32) {
        guard let el = element(id) else { return }
        AXUIElementSetAttributeValue(el, kAXMinimizedAttribute as CFString, kCFBooleanTrue)
    }
}

// MARK: - Claude desktop

// The Claude desktop app writes each session pop-out window to session-popouts.json with its
// session id and title. Keeping the id means a group still finds a session after it is renamed.
enum Claude {
    static var cache: [String: String] = [:]
    static var cacheAt = Date.distantPast
    static let gate = NSLock()

    static func titles() -> [String: String] {
        gate.lock(); defer { gate.unlock() }
        if Date().timeIntervalSince(cacheAt) < 2 { return cache }
        var map: [String: String] = [:]
        if let f = findPopouts(), let data = try? Data(contentsOf: f),
           let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let arr = root["popouts"] as? [[String: Any]] {
            for d in arr {
                if let sid = d["sessionId"] as? String, let t = d["title"] as? String { map[sid] = t }
            }
        }
        cache = map; cacheAt = Date()
        return map
    }

    static func findPopouts() -> URL? {
        let fm = FileManager.default
        let support = fm.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
        var best: URL?, bestAt = Date.distantPast
        for r in ["Claude", "Claude-Data"] {
            let root = support.appendingPathComponent(r)
            var candidates = [root.appendingPathComponent("session-popouts.json")]
            if let kids = try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil) {
                candidates += kids.map { $0.appendingPathComponent("session-popouts.json") }
            }
            for c in candidates {
                guard let at = (try? fm.attributesOfItem(atPath: c.path))?[.modificationDate] as? Date else { continue }
                if at > bestAt { bestAt = at; best = c }
            }
        }
        return best
    }
}

// MARK: - data

extension KeyedDecodingContainer {
    func v<T: Decodable>(_ key: Key, _ fallback: T) -> T {
        if let x = try? decodeIfPresent(T.self, forKey: key) { return x }
        return fallback
    }
}

final class Slot: Codable {
    var sessionId: String?
    var title = ""
    var process = ""
    var x = 0, y = 0, w = 0, h = 0
    var z = 0
    init() {}
    enum K: String, CodingKey { case sessionId, title, process, x, y, w, h, z }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: K.self)
        sessionId = (try? c.decodeIfPresent(String.self, forKey: .sessionId)) ?? nil
        title = c.v(.title, ""); process = c.v(.process, "")
        x = c.v(.x, 0); y = c.v(.y, 0); w = c.v(.w, 0); h = c.v(.h, 0); z = c.v(.z, 0)
    }
    func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: K.self)
        try c.encodeIfPresent(sessionId, forKey: .sessionId); try c.encode(title, forKey: .title); try c.encode(process, forKey: .process)
        try c.encode(x, forKey: .x); try c.encode(y, forKey: .y); try c.encode(w, forKey: .w); try c.encode(h, forKey: .h); try c.encode(z, forKey: .z)
    }
}

final class Group: Codable {
    var id = ""
    var name = ""
    var auto = true
    var saved = ""
    var slots: [Slot] = []
    init() {}
    enum K: String, CodingKey { case id, name, auto, saved, slots }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: K.self)
        id = c.v(.id, ""); name = c.v(.name, ""); auto = c.v(.auto, true); saved = c.v(.saved, ""); slots = c.v(.slots, [Slot]())
    }
    func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: K.self)
        try c.encode(id, forKey: .id); try c.encode(name, forKey: .name); try c.encode(auto, forKey: .auto)
        try c.encode(saved, forKey: .saved); try c.encode(slots, forKey: .slots)
    }
}

final class StoreData: Codable {
    var version = 1
    var autoRestore = true
    var groups: [Group] = []
    init() {}
    enum K: String, CodingKey { case version, autoRestore, groups }
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: K.self)
        version = c.v(.version, 1); autoRestore = c.v(.autoRestore, true); groups = c.v(.groups, [Group]())
    }
    func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: K.self)
        try c.encode(version, forKey: .version); try c.encode(autoRestore, forKey: .autoRestore); try c.encode(groups, forKey: .groups)
    }
}

enum Store {
    static var dir: URL = {
        if let custom = ProcessInfo.processInfo.environment["WG_HOME"] { return URL(fileURLWithPath: custom) }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/Brinback")
    }()
    static var file: URL { return dir.appendingPathComponent("groups.json") }

    // Brinback was Window Groups up to 1.5. Its groups are copied over once - copied, not moved,
    // so the old folder stays as a backup.
    static func bringOld() {
        let old = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/WindowGroups/groups.json")
        guard !FileManager.default.fileExists(atPath: file.path), FileManager.default.fileExists(atPath: old.path) else { return }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        if (try? FileManager.default.copyItem(at: old, to: file)) != nil { Log.write("carried the groups over from Window Groups") }
    }
    static var d = StoreData()
    static let gate = NSRecursiveLock()

    static func load() {
        gate.lock(); defer { gate.unlock() }
        guard let data = try? Data(contentsOf: file) else { d = StoreData(); return }
        if let x = try? JSONDecoder().decode(StoreData.self, from: data) { d = x; return }
        // Never start empty over a file we could not read - that would overwrite it on the next
        // save. Keep it aside, under a name that says what happened.
        let aside = dir.appendingPathComponent("groups.unreadable-\(Int(Date().timeIntervalSince1970)).json")
        try? FileManager.default.copyItem(at: file, to: aside)
        Log.write("store: groups.json unreadable - kept as \(aside.lastPathComponent)")
        d = StoreData()
    }

    // Temp file, then replace: a crash halfway must never leave an empty groups.json.
    static func save() {
        gate.lock(); defer { gate.unlock() }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let enc = JSONEncoder(); enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? enc.encode(d) else { return }
        try? data.write(to: file, options: .atomic)
    }

    static func find(_ id: String?) -> Group? { return d.groups.first { $0.id == id } }

    static func freeName(_ base: String) -> String {
        let taken: (String) -> Bool = { x in d.groups.contains { $0.name.lowercased() == x.lowercased() } }
        if !taken(base) { return base }
        var i = 2
        while taken("\(base) \(i)") { i += 1 }
        return "\(base) \(i)"
    }

    static func newId() -> String { return String(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased().prefix(10)) }

    static func stamp() -> String {
        let f = DateFormatter(); f.dateFormat = "dd MMM HH:mm"; f.locale = Locale(identifier: "en_US_POSIX")
        return f.string(from: Date())
    }
}

enum Log {
    static let gate = NSLock()
    static func write(_ m: String) {
        gate.lock(); defer { gate.unlock() }
        try? FileManager.default.createDirectory(at: Store.dir, withIntermediateDirectories: true)
        let f = Store.dir.appendingPathComponent("log.txt")
        let fmt = DateFormatter(); fmt.dateFormat = "yyyy-MM-dd HH:mm:ss"
        let line = fmt.string(from: Date()) + " " + m + "\n"
        if let attrs = try? FileManager.default.attributesOfItem(atPath: f.path), let size = attrs[.size] as? Int, size > 1_000_000 {
            try? FileManager.default.removeItem(at: f.appendingPathExtension("old"))
            try? FileManager.default.moveItem(at: f, to: f.appendingPathExtension("old"))
        }
        if let h = try? FileHandle(forWritingTo: f) { h.seekToEndOfFile(); h.write(line.data(using: .utf8)!); h.closeFile() }
        else { try? line.data(using: .utf8)!.write(to: f) }
    }
}

// MARK: - layouts

final class SlotState { var index = 0; var slot = Slot(); var window: Win?; var title = ""; var mon = -1 }

final class Detected {
    var mon = 0, monName = "", index = 0
    var tiled = false, coverage = 0.0, behind = 0, hidden = false
    var windows: [Win] = []
    var key: String { return "\(mon)-\(index)" }
}

enum Layouts {
    // Match a saved group to the windows open now: Claude sessions by session id first (their
    // current title), then any window by exact title; two windows with one title go to whichever
    // already sits nearest the slot.
    static func resolve(_ g: Group, _ open: [Win], _ titles: [String: String], _ mons: [Mon]) -> [SlotState] {
        var used = Set<UInt32>()
        var res: [SlotState] = []
        for (i, s) in g.slots.enumerated() {
            var want = s.title
            if let sid = s.sessionId, let t = titles[sid] { want = t }
            var hit = pick(open, used, want, s)
            if hit == nil && want != s.title { hit = pick(open, used, s.title, s) }
            if let h = hit { used.insert(h.id) }
            let st = SlotState()
            st.index = i; st.slot = s; st.window = hit
            st.title = hit?.title ?? want
            st.mon = Desk.monitorOf(s.x, s.y, s.w, s.h, mons)
            res.append(st)
        }
        return res
    }

    static func pick(_ open: [Win], _ used: Set<UInt32>, _ title: String, _ s: Slot) -> Win? {
        return open.filter { !used.contains($0.id) && $0.title == title && (s.process.isEmpty || $0.process.lowercased() == s.process.lowercased()) }
                   .min { abs($0.x - s.x) + abs($0.y - s.y) < abs($1.x - s.x) + abs($1.y - s.y) }
    }

    static func snapshot(_ wins: [Win]) -> [Slot] {
        var bySession: [String: String] = [:]
        for (sid, t) in Claude.titles() where bySession[t] == nil { bySession[t] = sid }
        return wins.sorted { $0.z < $1.z }.map { w in
            let s = Slot()
            s.title = w.title; s.process = w.process
            if w.isClaude { s.sessionId = bySession[w.title] }
            s.x = w.x; s.y = w.y; s.w = w.w; s.h = w.h; s.z = w.z
            return s
        }
    }

    struct Result { var group = ""; var placed = 0; var total = 0; var missing: [String] = [] }

    static let deployGate = NSLock()

    static func deploy(_ g: Group) -> Result {
        deployGate.lock(); defer { deployGate.unlock() }
        let mons = Desk.monitors()
        let states = resolve(g, Desk.list(mons), Claude.titles(), mons)
        var r = Result(group: g.name, placed: 0, total: states.count, missing: [])
        // Lowest in the saved stack first, so raising them one by one leaves the top one on top.
        let live = states.filter { $0.window != nil }.sorted { $0.slot.z > $1.slot.z }
        for s in live { if Desk.place(s.window!.id, s.slot.x, s.slot.y, s.slot.w, s.slot.h) { r.placed += 1 } }
        for (k, s) in live.enumerated() { Desk.raise(s.window!.id, pid: s.window!.pid, activate: k == live.count - 1) }
        r.missing = states.filter { $0.window == nil }.map { $0.title }
        Log.write("deploy '\(g.name)': \(r.placed) of \(r.total) placed" + (r.missing.isEmpty ? "" : ", not open: " + r.missing.joined(separator: ", ")))
        return r
    }

    static func summary(_ r: Result) -> String {
        var t = "\(r.placed) of \(r.total) in place"
        if !r.missing.isEmpty { t += " - not open: " + r.missing.joined(separator: ", ") }
        return t
    }

    static func inter(_ a: Win, _ b: Win) -> Int {
        let ix = max(0, min(a.x + a.w, b.x + b.w) - max(a.x, b.x)), iy = max(0, min(a.y + a.h, b.y + b.h) - max(a.y, b.y))
        return ix * iy
    }

    // How much of w the windows above it hide, sampled on a grid.
    static func covered(_ w: Win, _ above: [Win]) -> Double {
        if above.isEmpty || w.w <= 0 || w.h <= 0 { return 0 }
        var hit = 0, all = 0
        for i in 0..<12 { for j in 0..<8 {
            let px = Double(w.x) + (Double(i) + 0.5) * Double(w.w) / 12, py = Double(w.y) + (Double(j) + 0.5) * Double(w.h) / 8
            all += 1
            if above.contains(where: { px >= Double($0.x) && px < Double($0.x + $0.w) && py >= Double($0.y) && py < Double($0.y + $0.h) }) { hit += 1 }
        } }
        return Double(hit) / Double(all)
    }

    // What is arranged on each screen right now: the windows you can see there, two or more.
    // (No snap layouts to tell apart on a Mac - every layout is a placed one.)
    static func detect(_ wins: [Win], _ mons: [Mon], claudeOnly: Bool) -> [Detected] {
        var res: [Detected] = []
        for m in mons {
            let stack = wins.filter { $0.mon == m.index && !$0.min && (!claudeOnly || $0.isClaude) }.sorted { $0.z < $1.z }
            var visible: [Win] = []
            var behind = 0
            for w in stack { if covered(w, visible) > 0.7 { behind += 1 } else { visible.append(w) } }
            guard visible.count >= 2 else { continue }
            let d = Detected()
            d.mon = m.index; d.monName = m.name; d.index = 0; d.windows = visible; d.behind = behind
            // share of the work area the visible windows cover
            var hit = 0, all = 0
            for i in 0..<24 { for j in 0..<16 {
                let px = Double(m.wx) + (Double(i) + 0.5) * Double(m.ww) / 24, py = Double(m.wy) + (Double(j) + 0.5) * Double(m.wh) / 16
                all += 1
                if visible.contains(where: { px >= Double($0.x) && px < Double($0.x + $0.w) && py >= Double($0.y) && py < Double($0.y + $0.h) }) { hit += 1 }
            } }
            d.coverage = Double(hit) / Double(max(1, all))
            var overlap = 0
            for a in 0..<visible.count { for b in (a + 1)..<visible.count { overlap += inter(visible[a], visible[b]) } }
            d.tiled = d.coverage > 0.85 && Double(overlap) < Double(m.ww * m.wh) * 0.05
            res.append(d)
        }
        return res
    }
}

// MARK: - auto-restore

// When a window that belongs to a group appears - after a restart, or when its app reopens it -
// it is put in its slot, once. Windows that were already open when Brinback started are
// left where they are; moving a window afterwards is never fought.
enum AutoRestore {
    static var seen = Set<UInt32>()
    static var pending: [UInt32: Date] = [:]
    static var started = false
    static let queue = DispatchQueue(label: "wg.autorestore")

    static func start() {
        queue.async { seen = Set(Desk.list(Desk.monitors()).map { $0.id }); started = true }
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 2, repeating: 1.5)
        t.setEventHandler { tick() }
        t.resume()
        timer = t
    }
    static var timer: DispatchSourceTimer?

    static func tick() {
        guard started, Desk.trusted else { return }
        let mons = Desk.monitors()
        let wins = Desk.list(mons)
        let now = Date()
        for w in wins where !seen.contains(w.id) { seen.insert(w.id); pending[w.id] = now }
        let live = Set(wins.map { $0.id })
        seen = seen.intersection(live)
        // The owning app positions a new window itself, and titles often arrive late: wait.
        let due = pending.filter { now.timeIntervalSince($0.value) >= 2.5 }.map { $0.key }
        guard !due.isEmpty else { return }
        for id in due { pending[id] = nil }
        Store.gate.lock()
        let on = Store.d.autoRestore
        let groups = Store.d.groups.filter { $0.auto }
        Store.gate.unlock()
        guard on else { return }
        let fresh = Desk.list(mons)
        let titles = Claude.titles()
        for g in groups {
            for s in Layouts.resolve(g, fresh, titles, mons) {
                guard let w = s.window, due.contains(w.id) else { continue }
                Desk.place(w.id, s.slot.x, s.slot.y, s.slot.w, s.slot.h)
                Log.write("auto-restore: '\(w.title)' back into '\(g.name)'")
            }
        }
    }
}

// MARK: - overlay

// "Show on screen": a group's slots drawn on the real screens, with names, for a few seconds.
// An open window, brought to the front, gets only an outline and a name tag - it stays visible and
// usable. A filled placeholder is only for a window that is not open.
final class TileView: NSView {
    var title = "", sub = "", group = "", missing = false, frameOnly = false
    override func draw(_ dirty: NSRect) {
        let green = NSColor(calibratedRed: 0.15, green: 0.97, blue: 0.09, alpha: 1)
        if frameOnly {
            let edge = NSBezierPath(rect: bounds.insetBy(dx: 2, dy: 2)); edge.lineWidth = 4
            green.setStroke(); edge.stroke()
            let label = (group.uppercased() + "   " + title).trimmingCharacters(in: .whitespaces) as NSString
            let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedSystemFont(ofSize: 12, weight: .medium), .foregroundColor: NSColor(white: 0.04, alpha: 1)]
            let sz = label.size(withAttributes: attrs)
            let tag = NSRect(x: 4, y: bounds.height - sz.height - 14, width: min(sz.width + 20, bounds.width - 8), height: sz.height + 10)
            green.setFill(); NSBezierPath(rect: tag).fill()
            label.draw(in: tag.insetBy(dx: 10, dy: 5), withAttributes: attrs)
            return
        }
        let r = bounds.insetBy(dx: 3, dy: 3)
        NSColor(calibratedRed: 0.04, green: 0.04, blue: 0.04, alpha: 0.78).setFill()
        NSBezierPath(rect: r).fill()
        let edge = NSBezierPath(rect: r)
        edge.lineWidth = 3
        if missing { edge.setLineDash([10, 6], count: 2, phase: 0) }
        (missing ? NSColor(calibratedRed: 0.91, green: 0.67, blue: 0.28, alpha: 1) : NSColor(calibratedRed: 0.15, green: 0.97, blue: 0.09, alpha: 1)).setStroke()
        edge.stroke()
        let pad: CGFloat = 22
        let cap: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedSystemFont(ofSize: 12, weight: .medium), .foregroundColor: NSColor(white: 0.62, alpha: 1)]
        let big: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 28, weight: .light), .foregroundColor: NSColor(white: 0.96, alpha: 1)]
        (group.uppercased() as NSString).draw(at: NSPoint(x: r.minX + pad, y: r.maxY - pad - 14), withAttributes: cap)
        (title as NSString).draw(in: NSRect(x: r.minX + pad, y: r.minY + pad + 24, width: r.width - pad * 2, height: r.height - pad * 2 - 44), withAttributes: big)
        (sub as NSString).draw(at: NSPoint(x: r.minX + pad, y: r.minY + pad), withAttributes: cap)
    }
}

enum Overlay {
    static var windows: [NSWindow] = []
    struct Tile { var x, y, w, h: Int; var title: String; var group: String; var sub: String; var missing: Bool; var frame: Bool = false }

    static func show(_ tiles: [Tile], seconds: Double) {
        DispatchQueue.main.async {
            for w in windows { w.orderOut(nil) }
            windows = []
            let primaryH = CGDisplayBounds(CGMainDisplayID()).height
            for t in tiles {
                let frame = NSRect(x: CGFloat(t.x), y: primaryH - CGFloat(t.y + t.h), width: CGFloat(t.w), height: CGFloat(t.h))
                let win = NSWindow(contentRect: frame, styleMask: .borderless, backing: .buffered, defer: false)
                win.isOpaque = false; win.backgroundColor = .clear; win.hasShadow = false
                win.level = .floating; win.ignoresMouseEvents = true
                win.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
                let v = TileView(frame: NSRect(origin: .zero, size: frame.size))
                v.title = t.title; v.sub = t.sub; v.group = t.group; v.missing = t.missing; v.frameOnly = t.frame
                win.contentView = v
                win.orderFrontRegardless()
                windows.append(win)
            }
            let shown = windows
            DispatchQueue.main.asyncAfter(deadline: .now() + seconds) {
                for w in shown { w.orderOut(nil) }
                windows.removeAll { w in shown.contains { $0 === w } }
            }
        }
    }
}

// MARK: - start at login

enum Startup {
    static var on: Bool { return SMAppService.mainApp.status == .enabled }
    static func set(_ on: Bool) -> String? {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            return nil
        } catch {
            return error.localizedDescription
        }
    }
}

// MARK: - updates

// Asks GitHub for the latest release - at start and twice a day. One plain request, no token, a
// generic User-Agent, nothing about this Mac or its windows. The Mac app is built from source, so
// Update does what you would do by hand: git pull in the clone this copy was built from (build.sh
// writes that path into Info.plist), then mac/build.sh --install, which swaps the app and reopens
// it. Each build is a new ad-hoc signature, so macOS asks for Accessibility again afterwards.
enum Updater {
    static let repo = "Balkaris-ch/brinback"
    static var latest: String?              // a newer version than this one, or nil
    static var lastError: String?
    static var working = false

    static var source: String? {
        guard let p = Bundle.main.object(forInfoDictionaryKey: "BrinbackSource") as? String, !p.isEmpty,
              FileManager.default.fileExists(atPath: p + "/mac/build.sh"),
              FileManager.default.fileExists(atPath: p + "/.git") else { return nil }
        return p
    }

    static func newer(_ a: String, than b: String) -> Bool {
        let pa = a.split(separator: ".").map { Int($0) ?? 0 }
        let pb = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0
            let y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    static func start() {
        DispatchQueue.global().asyncAfter(deadline: .now() + 15) { check() }
        let t = Timer(timeInterval: 12 * 60 * 60, repeats: true) { _ in DispatchQueue.global().async { check() } }
        RunLoop.main.add(t, forMode: .common)
    }

    static func state() -> Any {
        if latest == nil && lastError == nil { return NSNull() }
        var o: [String: Any] = ["working": working]
        o["version"] = latest ?? NSNull()
        o["error"] = working ? NSNull() : (lastError ?? NSNull())
        return o
    }

    // Never on the main thread: it waits for the answer.
    static func check() {
        if working { return }
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases/latest")!)
        req.setValue("Brinback-updater", forHTTPHeaderField: "User-Agent")
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.timeoutInterval = 20
        let sem = DispatchSemaphore(value: 0)
        var body = Data()
        var problem: String? = "no answer from GitHub"
        URLSession.shared.dataTask(with: req) { d, r, e in
            if let e = e { problem = e.localizedDescription }
            else if let h = r as? HTTPURLResponse, h.statusCode != 200 { problem = "GitHub answered \(h.statusCode)" }
            else { problem = nil; body = d ?? Data() }
            sem.signal()
        }.resume()
        _ = sem.wait(timeout: .now() + 30)
        if let p = problem { lastError = p; Log.write("update: check failed - \(p)"); return }
        let json = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any] ?? [:]
        var tag = (json["tag_name"] as? String) ?? ""
        if tag.hasPrefix("v") || tag.hasPrefix("V") { tag.removeFirst() }
        lastError = nil
        if newer(tag, than: appVersion) {
            if latest != tag { Log.write("update: \(tag) is available") }
            latest = tag
        } else {
            latest = nil
        }
        App.shared?.refreshMenu()
    }

    static func quote(_ s: String) -> String { return "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }

    // nil when the rebuild is under way, otherwise why it is not.
    static func apply() -> String? {
        if working { return nil }
        guard let src = source else {
            return "This copy does not know the folder it was built from. In that folder, run: git pull && mac/build.sh --install"
        }
        working = true
        lastError = nil
        try? FileManager.default.createDirectory(at: Store.dir, withIntermediateDirectories: true)
        let log = Store.dir.appendingPathComponent("update.log").path
        let failed = Store.dir.appendingPathComponent("update.failed").path
        try? FileManager.default.removeItem(atPath: failed)
        // Detached, because build.sh --install stops this app before it copies the new one in.
        let work = "cd \(quote(src)) && git pull --ff-only && bash mac/build.sh --install"
        let script = "nohup /bin/sh -c \(quote("( \(work) ) > \(quote(log)) 2>&1 || touch \(quote(failed))")) >/dev/null 2>&1 &"
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", script]
        do { try p.run() } catch { working = false; lastError = error.localizedDescription; return lastError }
        Log.write("update: pulling and rebuilding in \(src)")
        App.shared?.refreshMenu()
        watch(failed, tries: 120)
        return nil
    }

    // If the pull or the build fails, the app is still running: say so instead of waiting for ever.
    static func watch(_ failed: String, tries: Int) {
        DispatchQueue.global().asyncAfter(deadline: .now() + 5) {
            if FileManager.default.fileExists(atPath: failed) || tries <= 0 {
                working = false
                lastError = "the pull or the build did not go through - see update.log in ~/Library/Application Support/Brinback"
                Log.write("update: failed")
                App.shared?.refreshMenu()
            } else {
                watch(failed, tries: tries - 1)
            }
        }
    }
}

// MARK: - HTTP

// A loopback-only server for the interface. Only the loopback interface, the Host header checked
// (no DNS rebinding), and every API call needs the token generated at each start, which only the
// app's own window is given.
final class Request {
    var method = "", path = "", query: [String: String] = [:], headers: [String: String] = [:], body = Data()

    static func parse(_ buf: Data) -> Request? {
        guard let sep = buf.range(of: Data("\r\n\r\n".utf8)) else { return nil }
        guard let head = String(data: buf.subdata(in: 0..<sep.lowerBound), encoding: .utf8) else { return nil }
        let lines = head.components(separatedBy: "\r\n")
        let first = lines[0].split(separator: " ")
        guard first.count >= 2 else { return nil }
        let r = Request()
        r.method = String(first[0])
        let target = String(first[1])
        let parts = target.split(separator: "?", maxSplits: 1)
        r.path = String(parts[0])
        if parts.count > 1 {
            for kv in parts[1].split(separator: "&") {
                let p = kv.split(separator: "=", maxSplits: 1)
                let k = String(p[0]).removingPercentEncoding ?? String(p[0])
                r.query[k] = p.count > 1 ? (String(p[1]).removingPercentEncoding ?? String(p[1])) : ""
            }
        }
        for l in lines.dropFirst() {
            guard let c = l.firstIndex(of: ":") else { continue }
            r.headers[l[..<c].trimmingCharacters(in: .whitespaces).lowercased()] = l[l.index(after: c)...].trimmingCharacters(in: .whitespaces)
        }
        let len = Int(r.headers["content-length"] ?? "0") ?? 0
        let bodyStart = sep.upperBound
        if buf.count - bodyStart < len { return nil }            // not all of it yet
        r.body = buf.subdata(in: bodyStart..<(bodyStart + len))
        return r
    }
}

struct Response {
    var status: Int; var type: String; var body: Data
    static func json(_ s: Int, _ o: Any) -> Response {
        let d = (try? JSONSerialization.data(withJSONObject: o, options: [])) ?? Data("{}".utf8)
        return Response(status: s, type: "application/json; charset=utf-8", body: d)
    }
    static func text(_ s: Int, _ t: String) -> Response { return Response(status: s, type: "text/plain; charset=utf-8", body: Data(t.utf8)) }
    static func html(_ s: Int, _ t: String) -> Response { return Response(status: s, type: "text/html; charset=utf-8", body: Data(t.utf8)) }

    func bytes() -> Data {
        let reason = [200: "OK", 400: "Bad Request", 403: "Forbidden", 404: "Not Found", 409: "Conflict", 500: "Server Error"][status] ?? "OK"
        var h = "HTTP/1.1 \(status) \(reason)\r\nContent-Type: \(type)\r\nContent-Length: \(body.count)\r\nCache-Control: no-store\r\n"
        h += "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\n"
        if type.hasPrefix("text/html") {
            h += "Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'\r\n"
        }
        h += "Connection: close\r\n\r\n"
        var d = Data(h.utf8); d.append(body)
        return d
    }
}

final class Server {
    var port: UInt16
    let token: String
    var listener: NWListener?
    let queue = DispatchQueue(label: "wg.http", attributes: .concurrent)

    init(port: UInt16) {
        self.port = port
        var bytes = [UInt8](repeating: 0, count: 24)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        token = bytes.map { String(format: "%02x", $0) }.joined()
    }

    func start() throws {
        let params = NWParameters.tcp
        params.requiredInterfaceType = .loopback
        params.allowLocalEndpointReuse = true
        let l = try NWListener(using: params, on: NWEndpoint.Port(rawValue: port)!)
        l.newConnectionHandler = { [weak self] c in
            guard let self = self else { return }
            c.start(queue: self.queue)
            self.receive(c, Data())
        }
        let ready = DispatchSemaphore(value: 0)
        var failed: Error?
        l.stateUpdateHandler = { st in
            switch st {
            case .ready: ready.signal()
            case .failed(let e): failed = e; ready.signal()
            default: break
            }
        }
        l.start(queue: queue)
        _ = ready.wait(timeout: .now() + 5)
        if let e = failed { throw e }
        listener = l
    }

    func receive(_ c: NWConnection, _ buf: Data) {
        c.receive(minimumIncompleteLength: 1, maximumLength: 1 << 16) { [weak self] data, _, done, err in
            guard let self = self else { return }
            var b = buf
            if let d = data { b.append(d) }
            if let req = Request.parse(b) {
                let resp: Response
                resp = Api.handle(req, self)
                c.send(content: resp.bytes(), completion: .contentProcessed { _ in c.cancel() })
            } else if done || err != nil || b.count > 4_000_000 {
                c.cancel()
            } else {
                self.receive(c, b)
            }
        }
    }
}

// MARK: - API

enum Api {
    static var indexHtml: String = {
        if let u = Bundle.main.url(forResource: "index", withExtension: "html"), let s = try? String(contentsOf: u, encoding: .utf8) { return s }
        // running from the source tree (build.sh --selftest, or swiftc by hand)
        let here = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent()
        for p in ["../src/ui/index.html", "../../src/ui/index.html", "../../../src/ui/index.html", "../../../../src/ui/index.html"] {
            if let s = try? String(contentsOf: here.appendingPathComponent(p), encoding: .utf8) { return s }
        }
        return "<!doctype html><title>Brinback</title><body>index.html is missing from the app.</body>"
    }()

    static func handle(_ r: Request, _ srv: Server) -> Response {
        let host = r.headers["host"] ?? ""
        if host != "127.0.0.1:\(srv.port)" && host != "localhost:\(srv.port)" { return .text(403, "forbidden") }
        if r.method == "GET" && r.path == "/" {
            if r.query["t"] != srv.token {
                return .html(403, "<!doctype html><title>Brinback</title><body style=\"background:#0a0a0a;color:#a09f9a;font:14px system-ui;padding:40px\">Open Brinback from its icon in the menu bar.</body>")
            }
            return .html(200, indexHtml.replacingOccurrences(of: "__TOKEN__", with: srv.token))
        }
        guard r.path.hasPrefix("/api/") else { return .text(404, "not found") }
        if r.headers["x-token"] != srv.token { return .json(403, ["error": "forbidden"]) }
        if r.method == "GET" && r.path == "/api/state" { return .json(200, state()) }
        if r.method == "POST" && r.path == "/api/action" {
            let b = (try? JSONSerialization.jsonObject(with: r.body.isEmpty ? Data("{}".utf8) : r.body)) as? [String: Any] ?? [:]
            return action(b)
        }
        return .text(404, "not found")
    }

    static func nz(_ v: String?) -> Any { if let x = v { return x }; return NSNull() }

    static func monName(_ mons: [Mon], _ i: Int) -> String { return mons.first { $0.index == i }?.name ?? "Off screen" }

    static func state() -> [String: Any] {
        let mons = Desk.monitors()
        let wins = Desk.list(mons)
        let titles = Claude.titles()
        var res: [String: Any] = [:]
        res["platform"] = "mac"
        res["inset"] = 0
        res["trusted"] = Desk.trusted
        res["monitors"] = mons.map { ["i": $0.index, "name": $0.name, "x": $0.x, "y": $0.y, "w": $0.w, "h": $0.h, "primary": $0.primary] as [String: Any] }
        res["windows"] = wins.map { w -> [String: Any] in
            ["h": String(w.id), "title": w.title, "process": w.process, "claude": w.isClaude,
             "x": w.x, "y": w.y, "w": w.w, "h2": w.h, "vx": w.x, "vy": w.y, "vw": w.w, "vh": w.h,
             "min": w.min, "max": false, "z": w.z, "mon": w.mon, "snapped": false]
        }
        var groups: [[String: Any]] = []
        var savedSets: [(String, Set<UInt32>)] = []
        Store.gate.lock()
        for g in Store.d.groups {
            let states = Layouts.resolve(g, wins, titles, mons)
            savedSets.append((g.name, Set(states.compactMap { $0.window?.id })))
            groups.append([
                "id": g.id, "name": g.name, "auto": g.auto, "saved": g.saved, "snap": false,
                "open": states.filter { $0.window != nil }.count,
                "slots": states.map { s -> [String: Any] in
                    var o: [String: Any] = ["i": s.index, "title": s.title, "savedTitle": s.slot.title, "process": s.slot.process,
                                            "x": s.slot.x, "y": s.slot.y, "w": s.slot.w, "h2": s.slot.h, "max": false, "z": s.slot.z,
                                            "mon": s.mon, "open": s.window != nil, "layout": false]
                    o["sessionId"] = nz(s.slot.sessionId)
                    o["h"] = nz(s.window.map { String($0.id) })
                    o["zone"] = NSNull()
                    return o
                }])
        }
        res["settings"] = ["autoRestore": Store.d.autoRestore, "startWithWindows": Startup.on, "keepSnaps": false]
        Store.gate.unlock()
        res["groups"] = groups
        let detected: (Bool) -> [[String: Any]] = { claudeOnly in
            Layouts.detect(wins, mons, claudeOnly: claudeOnly).map { d in
                let set = Set(d.windows.map { $0.id })
                let match = savedSets.first { $0.1 == set }?.0
                return ["key": d.key, "kind": "placed", "hidden": d.hidden, "index": d.index, "mon": d.mon, "monName": d.monName,
                        "tiled": d.tiled, "coverage": (d.coverage * 100).rounded() / 100, "behind": d.behind,
                        "snap": [String](), "handles": d.windows.map { String($0.id) }, "savedAs": nz(match)] as [String: Any]
            }
        }
        res["detected"] = detected(true)
        res["detectedAll"] = detected(false)
        res["claudeFound"] = wins.contains { $0.isClaude } || !titles.isEmpty
        res["version"] = appVersion
        res["update"] = Updater.state()
        return res
    }

    static func s(_ b: [String: Any], _ k: String) -> String? {
        guard let v = b[k], !(v is NSNull) else { return nil }
        return v as? String ?? "\(v)"
    }
    static func bool(_ b: [String: Any], _ k: String) -> Bool { return (b[k] as? Bool) ?? false }
    static func int(_ b: [String: Any], _ k: String) -> Int { return (b[k] as? Int) ?? (b[k] as? NSNumber)?.intValue ?? -1 }
    static func list(_ b: [String: Any], _ k: String) -> [String] { return ((b[k] as? [Any]) ?? []).map { "\($0)" } }
    static func ok(_ m: String) -> Response { return .json(200, ["ok": true, "message": m]) }
    static func fail(_ s: Int, _ m: String) -> Response { return .json(s, ["error": m]) }

    static func action(_ b: [String: Any]) -> Response {
        let op = s(b, "op") ?? ""
        let needsAccess: Set<String> = ["deploy", "save", "update", "replace", "front", "minimize", "frontWindow", "frontHandles", "overlayHandles", "addToGroup"]
        if needsAccess.contains(op) && !Desk.trusted {
            return fail(403, "Brinback needs Accessibility access to read and move windows: System Settings > Privacy & Security > Accessibility.")
        }
        switch op {
        case "deploy":
            let ids = list(b, "ids")
            Store.gate.lock(); let gs = Store.d.groups.filter { ids.contains($0.id) }; Store.gate.unlock()
            if gs.isEmpty { return fail(404, "No such group.") }
            let parts = gs.map { g -> String in "\(g.name): " + Layouts.summary(Layouts.deploy(g)) }
            return ok("Deployed " + parts.joined(separator: " | ") + ".")

        case "save":
            let name = (s(b, "name") ?? "").trimmingCharacters(in: .whitespaces)
            if name.isEmpty { return fail(400, "Give the group a name.") }
            let handles = Set(list(b, "handles"))
            let wins = Desk.list(Desk.monitors()).filter { handles.contains(String($0.id)) }
            if wins.isEmpty { return fail(400, "None of those windows are open any more.") }
            let slots = Layouts.snapshot(wins)
            Store.gate.lock(); defer { Store.gate.unlock() }
            let existing = Store.d.groups.first { $0.name.lowercased() == name.lowercased() }
            if let e = existing, !bool(b, "replace") { return fail(409, "A group called \(e.name) already exists.") }
            let g = existing ?? { let n = Group(); n.id = Store.newId(); return n }()
            g.name = name; g.slots = slots; g.saved = Store.stamp()
            if existing == nil { Store.d.groups.append(g) }
            Store.save()
            Log.write("saved '\(name)' with \(slots.count) window(s)")
            return .json(200, ["ok": true, "id": g.id, "message": "Saved \(name) - \(slots.count) window(s)."])

        case "update":
            let mons = Desk.monitors()
            let open = Desk.list(mons)
            Store.gate.lock(); defer { Store.gate.unlock() }
            guard let g = Store.find(s(b, "id")) else { return fail(404, "No such group.") }
            var n = 0
            for st in Layouts.resolve(g, open, Claude.titles(), mons) {
                guard let w = st.window else { continue }
                st.slot.x = w.x; st.slot.y = w.y; st.slot.w = w.w; st.slot.h = w.h; st.slot.z = w.z; st.slot.title = w.title
                n += 1
            }
            g.saved = Store.stamp(); Store.save()
            let missing = g.slots.count - n
            return ok("Updated \(g.name) from where its windows are now" + (missing > 0 ? " - \(missing) not open, kept as they were." : "."))

        case "rename":
            let name = (s(b, "name") ?? "").trimmingCharacters(in: .whitespaces)
            if name.isEmpty { return fail(400, "A group needs a name.") }
            Store.gate.lock(); defer { Store.gate.unlock() }
            guard let g = Store.find(s(b, "id")) else { return fail(404, "No such group.") }
            if Store.d.groups.contains(where: { $0 !== g && $0.name.lowercased() == name.lowercased() }) { return fail(409, "Another group is already called \(name).") }
            g.name = name; Store.save()
            return ok("Renamed to \(name).")

        case "delete":
            Store.gate.lock(); defer { Store.gate.unlock() }
            // One group ("id") or several ("ids", from Select all in the list).
            var ids = list(b, "ids")
            if let one = b["id"] { ids.append("\(one)") }
            let gs = Store.d.groups.filter { ids.contains($0.id) }
            if gs.isEmpty { return fail(404, "No such group.") }
            Store.d.groups.removeAll { g in gs.contains { $0 === g } }
            Store.save()
            Log.write(gs.count == 1 ? "deleted '\(gs[0].name)'" : "deleted \(gs.count) groups, \(Store.d.groups.count) left")
            return ok("Deleted \(gs.count == 1 ? gs[0].name : "\(gs.count) groups"). The windows themselves were not touched.")

        case "auto":
            Store.gate.lock(); defer { Store.gate.unlock() }
            guard let g = Store.find(s(b, "id")) else { return fail(404, "No such group.") }
            g.auto = bool(b, "on"); Store.save()
            return ok(g.name + (g.auto ? " will put itself back when its windows open." : " only moves when you deploy it."))

        case "replace":
            // Another open window takes a slot. The slot keeps its place and size.
            guard let w = Desk.list(Desk.monitors()).first(where: { String($0.id) == s(b, "handle") }) else { return fail(404, "That window is not open any more.") }
            Store.gate.lock(); defer { Store.gate.unlock() }
            let i = int(b, "index")
            guard let g = Store.find(s(b, "id")), i >= 0, i < g.slots.count else { return fail(404, "No such slot.") }
            let sl = g.slots[i]
            let was = sl.title
            sl.title = w.title; sl.process = w.process
            sl.sessionId = w.isClaude ? Claude.titles().first(where: { $0.value == w.title })?.key : nil
            g.saved = Store.stamp(); Store.save()
            return ok("\(w.title) takes the place of \(was) in \(g.name).")

        case "removeSlot":
            Store.gate.lock(); defer { Store.gate.unlock() }
            let i = int(b, "index")
            guard let g = Store.find(s(b, "id")), i >= 0, i < g.slots.count else { return fail(404, "No such slot.") }
            let t = g.slots[i].title
            g.slots.remove(at: i); Store.save()
            return ok("Removed \(t) from \(g.name).")

        case "front":
            // A group's windows on top, without moving them - the quick answer to two groups
            // stacked on one screen.
            Store.gate.lock(); let g = Store.find(s(b, "id")); Store.gate.unlock()
            guard let grp = g else { return fail(404, "No such group.") }
            let mons = Desk.monitors()
            let live = Layouts.resolve(grp, Desk.list(mons), Claude.titles(), mons).compactMap { st -> (Win, Int)? in st.window.map { ($0, st.slot.z) } }.sorted { $0.1 > $1.1 }
            Desk.bring(live.map { $0.0 })
            return ok("\(live.count) window(s) of \(grp.name) brought to the front.")

        case "minimize":
            Store.gate.lock(); let g = Store.find(s(b, "id")); Store.gate.unlock()
            guard let grp = g else { return fail(404, "No such group.") }
            let mons = Desk.monitors()
            let live = Layouts.resolve(grp, Desk.list(mons), Claude.titles(), mons).compactMap { $0.window }
            for w in live { Desk.minimize(w.id) }
            return ok("\(live.count) window(s) of \(grp.name) minimized. Deploy or Bring to front puts them back.")

        case "frontWindow":
            guard let w = Desk.list(Desk.monitors()).first(where: { String($0.id) == s(b, "handle") }) else { return fail(404, "That window is not open any more.") }
            Desk.raise(w.id, pid: w.pid, activate: true)
            return ok("\(w.title) brought to the front.")

        case "addToGroup":
            var want = Set(list(b, "handles"))
            if let one = s(b, "handle") { want.insert(one) }
            let mons = Desk.monitors()
            let open = Desk.list(mons)
            let wins = open.filter { want.contains(String($0.id)) }
            if wins.isEmpty { return fail(404, "Those windows are not open any more.") }
            Store.gate.lock(); defer { Store.gate.unlock() }
            guard let g = Store.find(s(b, "id")) else { return fail(404, "No such group.") }
            let already = Set(Layouts.resolve(g, open, Claude.titles(), mons).compactMap { $0.window?.id })
            let add = wins.filter { !already.contains($0.id) }
            if add.isEmpty { return ok("Already in \(g.name).") }
            g.slots += Layouts.snapshot(add)
            g.saved = Store.stamp(); Store.save()
            Log.write("added \(add.count) window(s) to '\(g.name)'")
            return ok((add.count == 1 ? "\(add[0].title) is" : "\(add.count) windows are") + " now in \(g.name), where " + (add.count == 1 ? "it sits" : "they sit") + " now.")

        case "overlay":
            Store.gate.lock(); let g = Store.find(s(b, "id")); Store.gate.unlock()
            guard let grp = g else { return fail(404, "No such group.") }
            let mons = Desk.monitors()
            let states = Layouts.resolve(grp, Desk.list(mons), Claude.titles(), mons).sorted { $0.slot.z > $1.slot.z }
            if Desk.trusted { Desk.bring(states.compactMap { $0.window }) }
            let tiles = states.map { st -> Overlay.Tile in
                if let w = st.window { return Overlay.Tile(x: w.x, y: w.y, w: w.w, h: w.h, title: st.title, group: grp.name, sub: "", missing: false, frame: true) }
                return Overlay.Tile(x: st.slot.x, y: st.slot.y, w: st.slot.w, h: st.slot.h, title: st.title, group: grp.name, sub: "NOT OPEN", missing: true)
            }
            Overlay.show(tiles, seconds: 4)
            return ok("Brought \(grp.name) to the front" + (states.contains { $0.window == nil } ? " - the dashed places are windows that are not open." : "."))

        case "overlayHandles", "frontHandles":
            let hs = Set(list(b, "handles"))
            let mons = Desk.monitors()
            let wins = Desk.list(mons).filter { hs.contains(String($0.id)) }.sorted { $0.z > $1.z }
            if wins.isEmpty { return fail(404, "Those windows are not open any more.") }
            Desk.bring(wins)
            if op == "overlayHandles" {
                let label = s(b, "label") ?? "On screen"
                Overlay.show(wins.map { Overlay.Tile(x: $0.x, y: $0.y, w: $0.w, h: $0.h, title: $0.title, group: label, sub: "", missing: false, frame: true) }, seconds: 4)
            }
            return ok((wins.count == 1 ? "\(wins[0].title) is" : "\(wins.count) windows are") + " in front now.")

        case "openAccessibility":
            Desk.askForAccess()
            return ok("In System Settings, switch on Brinback under Accessibility.")

        case "setting":
            let key = s(b, "key"), on = bool(b, "value")
            if key == "autoRestore" {
                Store.gate.lock(); Store.d.autoRestore = on; Store.save(); Store.gate.unlock()
                App.shared?.refreshMenu()
                return ok(on ? "Groups will put themselves back as their windows open." : "Auto-restore is off. Deploy groups by hand.")
            }
            if key == "startWithWindows" {
                if let err = Startup.set(on) { return fail(500, "Could not change Open at Login: \(err)") }
                App.shared?.refreshMenu()
                return ok(on ? "Brinback opens when you log in." : "Brinback no longer opens when you log in.")
            }
            if key == "keepSnaps" { return ok("macOS has no snap groups to keep.") }
            return fail(400, "Unknown setting.")

        case "selfUpdate":
            guard let v = Updater.latest else { return ok("Brinback is up to date.") }
            if let err = Updater.apply() { return fail(500, err) }
            return ok("Updating to \(v): pulling the source and rebuilding. Brinback reopens by itself - then switch it off and on again under Accessibility.")

        case "checkUpdate":
            Updater.check()
            if let v = Updater.latest { return ok("Brinback \(v) is available.") }
            if let e = Updater.lastError { return fail(502, "Could not check for updates: \(e)") }
            return ok("Brinback \(appVersion) is the latest.")

        default:
            return fail(400, "Unknown action.")
        }
    }
}

extension Desk {
    // Asks once (macOS shows its own prompt), then opens the right pane of System Settings.
    static func askForAccess() {
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        if !AXIsProcessTrustedWithOptions(opts), let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            DispatchQueue.main.async { NSWorkspace.shared.open(u) }
        }
    }
}

// MARK: - the app

final class App: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuDelegate {
    static var shared: App?
    var server: Server!
    var status: NSStatusItem!
    var window: NSWindow?
    var deployMenu = NSMenu()
    var autoItem: NSMenuItem!
    var loginItem: NSMenuItem!
    var updateItem: NSMenuItem!

    func applicationDidFinishLaunching(_ n: Notification) {
        App.shared = self
        Store.bringOld()
        Store.load()
        server = Server(port: 47391)
        do { try server.start() } catch {
            // Most likely another copy already has the port: ask it to show itself, and leave.
            DistributedNotificationCenter.default().postNotificationName(Notification.Name("ch.balkaris.brinback.show"), object: nil, userInfo: nil, deliverImmediately: true)
            Log.write("server: port \(server.port) busy (\(error)) - another copy is running")
            NSApp.terminate(nil)
            return
        }
        DistributedNotificationCenter.default().addObserver(forName: Notification.Name("ch.balkaris.brinback.show"), object: nil, queue: .main) { [weak self] _ in self?.showWindow() }
        buildStatusItem()
        if !Desk.trusted { Desk.askForAccess() }
        AutoRestore.start()
        Updater.start()
        Log.write("started \(appVersion)")
        if !CommandLine.arguments.contains("--tray") { showWindow() }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showWindow(); return true }

    // Four tiles, the top-left one lit - drawn as a template so it follows the menu bar's colour.
    func icon() -> NSImage {
        let img = NSImage(size: NSSize(width: 16, height: 16), flipped: false) { _ in
            NSColor.black.setFill(); NSColor.black.setStroke()
            for (i, r) in [NSRect(x: 2, y: 9, width: 5, height: 5), NSRect(x: 9, y: 9, width: 5, height: 5),
                           NSRect(x: 2, y: 2, width: 5, height: 5), NSRect(x: 9, y: 2, width: 5, height: 5)].enumerated() {
                if i == 0 { NSBezierPath(rect: r).fill() } else { let p = NSBezierPath(rect: r.insetBy(dx: 0.5, dy: 0.5)); p.lineWidth = 1; p.stroke() }
            }
            return true
        }
        img.isTemplate = true
        return img
    }

    func buildStatusItem() {
        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        status.button?.image = icon()
        status.button?.toolTip = uiTitle
        let m = NSMenu()
        m.delegate = self
        m.addItem(withTitle: "Open Brinback", action: #selector(openUi), keyEquivalent: "").target = self
        m.addItem(.separator())
        let d = NSMenuItem(title: "Deploy", action: nil, keyEquivalent: "")
        d.submenu = deployMenu
        m.addItem(d)
        m.addItem(.separator())
        autoItem = NSMenuItem(title: "Auto-restore", action: #selector(toggleAuto), keyEquivalent: ""); autoItem.target = self
        loginItem = NSMenuItem(title: "Open at Login", action: #selector(toggleLogin), keyEquivalent: ""); loginItem.target = self
        m.addItem(autoItem); m.addItem(loginItem)
        m.addItem(.separator())
        updateItem = NSMenuItem(title: "Check for Updates", action: #selector(updateFromMenu), keyEquivalent: ""); updateItem.target = self
        m.addItem(updateItem)
        m.addItem(.separator())
        m.addItem(withTitle: "Quit Brinback", action: #selector(quit), keyEquivalent: "q").target = self
        status.menu = m
        refreshMenu()
    }

    func menuWillOpen(_ menu: NSMenu) { refreshMenu() }

    func refreshMenu() {
        DispatchQueue.main.async { [self] in
            guard autoItem != nil else { return }
            Store.gate.lock()
            autoItem.state = Store.d.autoRestore ? .on : .off
            let gs = Store.d.groups.map { ($0.id, $0.name) }
            Store.gate.unlock()
            loginItem.state = Startup.on ? .on : .off
            if Updater.working { updateItem.title = "Updating..." }
            else if let v = Updater.latest { updateItem.title = "Update to \(v)" }
            else { updateItem.title = "Check for Updates  (v\(appVersion))" }
            updateItem.isEnabled = !Updater.working
            deployMenu.removeAllItems()
            if gs.isEmpty { deployMenu.addItem(withTitle: "No groups yet", action: nil, keyEquivalent: "") }
            for (id, name) in gs {
                let it = NSMenuItem(title: name, action: #selector(deployFromMenu(_:)), keyEquivalent: "")
                it.target = self; it.representedObject = id
                deployMenu.addItem(it)
            }
        }
    }

    @objc func openUi() { showWindow() }
    @objc func quit() { NSApp.terminate(nil) }
    @objc func toggleAuto() {
        Store.gate.lock(); Store.d.autoRestore.toggle(); Store.save(); Store.gate.unlock()
        refreshMenu()
    }
    @objc func toggleLogin() { _ = Startup.set(!Startup.on); refreshMenu() }
    @objc func updateFromMenu() {
        DispatchQueue.global().async { [self] in
            if Updater.latest == nil { Updater.check() }
            if Updater.latest != nil {
                if let err = Updater.apply() { say("Brinback could not update", err) }
            } else if let e = Updater.lastError {
                say("Could not check for updates", e)
            } else {
                say("Brinback is up to date", "Version \(appVersion) is the latest.")
            }
        }
    }
    func say(_ title: String, _ text: String) {
        DispatchQueue.main.async {
            let a = NSAlert()
            a.messageText = title
            a.informativeText = text
            NSApp.activate(ignoringOtherApps: true)
            a.runModal()
        }
    }
    @objc func deployFromMenu(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        DispatchQueue.global().async {
            Store.gate.lock(); let g = Store.find(id); Store.gate.unlock()
            guard let grp = g, Desk.trusted else { Desk.askForAccess(); return }
            _ = Layouts.deploy(grp)
        }
    }

    func showWindow() {
        if window == nil {
            let cfg = WKWebViewConfiguration()
            let web = WKWebView(frame: .zero, configuration: cfg)
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1380, height: 900),
                             styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
            w.title = uiTitle
            w.appearance = NSAppearance(named: .darkAqua)
            w.backgroundColor = NSColor(calibratedWhite: 0.04, alpha: 1)
            w.isReleasedWhenClosed = false
            w.delegate = self
            w.contentView = web
            w.minSize = NSSize(width: 900, height: 600)
            w.center()
            w.setFrameAutosaveName("BrinbackMain")
            web.load(URLRequest(url: URL(string: "http://127.0.0.1:\(server.port)/?t=\(server.token)")!))
            window = w
        }
        NSApp.setActivationPolicy(.regular)               // a Dock icon while the window is open
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowWillClose(_ n: Notification) {
        NSApp.setActivationPolicy(.accessory)             // back to the menu bar only
    }
}

// MARK: - self-test (build.sh --selftest, and CI): the logic and the server, without touching any window

enum SelfTest {
    static var failures = 0
    static func check(_ ok: Bool, _ what: String) {
        print((ok ? "  ok    " : "  FAIL  ") + what)
        if !ok { failures += 1 }
    }

    static func win(_ id: UInt32, _ title: String, _ x: Int, _ y: Int, _ w: Int, _ h: Int, z: Int, mon: Int, process: String = "Claude") -> Win {
        let v = Win(); v.id = id; v.title = title; v.process = process; v.x = x; v.y = y; v.w = w; v.h = h; v.z = z; v.mon = mon
        return v
    }

    static func run() -> Int32 {
        print("Brinback \(appVersion) self-test")
        // screens: names from where they sit
        let ms: [Mon] = [(-2560, 0), (0, 0), (2560, 0), (0, -1440)].enumerated().map { (i, p) -> Mon in
            let m = Mon(); m.index = i; m.x = p.0; m.y = p.1; m.w = 2560; m.h = 1440; m.wx = p.0; m.wy = p.1 + 25; m.ww = 2560; m.wh = 1415; m.primary = p.0 == 0 && p.1 == 0
            return m
        }
        Desk.nameThem(ms)
        check(ms.map { $0.name } == ["Left", "Main", "Right", "Top"], "screen names: \(ms.map { $0.name })")
        check(Desk.monitorOf(-1000, 100, 800, 600, ms) == 0, "a window on the left screen belongs to it")
        check(Desk.monitorOf(9000, 100, 800, 600, ms) == 2, "a window off every screen goes to the nearest")

        // two layouts on Main: a 2x2 behind and a window in front of half of it
        let wins = [win(1, "API refactor", 0, 25, 1280, 707, z: 1, mon: 1), win(2, "Release notes", 1280, 25, 1280, 707, z: 2, mon: 1),
                    win(3, "Data pipeline", 0, 732, 1280, 708, z: 3, mon: 1), win(4, "Load testing", 1280, 732, 1280, 708, z: 4, mon: 1),
                    win(5, "Mail", 0, 25, 2560, 1415, z: 0, mon: 1, process: "Mail"),
                    win(6, "Notes", -2560, 25, 1280, 1415, z: 5, mon: 0), win(7, "Scratch", -1280, 25, 1280, 1415, z: 6, mon: 0)]
        let det = Layouts.detect(wins, ms, claudeOnly: false)
        check(det.count == 1 && det[0].mon == 0, "a window covering a whole screen hides the layout behind it; the left screen's pair is found")
        check(det.first?.tiled == true, "two halves of a screen read as tiled")
        let detClaude = Layouts.detect(wins.filter { $0.id != 5 }, ms, claudeOnly: true)
        check(detClaude.count == 2, "with the cover gone, both screens have a layout (\(detClaude.count))")

        // save and match
        let g = Group(); g.name = "Platform"; g.slots = Layouts.snapshot(Array(wins[0..<4]))
        check(g.slots.count == 4 && g.slots[0].title == "API refactor", "snapshot keeps windows in stacking order")
        let st = Layouts.resolve(g, wins, [:], ms)
        check(st.allSatisfy { $0.window != nil }, "every slot finds its window by title")
        let moved = wins.filter { $0.id != 4 }
        let st2 = Layouts.resolve(g, moved, [:], ms)
        check(st2.filter { $0.window == nil }.map { $0.title } == ["Load testing"], "a window that is closed shows as missing")
        let dup = [win(8, "Claude", 100, 100, 500, 500, z: 0, mon: 1), win(9, "Claude", 1500, 900, 500, 500, z: 1, mon: 1)]
        let gd = Group(); let s1 = Slot(); s1.title = "Claude"; s1.process = "Claude"; s1.x = 1490; s1.y = 880; gd.slots = [s1]
        check(Layouts.resolve(gd, dup, [:], ms).first?.window?.id == 9, "two windows with one title: the nearer one wins")

        // the store round trip, in a temp folder
        Store.dir = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("wg-selftest-\(getpid())")
        Store.d = StoreData(); g.id = Store.newId(); Store.d.groups = [g]; Store.save(); Store.d = StoreData(); Store.load()
        check(Store.d.groups.first?.slots.count == 4, "groups.json saves and loads")
        check(Store.freeName("Platform") == "Platform 2", "a free name next to a taken one")
        check(Updater.newer("1.7.0", than: "1.6.0") && Updater.newer("1.10.0", than: "1.9.3") && !Updater.newer("1.6.0", than: "1.6.0") && !Updater.newer("1.5.9", than: "1.6.0"), "versions compare by number, not by text")
        try? FileManager.default.removeItem(at: Store.dir)

        // the server: token, Host check, state shape
        let srv = Server(port: 47399)
        do { try srv.start() } catch { check(false, "server starts (\(error))"); return 1 }
        func get(_ path: String, token: String? = nil) -> (Int, Data) {
            var req = URLRequest(url: URL(string: "http://127.0.0.1:47399" + path)!)
            if let t = token { req.setValue(t, forHTTPHeaderField: "X-Token") }
            let sem = DispatchSemaphore(value: 0)
            var out = (0, Data())
            URLSession.shared.dataTask(with: req) { d, r, _ in out = ((r as? HTTPURLResponse)?.statusCode ?? 0, d ?? Data()); sem.signal() }.resume()
            _ = sem.wait(timeout: .now() + 10)
            return out
        }
        check(get("/").0 == 403, "the page needs the token")
        check(get("/?t=\(srv.token)").0 == 200, "the page opens with the token")
        check(get("/api/state").0 == 403, "the API needs the token")
        let forged = Request(); forged.method = "GET"; forged.path = "/api/state"
        forged.headers = ["host": "evil.example:47399", "x-token": srv.token]
        check(Api.handle(forged, srv).status == 403, "a foreign Host header is refused (DNS rebinding)")
        let (code, body) = get("/api/state", token: srv.token)
        let json = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any] ?? [:]
        check(code == 200, "state answers")
        for k in ["monitors", "windows", "groups", "settings", "detected", "detectedAll", "version", "platform", "update"] { check(json[k] != nil, "state has \(k)") }
        check((json["monitors"] as? [Any])?.isEmpty == false, "at least one screen is seen (\((json["monitors"] as? [Any])?.count ?? 0))")
        print(failures == 0 ? "all good" : "\(failures) failed")
        return failures == 0 ? 0 : 1
    }
}

// MARK: - main

if CommandLine.arguments.contains("--selftest") {
    _ = NSApplication.shared                                // NSScreen needs an application object
    // Off the main thread, with the main run loop turning: reading the screens hops to the main
    // thread, and a test blocked on main waiting for its own server would never finish.
    Thread.detachNewThread { exit(SelfTest.run()) }
    RunLoop.main.run()
}

let app = NSApplication.shared
let delegate = App()
app.delegate = delegate
app.setActivationPolicy(.accessory)                         // menu bar only, no Dock icon
app.run()
