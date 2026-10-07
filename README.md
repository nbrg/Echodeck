<img src="src/Echodeck.App/Assets/echodeck.png" width="96" alt="Echodeck icon" align="right">

# Echodeck

Instant-replay soundboard for Discord on Windows.

Echodeck keeps the last 30 seconds of audio coming **from** Discord in memory, and only Discord's
audio: CS2, Spotify and browser audio are left out. When a friend says something funny, you can
replay it straight into the voice channel, or trim it and keep it as a clip. Your live mic keeps
working the whole time.

> **Status: Phase 2.** Discord-only capture, rolling buffer, play clips into Discord over your live
> mic (via VB-CABLE), clip editor with waveform trimming, rename/delete, and setup warnings. Global
> hotkeys come in Phase 3. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design and roadmap.

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
* [VB-CABLE](https://vb-audio.com/Cable/) (free) to play into Discord.

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

## Setting up audio (one time)

1. Install [VB-CABLE](https://vb-audio.com/Cable/): run `VBCABLE_Setup_x64.exe` as administrator, then reboot.
2. **Windows sound output: keep your headset.** Never pick a CABLE device there, or you'll hear nothing.
3. In Echodeck → **Audio** tab:
   * **Microphone:** your real mic.
   * **Send to Discord via:** `CABLE Input (VB-Audio Virtual Cable)`. This is the default when VB-CABLE is installed.
   * **Headphones for previews:** your headset.
4. In Discord → User Settings → Voice & Video:
   * **Input Device:** `CABLE Output (VB-Audio Virtual Cable)`.
   * **Output Device:** your headset.
   * Use **Voice Activity**. With Push to Talk, friends only hear clips while you hold the key.

Echodeck now carries your voice to Discord, so keep it running while you're in a call. If anything
is routed wrong, an orange or red banner at the top of the window says what's wrong and how to fix
it. It checks the devices Discord is actually using, not just Echodeck's own settings.

## Using it

| Action | How |
|---|---|
| Replay what a friend just said into Discord | **▶ Replay last 5 s to Discord** (the length is selectable) |
| Trim before playing or saving | **✂ Edit last 30 s…**, drag the white markers, then press **Enter** to play or **Ctrl+S** to save |
| Save without editing | **💾 Save last 5 s** |
| Saved clips | **▶ Preview** (headphones only) · **▶ Discord** · **✂ Edit** · **Rename** (F2) · **🗑** (Del) · double-click to edit |
| Stop a clip that's playing in Discord | **■ Stop clip** |

Editor keys: **Space** preview · **Enter** play to Discord · **Ctrl+S** save · **Esc** cancel ·
**←/→** move the start marker · **Shift+←/→** move the end marker (hold **Ctrl** for 100 ms steps).

Replays include **both sides** of the conversation by default: your friends (from Discord) plus what
you sent to Discord (your mic and any clips you played), lined up in time. Clips you play into
Discord are also played on your headphones, but your own mic never is. Both options are on the
Audio tab.

Only one Echodeck runs at a time. Starting it again brings the open window to the front.

Clips are saved in `%AppData%\Echodeck\clips`, logs in `%AppData%\Echodeck\logs`. Testing checklists
are in [docs/TESTING.md](docs/TESTING.md).

## Project layout

| Project | Contents |
|---|---|
| `src/Echodeck.Core` | Platform-neutral logic: rolling buffer, timeline, WAV, settings, logging, Discord process selection |
| `src/Echodeck.Audio` | Windows audio engine: process loopback interop, WASAPI capture and playback, devices, replay |
| `src/Echodeck.App` | WPF UI and DI composition root |
| `tests/Echodeck.Core.Tests` | xUnit tests |
