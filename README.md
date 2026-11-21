# Window Groups

**Save window layouts. Bring them back.**

A small Windows tray app for people who work across many windows and several screens. It sees
how your windows are arranged on each screen, saves a set of them as a named group - which
windows, where, how big, which one is on top - and puts the whole set back with one click.
When the app that owns them restarts, the windows go back into their groups by themselves.

It knows the [Claude desktop app](https://claude.ai/download): session windows are remembered
by their session, so a group still finds them after a session is renamed. It works with any
other app's windows too.

![A saved group, zoomed to its screen, deploying as a Windows snap group - one window not open](docs/group.png)

## What it does

- **Detects layouts - several per screen.** Every layout on every screen shows up under *On
  screen now*, named by where the screen sits ("Top left", "Main", "Far right"). A screen can
  hold more than one: a 3-column snap group in front of a 2x2 that is still snapped behind it is
  two layouts, listed and kept separately. Snapped windows that overlap each other belong to
  different layouts; windows just placed side by side count only where you can see them.
- **Pick what goes in.** Open a found layout and click windows on the map to take them out or
  put them in - including windows hidden behind. The map brings the layout you are looking at
  to the front, even when it is behind others on the real screen. Click the row again, or press
  Esc, to go back to everything.
- **Recognises the snap groups you make in Windows - and keeps them.** Windows 11 groups
  windows you snap with Snap Layouts, then forgets every one of those groups when it restarts.
  Window Groups sees which windows are snapped (`IsWindowArranged`, the one signal Windows
  gives), works out which snapped windows form one layout, and once it
  has held still for twenty seconds keeps it as a group named after its screen - so it comes back
  after a reboot. A kept snap group follows its layout as you resize it; rename or edit it and it
  becomes an ordinary group that is left alone; delete it and it is not kept again unless it
  changes. A group you saved yourself is never duplicated. Switch *Keep snap groups* off to only
  ever save by hand.
- **A map you can read with several groups per screen.** Every screen is its own card, to scale
  and where it sits, with a header strip naming the screen and listing every group on it as a
  chip, front to back - *behind* when it is stacked under another. Point at a chip and that group
  lifts to the front of the map; click it to open the group. Selecting a group zooms the map to
  its screens (*All screens* shows the rest).
- **Shows what is grouped and what is just placed.** Every saved group gets a layer on the map -
  a frame that follows its windows exactly - and every window carries an icon: two stacked frames
  for grouped, one dashed frame for not grouped. A window that merely sits between two members
  stays outside the layer.
- **Filter and search.** *All*, *Grouped*, *Not grouped*, and a search box (press `/`). Windows
  that do not match go quiet on the map, and the list under it shows exactly the ones that do -
  with the screen each is on and the group it belongs to. Tick them there or on the map.
- **Saves any set, or adds to one.** Pick windows straight off the map or the list, across as
  many screens as you like, and save them as a new group or *Add to group* an existing one.
- **Deploys as a real Windows snap group.** Windows that together fill a screen are deployed
  through Snap Layouts and Snap Assist - the way you would do it by hand - so Windows itself
  makes them one snap group: listed as a *Group* in the taskbar and in Alt+Tab, square corners,
  shared edges. Any layout Snap Layouts offers on that screen works: halves, quarters, thirds,
  two-thirds, the wide centre. Windows that do not fill a screen go back to their exact place and
  size. Tick several groups and deploy them together, or pick one from the tray menu.
- **Bring to front, minimize.** Bring a group's windows on top of everything without moving
  them - the quick answer to two groups stacked on one screen - or minimize them all.
  Double-click any window on the map to bring just that one to the front.
- **Auto-restore.** When a window that belongs to a group opens - after a reboot, or when you
  reopen the app - it is put in its slot. It happens once, when the window appears; moving it
  afterwards is never fought.
- **Shows it on your screens.** *Show on screen* draws a group's slots on the real monitors
  with the window names, for six seconds.
- **Handles what is missing.** A window that is not open is drawn dashed. Click it to put any
  other open window in that place; the slot keeps its position and size.
- **Updates.** Rearranged a group and like it better? *Update positions*.

![Every screen with the groups on it, front to back - and the windows in none](docs/detected.png)

## Install

Download `WindowGroups.exe` from [Releases](../../releases) and run it. It lives in the tray
(four tiles, one lit). On first run it adds itself to start with Windows; switch that off in
the app or the tray menu.

Windows 10 or 11. The interface opens in a chromeless Microsoft Edge window (Chrome if Edge is
missing), using its own separate profile - it never touches your browser.

Windows may warn about an unrecognised app the first time, because the exe is not code-signed.
If you would rather not run a downloaded binary, build it yourself - it takes seconds and
needs nothing installed:

```
build.cmd
```

That compiles `src/WindowGroups.cs` with the C# compiler that ships with Windows (.NET
Framework 4) into `dist/WindowGroups.exe`, with the interface embedded. One file, no
dependencies.

## Privacy and security

- Everything stays on your PC. Groups are kept in `%APPDATA%/WindowGroups/groups.json`.
  There is no account, no telemetry, nothing sent anywhere.
- The interface is served on `127.0.0.1` only. Every request must carry the right `Host`
  header (no DNS rebinding) and a random token generated at each start, which only the window
  the tray opens is given. Other programs and web pages cannot drive it.
- The one outside request is the interface loading the Geist typeface from Google Fonts. With
  no connection it falls back to Segoe UI.
- It reads window titles and positions, and the Claude desktop app's `session-popouts.json`
  (session id and title only). It moves windows only when you deploy, or when auto-restore
  puts a newly opened window into its saved place.

## How it works

- **Real snap groups.** Windows has no function an app can call to snap a window or to group
  windows. A window moved to the right coordinates keeps rounded corners and a visible seam, and
  windows snapped one by one with Win+Arrow sit in the right places but are not a group - the
  taskbar does not list them as one. A snap group forms only when windows are snapped through
  Snap Layouts and Snap Assist. So for a layout that fills a screen, Window Groups does exactly
  that: Win+Z on the first window, presses the zone of the matching layout, then picks each
  remaining window in Snap Assist. It reads both through UI Automation, so nothing is guessed:
  - the layout is chosen by the shape of its zones - not by its number, which shifts with the
    suggestions Windows puts first, and not by its name, which changes with the display language;
  - the place Snap Assist is filling is read from where its window list sits, and the window is
    picked by invoking its thumbnail, matched on the window's title (the thumbnail's own Close
    button is never touched);
  - Win+Z is only sent while the window being grouped is the one in front, and Escape only while
    the flyout or Snap Assist is.

  Anything that does not work out falls back to the next method and Deploy says so - "not
  grouped on Left, snapped one by one".

- **Snapping one window, not just moving it.** For a single window in a half or a quarter that is
  not part of a full layout, Window Groups snaps it the way a person does - Win+Left/Right for a
  half, then Win+Up/Down for a quarter - with safeguards:
  - a snapped window stays "snapped" even after it is moved by code, and Win+Left on a window
    already snapped left throws it onto the next screen, so every window is unsnapped first;
  - it is moved onto the right screen before any key, because snapping acts on the screen the
    window is on;
  - no key is sent unless that exact window is in front at that moment, so a snap key can never
    land on another window;
  - the result is checked with Windows; anything that did not snap is placed exactly instead;
  - Snap Assist is closed afterwards with Escape, but only when it is the window in front, so
    Escape never reaches an app.

  Snapping takes the keyboard for a moment - a second or two for a whole snap group. Deploy does
  it when you click; auto-restore waits until you have not touched the keyboard or mouse for
  three seconds. The interface says for every window whether it deploys as part of a *snap group*,
  *snaps* on its own, or is *placed*.

- **Physical pixels throughout.** The process is per-monitor DPI aware, so a window on a 125%
  screen is saved and restored in the same coordinates. Without this, mixed-scale setups
  restore windows in the wrong place.
- **Placement is done twice.** A window moved onto a screen with a different scale resizes
  itself on arrival; the second call puts the size back.
- **Stacking without stealing focus.** Windows are raised by toggling topmost, which works from
  a background process where `SetForegroundWindow` does not.
- **Matching.** Claude sessions by session id first (their current title), then any window by
  exact title; when two windows share a title, the one already nearest its saved spot wins.
- **Auto-restore waits 2.5 seconds** after a window appears before placing it: the owning app
  positions its own windows on creation, and titles often arrive late.
- **Saved safely.** `groups.json` is written to a temp file and swapped in, so a crash can never
  leave it empty. A file that cannot be read is set aside, never overwritten.

## Uninstall

Exit from the tray menu, switch off *Start with Windows* first (or delete the `Window Groups`
value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`), delete the exe, and delete
`%APPDATA%/WindowGroups`.

## Building and hacking

| | |
|---|---|
| `src/WindowGroups.cs` | the whole program: window handling, detection, auto-restore, the local server, tray, overlay |
| `src/ui/index.html` | the interface, embedded into the exe at build time |
| `build.cmd` | builds `dist/WindowGroups.exe` |
| `tools/make-interop.ps1` | writes the UI Automation interop from the type library inside Windows; `build.cmd` runs it and links the few interfaces it needs into the exe |
| `tools/make-icon.ps1` | draws `assets/app.ico` |
| `tools/demo_server.py` | serves the interface against made-up windows, for screenshots |

## License

MIT - see [LICENSE](LICENSE). Designed in the Balkaris design language.
