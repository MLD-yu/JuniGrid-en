# JuniGrid

JuniGrid is a free desktop mod manager and launcher for **Stardew Valley**, built as a WPF app with a Blazor WebView2 front end. It combines game launching, mod installation and updating, save-profile management, and a built-in Nexus Mods browser in a single desktop application.

## Features

- **One-click game launching** for the vanilla game and SMAPI, with a mode toggle between the two, live status on the launch button, and a close-game button while the game is running.
- **Nexus Mods integration**
  - Log in via Nexus Mods SSO or a personal API key.
  - Handles `nxm://` protocol links, so "Mod Manager Download" buttons on the Nexus site open JuniGrid.
  - Browse trending mods, search, view mod detail pages (description, images/gallery, files, requirements), and install mods directly from the in-app browser.
  - Per-mod update checks with one-click updates (Premium users get full-speed downloads; free accounts fall back to the browser where required).
- **Mod install management** — installed-mod list with categories, sorting, search, batch actions, per-mod notes/renames, and safe deletion via a `.junigrid_trash` recycling folder (skipped during scans and cleaned up automatically).
- **Save profiles** — multiple mod-loadout profiles that can be switched before launch; each profile is applied independently and the last selected profile is restored on startup.
- **Task center** — a floating download/task dock with progress, filtering (all/downloading/succeeded/failed), detailed download info, and a completion animation.
- **Resumable downloads** with a download history panel.
- **SMAPI console** — in-app log viewer with SMAPI-style colors and a command input capsule for sending SMAPI commands.
- **Settings** — game directory selection, adult-content filter toggle (with verification), memory/cache management with an elastic slider, and an about card.

## Technical notes

- Built with .NET, WPF (borderless window with custom titlebar), Blazor WebAssembly-style WebView2 front end, GSAP for animations, and PCL2-inspired UI styling.
- All gsap plugins are bundled locally; scripts are ordered so `interop.js` loads before `blazor.webview.js` to avoid a startup race.
- Startup uses a WPF splash window; the web front end only hides the shell until Blazor has mounted (`jg-booting`).

## v0.52.0 fixes

1. **Empty `.junigrid_trash` folder fix**: empty recycle directories are deleted right after mods are removed; scans skip the trash; leftover trash from previous sessions (left behind while files were locked) is cleared at the start of each scan when possible.
2. **Save profile cross-contamination fixed**: the root cause was an inverted "skip if state is consistent" condition in `ApplyProfile` (it skipped profiles that did need changes, so switching profiles effectively did nothing). After the fix, each save profile is truly independent.
3. **Remember last profile**: on startup, if the last selected profile was not "Default", it is restored automatically (after a 300ms delay so the first frame finishes rendering).
4. **Taskbar-restore black screen mitigated**: the WebView2 restore delay was reduced from 160ms to 60ms (fully eliminating it requires system-level handling; it is an inherent WPF + WebView2 limitation).
5. **CS4014 warnings eliminated**: 7 `Task.Run` calls prefixed with `_ =`.

### Changed files

- `Components/Pages/Mods.razor` (skip-condition fix / restore profile on launch / `_ =` prefixes)
- `Services/ModService.cs` (immediate trash cleanup / skip trash during scans / clear leftovers at scan start)
- `MainWindow.xaml.cs` (restore delay 160 -> 60ms)
