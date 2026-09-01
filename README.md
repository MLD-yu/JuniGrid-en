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

# release build (output in JuniGrid/bin/Release/net10.0-windows10.0.17763.0/)
dotnet publish JuniGrid/JuniGrid.csproj -c Release
```

## Security notes

- No API keys or secrets are embedded in the source. Credentials (Nexus API key / OAuth tokens) are entered by the user at login and stored only in the local per-user configuration file on their machine.
- All downloads act strictly on behalf of the currently logged-in Nexus user; the app does not proxy, redistribute, or cache mod files for other users.

## License

All rights reserved. This project is provided for review by the Nexus Mods API team as part of the API access registration process.
