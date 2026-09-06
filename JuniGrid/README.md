# JuniGrid v0.52.0

## Fixes in this release
1. **Empty .junigrid_trash folders fixed**: empty recycle-bin directories are deleted immediately after mods are removed; scans skip the recycle bin; each scan starts by clearing anything left over from the previous run (left behind while files were in use, deleted on a best-effort basis)
2. **Save-profile cross-contamination fixed at the root**: the real cause was an inverted "state is consistent, skip it" condition in ApplyProfile (it also skipped the profiles that did need updating, so switching saves actually did nothing). After the fix, each save is genuinely independent
3. **Remember the last save profile**: at startup, if the previously selected profile was not "Default", the app automatically restores that save (delayed 300 ms so the first render finishes before it is applied)
4. **Taskbar-restore black screen mitigated**: the WebView2 restore delay was reduced from 160 ms to 60 ms (fully eliminating it would require taking over at the system level; it is an inherent limitation of WPF + WebView2)
5. **CS4014 warnings eliminated**: 7 Task.Run calls prefixed with `_ =`

## Changed files
- `Components/Pages/Mods.razor` (skip-condition fix / startup save restore / `_ =` prefixes)
- `Services/ModService.cs` (immediate recycle-bin cleanup / skip during scans / clear leftovers at the start)
- `MainWindow.xaml.cs` (restore delay 160→60 ms)
