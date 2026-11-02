# RELAY

A calm, fast web browser for Windows 10 and 11, built with C#, WPF and Microsoft Edge WebView2.

RELAY runs without administrator rights, blocks ads and trackers out of the box, and keeps all of its data in one folder you can see.

---

## Install or run RELAY

### Option A — `RELAY Setup.exe` (recommended)

RELAY's own setup wizard. It doesn't use Windows Installer, so it works on PCs where .msi installs are blocked, and it never asks for administrator rights:

1. Double-click **RELAY Setup.exe**. (SmartScreen may say "Windows protected your PC" because the file isn't code-signed — choose *More info → Run anyway*.)
2. Click **Install**, or **Install options** to pick a folder and choose Start menu / desktop shortcuts.
3. Click **Open RELAY** on the last page.

* Program files go to `%LOCALAPPDATA%\Programs\RELAY`; your data lives in `%LOCALAPPDATA%\RELAY`.
* Running a newer Setup updates RELAY in place and keeps your bookmarks, history and settings. If RELAY is open, Setup offers to close it.
* Uninstall from **Settings → Apps → Installed apps → RELAY**. You can choose whether to keep your browsing data.

### Option B — portable (`RELAY-win-x64-portable.zip`)

1. Right-click the ZIP → **Extract All…** into any folder you can write to (Desktop, Documents, a USB stick).
2. Open the `RELAY` folder and double-click **Relay.exe**.

Because that folder contains `relay.portable`, everything (history, bookmarks, cookies, settings) is stored in a `Data` folder next to `Relay.exe`. Delete the folder and RELAY is gone without a trace.

### First launch

RELAY asks which search engine you want — Google, Brave Search, Bing, Yahoo, DuckDuckGo, Ecosia or Startpage. The list is shuffled and nothing is preselected. You can change it any time in **Settings → General**.

**Windows SmartScreen** may warn the first time because the build isn't code-signed. Choose *More info → Run anyway* if you trust the source.

### Requirement: the WebView2 Runtime

Web pages are drawn by the Evergreen **Microsoft Edge WebView2 Runtime**, which ships with Windows 11 and is installed on almost every up-to-date Windows 10 PC. If it's missing, RELAY shows a clear message with a link to Microsoft's download page instead of crashing. RELAY never installs anything on its own.

---

## Build from source

Requirements: **.NET 10 SDK** (the current LTS). Visual Studio 2022 17.14+/2026 with the *.NET desktop development* workload, or just the SDK and any editor.

```powershell
git clone <this repo>
cd Relay
dotnet test tests/Relay.Tests          # 81 unit tests (core logic, ad-blocking engine, SQLite data layer)
dotnet run --project src/Relay.App     # launch RELAY (Windows only)
./publish.ps1                          # portable ZIP → artifacts/RELAY-win-x64-portable.zip
```

`Relay.sln` opens directly in Visual Studio; set **Relay.App** as the startup project.

`publish.sh` (Linux, macOS or WSL) builds `RELAY Setup.exe` (project `src/Relay.Setup`, a single self-contained WPF wizard with the app embedded) and the portable ZIP. `MSI=1 ./publish.sh` additionally builds an optional per-user .msi from `installer/Relay.wxs` (needs `wixl`); note that some managed PCs block .msi installs for non-admin users (errors 2502/2503), which is why Setup.exe is the recommended installer.

---

## Project layout

```
Relay/
├── Relay.sln
├── src/
│   ├── Relay.Core/      Pure logic: address-bar parsing, search engines, download filename safety,
│   │                    bookmark import/export (Netscape HTML), settings store, data paths
│   ├── Relay.Data/      SQLite persistence (history, bookmarks, quick links, reading list,
│   │                    downloads, site permissions, favicons, session)
│   ├── Relay.Browser/   WebView2 engine layer: shared environment & profiles, BrowserTab,
│   │                    download manager, permission handling, new-tab-page bridge
│   └── Relay.App/       WPF app (MVVM): windows, view models, theme system, controls, dialogs,
│       ├── NewTab/      the RELAY new tab page (local HTML/CSS/JS)
│       ├── Themes/      ThemeManager (light / dark / private palettes) + control styles
│       ├── ViewModels/  Main window, address bar suggestions, sidebar panels, settings
│       ├── Views/       Main window, settings, dialogs
│       └── Services/    Window manager & session, single-instance, engine prompts
└── tests/Relay.Tests/   xUnit tests
```

---

## Features

**What's new in 1.1**
* **Search engine choice on first launch** (shuffled, nothing preselected) — now including Yahoo.
* **RELAY Setup.exe** — a custom installer wizard (per-user, no admin, no Windows Installer) with update-in-place and a matching uninstaller, alongside the portable ZIP.
* **Built-in ad and tracker blocker** using EasyList, EasyPrivacy, Peter Lowe's list and (optional) cookie-notice hiding; a shield in the address bar shows how much was blocked and turns blocking off for a site; your own filter rules; a running total on the new tab page. On YouTube, ad data is removed from the player before it loads and any ad that still starts is skipped.
* **Sleeping tabs**: background tabs use less memory right away and are suspended after 15 minutes (configurable; tabs playing audio stay awake). The engine also starts while the window is being built.
* **Memory saver**: tabs left in the background for an hour (configurable) are unloaded completely, freeing all of their memory, and reload when you open them. Tabs playing audio, private tabs and tabs with a download running are kept. Right-click a tab → "Unload tab to free memory" to do it now.
* **Task manager** (Shift+Esc): real memory and CPU for every tab and every WebView2 process, measured like Windows Task Manager, with a total for the whole browser.
* **Tab hover previews** with a thumbnail, and a gentle animation when tabs open.
* **Tab groups** with names and nine colours: collapse, rename, recolour, ungroup or close a group; groups are restored with your session.
* **Vertical tabs** (collapsible to icons) as an alternative to the tab strip.
* **Split view**: two tabs side by side with a draggable divider; the address bar follows the pane you click.
* **Command palette (Ctrl+K)**: fuzzy search over every command, your open tabs, bookmarks and history.
* **Reader view (F9)** with serif/sans, text size and light/sepia/dark themes.
* **Screenshots** of the visible area or the full page (saved and copied to the clipboard), **picture-in-picture**, and a **QR code** for the current page.

**Browsing**
* Real Chromium browsing through WebView2: HTML5 video, WebGL, PDFs, devtools (F12), print, find on page, page zoom.
* Multiple windows; one RELAY process per data folder (launching again opens a new window).
* Page-requested fullscreen (videos) and browser fullscreen (F11).
* Per-tab crash recovery: a crashed or frozen page shows a *Reload* panel in that tab only. If the whole engine process dies, every tab is rebuilt at its last address.

**Tabs**
* New, close, switch, drag to reorder, middle-click to close, mouse-wheel over the strip to switch.
* Titles, favicons (cached in SQLite), loading spinner, audio indicator with mute, clear active-tab styling.
* Context menu: new tab to the right, reload, duplicate, mute, add to reading list, copy link, close / close others / close to the right, reopen closed tab.
* Restore previous tabs on startup (restored background tabs load only when opened).

**Address bar**
* Tells URLs from searches (`google.com` → navigate, `best gaming laptops` → search, `localhost:3000`, IPs, `file` paths, `?query` forces a search). `javascript:` and `data:` typed URLs are never navigated.
* Suggestions from bookmarks, history and (optionally) the search engine; ↑/↓ to choose, Enter to go, Alt+Enter for a new tab, Ctrl+Enter for `www.…com`, Shift+Delete removes a history suggestion.
* Site-information button: secure / not secure / local file, saved permissions for the site with one-click reset.
* Search engines: Google, Bing, DuckDuckGo, Brave Search, Ecosia, Startpage, or a custom `…%s…` URL.

**New tab page**
* RELAY branding, large search field, clock and date, quick links (add, edit, remove, drag to rearrange), recently visited sites, recent bookmarks.
* Six calm background presets or your own image; each section can be hidden.
* Strict Content-Security-Policy, refuses to run inside a frame, and only RELAY's own page (as the top-level document) can talk to the browser.

**Bookmarks, history, reading list, downloads** (sidebar)
* Bookmarks with folders, rename/edit, move between folders, reorder, search, open all in tabs, import/export in the standard HTML format every browser uses.
* Star button / Ctrl+D shows an edit popup (name, folder, remove).
* History grouped by day (Today, Yesterday, weekdays, dates), search, delete entries, load more, clear browsing data by time range.
* Reading list: save pages, mark read/unread, remove.
* Downloads: progress, speed and time left, pause/resume (when the server supports it), cancel, retry, open, show in folder, failure reasons, and a small notice when downloads start/finish.

**Private browsing**
* Separate WebView2 InPrivate profile: its own cookies and storage, no history, no session saving, nothing remembered about site permissions; Chromium deletes the data when the last private window closes.
* Clearly marked: violet window chrome, a *Private* badge, and a new tab page that explains honestly what private browsing does and doesn't do.

**Settings** (all saved immediately to `settings.json` and applied live)
* General: startup behaviour, homepage, home button, search engine, search suggestions.
* Appearance: system / light / dark theme, six accent colours, comfortable or compact tabs, tab icons on/off, interface font and size, default page zoom, sidebar on/off, new tab page options and background.
* Privacy: clear browsing data (time range + data types), clear on exit, password saving and autofill switches, private-window suggestions, cookie manager (per site, filter, delete), site-permission manager (allow / block / forget).
* Downloads: folder, ask where to save, notifications.
* About: app, engine, SDK and .NET versions, data location, open-source licenses.

**Keyboard shortcuts**

| Shortcut | Action | Shortcut | Action |
|---|---|---|---|
| Ctrl+T | New tab | Ctrl+D | Bookmark page |
| Ctrl+W / Ctrl+F4 | Close tab | Ctrl+H | History |
| Ctrl+Shift+T | Reopen closed tab | Ctrl+J | Downloads |
| Ctrl+Tab / Ctrl+PgDn | Next tab | Ctrl+Shift+O | Bookmarks |
| Ctrl+Shift+Tab / Ctrl+PgUp | Previous tab | Ctrl+Shift+B | Toggle sidebar |
| Ctrl+1 … Ctrl+9 | Go to tab | Ctrl+N | New window |
| Ctrl+L / Alt+D / F6 | Focus address bar | Ctrl+Shift+N | New private window |
| Ctrl+R / F5 | Reload | Ctrl+F | Find on page |
| Alt+Left / Alt+Right | Back / Forward | Ctrl+P | Print |
| Ctrl+ + / − / 0 | Zoom in / out / reset | F11 | Full screen |
| Alt+F | Menu | F12 | Developer tools |
| Ctrl+K | Command palette | F9 | Reader view |
| Shift+Esc | Task manager | | |

---

## Security and privacy design

* Chromium's protections stay on: certificate errors are **never** bypassed (the engine's blocking page is kept), SmartScreen is untouched, no command-line flags weaken the engine.
* `AreHostObjectsAllowed = false` — no native .NET objects are exposed to any page. Web messages are only accepted from `https://newtab.relay/` as the top-level document, are size-limited and validated field by field.
* Downloads: names are sanitized (path separators, reserved device names, control and bidi-spoofing characters, length), always saved inside the chosen folder with a unique name, executables and scripts require confirmation to keep **and** to open, and nothing is ever opened automatically.
* Passwords are never stored by RELAY. If you enable password saving, the WebView2 engine stores them encrypted with your Windows account (DPAPI); private windows never save them.
* Normal and private profiles are separate WebView2 profiles. Private sessions write nothing to RELAY's database.
* No telemetry. The only network requests RELAY itself makes are optional search suggestions to your chosen engine (can be turned off).
* No admin rights, services, drivers, registry writes (it only *reads* the Windows theme preference) or system changes. The app manifest requests `asInvoker`.

---

## Known limitations (honest list)

* **Built and unit-tested in a Linux environment.** The solution compiles cleanly for Windows (0 warnings), the 81 unit tests pass, the ad-blocking engine was checked against the real EasyList/EasyPrivacy lists, and the new tab page and reader view were rendered and click-tested in Chromium — but the WPF window itself can't be launched there, so new 1.1 features were not clicked through by hand on Windows. Please report anything that misbehaves on first run; `relay-errors.log` in the data folder records unexpected errors.
* No browser extensions (WebView2's extension support is limited and unstable), no sync.
* WebView2 doesn't tell RELAY whether a link was Ctrl-clicked, so links that open new tabs always open in the foreground.
* Tabs can be reordered within a window but not dragged out into another window.
* Pause/resume works only when the server supports resuming; otherwise the download restarts from *Retry*.
* **No list of saved passwords.** WebView2 doesn't let apps read the passwords its engine saves, so RELAY can offer to save and fill them (encrypted by Windows) and can remove them all, but it can't show or edit them one by one. A dedicated password manager is the better choice if you need that.
* The ad blocker works on network requests and element hiding, plus a small script on YouTube only. Ads served from the same address as the content on other sites may still appear, and YouTube can change its player at any time. Videos inside embedded frames can't be put into picture-in-picture from the menu.
* `RELAY Setup.exe` is about 120 MB because it carries its own copy of .NET so nothing else has to be installed first. It isn't code-signed, so SmartScreen asks for confirmation the first time.
* Cookie management and clearing site data need at least one page open in a normal window (that's when the engine profile is live).
* "Do Not Track" isn't offered: WebView2 has no supported switch for it and RELAY doesn't inject headers into every request.
* Font settings cover RELAY's interface font/size and default page zoom; websites still choose their own fonts.
* Menus opened inside a private window use the normal theme colours (the window chrome itself is clearly private).
* x64 only. The portable build is self-contained (~140 MB) so it runs without installing .NET.
