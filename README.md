# Echodeck

Instant-replay soundboard for Discord on Windows.

Echodeck keeps the last 30 seconds of audio coming **from** Discord in memory, and only Discord's
audio: CS2, Spotify and browser audio are left out. Press a button and the moment a friend said
something is saved as a clip. Later phases play clips straight back into the voice channel over your
live mic.

> **Status: Phase 1.** Discord detection, per-process capture, rolling buffer, "save last N seconds"
> to WAV, and local preview. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design and the
> roadmap.

## Requirements

* Windows 10 version 2004 or later, or Windows 11. This is needed for Discord-only capture; older
  versions fall back to whole-device capture.
* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build.
* Discord desktop app.
* From Phase 2: [VB-CABLE](https://vb-audio.com/Cable/) (free).

## Build and run

From a Developer PowerShell or any terminal in the repo root:

```powershell
dotnet restore
dotnet build -c Release
dotnet test
dotnet run --project src/Echodeck.App -c Release
```

In Visual Studio 2022 (17.8+): open `Echodeck.sln`, set **Echodeck.App** as the startup project and
press F5.

NuGet packages are restored automatically. To add them by hand:

```powershell
dotnet add src/Echodeck.Core  package Microsoft.Extensions.Logging.Abstractions --version 8.0.2
dotnet add src/Echodeck.Audio package NAudio --version 2.2.1
dotnet add src/Echodeck.Audio package Microsoft.Extensions.Logging.Abstractions --version 8.0.2
dotnet add src/Echodeck.App   package CommunityToolkit.Mvvm --version 8.2.2
dotnet add src/Echodeck.App   package Microsoft.Extensions.DependencyInjection --version 8.0.1
dotnet add src/Echodeck.App   package Microsoft.Extensions.Logging --version 8.0.1
dotnet add src/Echodeck.App   package Microsoft.Extensions.Logging.Debug --version 8.0.1
```

## Using Phase 1

1. Start Echodeck and Discord, then join a voice channel.
2. The status should read **Active: Capturing Discord only (per-process)**, and the green meter
   moves when friends talk.
3. Click **Save last 5 seconds**. The clip appears in the list. Use **▶ Preview** to hear it on your
   headphones only.
4. Clips are saved in `%AppData%\Echodeck\clips`. Logs are in `%AppData%\Echodeck\logs`.

Testing checklists are in [docs/TESTING.md](docs/TESTING.md).

## Project layout

| Project | Contents |
|---|---|
| `src/Echodeck.Core` | Platform-neutral logic: rolling buffer, timeline, WAV, settings, logging, Discord process selection |
| `src/Echodeck.Audio` | Windows audio engine: process loopback interop, WASAPI capture and playback, devices, replay |
| `src/Echodeck.App` | WPF UI and DI composition root |
| `tests/Echodeck.Core.Tests` | xUnit tests |
