// Brinback - save a set of windows exactly as they sit, bring them all back with one click,
// and put them back by themselves when the app that owns them restarts.
//
// One file, C# 5, .NET Framework 4.x - so it builds with the csc.exe that ships with every copy
// of Windows 10 and 11 and needs nothing installed. See build.cmd.
//
// Shape of it:
//   Desk         reads and moves top-level windows; every coordinate is physical pixels
//   Claude       knows where the Claude desktop app keeps its session pop-outs, so a window is
//                remembered by its session, not just its title
//   Store        groups.json in %APPDATA%\Brinback, written via temp file + replace
//   Layouts      matches a saved group to the windows open now, detects layouts on each screen
//   AutoRestore  watches for windows that belong to a group and puts them in place as they open
//   Overlay      draws a group on the real screens, with names, for a few seconds
//   Http         a tiny loopback-only server for the UI (a web page in a chromeless window)
//   Tray         the icon, its menu, starting with Windows
//   Updater      asks GitHub for the latest release, and swaps the exe when you say so
//
// Brinback was called Window Groups up to 1.5. Legacy carries groups and settings over.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Interop.UIAutomationClient;

[assembly: AssemblyTitle("Brinback")]
[assembly: AssemblyDescription("Save window layouts. Bring them back.")]
[assembly: AssemblyProduct("Brinback")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion(Brinback.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(Brinback.AppInfo.Version + ".0")]

namespace Brinback
{
    // The one place the version lives. It must match the release tag (v + this): the updater
    // compares it with the tag of the latest release.
    static class AppInfo
    {
        public const string Version = "1.6.10";
        public const string Repo = "Balkaris-ch/brinback";
    }

    // ============================================================ native

    static class N
    {
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        public delegate bool MonProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr l);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
        [StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT { public int length; public int flags; public int showCmd; public POINT ptMin; public POINT ptMax; public RECT rcNormal; }

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT val, int size);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll", EntryPoint = "IsWindowArranged")] static extern bool IsWindowArrangedRaw(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] public static extern uint GetTickCount();
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);

        // Sockets are inheritable by default, and the browser this app starts for its window would
        // take a copy of the listening one along: the port then stays taken after this app exits,
        // the next copy has to use another, and the open window talks to nothing.
        public static void NoInherit(System.Net.Sockets.Socket so) { try { SetHandleInformation(so.Handle, 1, 0); } catch { } }

        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO li);

        // Is this window snapped? The one documented signal Windows gives about Snap Layouts.
        // Snap GROUPS themselves - which snapped windows belong together - are not exposed at all.
        static bool noArranged;
        public static bool IsArranged(IntPtr h)
        {
            if (noArranged) return false;
            try { return IsWindowArrangedRaw(h); }
            catch (EntryPointNotFoundException) { noArranged = true; return false; }
        }

        public const int SW_RESTORE = 9, SW_MAXIMIZE = 3;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        public static readonly IntPtr TOPMOST = new IntPtr(-1), NOTOPMOST = new IntPtr(-2);
    }

    // ============================================================ the desktop

    class Win
    {
        public long Handle; public string Title; public string Process; public string Class;
        public int X, Y, W, H;          // GetWindowRect - what SetWindowPos takes back
        public int VX, VY, VW, VH;      // DWM frame - what you actually see
        public bool Min, Max; public int Z; public int Mon = -1;
        public bool Snapped;            // arranged by Windows Snap Layouts, as opposed to just placed
        public bool IsClaude { get { return string.Equals(Process, ClaudeProcess, StringComparison.OrdinalIgnoreCase); } }
        // The Claude desktop app's process. Tests set BRINBACK_CLAUDE_PROCESS to the name of a
        // throwaway window app, so they never touch real Claude windows.
        public static readonly string ClaudeProcess = Environment.GetEnvironmentVariable("BRINBACK_CLAUDE_PROCESS") ?? "claude";
    }

    class Mon
    {
        public int Index; public int X, Y, W, H; public bool Primary; public string Name;
        public int WX, WY, WW, WH;      // work area: the screen minus the taskbar - what Snap divides
    }

    static class Desk
    {
        public const string UiTitle = "Brinback";
        static readonly int SelfPid = Process.GetCurrentProcess().Id;

        public static List<Mon> Monitors()
        {
            var list = new List<Mon>();
            N.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr hm, IntPtr hdc, ref N.RECT rc, IntPtr l)
            {
                var mi = new N.MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(N.MONITORINFO));
                N.GetMonitorInfo(hm, ref mi);
                var m = new Mon();
                m.X = mi.rcMonitor.L; m.Y = mi.rcMonitor.T;
                m.W = mi.rcMonitor.R - mi.rcMonitor.L; m.H = mi.rcMonitor.B - mi.rcMonitor.T;
                m.Primary = (mi.dwFlags & 1) != 0;
                m.WX = mi.rcWork.L; m.WY = mi.rcWork.T; m.WW = mi.rcWork.R - mi.rcWork.L; m.WH = mi.rcWork.B - mi.rcWork.T;
                list.Add(m);
                return true;
            }, IntPtr.Zero);
            list = list.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
            for (int i = 0; i < list.Count; i++) list[i].Index = i;
            NameThem(list);
            return list;
        }

        // "Top left", "Main", "Far right" - names a person uses, worked out from where each screen
        // sits relative to the primary one.
        static void NameThem(List<Mon> ms)
        {
            if (ms.Count == 0) return;
            Mon p = ms.FirstOrDefault(m => m.Primary) ?? ms[0];
            double pcx = p.X + p.W / 2.0, pcy = p.Y + p.H / 2.0;
            foreach (var m in ms)
            {
                if (m == p) { m.Name = ms.Count == 1 ? "Screen" : "Main"; continue; }
                double dx = m.X + m.W / 2.0 - pcx, dy = m.Y + m.H / 2.0 - pcy;
                string v = dy < -p.H * 0.5 ? "Top" : dy > p.H * 0.5 ? "Bottom" : "";
                string h = "";
                if (dx < -p.W * 0.25) h = dx < -p.W * 1.5 ? "far left" : "left";
                else if (dx > p.W * 0.25) h = dx > p.W * 1.5 ? "far right" : "right";
                if (v != "" && h != "") m.Name = v + " " + h;
                else if (v != "") m.Name = v;
                else if (h != "") m.Name = char.ToUpper(h[0]) + h.Substring(1);
                else m.Name = "Screen";
            }
            foreach (var dup in ms.GroupBy(m => m.Name).Where(g => g.Count() > 1))
            {
                int n = 1;
                foreach (var m in dup.OrderBy(m => m.X).ThenBy(m => m.Y)) m.Name = m.Name + " " + (n++);
            }
        }

        public static int MonitorOf(int x, int y, int w, int h, List<Mon> mons)
        {
            int best = -1; long bestArea = 0;
            foreach (var m in mons)
            {
                long ix = Math.Max(0, Math.Min(x + w, m.X + m.W) - Math.Max(x, m.X));
                long iy = Math.Max(0, Math.Min(y + h, m.Y + m.H) - Math.Max(y, m.Y));
                if (ix * iy > bestArea) { bestArea = ix * iy; best = m.Index; }
            }
            if (best >= 0 || mons.Count == 0) return best;
            // Entirely off every screen: the nearest one.
            double cx = x + w / 2.0, cy = y + h / 2.0, bd = double.MaxValue;
            foreach (var m in mons)
            {
                double d = Math.Abs(m.X + m.W / 2.0 - cx) + Math.Abs(m.Y + m.H / 2.0 - cy);
                if (d < bd) { bd = d; best = m.Index; }
            }
            return best;
        }

        public static List<Win> List(List<Mon> mons)
        {
            var list = new List<Win>();
            var names = new Dictionary<uint, string>();
            int z = 0;
            N.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!N.IsWindowVisible(h)) return true;
                if (N.GetWindow(h, 4) != IntPtr.Zero) return true;                 // owned: dialogs, menus
                if ((N.GetWindowLong(h, -20) & 0x80) != 0) return true;             // tool windows
                int cloaked;
                if (N.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                int len = N.GetWindowTextLength(h);
                if (len <= 0) return true;
                var cls = new StringBuilder(256);
                N.GetClassName(h, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return true;
                uint pid;
                N.GetWindowThreadProcessId(h, out pid);
                if (pid == SelfPid) return true;
                string pn;
                if (!names.TryGetValue(pid, out pn))
                {
                    try { pn = Process.GetProcessById((int)pid).ProcessName; } catch { pn = "?"; }
                    names[pid] = pn;
                }
                var sb = new StringBuilder(len + 1);
                N.GetWindowText(h, sb, sb.Capacity);
                string title = sb.ToString();
                if (IsUiWindow(title, pn)) return true;

                var w = new Win();
                w.Handle = h.ToInt64(); w.Title = title; w.Process = pn; w.Class = c;
                w.Min = N.IsIconic(h); w.Max = N.IsZoomed(h); w.Z = z++;
                w.Snapped = !w.Min && !w.Max && N.IsArranged(h);
                N.RECT r;
                if (w.Min)
                {
                    // A minimized window reports -32000,-32000. Where it lives is where it will
                    // come back to, and only the placement record knows that.
                    var p = new N.WINDOWPLACEMENT();
                    p.length = Marshal.SizeOf(typeof(N.WINDOWPLACEMENT));
                    N.GetWindowPlacement(h, ref p);
                    r = p.rcNormal;
                }
                else N.GetWindowRect(h, out r);
                w.X = r.L; w.Y = r.T; w.W = r.R - r.L; w.H = r.B - r.T;
                N.RECT v;
                if (!w.Min && N.DwmGetWindowAttribute(h, 9, out v, Marshal.SizeOf(typeof(N.RECT))) == 0)
                { w.VX = v.L; w.VY = v.T; w.VW = v.R - v.L; w.VH = v.B - v.T; }
                else { w.VX = w.X; w.VY = w.Y; w.VW = w.W; w.VH = w.H; }
                w.Mon = MonitorOf(w.VX, w.VY, w.VW, w.VH, mons);
                list.Add(w);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        static bool IsUiWindow(string title, string process)
        {
            return title == UiTitle &&
                   (process.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                    process.Equals("chrome", StringComparison.OrdinalIgnoreCase));
        }

        public static List<IntPtr> FindAllUi()
        {
            var all = new List<IntPtr>();
            N.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!N.IsWindowVisible(h)) return true;
                var sb = new StringBuilder(64);
                N.GetWindowText(h, sb, sb.Capacity);
                if (sb.ToString() != UiTitle) return true;
                uint pid;
                N.GetWindowThreadProcessId(h, out pid);
                string pn;
                try { pn = Process.GetProcessById((int)pid).ProcessName; } catch { return true; }
                if (IsUiWindow(UiTitle, pn)) all.Add(h);
                return true;
            }, IntPtr.Zero);
            return all;
        }

        public static IntPtr FindUi()
        {
            IntPtr found = IntPtr.Zero;
            N.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!N.IsWindowVisible(h)) return true;
                int len = N.GetWindowTextLength(h);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                N.GetWindowText(h, sb, sb.Capacity);
                if (sb.ToString() != UiTitle) return true;
                uint pid;
                N.GetWindowThreadProcessId(h, out pid);
                string pn;
                try { pn = Process.GetProcessById((int)pid).ProcessName; } catch { return true; }
                if (IsUiWindow(UiTitle, pn)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static bool Place(long handle, int x, int y, int w, int h, bool max)
        {
            var hw = new IntPtr(handle);
            if (!N.IsWindow(hw)) return false;
            if (N.IsIconic(hw) || N.IsZoomed(hw)) N.ShowWindow(hw, N.SW_RESTORE);
            // Twice on purpose: a window moved onto a screen with a different scale resizes itself
            // on arrival, and the second call puts the size back.
            N.SetWindowPos(hw, IntPtr.Zero, x, y, w, h, N.SWP_NOZORDER | N.SWP_NOACTIVATE);
            N.SetWindowPos(hw, IntPtr.Zero, x, y, w, h, N.SWP_NOZORDER | N.SWP_NOACTIVATE);
            if (max) N.ShowWindow(hw, N.SW_MAXIMIZE);
            return true;
        }

        // To the top of the stack without taking keyboard focus - this works from a background
        // process, which SetForegroundWindow does not.
        public static void Raise(long handle)
        {
            var hw = new IntPtr(handle);
            if (N.IsIconic(hw)) N.ShowWindow(hw, N.SW_RESTORE);
            N.SetWindowPos(hw, N.TOPMOST, 0, 0, 0, 0, N.SWP_NOSIZE | N.SWP_NOMOVE | N.SWP_NOACTIVATE);
            N.SetWindowPos(hw, N.NOTOPMOST, 0, 0, 0, 0, N.SWP_NOSIZE | N.SWP_NOMOVE | N.SWP_NOACTIVATE);
        }
    }

    // ============================================================ real Windows snapping

    enum Zone { None, Left, Right, TopLeft, TopRight, BottomLeft, BottomRight }

    // Windows has no function an app can call to snap a window. Placing it at the right
    // coordinates is not the same thing: it keeps rounded corners and a seam, Windows does not
    // count it as snapped, and it never becomes a snap group - which is exactly what a user sees
    // and objects to. The only way in is the shortcut a person would use: Win+Left/Right for a
    // half, then Win+Up/Down for a quarter. So that is what this does, with every safeguard:
    //
    //  - a snapped window stays "snapped" when moved by code, and Win+Left on a window already
    //    snapped left throws it onto the next screen - so it is restored (unsnapped) first;
    //  - it is moved onto the right screen before any key, because snapping acts on the screen
    //    the window is on;
    //  - no key is sent unless that exact window is in front at that moment, so a snap key can
    //    never land on a window that is not being moved;
    //  - the result is checked with Windows, and anything that did not take falls back to exact
    //    placement.
    //
    // Halves and quarters only. Thirds and the other Snap Layouts shapes have no shortcut; those
    // windows are placed exactly instead, and the interface says which is which.
    static class Snapper
    {
        public static volatile bool Busy;                 // auto-restore and snap keeping hold off while set
        static readonly object gate = new object();
        const ushort VK_LWIN = 0x5B, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_ESC = 0x1B, VK_ALT = 0x12;

        public static Zone ZoneOf(int x, int y, int w, int h, Mon m)
        {
            if (m == null || m.WW <= 0 || m.WH <= 0) return Zone.None;
            double wr = w / (double)m.WW, hr = h / (double)m.WH;
            double cx = (x + w / 2.0 - m.WX) / m.WW, cy = (y + h / 2.0 - m.WY) / m.WH;
            if (wr < 0.44 || wr > 0.57) return Zone.None;
            if (hr > 0.93 && hr < 1.05) return cx < 0.5 ? Zone.Left : Zone.Right;
            if (hr > 0.44 && hr < 0.57)
                return cy < 0.5 ? (cx < 0.5 ? Zone.TopLeft : Zone.TopRight) : (cx < 0.5 ? Zone.BottomLeft : Zone.BottomRight);
            return Zone.None;
        }

        public static Mon MonOf(Slot s, List<Mon> mons)
        {
            int i = Desk.MonitorOf(s.X + 7, s.Y, Math.Max(1, s.W - 14), Math.Max(1, s.H - 7), mons);
            return mons.FirstOrDefault(m => m.Index == i);
        }

        public static Zone ZoneOf(Slot s, List<Mon> mons) { return s.Max ? Zone.None : ZoneOf(s.X, s.Y, s.W, s.H, MonOf(s, mons)); }

        // Already snapped in exactly that zone on that screen? Then leave it alone.
        public static bool IsSnappedAt(long handle, Mon m, Zone z)
        {
            var hw = new IntPtr(handle);
            if (!N.IsArranged(hw)) return false;
            N.RECT r; N.GetWindowRect(hw, out r);
            return ZoneOf(r.L, r.T, r.R - r.L, r.B - r.T, m) == z &&
                   Desk.MonitorOf(r.L + 7, r.T, r.R - r.L - 14, r.B - r.T - 7, new List<Mon> { m }) == m.Index;
        }

        public static bool Snap(long handle, Slot s, Mon m, Zone z)
        {
            lock (gate)
            {
                var hw = new IntPtr(handle);
                if (!N.IsWindow(hw) || z == Zone.None || m == null) return false;
                if (IsSnappedAt(handle, m, z)) return true;
                CloseSnapAssist();
                if (N.IsArranged(hw) || N.IsZoomed(hw) || N.IsIconic(hw)) { N.ShowWindow(hw, N.SW_RESTORE); Thread.Sleep(350); }
                Desk.Place(handle, s.X, s.Y, s.W, s.H, false);
                Thread.Sleep(150);
                bool left = z == Zone.Left || z == Zone.TopLeft || z == Zone.BottomLeft;
                if (!Key(hw, left ? VK_LEFT : VK_RIGHT)) return false;
                if (z == Zone.TopLeft || z == Zone.TopRight) { if (!Key(hw, VK_UP)) return false; }
                else if (z == Zone.BottomLeft || z == Zone.BottomRight) { if (!Key(hw, VK_DOWN)) return false; }
                return IsSnappedAt(handle, m, z);
            }
        }

        static bool Key(IntPtr hw, ushort vk)
        {
            if (!Front(hw)) return false;
            Thread.Sleep(250);
            if (N.GetForegroundWindow() != hw) return false;     // never a snap key into a window we are not moving
            Combo(VK_LWIN, vk, true);
            Thread.Sleep(550);
            return true;
        }

        // Windows only lets the program the user is working in change the foreground window. A
        // tapped Alt, plus briefly sharing input with the current foreground thread, is the
        // well-worn way to be allowed to - and it is checked, never assumed.
        internal static bool Front(IntPtr h)
        {
            // Already in front: no Alt tap. Alt on the focused window opens its menu mode, and
            // that swallows the next shortcut (found when Win+Z did nothing after a deploy).
            if (N.GetForegroundWindow() == h) return true;
            uint fg = N.GetWindowThreadProcessId(N.GetForegroundWindow(), IntPtr.Zero), me = N.GetCurrentThreadId();
            Tap(VK_ALT);
            bool attached = fg != 0 && fg != me && N.AttachThreadInput(me, fg, true);
            N.BringWindowToTop(h);
            N.SetForegroundWindow(h);
            if (attached) N.AttachThreadInput(me, fg, false);
            return N.GetForegroundWindow() == h;
        }

        static N.INPUT KeyIn(ushort vk, bool up, bool ext)
        {
            var i = new N.INPUT { type = 1 };
            i.u.ki.wVk = vk;
            i.u.ki.dwFlags = (up ? 2u : 0u) | (ext ? 1u : 0u);
            return i;
        }

        static void Combo(ushort mod, ushort key, bool ext)
        {
            var seq = new[] { KeyIn(mod, false, false), KeyIn(key, false, ext), KeyIn(key, true, ext), KeyIn(mod, true, false) };
            N.SendInput((uint)seq.Length, seq, Marshal.SizeOf(typeof(N.INPUT)));
        }

        internal static void ComboPlain(ushort mod, ushort key) { Combo(mod, key, false); }

        internal static void Tap(ushort vk)
        {
            var seq = new[] { KeyIn(vk, false, false), KeyIn(vk, true, false) };
            N.SendInput((uint)seq.Length, seq, Marshal.SizeOf(typeof(N.INPUT)));
        }

        // Snap Assist - the "pick a window for the rest of the screen" overlay - opens after a snap
        // that leaves space. Escape closes it, but only once it is the window in front, so Escape
        // can never reach an app (in Claude it would stop whatever the session is doing).
        public static void CloseSnapAssist()
        {
            IntPtr sa = IntPtr.Zero;
            N.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!N.IsWindowVisible(h)) return true;
                var c = new StringBuilder(128); N.GetClassName(h, c, c.Capacity);
                if (c.ToString() != "XamlExplorerHostIslandWindow") return true;
                var t = new StringBuilder(128); N.GetWindowText(h, t, t.Capacity);
                if (t.ToString() == "Snap Assist") { sa = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (sa == IntPtr.Zero) return;
            if (!Front(sa)) return;
            Thread.Sleep(120);
            if (N.GetForegroundWindow() == sa) { Tap(VK_ESC); Thread.Sleep(250); }
        }

        public static double IdleSeconds()
        {
            var li = new N.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(N.LASTINPUTINFO)) };
            if (!N.GetLastInputInfo(ref li)) return 0;
            return unchecked(N.GetTickCount() - li.dwTime) / 1000.0;
        }
    }

    // ============================================================ real Windows snap groups

    // A Snap Group - what the taskbar and Alt+Tab show as one "Group" and bring back together -
    // forms only when windows are snapped through Snap Layouts and Snap Assist. Win+Arrow on
    // each window puts them in exactly the right places, but they stay strangers to each other:
    // that is how a deployed group ended up snapped yet missing from the taskbar's group list.
    //
    // So this does what a person does: Win+Z on the first window, press the zone of the layout
    // that matches the saved one, then pick each remaining window in Snap Assist. Both are read
    // through UI Automation, so nothing is guessed:
    //  - the layout is chosen by the shape of its zones - not by its number, which shifts with
    //    the suggestions Windows puts first, and not by its name, which changes with the language;
    //  - which zone Snap Assist is filling is read from where its window list sits;
    //  - a window is picked by invoking its thumbnail, matched on the window's title; the Close
    //    button on each thumbnail is never touched;
    //  - Win+Z is only sent while the window being grouped is the one in front, and Escape only
    //    while the flyout or Snap Assist is.
    // Anything that does not work out is reported, and Deploy snaps those windows one by one.
    static class SnapGroups
    {
        const string Host = "XamlExplorerHostIslandWindow";
        const int UIA_Button = 50000, UIA_ListItem = 50007, UIA_Invoke = 10000;
        const ushort VK_LWIN = 0x5B, VK_Z = 0x5A, VK_ESC = 0x1B;

        static IUIAutomation ua;
        static IUIAutomation Ua
        {
            get
            {
                if (ua == null) ua = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e")));
                return ua;
            }
        }

        public class Box { public double X, Y, W, H; }

        // A slot as fractions of its screen's work area.
        public static Box Rel(Slot s, Mon m)
        {
            return new Box { X = (s.X - m.WX) / (double)m.WW, Y = (s.Y - m.WY) / (double)m.WH, W = s.W / (double)m.WW, H = s.H / (double)m.WH };
        }

        static double Err(Box a, Box b)
        {
            return Math.Abs(a.X + a.W / 2 - b.X - b.W / 2) + Math.Abs(a.Y + a.H / 2 - b.Y - b.H / 2) + Math.Abs(a.W - b.W) + Math.Abs(a.H - b.H);
        }

        // These windows fill their screen the way a Snap Layout does: two to four of them, none
        // maximized, together covering the work area with next to no overlap.
        static readonly double[] ZoneEdges = { 0, 0.25, 1.0 / 3, 0.5, 2.0 / 3, 0.75, 1 };
        static bool OnEdge(double f) { return ZoneEdges.Any(e => Math.Abs(e - f) < 0.025); }

        // A rectangle (clipped to the work area) whose edges all sit on Snap Layouts zone lines.
        public static bool IsZone(N.RECT r, Mon m)
        {
            double x0 = (r.L - m.WX) / (double)m.WW, x1 = (r.R - m.WX) / (double)m.WW;
            double y0 = (r.T - m.WY) / (double)m.WH, y1 = (r.B - m.WY) / (double)m.WH;
            return OnEdge(x0) && OnEdge(x1) && OnEdge(y0) && OnEdge(y1) && x1 - x0 > 0.2 && y1 - y0 > 0.3;
        }

        public static bool LooksLikeLayout(List<Slot> slots, Mon m)
        {
            if (m == null || slots.Count < 2 || slots.Count > 4 || slots.Any(s => s.Max)) return false;
            var rs = slots.Select(s => new N.RECT {
                L = Math.Max(s.X, m.WX), T = Math.Max(s.Y, m.WY),
                R = Math.Min(s.X + s.W, m.WX + m.WW), B = Math.Min(s.Y + s.H, m.WY + m.WH) }).ToList();
            if (rs.Any(r => r.R <= r.L || r.B <= r.T)) return false;
            for (int i = 0; i < rs.Count; i++)
                for (int j = i + 1; j < rs.Count; j++)
                {
                    long ix = Math.Max(0, Math.Min(rs[i].R, rs[j].R) - Math.Max(rs[i].L, rs[j].L));
                    long iy = Math.Max(0, Math.Min(rs[i].B, rs[j].B) - Math.Max(rs[i].T, rs[j].T));
                    long small = Math.Min((long)(rs[i].R - rs[i].L) * (rs[i].B - rs[i].T), (long)(rs[j].R - rs[j].L) * (rs[j].B - rs[j].T));
                    if (ix * iy > small * 0.06) return false;
                }
            // Every window exactly in a Snap Layouts zone (halves, thirds, quarters): a layout even
            // with zones left empty - two windows in a three-column layout are a Windows snap group too.
            if (rs.All(r => IsZone(r, m))) return true;
            // Area covered, counted on the grid the rectangles' own edges make.
            var xs = rs.SelectMany(r => new[] { r.L, r.R }).Distinct().OrderBy(v => v).ToList();
            var ys = rs.SelectMany(r => new[] { r.T, r.B }).Distinct().OrderBy(v => v).ToList();
            long cover = 0;
            for (int a = 0; a + 1 < xs.Count; a++)
                for (int b = 0; b + 1 < ys.Count; b++)
                {
                    int cx = (xs[a] + xs[a + 1]) / 2, cy = (ys[b] + ys[b + 1]) / 2;
                    if (rs.Any(r => cx >= r.L && cx < r.R && cy >= r.T && cy < r.B)) cover += (long)(xs[a + 1] - xs[a]) * (ys[b + 1] - ys[b]);
                }
            return cover >= (long)m.WW * m.WH * 0.9;
        }

        // Snapped, and where the slot says (within a few percent of the screen).
        public static bool InPlace(long handle, Slot s, Mon m)
        {
            var hw = new IntPtr(handle);
            if (!N.IsArranged(hw)) return false;
            N.RECT r; N.GetWindowRect(hw, out r);
            double tx = m.WW * 0.03, ty = m.WH * 0.03;
            return Math.Abs(r.L - s.X) < tx && Math.Abs(r.T - s.Y) < ty && Math.Abs(r.R - r.L - s.W) < tx && Math.Abs(r.B - r.T - s.H) < ty;
        }

        static string ClassOf(IntPtr h) { var c = new StringBuilder(128); N.GetClassName(h, c, c.Capacity); return c.ToString(); }
        static string TitleOf(IntPtr h) { var t = new StringBuilder(512); N.GetWindowText(h, t, t.Capacity); return t.ToString(); }

        // The shell window in front, once it is one with this automation id inside.
        static IntPtr WaitFor(string automationId, int ms)
        {
            for (int t = 0; t < ms; t += 60)
            {
                var h = N.GetForegroundWindow();
                if (ClassOf(h) == Host && All(h).Any(e => Id(e) == automationId)) return h;
                Thread.Sleep(60);
            }
            return IntPtr.Zero;
        }

        // The flyout and Snap Assist animate in and out, and an element can vanish between
        // finding it and reading it (UIA_E_ELEMENTNOTAVAILABLE). Every read tolerates that.
        static string Id(IUIAutomationElement e) { try { return e.CurrentAutomationId ?? ""; } catch (COMException) { return ""; } }
        static string NameOf(IUIAutomationElement e) { try { return e.CurrentName ?? ""; } catch (COMException) { return ""; } }
        static int TypeOf(IUIAutomationElement e) { try { return e.CurrentControlType; } catch (COMException) { return 0; } }

        static List<IUIAutomationElement> Kids(IUIAutomationElement e)
        {
            var l = new List<IUIAutomationElement>();
            try
            {
                var w = Ua.RawViewWalker;
                var c = w.GetFirstChildElement(e);
                while (c != null) { l.Add(c); c = w.GetNextSiblingElement(c); }
            }
            catch (COMException) { }
            return l;
        }

        static void Collect(IUIAutomationElement e, List<IUIAutomationElement> into, int depth)
        {
            if (e == null || depth > 20) return;
            into.Add(e);
            foreach (var c in Kids(e)) Collect(c, into, depth + 1);
        }

        static List<IUIAutomationElement> All(IntPtr h)
        {
            var l = new List<IUIAutomationElement>();
            try { Collect(Ua.ElementFromHandle(h), l, 0); } catch (COMException) { }
            return l;
        }

        // Escape for the flyout or Snap Assist - never for an app, where it would stop work.
        static void Dismiss()
        {
            if (ClassOf(N.GetForegroundWindow()) == Host) { Snapper.Tap(VK_ESC); Thread.Sleep(200); }
        }

        // The window to the front, then Win+Z: the Snap Layouts flyout, or Zero with why.
        static IntPtr Open(Win w, out string why)
        {
            why = null;
            var h = new IntPtr(w.Handle);
            if (!Snapper.Front(h)) { why = "could not bring the first window to the front"; return IntPtr.Zero; }
            Thread.Sleep(250);
            IntPtr fly = IntPtr.Zero;
            for (int k = 0; k < 2 && fly == IntPtr.Zero; k++)
            {
                if (N.GetForegroundWindow() != h) break;          // never Win+Z into a window we are not grouping
                Snapper.ComboPlain(VK_LWIN, VK_Z);
                fly = WaitFor("SnapFlyoutControl", 1600);
            }
            if (fly == IntPtr.Zero) { why = "the Snap Layouts flyout did not open"; return IntPtr.Zero; }
            Thread.Sleep(250);
            return fly;
        }

        // The plain layouts (not Windows' suggestions) with at least as many zones as windows; the
        // one whose zone shapes match best, exact counts first. Returns the zone for targets[first];
        // zoneOf[i] is the zone index targets[i] goes to.
        static IUIAutomationElement Pick(IntPtr fly, List<Box> targets, int first, out int zonesUsed, out List<int> zoneOf)
        {
            int n = targets.Count;
            IUIAutomationElement zoneForFirst = null; double best = double.MaxValue; zonesUsed = n; zoneOf = null;
            foreach (var grid in All(fly))
            {
                if (Id(grid) != "LayoutGrid") continue;
                try
                {
                    var parent = Ua.RawViewWalker.GetParentElement(grid);
                    if (parent != null && Id(parent) == "WindowGroupSuggestionButton") continue;
                    var zones = Kids(grid).Where(z => TypeOf(z) == UIA_Button).ToList();
                    if (zones.Count < n) continue;          // more zones is fine: the empty ones stay empty
                    var gr = grid.CurrentBoundingRectangle;
                    double gw = gr.right - gr.left, gh = gr.bottom - gr.top;
                    if (gw <= 0 || gh <= 0) continue;
                    var shapes = zones.Select(z => { var r = z.CurrentBoundingRectangle; return new Box { X = (r.left - gr.left) / gw, Y = (r.top - gr.top) / gh, W = (r.right - r.left) / gw, H = (r.bottom - r.top) / gh }; }).ToList();
                    var used = new bool[zones.Count]; double total = 0.1 * (zones.Count - n); bool fits = true;
                    var assign = new List<int>();
                    for (int i = 0; i < n && fits; i++)
                    {
                        int bi = -1; double be = double.MaxValue;
                        for (int j = 0; j < zones.Count; j++) if (!used[j]) { double e = Err(shapes[j], targets[i]); if (e < be) { be = e; bi = j; } }
                        if (be > 0.25) fits = false;
                        else { used[bi] = true; total += be; assign.Add(bi); }
                    }
                    if (fits && total < best) { best = total; zoneForFirst = zones[assign[first]]; zonesUsed = zones.Count; zoneOf = assign; }
                }
                catch (COMException) { }      // a layout that vanished mid-read: the others still count
            }
            return zoneForFirst;
        }

        // wins[i] goes to targets[i], all on screen m.
        public static bool Form(List<Win> wins, List<Box> targets, Mon m, out string why)
        {
            why = null;
            int n = wins.Count;
            try
            {
                IntPtr fly = Open(wins[0], out why);
                if (fly == IntPtr.Zero) return false;
                int zonesUsed; List<int> zoneOf;
                var zoneForFirst = Pick(fly, targets, 0, out zonesUsed, out zoneOf);
                if (zoneForFirst == null) { Dismiss(); why = "no Snap Layout on " + m.Name + " has that shape"; return false; }

                // Zones left empty: Snap Assist offers the remaining zones in layout order, so the
                // first window has to be the one that leaves the group's other zones next in line
                // (left and right thirds: start with the right one, and the empty middle comes last).
                int fi = 0;
                if (zonesUsed > n)
                {
                    var unused = Enumerable.Range(0, zonesUsed).Where(z => !zoneOf.Contains(z)).ToList();
                    for (int f = 0; f < n; f++)
                    {
                        var rest = zoneOf.Where((z, i) => i != f).ToList();
                        if (rest.All(r => unused.All(x => r < x))) { fi = f; break; }
                    }
                    if (fi != 0)
                    {
                        Dismiss();
                        fly = Open(wins[fi], out why);
                        if (fly == IntPtr.Zero) return false;
                        zoneForFirst = Pick(fly, targets, fi, out zonesUsed, out zoneOf);
                        if (zoneForFirst == null) { Dismiss(); why = "no Snap Layout on " + m.Name + " has that shape"; return false; }
                    }
                }
                var press = zoneForFirst.GetCurrentPattern(UIA_Invoke) as IUIAutomationInvokePattern;
                if (press == null) { Dismiss(); why = "the layout zone could not be pressed"; return false; }
                press.Invoke();

                var done = new bool[n]; done[fi] = true;
                for (int step = 1; step < n; step++)
                {
                    var sa = WaitFor("ItemList", 2500);
                    if (sa == IntPtr.Zero) { why = "Snap Assist did not offer the next place"; return false; }
                    Thread.Sleep(200);                     // let it finish animating in
                    var els = All(sa);
                    var list = els.FirstOrDefault(e => Id(e) == "ItemList");
                    if (list == null) { Dismiss(); why = "Snap Assist had no window list"; return false; }
                    tagRECT lr;
                    try { lr = list.CurrentBoundingRectangle; }
                    catch (COMException) { Thread.Sleep(250); els = All(sa); list = els.FirstOrDefault(e => Id(e) == "ItemList"); if (list == null) { Dismiss(); why = "Snap Assist closed"; return false; } lr = list.CurrentBoundingRectangle; }
                    double cx = ((lr.left + lr.right) / 2.0 - m.WX) / m.WW, cy = ((lr.top + lr.bottom) / 2.0 - m.WY) / m.WH;
                    int ti = -1; double te = double.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        if (done[i]) continue;
                        var b = targets[i];
                        double e = Math.Abs(b.X + b.W / 2 - cx) + Math.Abs(b.Y + b.H / 2 - cy);
                        if (cx >= b.X && cx <= b.X + b.W && cy >= b.Y && cy <= b.Y + b.H) e -= 10;   // the place the list sits in wins
                        if (e < te) { te = e; ti = i; }
                    }
                    if (zonesUsed > n && te > -5)
                    {
                        // Snap Assist offers a zone this group leaves empty. What is placed so far is
                        // a snap group if it is two or more windows; the rest go one by one.
                        Dismiss();
                        if (done.Count(d => d) >= 2) return true;
                        why = "Snap Assist offered a place the group leaves empty";
                        return false;
                    }
                    string want = TitleOf(new IntPtr(wins[ti].Handle));
                    var item = els.FirstOrDefault(e => TypeOf(e) == UIA_ListItem && NameOf(e) == want);
                    if (item == null) { Dismiss(); why = "Snap Assist did not offer '" + UiFont.Plain(want) + "'"; return false; }
                    var pick = item.GetCurrentPattern(UIA_Invoke) as IUIAutomationInvokePattern;
                    if (pick == null) { Dismiss(); why = "the window in Snap Assist could not be picked"; return false; }
                    pick.Invoke();
                    done[ti] = true;
                    Thread.Sleep(350);
                }
                if (zonesUsed > n)
                {
                    // Zones left empty: Snap Assist waits in the next one. Close it.
                    if (WaitFor("ItemList", 1500) != IntPtr.Zero) { Thread.Sleep(150); Dismiss(); }
                }
                return true;
            }
            catch (Exception e)
            {
                Dismiss();
                why = "UI Automation: " + e.Message;
                Log.Write("snap group: " + e.GetType().Name + " at " + (e.StackTrace ?? "").Split((char)10).FirstOrDefault());
                return false;
            }
        }
    }

    // ============================================================ Claude desktop

    // The Claude desktop app writes every session pop-out window to session-popouts.json with its
    // session id and title. Keeping the id means a group still finds a session after it is renamed.
    static class Claude
    {
        static Dictionary<string, string> cache = new Dictionary<string, string>();
        static DateTime cacheAt = DateTime.MinValue;
        static readonly object gate = new object();

        public static Dictionary<string, string> Titles()
        {
            lock (gate)
            {
                if ((DateTime.Now - cacheAt).TotalSeconds < 2) return cache;
                var map = new Dictionary<string, string>();
                string f = FindPopouts();
                if (f != null)
                {
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var root = ser.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
                        object arr;
                        if (root != null && root.TryGetValue("popouts", out arr) && arr is System.Collections.IEnumerable)
                        {
                            foreach (var o in (System.Collections.IEnumerable)arr)
                            {
                                var d = o as Dictionary<string, object>;
                                if (d == null) continue;
                                object sid, title;
                                if (d.TryGetValue("sessionId", out sid) && d.TryGetValue("title", out title) && sid != null && title != null)
                                    map[sid.ToString()] = title.ToString();
                            }
                        }
                    }
                    catch (Exception e) { Log.Write("claude: popouts unreadable - " + e.Message); }
                }
                cache = map; cacheAt = DateTime.Now;
                return map;
            }
        }

        static string FindPopouts()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var roots = new[] { Path.Combine(local, "Claude-Data"), Path.Combine(roam, "Claude"), Path.Combine(local, "Claude") };
            string best = null; DateTime bt = DateTime.MinValue;
            foreach (var r in roots)
            {
                if (!Directory.Exists(r)) continue;
                var candidates = new List<string> { Path.Combine(r, "session-popouts.json") };
                try { foreach (var d in Directory.GetDirectories(r)) candidates.Add(Path.Combine(d, "session-popouts.json")); } catch { }
                foreach (var c in candidates)
                {
                    if (!File.Exists(c)) continue;
                    var t = File.GetLastWriteTime(c);
                    if (t > bt) { bt = t; best = c; }
                }
            }
            return best;
        }

        public static DateTime? ProcessStarted()
        {
            DateTime? first = null;
            foreach (var p in Process.GetProcessesByName(Win.ClaudeProcess))
            {
                try { if (first == null || p.StartTime < first) first = p.StartTime; } catch { }
            }
            return first;
        }
    }

    // ============================================================ data

    class Slot
    {
        public string SessionId; public string Title; public string Process;
        public int X, Y, W, H; public bool Max; public int Z;
    }

    class Group
    {
        public string Id; public string Name; public bool Auto = true; public string Saved;
        // Kept automatically from a Windows snap group on this screen, and kept in step with it.
        // Any hand edit (rename, swap, remove) makes it an ordinary group that is left alone.
        public bool Snap; public string SnapScreen;
        public List<Slot> Slots = new List<Slot>();
    }

    class Data
    {
        public int Version = 2;
        public bool AutoRestore = true;
        public bool KeepSnaps = true;
        // System-wide shortcut for All Claude to front; empty turns it off.
        public string ClaudeHotkey = "Ctrl+Alt+Shift+C";
        public List<Group> Groups = new List<Group>();
        // Snap groups the user deleted after they were kept: not kept again unless they change.
        public List<string> Dismissed = new List<string>();
    }

    static class Store
    {
        public static readonly string Dir = Path.Combine(AppData(), "Brinback");
        public static readonly string FilePath = Path.Combine(Dir, "groups.json");
        public static Data D = new Data();
        public static readonly object Gate = new object();

        // True while groups.json exists but cannot be read. Then nothing is saved - an empty list
        // must never overwrite the real one - and the app keeps trying to read it.
        public static volatile bool Blocked;
        public static string Problem;

        // Right after sign-in Windows can answer an empty AppData path for a moment. A relative
        // "Brinback" folder next to the exe would then look like a first run.
        static string AppData()
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(p)) p = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(p)) p = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "C:\\", "AppData\\Roaming");
            return p;
        }

        // At start: groups.json can be locked for the first moments after sign-in (a scanner or a
        // sync tool holding it). Wait for it up to `wait` instead of starting with no groups.
        public static void Load(TimeSpan wait)
        {
            var until = DateTime.Now + wait;
            int tries = 0;
            while (true)
            {
                tries++;
                if (TryLoad()) { if (tries > 1) Log.Write("store: groups.json readable after " + tries + " tries"); return; }
                if (DateTime.Now >= until) break;
                Thread.Sleep(1000);
            }
            Log.Write("store: groups.json could not be read (" + Problem + ") - not saving anything until it can");
        }

        public static void Load() { Load(TimeSpan.Zero); }

        // Called by the timer while Blocked: picks the groups up as soon as the file opens.
        public static void Retry()
        {
            if (!Blocked) return;
            if (TryLoad()) Log.Write("store: groups.json readable again - " + D.Groups.Count + " group(s) loaded");
        }

        static bool TryLoad()
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath)) { D = new Data(); Blocked = false; Problem = null; return true; }
                string text;
                try { text = File.ReadAllText(FilePath, Encoding.UTF8); }
                catch (Exception e)
                {
                    // Locked or denied: the file is fine, we just cannot open it yet.
                    D = new Data(); Blocked = true; Problem = e.Message;
                    return false;
                }
                try
                {
                    var d = new JavaScriptSerializer().Deserialize<Data>(text);
                    D = d ?? new Data();
                    if (D.Groups == null) D.Groups = new List<Group>();
                    if (D.Dismissed == null) D.Dismissed = new List<string>();
                    foreach (var g in D.Groups) if (g.Slots == null) g.Slots = new List<Slot>();
                }
                catch (Exception e)
                {
                    // Opened but not valid: keep it aside under a name that says what happened, and
                    // start fresh. A retry would read the same broken text.
                    string aside = Path.Combine(Dir, "groups.unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    try { File.WriteAllText(aside, text, new UTF8Encoding(false)); } catch { }
                    Log.Write("store: groups.json is not valid (" + e.Message + ") - kept as " + Path.GetFileName(aside));
                    D = new Data();
                }
                Blocked = false; Problem = null;
                return true;
            }
        }

        // Temp file, then replace. A crash or a full disk halfway through must never leave an
        // empty groups.json behind. Once a day the current file is copied to backups\ first; the
        // last 14 days are kept.
        public static void Save()
        {
            lock (Gate)
            {
                if (Blocked) { Log.Write("store: not saved - groups.json could not be read yet"); return; }
                Directory.CreateDirectory(Dir);
                Backup();
                string json = new JavaScriptSerializer().Serialize(D);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
        }

        static void Backup()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                string dir = Path.Combine(Dir, "backups");
                string today = Path.Combine(dir, "groups-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json");
                if (File.Exists(today)) return;
                Directory.CreateDirectory(dir);
                File.Copy(FilePath, today, false);
                foreach (var old in Directory.GetFiles(dir, "groups-*.json").OrderByDescending(f => f).Skip(14)) File.Delete(old);
            }
            catch (Exception e) { Log.Write("store: backup skipped - " + e.Message); }
        }

        public static Group Find(string id)
        {
            return D.Groups.FirstOrDefault(g => g.Id == id);
        }

        // "Left", or "Left 2", "Left 3" ... - the first name no group has yet.
        public static string FreeName(string baseName)
        {
            Func<string, bool> taken = x => D.Groups.Any(g => string.Equals(g.Name, x, StringComparison.OrdinalIgnoreCase));
            if (!taken(baseName)) return baseName;
            for (int i = 2; ; i++) if (!taken(baseName + " " + i)) return baseName + " " + i;
        }

        public static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 10);
        }
    }

    // Brinback was Window Groups up to 1.5. Everything a person had there comes along: the
    // groups, Start with Windows, and the old copy is stopped so two never run side by side.
    static class Legacy
    {
        const string OldName = "Window Groups";
        static readonly string OldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowGroups");

        // Copied, not moved: the old folder stays as it was, as a backup.
        public static void BringGroups()
        {
            try
            {
                string old = Path.Combine(OldDir, "groups.json");
                if (File.Exists(Store.FilePath) || !File.Exists(old)) return;
                Directory.CreateDirectory(Store.Dir);
                File.Copy(old, Store.FilePath, false);
                Log.Write("carried the groups over from Window Groups");
            }
            catch (Exception e) { Log.Write("legacy: groups not carried over - " + e.Message); }
        }

        public static void StopOldCopy()
        {
            try
            {
                Mutex m;
                try { m = Mutex.OpenExisting("Local\\WindowGroups.Single"); } catch { return; }
                m.Dispose();
                foreach (var p in Process.GetProcessesByName("WindowGroups"))
                {
                    try { p.Kill(); p.WaitForExit(4000); } catch { }
                }
                // its window, a chromeless browser window that would now talk to nobody
                N.EnumWindows(delegate (IntPtr h, IntPtr l)
                {
                    if (!N.IsWindowVisible(h)) return true;
                    var sb = new StringBuilder(64);
                    N.GetWindowText(h, sb, sb.Capacity);
                    if (sb.ToString() != OldName) return true;
                    uint pid; N.GetWindowThreadProcessId(h, out pid);
                    try { if (Process.GetProcessById((int)pid).ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)) N.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); } catch { }
                    return true;
                }, IntPtr.Zero);
                Log.Write("stopped the running copy of Window Groups");
            }
            catch (Exception e) { Log.Write("legacy: " + e.Message); }
        }

        // Start with Windows, under the new name and pointing at this exe.
        public static void BringStartup()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
                {
                    if (k.GetValue(OldName) == null) return;
                    Startup.Set(true);
                    if (Startup.On) k.DeleteValue(OldName, false);
                    Log.Write("Start with Windows now starts Brinback");
                }
            }
            catch (Exception e) { Log.Write("legacy: startup not carried over - " + e.Message); }
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        public static void Write(string m)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Store.Dir);
                    string f = Path.Combine(Store.Dir, "log.txt");
                    if (File.Exists(f) && new FileInfo(f).Length > 1000000)
                        File.Copy(f, f + ".old", true);
                    if (File.Exists(f) && new FileInfo(f).Length > 1000000) File.Delete(f);
                    File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // The log folder cannot be written: keep the line in the temp folder instead,
                    // so the reason is not lost.
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "Brinback-log.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m + Environment.NewLine, Encoding.UTF8); } catch { }
                }
            }
        }
    }

    // ============================================================ layouts

    class SlotState { public int Index; public Slot Slot; public Win Window; public string Title; public int Mon; }

    static class Layouts
    {
        // Match a saved group to the windows open right now. Claude sessions by session id first
        // (their current title), then anything by exact title; two candidates with the same title
        // go to whichever already sits nearest the slot.
        public static List<SlotState> Resolve(Group g, List<Win> open, Dictionary<string, string> titles, List<Mon> mons)
        {
            var used = new HashSet<long>();
            var res = new List<SlotState>();
            for (int i = 0; i < g.Slots.Count; i++)
            {
                var s = g.Slots[i];
                string want = s.Title;
                if (!string.IsNullOrEmpty(s.SessionId) && titles.ContainsKey(s.SessionId)) want = titles[s.SessionId];
                Win hit = Pick(open, used, want, s);
                if (hit == null && want != s.Title) hit = Pick(open, used, s.Title, s);
                if (hit != null) used.Add(hit.Handle);
                var st = new SlotState();
                st.Index = i; st.Slot = s; st.Window = hit;
                st.Title = hit != null ? hit.Title : want;
                st.Mon = Desk.MonitorOf(s.X, s.Y, s.W, s.H, mons);
                res.Add(st);
            }
            return res;
        }

        static Win Pick(List<Win> open, HashSet<long> used, string title, Slot s)
        {
            return open.Where(w => !used.Contains(w.Handle) && w.Title == title &&
                                   (string.IsNullOrEmpty(s.Process) || string.Equals(w.Process, s.Process, StringComparison.OrdinalIgnoreCase)))
                       .OrderBy(w => Math.Abs(w.X - s.X) + Math.Abs(w.Y - s.Y))
                       .FirstOrDefault();
        }

        public static List<Slot> Snapshot(IEnumerable<Win> wins)
        {
            var titles = Claude.Titles();
            var bySession = new Dictionary<string, string>();
            foreach (var kv in titles) if (!bySession.ContainsKey(kv.Value)) bySession[kv.Value] = kv.Key;
            var slots = new List<Slot>();
            foreach (var w in wins.OrderBy(w => w.Z))
            {
                var s = new Slot();
                s.Title = w.Title; s.Process = w.Process;
                if (w.IsClaude && bySession.ContainsKey(w.Title)) s.SessionId = bySession[w.Title];
                s.X = w.X; s.Y = w.Y; s.W = w.W; s.H = w.H; s.Max = w.Max; s.Z = w.Z;
                slots.Add(s);
            }
            return slots;
        }

        public class DeployResult
        {
            public string Group; public int Placed; public int Snapped; public int Total;
            public int Grouped;                                       // Windows snap groups formed
            public List<string> NotGrouped = new List<string>();     // screens where that did not work out
            public List<string> Missing = new List<string>();

            public string Summary()
            {
                string t = Placed + " of " + Total + " in place";
                if (Grouped > 0) t += ", " + (Grouped == 1 ? "a Windows snap group" : Grouped + " Windows snap groups");
                else if (Snapped > 0) t += ", " + Snapped + " snapped";
                if (NotGrouped.Count > 0) t += " (not grouped on " + string.Join(", ", NotGrouped) + " - snapped one by one)";
                if (Missing.Count > 0) t += " - not open: " + string.Join(", ", Missing.Select(UiFont.Plain));
                return t;
            }
        }

        // The parts of a group that are whole Snap Layouts: per screen, its windows when together
        // they fill that screen. Those are deployed as one Windows snap group each.
        public static List<List<int>> SnapSets(Group g, List<Mon> mons)
        {
            var res = new List<List<int>>();
            var byScreen = Enumerable.Range(0, g.Slots.Count).Where(i => !g.Slots[i].Max)
                                     .GroupBy(i => Snapper.MonOf(g.Slots[i], mons));
            foreach (var set in byScreen)
            {
                if (set.Key == null) continue;
                var idx = set.ToList();
                if (SnapGroups.LooksLikeLayout(idx.Select(i => g.Slots[i]).ToList(), set.Key)) res.Add(idx);
            }
            return res;
        }

        static readonly object deployGate = new object();

        public static DeployResult Deploy(Group g) { return Deploy(g, false, null); }
        public static DeployResult Deploy(Group g, bool snapOnly) { return Deploy(g, snapOnly, null); }

        // snapOnly: leave placement alone, only snap what should be snapped and is not (used by
        // auto-restore once a group's windows have all reappeared).
        // regroup: windows whose snap group Windows has dropped although they still sit snapped
        // in place (it drops a group once its windows are minimized one by one) - a layout holding
        // one of them is formed again even though nothing has moved.
        public static DeployResult Deploy(Group g, bool snapOnly, HashSet<long> regroup)
        {
            lock (deployGate)
            {
                Snapper.Busy = true;
                try
                {
                    var mons = Desk.Monitors();
                    var open = Desk.List(mons);
                    var states = Resolve(g, open, Claude.Titles(), mons);
                    var r = new DeployResult { Group = g.Name, Total = states.Count };
                    // Lowest in the saved stack first, so raising them one by one leaves the top one on top.
                    var live = states.Where(s => s.Window != null).OrderByDescending(s => s.Slot.Z).ToList();
                    var done = new HashSet<int>();

                    // Whole layouts first, each formed as a real Windows snap group.
                    foreach (var set in SnapSets(g, mons))
                    {
                        var m = Snapper.MonOf(g.Slots[set[0]], mons);
                        var members = live.Where(s => set.Contains(s.Index)).OrderBy(s => s.Slot.Y).ThenBy(s => s.Slot.X).ToList();
                        if (members.Count < 2 || members.Count != set.Count) continue;     // part of it not open: one by one below
                        bool again = regroup != null && members.Any(s => regroup.Contains(s.Window.Handle));
                        if (snapOnly && !again && members.All(s => SnapGroups.InPlace(s.Window.Handle, s.Slot, m)))
                        {
                            foreach (var s in members) done.Add(s.Index);
                            continue;
                        }
                        // Out of any snap group they are in now, onto their screen, then grouped.
                        foreach (var s in members)
                        {
                            var hw = new IntPtr(s.Window.Handle);
                            if (N.IsArranged(hw) || N.IsZoomed(hw) || N.IsIconic(hw)) N.ShowWindow(hw, N.SW_RESTORE);
                        }
                        Thread.Sleep(350);
                        foreach (var s in members) Desk.Place(s.Window.Handle, s.Slot.X, s.Slot.Y, s.Slot.W, s.Slot.H, false);
                        Thread.Sleep(200);
                        Snapper.CloseSnapAssist();
                        string why;
                        bool formed = SnapGroups.Form(members.Select(s => s.Window).ToList(), members.Select(s => SnapGroups.Rel(s.Slot, m)).ToList(), m, out why);
                        Thread.Sleep(300);
                        int inPlace = 0;
                        foreach (var s in members)
                            if (SnapGroups.InPlace(s.Window.Handle, s.Slot, m)) { done.Add(s.Index); r.Snapped++; r.Placed++; inPlace++; }
                        if (formed && inPlace == members.Count)
                        {
                            r.Grouped++;
                            Log.Write("snap group: '" + g.Name + "' on " + m.Name + " formed with " + inPlace + " windows");
                        }
                        else
                        {
                            r.NotGrouped.Add(m.Name);
                            Log.Write("snap group: '" + g.Name + "' on " + m.Name + " not formed (" + (why ?? "windows not where expected") + ") - snapping one by one");
                        }
                    }

                    bool snappedAny = false;
                    foreach (var s in live)
                    {
                        if (done.Contains(s.Index)) continue;
                        var zone = Snapper.ZoneOf(s.Slot, mons);
                        if (zone != Zone.None)
                        {
                            if (Snapper.Snap(s.Window.Handle, s.Slot, Snapper.MonOf(s.Slot, mons), zone)) { r.Snapped++; r.Placed++; snappedAny = true; continue; }
                            Log.Write("snap: '" + s.Window.Title + "' did not snap - placed instead");
                        }
                        if (snapOnly) continue;
                        if (Desk.Place(s.Window.Handle, s.Slot.X, s.Slot.Y, s.Slot.W, s.Slot.H, s.Slot.Max)) r.Placed++;
                    }
                    if (snappedAny) Snapper.CloseSnapAssist();
                    if (!snapOnly) foreach (var s in live) Desk.Raise(s.Window.Handle);
                    foreach (var s in states.Where(s => s.Window == null)) r.Missing.Add(s.Title);
                    Log.Write("deploy '" + g.Name + "'" + (snapOnly ? " (snap only)" : "") + ": " + r.Placed + " of " + r.Total + " placed, " + r.Snapped + " snapped, " +
                              r.Grouped + " snap group(s)" + (r.Missing.Count > 0 ? ", not open: " + string.Join(", ", r.Missing) : ""));
                    return r;
                }
                finally { Snapper.Busy = false; }
            }
        }

        public class Detected
        {
            public int Mon; public string MonName; public string Kind;   // "snap" or "placed"
            public int Index;                                            // nth layout found on this screen, top first
            public bool Tiled; public double Coverage; public List<Win> Windows; public int Behind;
            public bool Hidden;                                          // entirely behind other windows
            public string Key { get { return Mon + "-" + Index; } }
            public List<Win> Snap { get { return Kind == "snap" ? Windows : new List<Win>(); } }
        }

        // What is arranged on each screen right now. A screen can hold several layouts at once - a
        // 3-column snap group in front of a 2x2 that is still snapped behind it is two groups, not
        // one. The first version found one layout per screen; with two stacked it saw only the
        // front one, and the snap keeper then overwrote the group saved from the back one.
        //
        //  - Snap layouts: Windows marks snapped windows, and the windows of one snap layout never
        //    overlap each other. So snapped windows are taken top of the stack first and each joins
        //    the first layout it does not overlap; one that overlaps every layout so far starts a
        //    new one, further back. Two or more windows is a snap group - visible or not.
        //  - Placed layouts: of the windows you can SEE (one mostly hidden behind others does not
        //    count), those in no snap layout. Two or more is a layout too, just not a snapped one.
        public static List<Detected> Detect(List<Win> wins, List<Mon> mons, bool claudeOnly)
        {
            var res = new List<Detected>();
            foreach (var m in mons)
            {
                var stack = wins.Where(w => w.Mon == m.Index && !w.Min && (!claudeOnly || w.IsClaude)).OrderBy(w => w.Z).ToList();

                var visible = new List<Win>();
                int behind = 0;
                foreach (var w in stack)
                {
                    if (Covered(w, visible) > 0.7) { behind++; continue; }
                    visible.Add(w);
                    if (w.Max) { behind += stack.Count - stack.IndexOf(w) - 1; break; }   // nothing below a maximized window shows
                }

                var layers = new List<List<Win>>();
                foreach (var w in stack.Where(x => x.Snapped))
                {
                    double area = Math.Max(1.0, (double)w.VW * w.VH);
                    var home = layers.FirstOrDefault(L => L.All(o => Inter(w.VX, w.VY, w.VW, w.VH, o.VX, o.VY, o.VW, o.VH) < 0.05 * area));
                    if (home != null) home.Add(w); else layers.Add(new List<Win> { w });
                }

                int idx = 0;
                var inSnap = new HashSet<long>();
                foreach (var L in layers.Where(L => L.Count >= 2))
                {
                    var d = Make(m, L, "snap", idx++);
                    d.Hidden = L.All(w => !visible.Contains(w));
                    res.Add(d);
                    foreach (var w in L) inSnap.Add(w.Handle);
                }
                var rest = visible.Where(w => !w.Max && !inSnap.Contains(w.Handle)).ToList();
                if (rest.Count >= 2)
                {
                    var d = Make(m, rest, "placed", idx++);
                    d.Behind = behind;
                    res.Add(d);
                }
            }
            return res;
        }

        static Detected Make(Mon m, List<Win> ws, string kind, int index)
        {
            double sum = 0, overlap = 0, inside = 0;
            foreach (var w in ws)
            {
                sum += (double)w.VW * w.VH;
                inside += Inter(w.VX, w.VY, w.VW, w.VH, m.X, m.Y, m.W, m.H);
            }
            for (int i = 0; i < ws.Count; i++)
                for (int j = i + 1; j < ws.Count; j++)
                    overlap += Inter(ws[i].VX, ws[i].VY, ws[i].VW, ws[i].VH, ws[j].VX, ws[j].VY, ws[j].VW, ws[j].VH);
            var d = new Detected { Mon = m.Index, MonName = m.Name, Kind = kind, Index = index };
            d.Coverage = Math.Min(1.0, inside / ((double)m.W * m.H));
            d.Tiled = sum > 0 && overlap / sum < 0.12 && d.Coverage > 0.55;
            d.Windows = ws.OrderBy(w => w.VY / 200).ThenBy(w => w.VX).ToList();
            return d;
        }

        // How much of a window is hidden behind the given ones, sampled on a 20x20 grid - exact
        // union areas of many rectangles are not worth the code for a yes/no question.
        static double Covered(Win w, List<Win> above)
        {
            if (above.Count == 0 || w.VW <= 0 || w.VH <= 0) return 0;
            int hit = 0, n = 20;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double px = w.VX + (i + 0.5) * w.VW / n, py = w.VY + (j + 0.5) * w.VH / n;
                    foreach (var a in above)
                        if (px >= a.VX && px < a.VX + a.VW && py >= a.VY && py < a.VY + a.VH) { hit++; break; }
                }
            return hit / (double)(n * n);
        }

        public static double Inter(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh)
        {
            double ix = Math.Max(0, Math.Min(ax + aw, bx + bw) - Math.Max(ax, bx));
            double iy = Math.Max(0, Math.Min(ay + ah, by + bh) - Math.Max(ay, by));
            return ix * iy;
        }
    }

    // ============================================================ auto-restore

    // Puts a window into its saved slot the first time it appears - not continuously, so moving
    // a window by hand afterwards is never fought. A new window is given a couple of seconds
    // first: the owning app positions it itself on creation, and its title often arrives late.
    // Windows forgets every snap group when it restarts. This keeps them: every snap layout that
    // has held still for twenty seconds is saved as a group, named after its screen ("Left",
    // "Left 2", ...), and a kept one follows its layout as it is resized or as a window in it is
    // swapped. Rules, each learned the hard way:
    //  - a screen can hold several snap groups; a new layout gets a new group, it never replaces
    //    another one;
    //  - a kept group follows a layout only when they share at least half their windows AND the
    //    kept group is not itself still on screen as a layout of its own;
    //  - a group the user saved or edited (Snap = false) is never touched, and never duplicated.
    class SnapKeeper
    {
        readonly Dictionary<string, KeyValuePair<string, DateTime>> seen = new Dictionary<string, KeyValuePair<string, DateTime>>();

        static string KeyOf(Slot x) { return !string.IsNullOrEmpty(x.SessionId) ? x.SessionId : (x.Process + ":" + x.Title); }

        public static string Signature(IEnumerable<Slot> slots)
        {
            return string.Join("|", slots.Select(KeyOf).OrderBy(x => x));
        }

        public void Tick(List<Win> open, List<Mon> mons)
        {
            if (Snapper.Busy) return;
            lock (Store.Gate) if (!Store.D.KeepSnaps) { seen.Clear(); return; }
            var now = DateTime.Now;
            var layouts = Layouts.Detect(open, mons, false).Where(d => d.Kind == "snap").ToList();
            var liveSets = layouts.Select(d => new HashSet<long>(d.Windows.Select(w => w.Handle))).ToList();
            var alive = new HashSet<string>();
            foreach (var d in layouts)
            {
                var slots = Layouts.Snapshot(d.Windows);
                string members = Signature(slots);
                string full = members + "@" + string.Join(",", slots.OrderBy(x => x.X).ThenBy(x => x.Y).Select(x => x.X + ":" + x.Y + ":" + x.W + ":" + x.H));
                alive.Add(members);
                KeyValuePair<string, DateTime> prev;
                if (!seen.TryGetValue(members, out prev) || prev.Key != full) { seen[members] = new KeyValuePair<string, DateTime>(full, now); continue; }
                if ((now - prev.Value).TotalSeconds < 20) continue;
                Keep(d, slots, members, open, mons, liveSets);
            }
            foreach (var k in seen.Keys.Where(k => !alive.Contains(k)).ToList()) seen.Remove(k);
        }

        void Keep(Layouts.Detected d, List<Slot> slots, string members, List<Win> open, List<Mon> mons, List<HashSet<long>> liveSets)
        {
            var handles = new HashSet<long>(d.Windows.Select(w => w.Handle));
            var keys = new HashSet<string>(slots.Select(KeyOf));
            lock (Store.Gate)
            {
                var titles = Claude.Titles();
                var openOf = new Dictionary<Group, HashSet<long>>();
                foreach (var g in Store.D.Groups)
                    openOf[g] = new HashSet<long>(Layouts.Resolve(g, open, titles, mons).Where(x => x.Window != null).Select(x => x.Window.Handle));

                foreach (var g in Store.D.Groups)
                {
                    if (!openOf[g].SetEquals(handles)) continue;
                    // Already kept. A kept snap group follows a resized layout; a user's group does not.
                    if (g.Snap && Moved(g.Slots, slots))
                    {
                        g.Slots = slots; g.Saved = DateTime.Now.ToString("dd MMM HH:mm"); Store.Save();
                        Log.Write("snap: '" + g.Name + "' layout changed - kept");
                    }
                    return;
                }
                if (Store.D.Dismissed.Contains(members)) return;

                Group grew = null; double best = 0;
                foreach (var g in Store.D.Groups.Where(x => x.Snap && x.SnapScreen == d.MonName))
                {
                    if (liveSets.Any(set => set.Count >= 2 && set.SetEquals(openOf[g]))) continue;   // still on screen as its own layout
                    var gk = new HashSet<string>(g.Slots.Select(KeyOf));
                    double j = gk.Intersect(keys).Count() / (double)Math.Max(1, gk.Union(keys).Count());
                    if (j >= 0.5 && j > best) { best = j; grew = g; }
                }
                if (grew != null)
                {
                    grew.Slots = slots; grew.Saved = DateTime.Now.ToString("dd MMM HH:mm");
                    Log.Write("snap: '" + grew.Name + "' follows its layout on " + d.MonName + " (" + slots.Count + " windows)");
                }
                else
                {
                    string name = Store.FreeName(d.MonName);
                    Store.D.Groups.Add(new Group { Id = Store.NewId(), Name = name, Snap = true, SnapScreen = d.MonName, Auto = true,
                                                   Slots = slots, Saved = DateTime.Now.ToString("dd MMM HH:mm") });
                    Log.Write("snap: kept a snap group on " + d.MonName + " as '" + name + "' (" + slots.Count + " windows)");
                }
                Store.Save();
            }
        }

        static bool Moved(List<Slot> a, List<Slot> b)
        {
            if (a.Count != b.Count) return true;
            foreach (var x in b)
            {
                var y = a.FirstOrDefault(s => KeyOf(s) == KeyOf(x));
                if (y == null || Math.Abs(y.X - x.X) > 4 || Math.Abs(y.Y - x.Y) > 4 || Math.Abs(y.W - x.W) > 4 || Math.Abs(y.H - x.H) > 4) return true;
            }
            return false;
        }
    }

    class AutoRestore
    {
        readonly SnapKeeper keeper = new SnapKeeper();
        int ticks;
        readonly HashSet<long> done = new HashSet<long>();
        readonly Dictionary<long, DateTime> pending = new Dictionary<long, DateTime>();
        // Groups whose windows were just put back: snapped for real once nothing new has arrived
        // for a few seconds and nobody is typing - snapping takes the keyboard for a moment.
        readonly Dictionary<string, DateTime> toSnap = new Dictionary<string, DateTime>();

        public void Init()
        {
            // Windows already open when this starts are left alone, unless Claude itself has only
            // just started - that is the "turned the PC on, opened Claude" case this exists for.
            var started = Claude.ProcessStarted();
            bool freshClaude = started.HasValue && (DateTime.Now - started.Value).TotalMinutes < 3;
            foreach (var w in Desk.List(Desk.Monitors()))
                if (!(freshClaude && w.IsClaude)) done.Add(w.Handle);
            if (freshClaude) Log.Write("auto: Claude started " + (int)(DateTime.Now - started.Value).TotalSeconds + "s ago - its windows will be put in place");
        }

        public void Tick()
        {
            if (Snapper.Busy) return;
            List<Mon> mons;
            List<Win> open;
            try { mons = Desk.Monitors(); open = Desk.List(mons); }
            catch { return; }
            var alive = new HashSet<long>(open.Select(w => w.Handle));
            done.RemoveWhere(h => !alive.Contains(h));
            foreach (var h in pending.Keys.Where(h => !alive.Contains(h)).ToList()) pending.Remove(h);

            List<Group> groups;
            lock (Store.Gate)
            {
                if (!Store.D.AutoRestore)
                {
                    // Switched off: everything open now counts as seen, so switching it back on
                    // later does not suddenly yank every window across the screens.
                    foreach (var w in open) done.Add(w.Handle);
                    pending.Clear();
                    groups = new List<Group>();
                }
                else groups = Store.D.Groups.Where(g => g.Auto && g.Slots.Count > 0).ToList();
            }
            if (groups.Count == 0)
            {
                foreach (var w in open) done.Add(w.Handle);
                if (++ticks % 3 == 0) { try { keeper.Tick(open, mons); } catch (Exception e) { Log.Write("snap: " + e.Message); } }
                return;
            }

            var titles = Claude.Titles();
            var touched = new HashSet<Group>();
            var now = DateTime.Now;
            foreach (var w in open)
            {
                if (done.Contains(w.Handle)) continue;
                DateTime first;
                if (!pending.TryGetValue(w.Handle, out first)) { pending[w.Handle] = now; continue; }
                if ((now - first).TotalSeconds < 2.5) continue;

                Group hitGroup = null; Slot hitSlot = null;
                foreach (var g in groups)
                {
                    foreach (var s in g.Slots)
                    {
                        string want = s.Title;
                        if (!string.IsNullOrEmpty(s.SessionId) && titles.ContainsKey(s.SessionId)) want = titles[s.SessionId];
                        bool proc = string.IsNullOrEmpty(s.Process) || string.Equals(w.Process, s.Process, StringComparison.OrdinalIgnoreCase);
                        if (proc && (w.Title == want || w.Title == s.Title)) { hitGroup = g; hitSlot = s; break; }
                    }
                    if (hitSlot != null) break;
                }
                if (hitSlot != null)
                {
                    Desk.Place(w.Handle, hitSlot.X, hitSlot.Y, hitSlot.W, hitSlot.H, hitSlot.Max);
                    touched.Add(hitGroup);
                    done.Add(w.Handle); pending.Remove(w.Handle);
                    Log.Write("auto: '" + w.Title + "' -> " + hitGroup.Name);
                }
                else if ((now - first).TotalSeconds > 30)
                {
                    done.Add(w.Handle); pending.Remove(w.Handle);       // never matched: not ours
                }
            }

            // Every third tick: is there a snap group worth keeping?
            if (++ticks % 3 == 0) { try { keeper.Tick(open, mons); } catch (Exception e) { Log.Write("snap: " + e.Message); } }

            foreach (var g in touched) toSnap[g.Id] = now;
            if (toSnap.Count > 0 && Snapper.IdleSeconds() > 3)
            {
                var ready = toSnap.Where(kv => (now - kv.Value).TotalSeconds > 5).Select(kv => kv.Key).ToList();
                foreach (var id in ready) toSnap.Remove(id);
                var gs = groups.Where(x => ready.Contains(x.Id)).ToList();
                if (gs.Count > 0)
                    ThreadPool.QueueUserWorkItem(delegate { foreach (var x in gs) { try { Layouts.Deploy(x, true); } catch (Exception e) { Log.Write("auto snap: " + e.Message); } } });
            }

            // Restore the stacking of every group that just received a window.
            foreach (var g in touched)
            {
                var states = Layouts.Resolve(g, open, titles, mons);
                foreach (var s in states.Where(s => s.Window != null).OrderByDescending(s => s.Slot.Z))
                    Desk.Raise(s.Window.Handle);
            }
        }
    }

    static class Front
    {
        // wins: bottom of the stack first. The last one ends on top and gets the keyboard.
        public static int Bring(List<Win> wins)
        {
            foreach (var w in wins) Desk.Raise(w.Handle);
            if (wins.Count > 0)
            {
                var top = new IntPtr(wins[wins.Count - 1].Handle);
                Thread.Sleep(60);
                Snapper.Front(top);
            }
            return wins.Count;
        }

        // A saved group's windows on top. Minimized ones are restored - and that is where a
        // Windows snap group is lost: minimized and restored one by one, its windows come back
        // snapped in place, but Windows no longer lists them as a Group in the taskbar or Alt+Tab.
        // So a layout that had minimized windows is formed again, the way Deploy forms it.
        public static string Group(Group g)
        {
            var mons = Desk.Monitors();
            var live = Layouts.Resolve(g, Desk.List(mons), Claude.Titles(), mons).Where(x => x.Window != null).OrderByDescending(x => x.Slot.Z).ToList();
            var wasMin = new HashSet<long>(live.Where(x => x.Window.Min).Select(x => x.Window.Handle));
            Bring(live.Select(x => x.Window).ToList());
            string note = "";
            bool inLayout = Layouts.SnapSets(g, mons).Any(set => live.Any(x => set.Contains(x.Index) && wasMin.Contains(x.Window.Handle)));
            if (inLayout)
            {
                Thread.Sleep(400);                    // let them finish coming back
                var r = Layouts.Deploy(g, true, wasMin);
                Bring(live.Select(x => x.Window).ToList());
                if (r.Grouped > 0) note = ", and grouped again as a Windows snap group";
                else if (r.NotGrouped.Count > 0) note = " - Windows did not group them again on " + string.Join(", ", r.NotGrouped);
            }
            return live.Count + " window(s) of " + g.Name + " brought to the front" + note + ".";
        }

        // Every Claude window on top, minimized ones restored, in the order they are stacked now.
        // A saved layout that had one of them minimized is formed again as a snap group, as above.
        public static string AllClaude()
        {
            var mons = Desk.Monitors();
            var wins = Desk.List(mons).Where(w => w.IsClaude).OrderByDescending(w => w.Z).ToList();
            if (wins.Count == 0) return "No Claude window is open.";
            var wasMin = new HashSet<long>(wins.Where(w => w.Min).Select(w => w.Handle));
            Bring(wins);
            int regrouped = 0;
            if (wasMin.Count > 0)
            {
                Thread.Sleep(400);                    // let them finish coming back
                List<Group> gs;
                lock (Store.Gate) gs = Store.D.Groups.ToList();
                var open = Desk.List(mons);
                var titles = Claude.Titles();
                foreach (var g in gs)
                {
                    var live = Layouts.Resolve(g, open, titles, mons).Where(x => x.Window != null).ToList();
                    if (!Layouts.SnapSets(g, mons).Any(set => live.Any(x => set.Contains(x.Index) && wasMin.Contains(x.Window.Handle)))) continue;
                    regrouped += Layouts.Deploy(g, true, wasMin).Grouped;
                }
                Bring(wins);
            }
            Log.Write("front: " + wins.Count + " Claude window(s)" + (wasMin.Count > 0 ? ", " + wasMin.Count + " restored" : "") + (regrouped > 0 ? ", " + regrouped + " snap group(s) formed again" : ""));
            return (wins.Count == 1 ? "1 Claude window" : wins.Count + " Claude windows") + " in front" +
                   (wasMin.Count > 0 ? ", " + wasMin.Count + " of them restored" : "") +
                   (regrouped > 0 ? ", " + (regrouped == 1 ? "a snap group" : regrouped + " snap groups") + " formed again" : "") + ".";
        }
    }

    // ============================================================ on-screen preview

    // Frame: an open window, brought to the front - only a thin outline and its name are drawn
    // round it, so it stays visible and usable. A filled tile is only for a window that is not
    // open: a placeholder where it would go.
    class Tile { public Rectangle R; public string Title; public string Sub; public bool Missing; public string Group; public bool Frame; }

    class Overlay : Form
    {
        static readonly List<Overlay> live = new List<Overlay>();
        static System.Windows.Forms.Timer timer;
        readonly Tile t;

        static readonly Color Ink = Color.FromArgb(10, 10, 10);
        static readonly Color Paper = Color.FromArgb(244, 243, 240);
        static readonly Color Mute = Color.FromArgb(160, 159, 154);
        static readonly Color Signal = Color.FromArgb(37, 247, 22);
        static readonly Color Amber = Color.FromArgb(232, 170, 72);

        public static void ShowTiles(List<Tile> tiles, int seconds)
        {
            CloseAll();
            foreach (var t in tiles)
            {
                if (t.R.Width < 40 || t.R.Height < 40) continue;
                var f = new Overlay(t);
                live.Add(f);
                f.Show();
            }
            if (timer == null)
            {
                timer = new System.Windows.Forms.Timer();
                timer.Tick += delegate { timer.Stop(); CloseAll(); };
            }
            timer.Stop();
            timer.Interval = seconds * 1000;
            timer.Start();
        }

        public static void CloseAll()
        {
            foreach (var f in live.ToArray()) { try { f.Close(); f.Dispose(); } catch { } }
            live.Clear();
        }

        Overlay(Tile tile)
        {
            t = tile;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = t.R;
            DoubleBuffered = true;
            if (t.Frame)
            {
                // everything but the outline and the name tag is see-through and click-through
                BackColor = Color.FromArgb(255, 0, 255);
                TransparencyKey = BackColor;
                Opacity = 0.99;
            }
            else
            {
                BackColor = Ink;
                Opacity = 0.9;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x20;     // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT: clicks go through
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var r = ClientRectangle;
            int pad = Math.Max(14, Math.Min(r.Width, r.Height) / 22);
            if (t.Frame)
            {
                g.SmoothingMode = SmoothingMode.None;
                using (var pen = new Pen(Signal, 4f)) g.DrawRectangle(pen, 2, 2, r.Width - 5, r.Height - 5);
                using (var mono = UiFont.Mono(12f))
                {
                    string label = ((t.Group ?? "").ToUpperInvariant() + "   " + UiFont.Plain(t.Title)).Trim();
                    var sz = g.MeasureString(label, mono);
                    float w = Math.Min(sz.Width + 20, r.Width - 8);
                    using (var bg = new SolidBrush(Signal)) g.FillRectangle(bg, 4, 4, w, sz.Height + 10);
                    using (var ink = new SolidBrush(Ink))
                    using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                        g.DrawString(label, mono, ink, new RectangleF(14, 9, w - 20, sz.Height + 2), fmt);
                }
                return;
            }

            using (var pen = new Pen(t.Missing ? Mute : Signal, 2f))
            {
                if (t.Missing) pen.DashPattern = new float[] { 5f, 4f };
                g.DrawRectangle(pen, 1, 1, r.Width - 3, r.Height - 3);
            }

            using (var mono = UiFont.Mono(11f))
            using (var b = new SolidBrush(t.Missing ? Mute : Signal))
                g.DrawString((t.Group ?? "").ToUpperInvariant(), mono, b, pad, pad);

            float size = Math.Max(16f, Math.Min(64f, Math.Min(r.Width / 14f, r.Height / 6f)));
            var box = new RectangleF(pad, pad, r.Width - 2 * pad, r.Height - 2 * pad);
            using (var title = UiFont.Display(size))
            using (var sub = UiFont.Mono(Math.Max(10f, size * 0.42f)))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord })
            {
                string text = UiFont.Plain(t.Title);
                var tsz = g.MeasureString(text, title, (int)box.Width, fmt);
                float th = Math.Min(tsz.Height, box.Height * 0.6f);
                float subH = sub.GetHeight(g);
                float top = box.Y + (box.Height - th - subH * 1.8f) / 2f;
                using (var pb = new SolidBrush(t.Missing ? Mute : Paper))
                    g.DrawString(text, title, pb, new RectangleF(box.X, top, box.Width, th), fmt);
                using (var sb = new SolidBrush(t.Missing ? Amber : Signal))
                    g.DrawString(t.Sub ?? "", sub, sb, new RectangleF(box.X, top + th + subH * 0.6f, box.Width, subH * 1.2f), fmt);
            }
        }
    }

    static class UiFont
    {
        static string display, mono;
        public static Font Display(float px) { return new Font(Pick(ref display, "Geist", "Segoe UI Variable Display", "Segoe UI"), px, FontStyle.Regular, GraphicsUnit.Pixel); }
        public static Font Mono(float px) { return new Font(Pick(ref mono, "Geist Mono", "Cascadia Mono", "Consolas"), px, FontStyle.Regular, GraphicsUnit.Pixel); }

        static string Pick(ref string cached, params string[] names)
        {
            if (cached != null) return cached;
            using (var fams = new InstalledFontCollection())
            {
                var have = new HashSet<string>(fams.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                cached = names.FirstOrDefault(n => have.Contains(n)) ?? "Segoe UI";
            }
            return cached;
        }

        // GDI+ has no colour emoji - they come out as empty boxes. The browser UI shows them; the
        // overlay drops them. Surrogate pairs, variation selector 16, zero-width joiner, and the
        // dingbat / misc-symbol blocks.
        public static string Plain(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                int v = c;
                if (char.IsSurrogate(c) || v == 0xFE0F || v == 0x200D || (v >= 0x2600 && v <= 0x27BF) || (v >= 0x2B00 && v <= 0x2BFF)) continue;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }

    // ============================================================ loopback HTTP for the UI

    class Req
    {
        public string Method = "", Path = "", Query = "", Body = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Q(string key)
        {
            foreach (var part in Query.Split('&'))
            {
                int i = part.IndexOf('=');
                if (i > 0 && Uri.UnescapeDataString(part.Substring(0, i)) == key) return Uri.UnescapeDataString(part.Substring(i + 1));
            }
            return null;
        }
    }

    class Resp
    {
        public int Status = 200; public string Type = "text/plain; charset=utf-8"; public byte[] Body = new byte[0];
        public static Resp Text(int status, string s) { return new Resp { Status = status, Body = Encoding.UTF8.GetBytes(s) }; }
        public static Resp Html(int status, string s) { return new Resp { Status = status, Type = "text/html; charset=utf-8", Body = Encoding.UTF8.GetBytes(s) }; }
        public static Resp Json(int status, object o) { return new Resp { Status = status, Type = "application/json; charset=utf-8", Body = Encoding.UTF8.GetBytes(Api.Ser.Serialize(o)) }; }
    }

    // A raw socket server rather than HttpListener: HttpListener needs a URL reservation, which
    // needs admin. Bound to 127.0.0.1 only, Host header checked (no DNS rebinding), and every
    // API call needs the per-run token that only the URL the tray opens carries.
    class Http
    {
        TcpListener lis;
        public int Port;
        public string Token; public int Prefer;      // a port to try first: the one handed over by an update
        static readonly string CRLF = "" + (char)13 + (char)10;

        public void Start()
        {
            foreach (int p in (Prefer > 0 && Prefer != 47391 ? new[] { Prefer, 47391, 0 } : new[] { 47391, 0 }))
            {
                try { lis = new TcpListener(IPAddress.Loopback, p); lis.Start(); N.NoInherit(lis.Server); break; }
                catch { lis = null; }
            }
            if (lis == null) throw new Exception("could not open a local port for the UI");
            Port = ((IPEndPoint)lis.LocalEndpoint).Port;
            var th = new Thread(Loop) { IsBackground = true, Name = "http" };
            th.Start();
        }

        void Loop()
        {
            while (true)
            {
                TcpClient c;
                try { c = lis.AcceptTcpClient(); N.NoInherit(c.Client); } catch { return; }
                ThreadPool.QueueUserWorkItem(o => Serve((TcpClient)o), c);
            }
        }

        void Serve(TcpClient c)
        {
            using (c)
            {
                try
                {
                    c.ReceiveTimeout = 8000; c.SendTimeout = 8000;
                    var s = c.GetStream();
                    var buf = new MemoryStream();
                    var chunk = new byte[8192];
                    int end = -1;
                    while (end < 0)
                    {
                        int n = s.Read(chunk, 0, chunk.Length);
                        if (n <= 0) return;
                        buf.Write(chunk, 0, n);
                        if (buf.Length > 65536) return;
                        end = Find(buf.GetBuffer(), (int)buf.Length);
                    }
                    byte[] all = buf.ToArray();
                    string head = Encoding.ASCII.GetString(all, 0, end);
                    var lines = head.Split(new[] { CRLF }, StringSplitOptions.None);
                    var first = lines[0].Split(' ');
                    if (first.Length < 2) return;
                    var req = new Req { Method = first[0] };
                    string target = first[1];
                    int qi = target.IndexOf('?');
                    req.Path = qi >= 0 ? target.Substring(0, qi) : target;
                    req.Query = qi >= 0 ? target.Substring(qi + 1) : "";
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int ci = lines[i].IndexOf(':');
                        if (ci > 0) req.Headers[lines[i].Substring(0, ci).Trim()] = lines[i].Substring(ci + 1).Trim();
                    }
                    int clen = 0;
                    string cl;
                    if (req.Headers.TryGetValue("Content-Length", out cl)) int.TryParse(cl, out clen);
                    if (clen > 2000000) { Write(s, Resp.Text(413, "too large")); return; }
                    var body = new MemoryStream();
                    body.Write(all, end + 4, all.Length - end - 4);
                    while (body.Length < clen)
                    {
                        int n = s.Read(chunk, 0, Math.Min(chunk.Length, clen - (int)body.Length));
                        if (n <= 0) break;
                        body.Write(chunk, 0, n);
                    }
                    req.Body = Encoding.UTF8.GetString(body.ToArray());
                    Resp resp;
                    try { resp = Api.Route(req, this); }
                    catch (Exception e)
                    {
                        Log.Write("http: " + req.Method + " " + req.Path + " failed - " + e.Message);
                        resp = Resp.Json(500, new Dictionary<string, object> { { "error", e.Message } });
                    }
                    Write(s, resp);
                }
                catch { }
            }
        }

        static int Find(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        void Write(Stream s, Resp r)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(Reason(r.Status)).Append(CRLF);
            sb.Append("Content-Type: ").Append(r.Type).Append(CRLF);
            sb.Append("Content-Length: ").Append(r.Body.Length).Append(CRLF);
            sb.Append("Cache-Control: no-store").Append(CRLF);
            sb.Append("X-Content-Type-Options: nosniff").Append(CRLF);
            sb.Append("Referrer-Policy: no-referrer").Append(CRLF);
            sb.Append("X-Frame-Options: DENY").Append(CRLF);
            if (r.Type.StartsWith("text/html"))
                sb.Append("Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'").Append(CRLF);
            sb.Append("Connection: close").Append(CRLF).Append(CRLF);
            var h = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(h, 0, h.Length);
            s.Write(r.Body, 0, r.Body.Length);
            s.Flush();
        }

        static string Reason(int s)
        {
            switch (s) { case 200: return "OK"; case 403: return "Forbidden"; case 404: return "Not Found"; case 409: return "Conflict"; case 413: return "Payload Too Large"; default: return "Error"; }
        }
    }

    // ============================================================ the API the UI talks to

    static class Api
    {
        public static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        static string indexHtml;

        static string Index()
        {
            if (indexHtml != null) return indexHtml;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Brinback.index.html"))
            using (var r = new StreamReader(s, Encoding.UTF8))
                indexHtml = r.ReadToEnd();
            return indexHtml;
        }

        public static Resp Route(Req r, Http h)
        {
            string host;
            r.Headers.TryGetValue("Host", out host);
            if (host != "127.0.0.1:" + h.Port && host != "localhost:" + h.Port) return Resp.Text(403, "forbidden");

            if (r.Method == "GET" && r.Path == "/")
            {
                if (r.Q("t") != h.Token)
                    return Resp.Html(403, "<!doctype html><title>Brinback</title><body style=\"background:#0a0a0a;color:#a09f9a;font:14px system-ui;padding:40px\">Open Brinback from its icon in the tray.</body>");
                return Resp.Html(200, Index().Replace("__TOKEN__", h.Token));
            }
            if (!r.Path.StartsWith("/api/")) return Resp.Text(404, "not found");

            string tok;
            r.Headers.TryGetValue("X-Token", out tok);
            if (tok != h.Token) return Resp.Json(403, new Dictionary<string, object> { { "error", "forbidden" } });

            if (r.Method == "GET" && r.Path == "/api/state") return Resp.Json(200, State());
            if (r.Method == "POST" && r.Path == "/api/action")
            {
                var body = Ser.DeserializeObject(string.IsNullOrEmpty(r.Body) ? "{}" : r.Body) as Dictionary<string, object>;
                return Action(body ?? new Dictionary<string, object>());
            }
            return Resp.Text(404, "not found");
        }

        static Dictionary<string, object> O() { return new Dictionary<string, object>(); }

        static object State()
        {
            var mons = Desk.Monitors();
            var wins = Desk.List(mons);
            var titles = Claude.Titles();
            var res = O();

            res["monitors"] = mons.Select(m => (object)new Dictionary<string, object> {
                { "i", m.Index }, { "name", m.Name }, { "x", m.X }, { "y", m.Y }, { "w", m.W }, { "h", m.H }, { "primary", m.Primary } }).ToList();

            res["windows"] = wins.Select(w => (object)new Dictionary<string, object> {
                { "h", w.Handle.ToString() }, { "title", w.Title }, { "process", w.Process }, { "claude", w.IsClaude },
                { "x", w.X }, { "y", w.Y }, { "w", w.W }, { "h2", w.H },
                { "vx", w.VX }, { "vy", w.VY }, { "vw", w.VW }, { "vh", w.VH },
                { "min", w.Min }, { "max", w.Max }, { "z", w.Z }, { "mon", w.Mon }, { "snapped", w.Snapped } }).ToList();

            var groups = new List<object>();
            var savedSets = new List<KeyValuePair<string, HashSet<long>>>();
            lock (Store.Gate)
            {
                foreach (var g in Store.D.Groups)
                {
                    var states = Layouts.Resolve(g, wins, titles, mons);
                    var inLayout = new HashSet<int>(Layouts.SnapSets(g, mons).SelectMany(x => x));
                    savedSets.Add(new KeyValuePair<string, HashSet<long>>(g.Name,
                        new HashSet<long>(states.Where(s => s.Window != null).Select(s => s.Window.Handle))));
                    groups.Add(new Dictionary<string, object> {
                        { "id", g.Id }, { "name", g.Name }, { "auto", g.Auto }, { "saved", g.Saved }, { "snap", g.Snap },
                        { "open", states.Count(s => s.Window != null) },
                        { "slots", states.Select(s => (object)new Dictionary<string, object> {
                            { "i", s.Index }, { "title", s.Title }, { "savedTitle", s.Slot.Title }, { "sessionId", s.Slot.SessionId },
                            { "process", s.Slot.Process },
                            { "x", s.Slot.X }, { "y", s.Slot.Y }, { "w", s.Slot.W }, { "h2", s.Slot.H }, { "max", s.Slot.Max }, { "z", s.Slot.Z },
                            { "mon", s.Mon }, { "open", s.Window != null }, { "h", s.Window != null ? s.Window.Handle.ToString() : null },
                            { "layout", inLayout.Contains(s.Index) },
                            { "zone", inLayout.Contains(s.Index) ? "layout" : Snapper.ZoneOf(s.Slot, mons) == Zone.None ? null : Snapper.ZoneOf(s.Slot, mons).ToString() } }).ToList() } });
                }
                res["settings"] = new Dictionary<string, object> { { "autoRestore", Store.D.AutoRestore }, { "startWithWindows", Startup.On }, { "keepSnaps", Store.D.KeepSnaps },
                    { "hotkey", Hotkey.Me != null ? Hotkey.Me.Current : null }, { "hotkeyProblem", Hotkey.Me != null ? Hotkey.Me.Problem : null } };
            }
            res["groups"] = groups;

            Func<bool, List<object>> detected = claudeOnly => Layouts.Detect(wins, mons, claudeOnly).Select(d =>
            {
                var set = new HashSet<long>(d.Windows.Select(w => w.Handle));
                var match = savedSets.FirstOrDefault(kv => kv.Value.SetEquals(set));
                return (object)new Dictionary<string, object> {
                    { "key", d.Key }, { "kind", d.Kind }, { "hidden", d.Hidden }, { "index", d.Index },
                    { "mon", d.Mon }, { "monName", d.MonName }, { "tiled", d.Tiled }, { "coverage", Math.Round(d.Coverage, 2) }, { "behind", d.Behind },
                    { "snap", d.Snap.Select(w => (object)w.Handle.ToString()).ToList() },
                    { "handles", d.Windows.Select(w => (object)w.Handle.ToString()).ToList() },
                    { "savedAs", match.Key } };
            }).ToList();
            res["detected"] = detected(true);
            res["detectedAll"] = detected(false);
            res["claudeFound"] = wins.Any(w => w.IsClaude) || titles.Count > 0;
            res["version"] = AppInfo.Version;
            res["arrange"] = Arrange.All();
            res["storeProblem"] = Store.Blocked ? (object)("Your saved groups could not be read yet (" + Store.Problem + "). Brinback keeps trying and saves nothing until then, so they are safe.") : null;
            res["update"] = Updater.State();
            res["platform"] = "windows";
            res["inset"] = 7;          // the invisible resize border around every window
            return res;
        }

        static string S(Dictionary<string, object> b, string k) { object v; return b.TryGetValue(k, out v) && v != null ? v.ToString() : null; }
        static bool B(Dictionary<string, object> b, string k) { object v; return b.TryGetValue(k, out v) && v is bool && (bool)v; }
        static int I(Dictionary<string, object> b, string k) { object v; return b.TryGetValue(k, out v) && v != null ? Convert.ToInt32(v) : -1; }
        static List<string> L(Dictionary<string, object> b, string k)
        {
            object v;
            var res = new List<string>();
            if (b.TryGetValue(k, out v) && v is System.Collections.IEnumerable && !(v is string))
                foreach (var o in (System.Collections.IEnumerable)v) if (o != null) res.Add(o.ToString());
            return res;
        }
        static Resp Ok(string msg) { return Resp.Json(200, new Dictionary<string, object> { { "ok", true }, { "message", msg } }); }
        static Resp Fail(int status, string msg) { return Resp.Json(status, new Dictionary<string, object> { { "error", msg } }); }

        static Resp Action(Dictionary<string, object> b)
        {
            string op = S(b, "op") ?? "";
            switch (op)
            {
                case "deploy":
                {
                    var ids = L(b, "ids");
                    List<Group> gs;
                    lock (Store.Gate) gs = Store.D.Groups.Where(g => ids.Contains(g.Id)).ToList();
                    if (gs.Count == 0) return Fail(404, "No such group.");
                    var parts = new List<string>();
                    foreach (var g in gs)
                    {
                        var r = Layouts.Deploy(g);
                        parts.Add(UiFont.Plain(r.Group) + ": " + r.Summary());
                    }
                    return Ok("Deployed " + string.Join(" | ", parts) + ".");
                }
                case "save":
                {
                    string name = (S(b, "name") ?? "").Trim();
                    if (name == "") return Fail(400, "Give the group a name.");
                    var handles = new HashSet<string>(L(b, "handles"));
                    var mons = Desk.Monitors();
                    var wins = Desk.List(mons).Where(w => handles.Contains(w.Handle.ToString())).ToList();
                    if (wins.Count == 0) return Fail(400, "None of those windows are open any more.");
                    var slots = Layouts.Snapshot(wins);
                    lock (Store.Gate)
                    {
                        var existing = Store.D.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (existing != null && !B(b, "replace")) return Fail(409, "A group called " + existing.Name + " already exists.");
                        var grp = existing ?? new Group { Id = Store.NewId() };
                        // Saving by hand makes it the user's group, even when it replaces one that
                        // was kept from a snap layout - otherwise the keeper goes on rewriting it.
                        grp.Name = name; grp.Slots = slots; grp.Saved = DateTime.Now.ToString("dd MMM HH:mm"); grp.Snap = false;
                        if (existing == null) Store.D.Groups.Add(grp);
                        Store.Save();
                        Log.Write("saved '" + name + "' with " + slots.Count + " window(s)");
                        return Resp.Json(200, new Dictionary<string, object> { { "ok", true }, { "id", grp.Id }, { "message", "Saved " + name + " - " + slots.Count + " window(s)." } });
                    }
                }
                case "update":
                {
                    lock (Store.Gate)
                    {
                        var g = Store.Find(S(b, "id"));
                        if (g == null) return Fail(404, "No such group.");
                        var mons = Desk.Monitors();
                        var states = Layouts.Resolve(g, Desk.List(mons), Claude.Titles(), mons);
                        int n = 0;
                        foreach (var s in states.Where(s => s.Window != null))
                        {
                            var w = s.Window;
                            s.Slot.X = w.X; s.Slot.Y = w.Y; s.Slot.W = w.W; s.Slot.H = w.H; s.Slot.Max = w.Max; s.Slot.Z = w.Z;
                            s.Slot.Title = w.Title; n++;
                        }
                        g.Saved = DateTime.Now.ToString("dd MMM HH:mm");
                        Store.Save();
                        int missing = states.Count - n;
                        return Ok("Updated " + g.Name + " from where its windows are now" + (missing > 0 ? " - " + missing + " not open, kept as they were." : "."));
                    }
                }
                case "rename":
                {
                    string name = (S(b, "name") ?? "").Trim();
                    if (name == "") return Fail(400, "A group needs a name.");
                    lock (Store.Gate)
                    {
                        var g = Store.Find(S(b, "id"));
                        if (g == null) return Fail(404, "No such group.");
                        if (Store.D.Groups.Any(x => x != g && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                            return Fail(409, "Another group is already called " + name + ".");
                        g.Name = name; g.Snap = false; Store.Save();
                        return Ok("Renamed to " + name + ".");
                    }
                }
                case "delete":
                {
                    // One group ("id") or several ("ids", from Select all in the list).
                    var ids = L(b, "ids");
                    if (S(b, "id") != null) ids.Add(S(b, "id"));
                    lock (Store.Gate)
                    {
                        var gs = Store.D.Groups.Where(g => ids.Contains(g.Id)).ToList();
                        if (gs.Count == 0) return Fail(404, "No such group.");
                        bool snaps = false;
                        foreach (var g in gs)
                        {
                            Store.D.Groups.Remove(g);
                            if (g.Snap)
                            {
                                snaps = true;
                                Store.D.Dismissed.Add(SnapKeeper.Signature(g.Slots));
                                if (Store.D.Dismissed.Count > 60) Store.D.Dismissed.RemoveAt(0);
                            }
                        }
                        Store.Save();
                        if (gs.Count == 1) Log.Write("deleted '" + gs[0].Name + "'");
                        else Log.Write("deleted " + gs.Count + " groups, " + Store.D.Groups.Count + " left");
                        string what = gs.Count == 1 ? gs[0].Name : gs.Count + " groups";
                        return Ok("Deleted " + what + "." + (snaps ? " Snap groups that were deleted are not kept again unless they change." : " The windows themselves were not touched."));
                    }
                }
                case "auto":
                {
                    lock (Store.Gate)
                    {
                        var g = Store.Find(S(b, "id"));
                        if (g == null) return Fail(404, "No such group.");
                        g.Auto = B(b, "on"); Store.Save();
                        return Ok(g.Name + (g.Auto ? " will put itself back when its windows open." : " only moves when you deploy it."));
                    }
                }
                case "replace":
                {
                    // Fill a slot with a different open window. The slot keeps its place and size;
                    // only who sits in it changes.
                    var mons = Desk.Monitors();
                    var w = Desk.List(mons).FirstOrDefault(x => x.Handle.ToString() == S(b, "handle"));
                    if (w == null) return Fail(404, "That window is not open any more.");
                    lock (Store.Gate)
                    {
                        var g = Store.Find(S(b, "id"));
                        int i = I(b, "index");
                        if (g == null || i < 0 || i >= g.Slots.Count) return Fail(404, "No such slot.");
                        var snap = Layouts.Snapshot(new[] { w })[0];
                        var s = g.Slots[i];
                        s.Title = snap.Title; s.SessionId = snap.SessionId; s.Process = snap.Process;
                        g.Saved = DateTime.Now.ToString("dd MMM HH:mm"); g.Snap = false;
                        Store.Save();
                        Log.Write("slot " + i + " of '" + g.Name + "' now holds '" + w.Title + "'");
                        return Ok(UiFont.Plain(w.Title) + " now holds that place in " + g.Name + ".");
                    }
                }
                case "removeSlot":
                {
                    lock (Store.Gate)
                    {
                        var g = Store.Find(S(b, "id"));
                        int i = I(b, "index");
                        if (g == null || i < 0 || i >= g.Slots.Count) return Fail(404, "No such slot.");
                        string t = g.Slots[i].Title;
                        g.Slots.RemoveAt(i); g.Snap = false; Store.Save();
                        return Ok("Removed " + UiFont.Plain(t) + " from " + g.Name + ".");
                    }
                }
                case "front":
                {
                    // Bring a group's windows to the top - instantly, without moving or snapping
                    // anything. The quick answer to two groups stacked on one screen.
                    Group g;
                    lock (Store.Gate) g = Store.Find(S(b, "id"));
                    if (g == null) return Fail(404, "No such group.");
                    return Ok(Front.Group(g));
                }
                case "minimize":
                {
                    Group g;
                    lock (Store.Gate) g = Store.Find(S(b, "id"));
                    if (g == null) return Fail(404, "No such group.");
                    var mons = Desk.Monitors();
                    var live = Layouts.Resolve(g, Desk.List(mons), Claude.Titles(), mons).Where(x => x.Window != null).ToList();
                    foreach (var x in live) N.ShowWindow(new IntPtr(x.Window.Handle), 6);      // SW_MINIMIZE
                    return Ok(live.Count + " window(s) of " + g.Name + " minimized. Deploy or Bring to front puts them back.");
                }
                case "frontWindow":
                {
                    var w = Desk.List(Desk.Monitors()).FirstOrDefault(x => x.Handle.ToString() == S(b, "handle"));
                    if (w == null) return Fail(404, "That window is not open any more.");
                    Front.Bring(new List<Win> { w });
                    return Ok(UiFont.Plain(w.Title) + " brought to the front.");
                }
                case "arrange":
                {
                    // Picked windows into a ready-made layout on one screen, in the order picked.
                    var want = L(b, "handles");
                    var mons = Desk.Monitors();
                    var open = Desk.List(mons);
                    var wins = want.Select(h => open.FirstOrDefault(x => x.Handle.ToString() == h)).Where(x => x != null).ToList();
                    if (wins.Count == 0) return Fail(404, "Those windows are not open any more.");
                    var shape = Arrange.For(wins.Count).FirstOrDefault(x => x.Id == S(b, "layout"));
                    if (shape == null) return Fail(400, "That layout is not one for " + wins.Count + " windows.");
                    int mi; Mon m = null;
                    if (int.TryParse(S(b, "mon") ?? "", out mi)) m = mons.FirstOrDefault(x => x.Index == mi);
                    if (m == null) m = mons.OrderByDescending(x => wins.Count(w => w.Mon == x.Index)).ThenByDescending(x => x.Primary).First();
                    var r = Arrange.Run(wins, shape, m);
                    Log.Write("arrange: " + wins.Count + " window(s) as '" + shape.Name + "' on " + m.Name);
                    string msg = shape.Name + " on " + m.Name + ": " + r.Placed + " of " + wins.Count + " placed";
                    if (r.Grouped > 0) msg += ", as a Windows snap group";
                    else if (wins.Count > 4) msg += " (more than four windows cannot form a Windows snap group)";
                    else if (wins.Count > 1 && r.Snapped > 0) msg += " (Windows did not group them, so each was snapped on its own)";
                    else if (wins.Count > 1) msg += " (Windows has no snap layout of that shape on " + m.Name + ", so they are placed, not snapped)";
                    return Ok(msg + ". Save as group keeps it.");
                }
                case "frontClaude":
                    return Ok(Front.AllClaude());
                case "frontHandles":
                {
                    // Picked windows, or a layout found on screen, on top - in their own stacking
                    // order, the top one with the keyboard.
                    var handles = new HashSet<string>(L(b, "handles"));
                    var wins = Desk.List(Desk.Monitors()).Where(x => handles.Contains(x.Handle.ToString())).OrderByDescending(x => x.Z).ToList();
                    if (wins.Count == 0) return Fail(404, "Those windows are not open any more.");
                    Front.Bring(wins);
                    return Ok((wins.Count == 1 ? UiFont.Plain(wins[0].Title) + " is" : wins.Count + " windows are") + " in front now.");
                }
                case "addToGroup":
                {
                    // Windows join a group at the place and size they have right now.
                    var want = new HashSet<string>(L(b, "handles"));
                    if (S(b, "handle") != null) want.Add(S(b, "handle"));
                    var mons = Desk.Monitors();
                    var open = Desk.List(mons);
                    var wins = open.Where(x => want.Contains(x.Handle.ToString())).ToList();
                    if (wins.Count == 0) return Fail(404, "Those windows are not open any more.");
                    lock (Store.Gate)
                    {
                        var g = Store.Find(S(b, "id"));
                        if (g == null) return Fail(404, "No such group.");
                        var already = new HashSet<long>(Layouts.Resolve(g, open, Claude.Titles(), mons).Where(s => s.Window != null).Select(s => s.Window.Handle));
                        var add = wins.Where(x => !already.Contains(x.Handle)).ToList();
                        if (add.Count == 0) return Ok("Already in " + g.Name + ".");
                        g.Slots.AddRange(Layouts.Snapshot(add));
                        g.Saved = DateTime.Now.ToString("dd MMM HH:mm"); g.Snap = false;
                        Store.Save();
                        Log.Write("added " + add.Count + " window(s) to '" + g.Name + "'");
                        return Ok((add.Count == 1 ? UiFont.Plain(add[0].Title) + " is" : add.Count + " windows are") + " now in " + g.Name + ", where " + (add.Count == 1 ? "it sits" : "they sit") + " now.");
                    }
                }
                case "overlay":
                {
                    var tiles = new List<Tile>();
                    var live = new List<Win>();
                    string name;
                    lock (Store.Gate)
                    {
                        var g = Store.Find(S(b, "id"));
                        if (g == null) return Fail(404, "No such group.");
                        name = g.Name;
                        var mons = Desk.Monitors();
                        foreach (var s in Layouts.Resolve(g, Desk.List(mons), Claude.Titles(), mons).OrderByDescending(x => x.Slot.Z))
                        {
                            if (s.Window != null && !s.Window.Min) live.Add(s.Window);
                            tiles.Add(new Tile {
                                R = s.Window != null && !s.Window.Min ? new Rectangle(s.Window.VX, s.Window.VY, s.Window.VW, s.Window.VH) : Inset(s.Slot.X, s.Slot.Y, s.Slot.W, s.Slot.H),
                                Title = s.Title, Group = g.Name, Missing = s.Window == null, Frame = s.Window != null,
                                Sub = s.Window == null ? "NOT OPEN" : "OPEN  " + s.Slot.W + " x " + s.Slot.H });
                        }
                    }
                    Front.Bring(live);
                    App.OnUi(() => Overlay.ShowTiles(tiles, 4));
                    return Ok("Brought " + name + " to the front" + (tiles.Any(x => x.Missing) ? " - the dashed places are windows that are not open." : "."));
                }
                case "overlayHandles":
                {
                    var handles = new HashSet<string>(L(b, "handles"));
                    string label = S(b, "label") ?? "On screen";
                    var mons = Desk.Monitors();
                    var wins = Desk.List(mons).Where(w => handles.Contains(w.Handle.ToString())).OrderByDescending(w => w.Z).ToList();
                    if (wins.Count == 0) return Fail(404, "Those windows are not open any more.");
                    Front.Bring(wins);
                    // positions after bringing them up (a minimized one is restored first)
                    var now = Desk.List(mons).Where(w => handles.Contains(w.Handle.ToString())).ToList();
                    var tiles = now.Select(w => new Tile { R = new Rectangle(w.VX, w.VY, w.VW, w.VH), Title = w.Title, Group = label, Frame = true, Sub = "" }).ToList();
                    App.OnUi(() => Overlay.ShowTiles(tiles, 4));
                    return Ok((wins.Count == 1 ? UiFont.Plain(wins[0].Title) + " is" : wins.Count + " windows are") + " in front, outlined for a moment.");
                }
                case "setting":
                {
                    string key = S(b, "key");
                    if (key == "hotkey")
                    {
                        string want = (S(b, "value") ?? "").Trim();
                        if (Hotkey.Me == null) return Fail(500, "Shortcuts are not available.");
                        bool ok = (bool)App.Ui.Invoke(new Func<bool>(() => Hotkey.Me.Set(want)));
                        if (!ok) return Fail(409, Hotkey.Me.Problem + ".");
                        lock (Store.Gate) { Store.D.ClaudeHotkey = Hotkey.Me.Current ?? ""; Store.Save(); }
                        Log.Write("hotkey: " + (Hotkey.Me.Current ?? "off"));
                        return Ok(Hotkey.Me.Current == null ? "The shortcut is off." : Hotkey.Me.Current + " now brings every Claude window to the front, from anywhere.");
                    }
                    bool on = B(b, "value");
                    if (key == "autoRestore") { lock (Store.Gate) { Store.D.AutoRestore = on; Store.Save(); } return Ok(on ? "Groups will put themselves back as their windows open." : "Auto-restore is off. Deploy groups by hand."); }
                    if (key == "startWithWindows") { Startup.Set(on); return Ok(on ? "Brinback starts with Windows." : "Brinback no longer starts with Windows."); }
                    if (key == "keepSnaps") { lock (Store.Gate) { Store.D.KeepSnaps = on; Store.Save(); } return Ok(on ? "Snap groups you make in Windows are kept, and come back after a restart." : "Snap groups are no longer kept automatically."); }
                    return Fail(400, "Unknown setting.");
                }
                case "selfUpdate":
                {
                    string v = Updater.Latest;
                    if (v == null) return Ok("Brinback is up to date.");
                    if (Updater.Working) return Ok("Already updating to " + v + ".");
                    Updater.Working = true;
                    ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(600); Updater.Apply(); });
                    return Ok("Updating to " + v + " - Brinback restarts in a moment.");
                }
                case "checkUpdate":
                {
                    Updater.Check();
                    if (Updater.Latest != null) return Ok("Brinback " + Updater.Latest + " is available.");
                    return Updater.LastError != null ? Fail(502, "Could not check for updates: " + Updater.LastError) : Ok("Brinback " + AppInfo.Version + " is the latest.");
                }
                default:
                    return Fail(400, "Unknown action.");
            }
        }

        // A window rect includes an invisible resize border of about 7px each side; drawing the
        // preview on the visible part looks right.
        static Rectangle Inset(int x, int y, int w, int h)
        {
            return new Rectangle(x + 7, y, Math.Max(40, w - 14), Math.Max(40, h - 7));
        }
    }

    // ============================================================ updates

    // Asks GitHub for the latest release - at start and twice a day. One plain request, no
    // token, a generic User-Agent, nothing about this PC or its windows. Nothing is downloaded
    // until you click Update. Then: the release exe is fetched next to this one, checked (size,
    // SHA-256 as GitHub publishes it, and that it runs and reports the version expected), this
    // exe is renamed to .old, the new one takes its name, and it is started in its place.
    static class Updater
    {
        public static volatile string Latest;        // a newer version that can be installed, or null
        public static volatile string LastError;     // why the last check or update failed
        public static volatile bool Working;
        static string assetUrl, assetDigest; static long assetSize;
        static System.Threading.Timer timer;
        static bool told;

        // For testing against another repository's releases, or a pretend-older version.
        static string Repo { get { return Environment.GetEnvironmentVariable("BRINBACK_UPDATE_REPO") ?? AppInfo.Repo; } }
        static string Mine { get { return Environment.GetEnvironmentVariable("BRINBACK_UPDATE_FROM") ?? AppInfo.Version; } }

        public static void Start()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }      // TLS 1.2
            try { string old = Application.ExecutablePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
            timer = new System.Threading.Timer(delegate { Check(); }, null, 15000, 12 * 60 * 60 * 1000);
        }

        public static object State()
        {
            if (Latest == null && LastError == null) return null;
            return new Dictionary<string, object> { { "version", Latest }, { "working", Working }, { "error", Working ? null : LastError } };
        }

        static HttpWebRequest Ask(string url)
        {
            var q = (HttpWebRequest)WebRequest.Create(url);
            q.UserAgent = "Brinback-updater";
            q.Timeout = 20000; q.ReadWriteTimeout = 60000;
            return q;
        }

        public static bool Newer(string candidate, string than)
        {
            Version a, b;
            return System.Version.TryParse(candidate, out a) && System.Version.TryParse(than, out b) && a > b;
        }

        public static void Check()
        {
            if (Working) return;
            try
            {
                var q = Ask("https://api.github.com/repos/" + Repo + "/releases/latest");
                q.Accept = "application/vnd.github+json";
                string json;
                using (var r = q.GetResponse())
                using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) json = sr.ReadToEnd();
                var rel = Api.Ser.DeserializeObject(json) as Dictionary<string, object>;
                string tag = rel != null && rel.ContainsKey("tag_name") ? Convert.ToString(rel["tag_name"]) : "";
                string ver = tag.TrimStart('v', 'V');
                string url = null, digest = null; long size = 0;
                var assets = rel != null && rel.ContainsKey("assets") ? rel["assets"] as object[] : null;
                foreach (var o in assets ?? new object[0])
                {
                    var a = o as Dictionary<string, object>;
                    if (a == null || !string.Equals(Convert.ToString(a["name"]), "Brinback.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    url = Convert.ToString(a["browser_download_url"]);
                    size = Convert.ToInt64(a["size"]);
                    object d; if (a.TryGetValue("digest", out d) && d != null) digest = d.ToString();
                }
                LastError = null;
                if (url != null && url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) && size > 0 && Newer(ver, Mine))
                {
                    assetUrl = url; assetSize = size; assetDigest = digest;
                    bool first = Latest != ver;
                    Latest = ver;
                    if (first) Log.Write("update: " + ver + " is available");
                    if (!told) { told = true; App.OnUi(() => { if (App.TrayIcon != null) App.TrayIcon.Say("Brinback " + ver + " is available", "Open Brinback, or right-click this icon, to update."); }); }
                }
                else Latest = null;
            }
            catch (Exception e) { LastError = e.Message; Log.Write("update: check failed - " + e.Message); }
        }

        static string Fail(string why, string fresh)
        {
            LastError = why; Working = false;
            Log.Write("update: " + why);
            try { if (fresh != null && File.Exists(fresh)) File.Delete(fresh); } catch { }
            return why;
        }

        // Returns null when the new copy has been started, otherwise why not. Nothing is touched
        // until the download has passed every check.
        public static string Apply()
        {
            Working = true;
            string ver = Latest, exe = Application.ExecutablePath;
            string fresh = Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + ".new.exe"), old = exe + ".old";
            if (ver == null || assetUrl == null) return Fail("no update to install", null);
            try
            {
                var q = Ask(assetUrl);
                using (var r = q.GetResponse())
                using (var net = r.GetResponseStream())
                using (var f = File.Create(fresh))
                {
                    var buf = new byte[81920]; int n; long total = 0;
                    while ((n = net.Read(buf, 0, buf.Length)) > 0) { f.Write(buf, 0, n); total += n; if (total > assetSize) break; }
                }
                long got = new FileInfo(fresh).Length;
                if (got != assetSize) return Fail("the download is " + got + " bytes, the release says " + assetSize, fresh);
                if (assetDigest != null && assetDigest.StartsWith("sha256:"))
                {
                    string sum;
                    using (var sha = SHA256.Create()) using (var f = File.OpenRead(fresh)) sum = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                    if (sum != assetDigest.Substring(7).ToLowerInvariant()) return Fail("the download does not match the checksum of the release", fresh);
                }
                // It has to run, and be the version it claims.
                string said = "";
                using (var p = Process.Start(new ProcessStartInfo(fresh, "--version") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true }))
                {
                    said = p.StandardOutput.ReadToEnd().Trim();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return Fail("the new version did not answer", fresh); }
                }
                if (said != ver) return Fail("the download says it is '" + said + "', not " + ver, fresh);

                try { if (File.Exists(old)) File.Delete(old); } catch { }
                File.Move(exe, old);
                try { File.Move(fresh, exe); }
                catch { File.Move(old, exe); throw; }
                Log.Write("update: " + AppInfo.Version + " -> " + ver + ", restarting");

                // The new copy takes over this run's port and key, so an open window just carries on
                // (it shows "offline" for the second or two in between). Handed over through the
                // environment of that one process, never on a command line.
                var psi = new ProcessStartInfo(exe, "--tray --after-update") { UseShellExecute = false };
                psi.EnvironmentVariables[App.HandoverVar] = App.Server.Port + ":" + App.Server.Token;
                Process.Start(psi);
                App.Quit();
                return null;
            }
            catch (Exception e) { return Fail(e.Message, fresh); }
        }
    }

    // ============================================================ arrange picked windows

    // Ready-made layouts for the windows picked in the window: two side by side, a 2x2 grid, and
    // so on. Boxes are fractions of a screen's work area, in reading order: the first window picked
    // takes the first box. Up to four windows the shapes are ones Windows' own Snap Layouts offer,
    // so they come out as a real snap group; more than four are placed as a grid.
    static class Arrange
    {
        public class Shape
        {
            public string Id, Name; public bool Max;
            public double[][] Boxes;
        }

        static Shape S(string id, string name, params double[][] boxes) { return new Shape { Id = id, Name = name, Boxes = boxes }; }
        static double[] B(double x, double y, double w, double h) { return new[] { x, y, w, h }; }
        const double T = 1.0 / 3, TT = 2.0 / 3;

        public static List<Shape> For(int n)
        {
            var l = new List<Shape>();
            if (n == 1)
            {
                var full = S("full", "Full screen", B(0, 0, 1, 1)); full.Max = true; l.Add(full);
                l.Add(S("left", "Left half", B(0, 0, .5, 1)));
                l.Add(S("right", "Right half", B(.5, 0, .5, 1)));
            }
            else if (n == 2)
            {
                l.Add(S("halves", "Side by side", B(0, 0, .5, 1), B(.5, 0, .5, 1)));
                l.Add(S("wideLeft", "Wide and narrow", B(0, 0, TT, 1), B(TT, 0, T, 1)));
                l.Add(S("wideRight", "Narrow and wide", B(0, 0, T, 1), B(T, 0, TT, 1)));
                l.Add(S("stack", "Top and bottom", B(0, 0, 1, .5), B(0, .5, 1, .5)));
            }
            else if (n == 3)
            {
                l.Add(S("thirds", "Three columns", B(0, 0, T, 1), B(T, 0, T, 1), B(TT, 0, T, 1)));
                l.Add(S("halfTwo", "Half and two stacked", B(0, 0, .5, 1), B(.5, 0, .5, .5), B(.5, .5, .5, .5)));
                l.Add(S("twoHalf", "Two stacked and half", B(0, 0, .5, .5), B(0, .5, .5, .5), B(.5, 0, .5, 1)));
                l.Add(S("wideMiddle", "Wide middle", B(0, 0, .25, 1), B(.25, 0, .5, 1), B(.75, 0, .25, 1)));
            }
            else if (n == 4)
            {
                l.Add(S("grid", "Grid 2 x 2", B(0, 0, .5, .5), B(.5, 0, .5, .5), B(0, .5, .5, .5), B(.5, .5, .5, .5)));
                l.Add(S("columns", "Four columns", B(0, 0, .25, 1), B(.25, 0, .25, 1), B(.5, 0, .25, 1), B(.75, 0, .25, 1)));
                l.Add(S("halfThree", "Half and three stacked", B(0, 0, .5, 1), B(.5, 0, .5, T), B(.5, T, .5, T), B(.5, TT, .5, T)));
            }
            else if (n > 4 && n <= 12)
            {
                // Rows of equal height; the last row's windows share its full width.
                int cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (int)Math.Ceiling(n / (double)cols);
                var boxes = new List<double[]>();
                for (int r = 0; r < rows; r++)
                {
                    int inRow = Math.Min(cols, n - r * cols);
                    for (int c = 0; c < inRow; c++) boxes.Add(B(c / (double)inRow, r / (double)rows, 1.0 / inRow, 1.0 / rows));
                }
                l.Add(S("grid", "Grid", boxes.ToArray()));
                if (n <= 6)
                {
                    var cs = new List<double[]>();
                    for (int c = 0; c < n; c++) cs.Add(B(c / (double)n, 0, 1.0 / n, 1));
                    l.Add(S("columns", n + " columns", cs.ToArray()));
                }
            }
            return l;
        }

        // What the window draws its choices from: shapes for 1 to 12 windows.
        public static Dictionary<string, object> All()
        {
            var d = new Dictionary<string, object>();
            for (int n = 1; n <= 12; n++)
                d[n.ToString()] = For(n).Select(s => (object)new Dictionary<string, object> {
                    { "id", s.Id }, { "name", s.Name }, { "boxes", s.Boxes.Select(b => (object)b.ToList()).ToList() } }).ToList();
            return d;
        }

        // wins in the order they were picked, onto screen m. Not saved as a group: Save as group
        // afterwards keeps the result.
        public static Layouts.DeployResult Run(List<Win> wins, Shape shape, Mon m)
        {
            var slots = new List<Slot>();
            for (int i = 0; i < wins.Count; i++)
            {
                var s = Layouts.Snapshot(new[] { wins[i] })[0];
                var b = shape.Boxes[i];
                int x0 = m.WX + (int)Math.Round(b[0] * m.WW), y0 = m.WY + (int)Math.Round(b[1] * m.WH);
                int x1 = m.WX + (int)Math.Round((b[0] + b[2]) * m.WW), y1 = m.WY + (int)Math.Round((b[1] + b[3]) * m.WH);
                s.X = x0; s.Y = y0; s.W = x1 - x0; s.H = y1 - y0; s.Max = shape.Max; s.Z = wins.Count - i;
                slots.Add(s);
            }
            return Layouts.Deploy(new Group { Id = "arrange", Name = shape.Name, Slots = slots });
        }
    }

    // ============================================================ keyboard shortcut

    // One system-wide shortcut: every Claude window to the front. Ctrl+Alt+Shift+C by default -
    // Ctrl+Alt alone is AltGr on Swiss and German keyboards, where AltGr+letter types characters.
    // Pressing a registered hotkey also gives this app the right to bring windows forward.
    class Hotkey : NativeWindow
    {
        const int WM_HOTKEY = 0x0312, Id = 1;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        public static Hotkey Me;
        public string Current;      // what is registered now, or null
        public string Problem;      // why the wanted one is not

        public Hotkey() { CreateHandle(new CreateParams()); }

        // "Ctrl+Alt+Shift+C" -> modifiers and key. Null when it is not a usable shortcut.
        public static bool Parse(string text, out uint mods, out uint vk, out string clean)
        {
            mods = 0; vk = 0; clean = null;
            if (string.IsNullOrEmpty(text)) return false;
            var parts = text.Split('+').Select(x => x.Trim()).Where(x => x != "").ToList();
            if (parts.Count < 2) return false;
            var names = new List<string>();
            foreach (var p in parts.Take(parts.Count - 1))
            {
                string q = p.ToLowerInvariant();
                if (q == "ctrl" || q == "control") { mods |= MOD_CONTROL; }
                else if (q == "alt") { mods |= MOD_ALT; }
                else if (q == "shift") { mods |= MOD_SHIFT; }
                else if (q == "win" || q == "meta") { mods |= MOD_WIN; }
                else return false;
            }
            if ((mods & MOD_CONTROL) != 0) names.Add("Ctrl");
            if ((mods & MOD_ALT) != 0) names.Add("Alt");
            if ((mods & MOD_SHIFT) != 0) names.Add("Shift");
            if ((mods & MOD_WIN) != 0) names.Add("Win");
            string k = parts[parts.Count - 1];
            Keys key;
            if (k.Length == 1 && char.IsLetterOrDigit(k[0])) key = char.IsDigit(k[0]) ? (Keys)((int)Keys.D0 + (k[0] - '0')) : (Keys)char.ToUpperInvariant(k[0]);
            else if (!Enum.TryParse(k, true, out key)) return false;
            // A plain letter needs Ctrl, Alt or Win, or typing it would trigger the shortcut.
            bool fkey = key >= Keys.F1 && key <= Keys.F24;
            if (!fkey && (mods & (MOD_CONTROL | MOD_ALT | MOD_WIN)) == 0) return false;
            vk = (uint)key;
            string kn = key >= Keys.D0 && key <= Keys.D9 ? ((int)(key - Keys.D0)).ToString() : key.ToString();
            names.Add(kn);
            clean = string.Join("+", names);
            return true;
        }

        // Swap to `text` (null or "" turns it off). On failure the previous one stays registered.
        public bool Set(string text)
        {
            uint mods, vk; string clean;
            if (string.IsNullOrEmpty(text))
            {
                UnregisterHotKey(Handle, Id); Current = null; Problem = null;
                return true;
            }
            if (!Parse(text, out mods, out vk, out clean)) { Problem = text + " is not a shortcut Brinback can use"; return false; }
            if (clean == Current) { Problem = null; return true; }
            UnregisterHotKey(Handle, Id);
            if (RegisterHotKey(Handle, Id, mods | MOD_NOREPEAT, vk)) { Current = clean; Problem = null; return true; }
            Problem = clean + " is taken by Windows or another app";
            uint pm, pv; string pc;
            if (Current != null && Parse(Current, out pm, out pv, out pc) && !RegisterHotKey(Handle, Id, pm | MOD_NOREPEAT, pv)) Current = null;
            return false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == Id)
            {
                ThreadPool.QueueUserWorkItem(delegate { try { Front.AllClaude(); } catch (Exception e) { Log.Write("hotkey: " + e.Message); } });
                return;
            }
            base.WndProc(ref m);
        }
    }

    // ============================================================ start with Windows

    // Start with Windows is a shortcut in the user's Startup folder. Up to 1.6.1 it was a value
    // under HKCU\...\Run, but recent Windows 11 builds only launch the Run entries they have already
    // acknowledged: a newly added one sits there and is skipped at every sign-in, with no flag
    // anywhere saying so. Startup-folder shortcuts are launched.
    static class Startup
    {
        const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string Name = "Brinback";

        static string Link { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Brinback.lnk"); } }

        public static bool On { get { return File.Exists(Link); } }

        public static void Set(bool on)
        {
            if (on) Shortcut.Write(Link, Application.ExecutablePath, "--tray");
            else if (File.Exists(Link)) File.Delete(Link);
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.DeleteValue(Name, false);
        }

        // At start: move a Run value left by 1.6.0/1.6.1 to the Startup folder, and repoint the
        // shortcut if the exe was moved.
        public static void Ensure()
        {
            try
            {
                bool run;
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) run = k != null && k.GetValue(Name) != null;
                if (run) { Set(true); Log.Write("Start with Windows: moved to the Startup folder, where Windows does launch it"); }
                else if (On && !string.Equals(Shortcut.Target(Link), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) Set(true);
            }
            catch (Exception e) { Log.Write("Start with Windows: " + e.Message); }
        }
    }

    // Windows shortcuts, through the scripting host that ships with Windows.
    static class Shortcut
    {
        static object Open(string lnk, out Type st)
        {
            st = null;
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) throw new InvalidOperationException("Windows Script Host is not available");
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, Activator.CreateInstance(t), new object[] { lnk });
            st = sc.GetType();
            return sc;
        }

        public static string Target(string lnk)
        {
            if (!File.Exists(lnk)) return null;
            Type st; object sc = Open(lnk, out st);
            return st.InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
        }

        public static void Write(string lnk, string exe, string args)
        {
            Type st; object sc = Open(lnk, out st);
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
            st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exe) });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Save window layouts, bring them back" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
    }

    // Brinback is one exe with no installer, so nothing puts it in the Start menu - and Windows
    // search only finds apps that are there. The app adds its own entry at start, and points it at
    // wherever the exe is now if it was moved.
    static class StartMenu
    {
        public static void Ensure()
        {
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string lnk = Path.Combine(dir, "Brinback.lnk");
                string exe = Application.ExecutablePath;
                if (!string.Equals(Shortcut.Target(lnk), exe, StringComparison.OrdinalIgnoreCase))
                {
                    Shortcut.Write(lnk, exe, "");
                    Log.Write("Start menu: Brinback is listed, so Windows search finds it");
                }
                string old = Path.Combine(dir, "Window Groups.lnk");
                if (File.Exists(old)) File.Delete(old);
            }
            catch (Exception e) { Log.Write("Start menu: " + e.Message); }
        }
    }

    // ============================================================ tray

    class DarkColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(14, 14, 16), Hover = Color.FromArgb(30, 30, 33), Line = Color.FromArgb(44, 44, 48);
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color MenuBorder { get { return Line; } }
        public override Color MenuItemBorder { get { return Hover; } }
        public override Color MenuItemSelected { get { return Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return Hover; } }
        public override Color MenuItemPressedGradientEnd { get { return Hover; } }
        public override Color SeparatorDark { get { return Line; } }
        public override Color SeparatorLight { get { return Bg; } }
        public override Color CheckBackground { get { return Bg; } }
        public override Color CheckSelectedBackground { get { return Hover; } }
        public override Color CheckPressedBackground { get { return Hover; } }
    }

    class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(244, 243, 240) : Color.FromArgb(111, 110, 106);
            base.OnRenderItemText(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using (var b = new SolidBrush(Color.FromArgb(37, 247, 22)))
                e.Graphics.FillRectangle(b, r.X + r.Width / 2 - 3, r.Y + r.Height / 2 - 3, 7, 7);
        }
    }

    class Tray
    {
        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu;

        public Tray()
        {
            menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = true, Font = new Font("Segoe UI", 9.5f) };
            menu.Opening += delegate (object s, System.ComponentModel.CancelEventArgs e) { Build(); e.Cancel = false; };
            icon = new NotifyIcon { Icon = AppIcon.Make(), Text = "Brinback", Visible = true, ContextMenuStrip = menu };
            icon.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) App.OpenUi(); };
            Build();
        }

        void Build()
        {
            menu.Items.Clear();
            var open = new ToolStripMenuItem("Open Brinback") { Font = new Font("Segoe UI Semibold", 9.5f) };
            open.Click += delegate { App.OpenUi(); };
            menu.Items.Add(open);
            var claude = new ToolStripMenuItem("Bring all Claude to front");
            if (Hotkey.Me != null && Hotkey.Me.Current != null) claude.ShortcutKeyDisplayString = Hotkey.Me.Current;
            claude.Click += delegate
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string said = Front.AllClaude();
                    if (said.StartsWith("No ")) App.OnUi(() => icon.ShowBalloonTip(2500, "Brinback", said, ToolTipIcon.None));
                });
            };
            menu.Items.Add(claude);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("DEPLOY") { Enabled = false, Font = new Font("Segoe UI", 8f) });
            List<Group> groups;
            lock (Store.Gate) groups = Store.D.Groups.ToList();
            if (groups.Count == 0) menu.Items.Add(new ToolStripMenuItem("No groups yet") { Enabled = false });
            foreach (var g in groups)
            {
                var id = g.Id;
                var item = new ToolStripMenuItem(UiFont.Plain(g.Name) + "    " + g.Slots.Count);
                item.Click += delegate
                {
                    Group grp;
                    lock (Store.Gate) grp = Store.Find(id);
                    if (grp == null) return;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        var r = Layouts.Deploy(grp);
                        App.OnUi(() => icon.ShowBalloonTip(2500, UiFont.Plain(grp.Name), r.Summary(), ToolTipIcon.None));
                    });
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            bool auto;
            lock (Store.Gate) auto = Store.D.AutoRestore;
            var autoItem = new ToolStripMenuItem("Put groups back when windows open") { Checked = auto };
            autoItem.Click += delegate { lock (Store.Gate) { Store.D.AutoRestore = !Store.D.AutoRestore; Store.Save(); } };
            menu.Items.Add(autoItem);
            bool keep;
            lock (Store.Gate) keep = Store.D.KeepSnaps;
            var keepItem = new ToolStripMenuItem("Keep snap groups made in Windows") { Checked = keep };
            keepItem.Click += delegate { lock (Store.Gate) { Store.D.KeepSnaps = !Store.D.KeepSnaps; Store.Save(); } };
            menu.Items.Add(keepItem);
            var start = new ToolStripMenuItem("Start with Windows") { Checked = Startup.On };
            start.Click += delegate { Startup.Set(!Startup.On); };
            menu.Items.Add(start);
            menu.Items.Add(new ToolStripSeparator());
            string latest = Updater.Latest;
            if (latest != null)
            {
                var up = new ToolStripMenuItem(Updater.Working ? "Updating to " + latest + "..." : "Update to " + latest) { Enabled = !Updater.Working, Font = new Font("Segoe UI Semibold", 9.5f) };
                up.Click += delegate
                {
                    if (Updater.Working) return;
                    Updater.Working = true;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string err = Updater.Apply();
                        if (err != null) App.OnUi(() => Say("Brinback could not update", err));
                    });
                };
                menu.Items.Add(up);
            }
            else
            {
                var chk = new ToolStripMenuItem("Check for updates    v" + AppInfo.Version);
                chk.Click += delegate
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Updater.Check();
                        string l = Updater.Latest, e = Updater.LastError;
                        App.OnUi(() => Say(l != null ? "Brinback " + l + " is available" : e != null ? "Could not check for updates" : "Brinback is up to date",
                                           l != null ? "Right-click this icon and choose Update." : e != null ? e : "Version " + AppInfo.Version + " is the latest."));
                    });
                };
                menu.Items.Add(chk);
            }
            menu.Items.Add(new ToolStripSeparator());
            var quit = new ToolStripMenuItem("Exit");
            quit.Click += delegate { App.Quit(); };
            menu.Items.Add(quit);
        }

        public void Hide() { icon.Visible = false; }
        public void Say(string title, string text) { try { icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None); } catch { } }
    }

    static class AppIcon
    {
        // Four tiles; the top-left one lit. Drawn rather than shipped so the tray icon is crisp at
        // any scale.
        public static Bitmap Draw(int px)
        {
            var bmp = new Bitmap(px, px);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.Clear(Color.Transparent);
                float gap = Math.Max(1f, px / 12f), cell = (px - gap * 3) / 2f;
                using (var lit = new SolidBrush(Color.FromArgb(37, 247, 22)))
                using (var paper = new SolidBrush(Color.FromArgb(244, 243, 240)))
                {
                    g.FillRectangle(lit, gap, gap, cell, cell);
                    g.FillRectangle(paper, gap * 2 + cell, gap, cell, cell);
                    g.FillRectangle(paper, gap, gap * 2 + cell, cell, cell);
                    g.FillRectangle(paper, gap * 2 + cell, gap * 2 + cell, cell, cell);
                }
            }
            return bmp;
        }

        public static Icon Make()
        {
            using (var bmp = Draw(32))
            {
                IntPtr h = bmp.GetHicon();
                var ic = (Icon)Icon.FromHandle(h).Clone();
                N.DestroyIcon(h);
                return ic;
            }
        }
    }

    // ============================================================ app

    static class App
    {
        public static Control Ui;
        public static Http Server;
        public static Tray TrayIcon;

        public const string HandoverVar = "BRINBACK_HANDOVER";

        public static void Quit()
        {
            OnUi(() => { try { if (TrayIcon != null) TrayIcon.Hide(); Overlay.CloseAll(); } catch { } Application.Exit(); });
        }

        public static void OnUi(Action a)
        {
            if (Ui == null) return;
            if (Ui.InvokeRequired) Ui.BeginInvoke(a); else a();
        }

        // The UI is a web page in a chromeless browser window (Edge's --app mode, Chrome as a
        // fallback), with its own profile folder so it never touches anyone's browsing.
        // True once the window on screen is known to be this run's: opened by it, or carried on
        // from the copy that handed over at an update. Any other Brinback window is a dead one.
        public static bool OwnUi;

        public static void OpenUi()
        {
            var found = Desk.FindAllUi();
            if (OwnUi && found.Count > 0)
            {
                var w = found[0];
                if (N.IsIconic(w)) N.ShowWindow(w, N.SW_RESTORE);
                N.SetForegroundWindow(w);
                return;
            }
            // Any Brinback window on screen now is from an earlier run and talks to nothing. Open
            // this run's window first - into the browser that is already running, so it is there at
            // once - then close the dead ones.
            string url = "http://127.0.0.1:" + Server.Port + "/?t=" + Server.Token;
            string browser = FindBrowser();
            try
            {
                if (browser != null)
                {
                    string profile = Path.Combine(Store.Dir, "ui");
                    ForgetWindows(profile);
                    Process.Start(new ProcessStartInfo(browser,
                        "--app=\"" + url + "\" --user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check --window-size=1380,900")
                        { UseShellExecute = false });
                    OwnUi = true;
                }
                else Process.Start(url);
            }
            catch (Exception e) { Log.Write("ui: could not open - " + e.Message); return; }
            if (found.Count > 0)
            {
                // Only once the new window is there: closing the last one first would shut the
                // browser down under the new window. The browser can take ten seconds or more to
                // open an app window, so this waits off the UI thread.
                new Thread(() =>
                {
                    for (int t = 0; t < 300 && !Desk.FindAllUi().Any(h => !found.Contains(h)); t++) Thread.Sleep(100);
                    foreach (var h in found) N.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    Log.Write("ui: replaced " + (found.Count == 1 ? "a window" : found.Count + " windows") + " left by an earlier run");
                }) { IsBackground = true }.Start();
            }
        }

        // The window's browser profile is Brinback's alone. After the browser was killed or crashed
        // it would bring back every Brinback window it had open - all of them from runs that are
        // gone, next to the new one. So when the browser is not running, its restore data goes.
        static void ForgetWindows(string profile)
        {
            try
            {
                // Running (its lock file is held): it restores nothing, and the window opens in it.
                string lockf = Path.Combine(profile, "lockfile");
                if (File.Exists(lockf)) { try { using (File.Open(lockf, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } } catch { return; } }
                string ses = Path.Combine(profile, "Default", "Sessions");
                if (Directory.Exists(ses)) foreach (var f in Directory.GetFiles(ses)) { try { File.Delete(f); } catch { } }
                string pref = Path.Combine(profile, "Default", "Preferences");
                if (File.Exists(pref))
                {
                    string t0 = File.ReadAllText(pref, Encoding.UTF8);
                    string t1 = t0.Replace("\"exit_type\":\"Crashed\"", "\"exit_type\":\"Normal\"").Replace("\"exited_cleanly\":false", "\"exited_cleanly\":true");
                    if (t1 != t0) File.WriteAllText(pref, t1, new UTF8Encoding(false));
                }
            }
            catch (Exception e) { Log.Write("ui: could not clear the window profile's restore data - " + e.Message); }
        }

        static string FindBrowser()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var c = new[] {
                Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe") };
            return c.FirstOrDefault(File.Exists);
        }

        static string NewToken()
        {
            var b = new byte[24];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        [STAThread]
        static void Main(string[] args)
        {
            // Physical pixels everywhere, on every screen. Must come before any window exists.
            try { if (!N.SetProcessDpiAwarenessContext(new IntPtr(-4))) N.SetProcessDPIAware(); }
            catch { try { N.SetProcessDPIAware(); } catch { } }

            // What the updater asks a download before trusting it: does it run, and which version is it.
            if (args.Any(a => a == "--version"))
            {
                try { Console.Out.WriteLine(AppInfo.Version); Console.Out.Flush(); } catch { }
                return;
            }

            bool trayOnly = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
            bool created;
            var mutex = new Mutex(true, "Local\\Brinback.Single", out created);
            if (!created && args.Any(a => a == "--after-update"))
            {
                // Started by the copy this one replaces: wait for it to leave.
                try { created = mutex.WaitOne(20000); } catch (AbandonedMutexException) { created = true; }
            }
            if (!created)
            {
                // Already running: bring its window forward instead of starting a second copy - unless
                // this start only asked for the tray (Windows starting it at sign-in).
                if (!trayOnly) try { EventWaitHandle.OpenExisting("Local\\Brinback.Show").Set(); } catch { }
                return;
            }
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Brinback.Show");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Log.Write("ui thread: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("fatal: " + e.ExceptionObject);

            Directory.CreateDirectory(Store.Dir);
            Legacy.StopOldCopy();
            Legacy.BringGroups();
            bool firstRun = !File.Exists(Store.FilePath);
            Store.Load(TimeSpan.FromSeconds(trayOnly ? 30 : 5));
            if (firstRun) { try { Startup.Set(true); } catch { } Store.Save(); }
            Legacy.BringStartup();
            Startup.Ensure();
            StartMenu.Ensure();

            Ui = new Control();
            var forceHandle = Ui.Handle;

            // After an update: the port and key the window already uses.
            int handPort = 0; string handToken = null;
            string hand = Environment.GetEnvironmentVariable(HandoverVar);
            Environment.SetEnvironmentVariable(HandoverVar, null);       // not passed on to the browser
            if (!string.IsNullOrEmpty(hand) && hand.IndexOf(':') > 0)
            {
                int.TryParse(hand.Substring(0, hand.IndexOf(':')), out handPort);
                handToken = hand.Substring(hand.IndexOf(':') + 1);
            }
            Server = new Http { Token = string.IsNullOrEmpty(handToken) ? NewToken() : handToken, Prefer = handPort };
            try { Server.Start(); }
            catch (Exception e)
            {
                Log.Write("fatal: " + e.Message);
                MessageBox.Show("Brinback could not start: " + e.Message, "Brinback");
                return;
            }
            Log.Write("started " + AppInfo.Version + (trayOnly ? " in the tray" : "") + ", port " + Server.Port + ", " + (Store.Blocked ? "groups.json not readable yet" : Store.D.Groups.Count + " group(s)"));

            // A Brinback window still open from a copy that is gone (an update from before 1.6.5, a
            // crash, a kill) talks to a dead port with a dead key, and opening the window would only
            // bring that one forward. Close it and open a working one in its place.
            bool carriedOn = handToken != null && Server.Port == handPort;
            bool reopen = false;
            if (Desk.FindAllUi().Count > 0)
            {
                if (carriedOn) OwnUi = true;      // the update's window carries on as it is
                else reopen = true;               // replaced by a working one below
            }

            Hotkey.Me = new Hotkey();
            string hk; lock (Store.Gate) hk = Store.D.ClaudeHotkey;
            if (!string.IsNullOrEmpty(hk) && !Hotkey.Me.Set(hk)) Log.Write("hotkey: " + Hotkey.Me.Problem);
            else if (!string.IsNullOrEmpty(hk)) Log.Write("hotkey: " + Hotkey.Me.Current + " brings every Claude window to the front");
            TrayIcon = new Tray();
            Updater.Start();
            var auto = new AutoRestore();
            auto.Init();
            var tick = new System.Windows.Forms.Timer { Interval = 1500 };
            tick.Tick += delegate { try { if (Store.Blocked) { Store.Retry(); if (Store.Blocked) return; auto.Init(); } auto.Tick(); } catch (Exception e) { Log.Write("auto: " + e.Message); } };
            tick.Start();

            var listener = new Thread(() => { while (true) { showEvent.WaitOne(); OnUi(OpenUi); } }) { IsBackground = true };
            listener.Start();

            if (!trayOnly || reopen) OpenUi();
            Application.Run();
            TrayIcon.Hide();
            GC.KeepAlive(mutex);
        }
    }
}
