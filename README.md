# Echodeck

Instant-replay soundboard for Discord on Windows.

Echodeck keeps the last 30 seconds of audio coming **from** Discord in memory, and only Discord's
audio: CS2, Spotify and browser audio are left out. Press a button and the moment a friend said
something is saved as a clip. Later phases play clips straight back into the voice channel over your
live mic.

> **Status: Phase 1.** Discord detection, per-process capture, rolling buffer, "save last N seconds"
> to WAV, and local preview. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design and the
> roadmap.

## Download and run (no build needed)

1. Open the repository's **Releases** page and download `Echodeck-<version>-win-x64.exe`.
2. Double-click it. That's the whole app: no installer, and no .NET install, because the runtime is
   bundled inside the exe. Settings, clips and logs go to `%AppData%\Echodeck`.
3. The exe isn't code-signed yet, so Windows SmartScreen may show "Windows protected your PC".
   Click **More info → Run anyway**.

To uninstall, delete the exe and the `%AppData%\Echodeck` folder.

### Making a release (maintainers)

The `release` GitHub Actions workflow builds the single-file exe on Windows, runs the tests, and
attaches the exe plus a `.sha256` checksum to a new GitHub Release. Start it either way:

* **From GitHub:** go to **Actions → release → Run workflow** and enter a version such as
  `0.1.0-preview.1`.
* **From git:** `git tag v0.2.0 && git push origin v0.2.0`

A version with a `-` suffix is published as a pre-release. Every normal push also uploads a test
build as a workflow artifact (open the Actions run, then **Artifacts**).

To build the same exe locally:

```powershell
dotnet publish src/Echodeck.App -p:PublishProfile=win-x64
# → artifacts/publish/win-x64/Echodeck.exe
```

## Requirements

* Windows 10 version 2004 or later, or Windows 11. This is needed for Discord-only capture; older
  versions fall back to whole-device capture.
* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), only if you build from source.
* Discord desktop app.
* From Phase 2: [VB-CABLE](https://vb-audio.com/Cable/) (free).

## Build from source

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
