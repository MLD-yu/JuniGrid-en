# JuniGrid

A desktop mod manager and launcher for **Stardew Valley**, built on .NET (WPF + Blazor WebView2).
This repository hosts the official English edition of JuniGrid; downloads are available at [Releases](https://github.com/MLD-yu/JuniGrid-en/releases).

## Security notes

- No API keys or secrets are embedded in the source code. Sign-in is OAuth2 only (authorization code + PKCE); tokens are stored only in the local configuration file of the current Windows user on that machine, and personal API keys are never requested from or shown to the user.
- All downloads are performed strictly as the currently signed-in Nexus user; the app never proxies, redistributes, or caches mod files on behalf of other users.
- The app talks only to official Nexus APIs (REST v1 + GraphQL). It never fetches or parses www.nexusmods.com pages.
- Adult content is excluded by default; when the user opts in, listings follow the signed-in user's Nexus account adult content setting (enforced server-side).

## Features

- **Nexus Mods integration** — sign-in runs through OAuth2 (authorization code + PKCE, callback URL `http://localhost:49162/auth/callback`, temporary loopback-only listener closed after the single callback). Browse mods through the Nexus GraphQL API and fetch files through the official download endpoints, acting as the signed-in user.
- **One-click install** — registers as the `nxm://` protocol handler, so the "Mod Manager Download" button on Nexus pages launches JuniGrid directly.
- **Mod management** — scans the Mods folder (including nested manifests), enable/disable/uninstall, dependency checks, save and configuration management.
- **Task center** — a unified progress view for downloads, installs, and updates, with resumable transfers.
- **SMAPI support** — install/update SMAPI, and pipe the SMAPI console into the in-app log viewer.
- **Automatic updates** — an update button next to the title-bar logo appears only when a new version shows up on GitHub Releases (a green ring around the logo). Hovering shows the version; clicking starts a ring-shaped progress that fills clockwise from 12 o'clock, and once full it automatically launches the silent installer. Clicking again while downloading cancels it, and the partially downloaded data is kept for resuming.

## Build and run

Requirements: Windows 10 1809+ and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (with the Windows Desktop workload).

```bash
# Restore + build
dotnet build JuniGrid.sln

# Run directly from the repository root
dotnet run --project JuniGrid/JuniGrid.csproj

# Or open JuniGrid.sln in Visual Studio 2022+ and press F5

# Release build (output goes to JuniGrid/bin/Release/net10.0-windows10.0.17763.0/)
dotnet publish JuniGrid/JuniGrid.csproj -c Release
```
## What to do if the download or installation gets blocked

Niche desktop apps and unsigned installers are sometimes flagged as risky by browsers, Windows Defender, or SmartScreen. Please make sure the installer comes from the official GitHub Releases page ([JuniGrid-en Releases](https://github.com/MLD-yu/JuniGrid-en/releases)) and that the file name follows the pattern JuniGrid-en-vX.Y.Z-setup.exe (for example, JuniGrid-en-v1.1.1-setup.exe).

1. If the browser's download bar shows a risk warning, open the downloads list, click the `···` (three dots) to the right of that download, choose `Keep` / `Keep anyway` / `Show more`, and then keep the file.
2. If Windows SmartScreen pops up a blue blocking window, click `More info`, then `Run anyway`.
3. If antivirus software explicitly reports a trojan or a high-severity threat, or has already quarantined the file, do not force it to run; delete the file and download it again from the official GitHub Releases page. If the problem persists, report it to the author with a screenshot.



## License

All rights reserved. This project also serves as the source-code submission for the Nexus Mods API team's registration review.
