# JuniGrid

A desktop mod manager and launcher for Stardew Valley, built with .NET (WPF + Blazor).

## Features

- **Nexus Mods integration** — sign in with your Nexus account via SSO (`wss://sso.nexusmods.com`), an OAuth2 authorization-code flow with PKCE (`NexusOAuthService`, callback `http://localhost:49162/auth/callback`), or a personal API key; browses mods through the Nexus GraphQL API and downloads files on behalf of the logged-in user through the authenticated download endpoints.
- **One-click installs** — registers as an `nxm://` protocol handler so "Mod Manager Download" buttons on Nexus Mods launch JuniGrid directly.
- **Mod management** — scans the Mods folder (including nested manifests), enables/disables/uninstalls mods, dependency checks, and save/profile management.
- **Task center** — unified progress view for downloads, installs, and updates with resumable downloads.
- **SMAPI support** — installs/updates SMAPI, streams the SMAPI console into an in-app log viewer with classification and filtering.
- **Auto-update** — checks for launcher updates and applies them from the in-app update queue.

## Building & Running

Requirements: Windows 10 1809+ and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with the Windows Desktop workload.

```bash
# restore + build
dotnet build JuniGrid.sln

# run directly from the repository root
dotnet run --project JuniGrid/JuniGrid.csproj

# or open JuniGrid.sln in Visual Studio 2022+ and press F5


```

## Security notes

- No API keys or secrets are embedded in the source. Credentials (Nexus API key / OAuth tokens) are entered by the user at login and stored only in the local per-user configuration file on their machine.
- All downloads act strictly on behalf of the currently logged-in Nexus user; the app does not proxy, redistribute, or cache mod files for other users.

## Download or installation blocked?

 Lesser-known desktop apps and unsigned installers are sometimes flagged by the browser, Windows Defender, or SmartScreen. First make sure the installer comes from the official GitHub Release above and the file name is `JuniGrid-v1.0.0-setup.exe` (portable version: `JuniGrid-v1.0.0-portable.zip`).

1. If the browser download bar shows a risk warning, open the downloads list, click the `...` (three dots) next to the download, then choose `Keep` / `Keep anyway` / `Show more` to keep it.
2. If a blue Windows SmartScreen window pops up, click `More info`, then `Run anyway`.
3. If your antivirus explicitly reports a trojan or high-risk threat and quarantines the file, do not force-run it; delete the file and re-download from the official GitHub Release. If it is still flagged, open an [issue](https://github.com/MLD-yu/JuniGrid-en/issues) with a screenshot.

## License

All rights reserved. This project is provided for review by the Nexus Mods API team as part of the API access registration process.
