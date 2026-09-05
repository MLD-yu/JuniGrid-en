# JuniGrid (English Edition)

Your helper for Stardew Valley — mod manager, SMAPI launcher, Nexus integration and more.

Built with WPF + Blazor Hybrid (.NET 10). This repository tracks the **English edition** of JuniGrid;
the Chinese edition lives at [MLD-yu/JuniGrid](https://github.com/MLD-yu/JuniGrid).

## Features

- Mod manager with profiles, enable/disable, batch updates and one-click Nexus installs
- SMAPI launcher with live log viewer and command console
- Nexus Mods browsing (trending, search by name or mod ID) and one-click install (Premium for some mods)
- Automatic SMAPI download/update
- Play-time tracking with a GitHub-style heatmap
- Task center, memory management and cache cleanup tools
- Dark / light theme with a circular reveal switch animation

## Download

Grab the latest installer from the [Releases](https://github.com/MLD-yu/JuniGrid-en/releases) page
(`JuniGrid-en-vX.Y.Z-setup.exe`).

## Build

Requires .NET 10 SDK (Windows). Open `JuniGrid.sln` and build, or run:

```
dotnet build JuniGrid/JuniGrid.csproj -c Debug
```

To package the self-contained installer:

```
powershell -File installer/build-installer.ps1
```
