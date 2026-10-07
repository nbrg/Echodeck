# Echodeck architecture

Echodeck is a Windows-only instant-replay soundboard for Discord. It keeps the last N seconds of
**incoming Discord audio** in memory, lets you clip it, and (from Phase 2) plays clips into Discord
mixed with your live microphone through a virtual audio cable.

This document covers the design decisions. Each numbered section answers one of the original
deliverables.

---

## 1. Recommended architecture

Three layers, with dependencies pointing downwards only:

```
┌──────────────────────────────────────────────────────────────────────┐
│ Echodeck.App  (WPF, net8.0-windows)                                  │
│   App.xaml.cs (DI composition root) · MainWindow · ViewModels        │
│   Phase 3+: ReplayEditorWindow, HotkeyService (needs an HWND)        │
└───────────────▲──────────────────────────────────────────────────────┘
                │ binds to services, never touches WASAPI
┌───────────────┴──────────────────────────────────────────────────────┐
│ Echodeck.Audio  (net8.0-windows: NAudio + Core Audio COM interop)    │
│   Capture/   IDiscordAudioSource ─┬─ ProcessLoopbackSource  (pref.)  │
│                                   └─ DeviceLoopbackSource   (fallb.) │
│              DiscordCaptureService (supervisor, recovery)            │
│              CaptureFormatConverter (→ 48 kHz stereo float)          │
│   Devices/   AudioDeviceService (enumeration, hot-plug, sessions)    │
│   Replay/    ReplayService (snapshot → clip → WAV)                   │
│   Playback/  LocalPreviewPlayer (headphones only)                    │
│   Phase 2:   MicrophoneCaptureService, AudioMixerService,            │
│              VirtualOutputService                                    │
│   Interop/   process-loopback activation, Toolhelp32                 │
└───────────────▲──────────────────────────────────────────────────────┘
                │
┌───────────────┴──────────────────────────────────────────────────────┐
│ Echodeck.Core  (net8.0, no Windows dependencies → unit-testable)     │
│   RollingAudioBuffer · CaptureTimeline · PeakMeter · AudioClip       │
│   WavFileWriter · DiscordProcessLocator · SettingsService · AppPaths │
│   FileLoggerProvider · Phase 4: SoundboardLibrary (clips.json)       │
└──────────────────────────────────────────────────────────────────────┘
```

Mapping to the components you listed:

| Requested component | Where it lives |
|---|---|
| AudioCaptureService / DiscordCaptureService | `Audio/Capture/DiscordCaptureService` + `IDiscordAudioSource` strategies |
| MicrophoneCaptureService | Phase 2, `Audio/Capture/MicrophoneCaptureService` |
| RollingAudioBuffer | `Core/Audio/RollingAudioBuffer` |
| ReplayService | `Audio/Replay/ReplayService` |
| AudioMixerService / VirtualOutputService | Phase 2, `Audio/Mixing/` and `Audio/Output/` |
| SoundboardService | Phase 4, library model in Core, playback in Audio |
| HotkeyService | Phase 3, in App (needs a window handle for `RegisterHotKey`) |
| AudioDeviceService | `Audio/Devices/AudioDeviceService` |
| SettingsService | `Core/Settings/SettingsService` |

Principles:

* **Audio threads never wait on the UI.** UI updates go through `Dispatcher.BeginInvoke`; meters are
  lock-free and polled by the UI at about 15 Hz.
* **One internal format**: 48 kHz, stereo, 32-bit float. Discord runs at 48 kHz, so the Discord path
  never resamples. Conversion happens once, at the edge (`CaptureFormatConverter`).
* **Capture methods are strategies** (`IDiscordAudioSource`). Better methods can be added later without
  touching the buffer, replay or UI code.
* **Everything is bounded**: the buffer is fixed-size, logs roll at 1 MB × 5, the log queue drops lines
  instead of growing, temporary files are cleaned at startup, and converter scratch buffers grow only
  to the largest packet size.
* **Future features slot in as pipeline stages.** Effects, pitch, speed, normalisation and silence
  trimming become `ISampleProvider` stages between the clip and the mixer. Stream Deck, MIDI, OBS and a
  bot become extra triggers that call the same `ReplayService` and `SoundboardService` methods the
  hotkeys use.

## 2. Windows audio routing

```
                    ┌──────────────────────── Echodeck ────────────────────────┐
Physical mic ──WASAPI capture──► mic ring buffer ─┐                            │
                    │                              ├─► Mixer ─► limiter ─► WasapiOut ──► "CABLE Input"
Clip / replay ─────────────────► clip voice(s) ───┘   (gain, optional ducking) │          (VB-CABLE render)
                    │                                                          │               │
                    │  Local preview ─────────────────────────────► WasapiOut ──► headphones   │
                    └──────────────────────────────────────────────────────────┘               ▼
                                                                                    "CABLE Output" (VB-CABLE capture)
                                                                                               │
Friends ──► Discord.exe ──► headphones                                                         ▼
               │                                                                   Discord input device
               └──process loopback──► Echodeck rolling buffer
```

Required settings (the Setup page in Phase 5 will show them):

| Where | Setting | Value |
|---|---|---|
| Echodeck | Microphone | your real microphone |
| Echodeck | Virtual output | **CABLE Input (VB-Audio Virtual Cable)** |
| Echodeck | Local headphones | your headset |
| Discord → Voice & Video | Input device | **CABLE Output (VB-Audio Virtual Cable)** |
| Discord → Voice & Video | Output device | your headset |

Echodeck never changes Windows or Discord settings. It only detects whether VB-CABLE exists and says so.

## 3. Capturing Discord without other PC audio

Three methods, tried in this order in **Auto** mode. You can also force any one of them in Settings.

1. **Per-process capture (WASAPI process loopback), the preferred method.**
   `ActivateAudioInterfaceAsync("VAD\\Process_Loopback", IID_IAudioClient, params)` with
   `AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, TargetProcessId = <root Discord.exe>, INCLUDE_TARGET_PROCESS_TREE }`.
   * Discord is built on Electron, and the voice audio is rendered by a **child** Discord.exe, not the
     window process. `DiscordProcessLocator` takes a Toolhelp32 snapshot, finds the root (a Discord.exe
     whose parent is not Discord.exe) and captures its whole tree.
   * This captures only Discord. CS2, Spotify, browsers, Windows sounds and Echodeck's own previews are
     all excluded. It also doesn't depend on which output device Discord uses.
   * It needs Windows 10 2004+ or Windows 11. Microsoft's documented minimum is build 20348, but it works on
     consumer builds from 19041. Echodeck requests 48 kHz float. If the system rejects that, it falls
     back to the exact format Microsoft's sample uses.
   * Implementation: `Audio/Interop/ProcessLoopbackActivator.cs` and `Audio/Capture/ProcessLoopbackSource.cs`.
     It uses hand-written COM interop, because NAudio's `AudioClient` calls `GetMixFormat()`, and the
     virtual loopback device doesn't implement that.
2. **Discord's output device.** Echodeck enumerates audio sessions on every render device to find
   the one where a Discord PID has a session, then runs normal WASAPI loopback on that device. If Discord
   switches devices, Echodeck follows. Other apps on the same device are captured too, and the UI warns
   you about that.
3. **Selected or default output device.** Whole-device loopback. This is the last resort and captures
   everything. In Auto mode it only runs while Discord is running, and Echodeck upgrades back to method 2
   as soon as Discord's session appears.

**Keeping the timeline accurate.** Loopback capture sends *no packets* while the source is silent. If
nothing filled those gaps, 20 seconds of silence would take up 0 seconds of buffer, and "the last 5
seconds" would reach back much further. `CaptureTimeline` compares the frames received against a
monotonic clock every 50 ms and inserts silence, so the buffer always covers real time.

**Recovering during long sessions.** `DiscordCaptureService` runs a 2-second supervisor:

| Event | Response |
|---|---|
| Discord starts, exits or restarts (new root PID) | Capture is re-pointed. |
| A device is unplugged or the default device changes (`IMMNotificationClient`, debounced) | Device loopback is reopened. Process loopback is device-independent, so it is left alone. |
| A source faults | The source is disposed and restarted, with progressive back-off up to 20 s. |
| Settings change | Only capture-related settings restart capture. |

## 4. Mixing microphone and soundboard into Discord

```
mic ─WasapiCapture (shared, event, 20 ms)─► CaptureFormatConverter ─► MicJitterBuffer (SPSC ring)
                                                                            │ pulled
clips ─ClipVoice (5 ms edge fades)─────────────────────────────► MixerEngine.Render ─► SoftLimiter
                                                                            │
                                     VirtualOutputService: WasapiOut on CABLE Input (shared, event, 30 ms)
```

* **Pull model.** The CABLE Input output thread asks for about 10 ms of audio at a time, and
  `MixerEngine.Render` produces exactly that. The output device's clock drives everything, so
  latency is fixed.
* **Microphone** (`MicrophoneCaptureService`): shared-mode `WasapiCapture`, converted once to
  48 kHz stereo float. Nothing is processed except your volume setting. The mic is read even while
  muted, so its buffer stays at the target latency.
* **Clock drift** (`MicJitterBuffer`). The mic and VB-CABLE run on different crystals, so the mic
  buffer slowly fills or drains. The buffer is primed to 20 ms. While the backlog is above about
  40 ms it drops one frame per block, which is inaudible. Past 150 ms it jumps straight back to the
  target. When it runs dry it outputs silence and re-primes. Unit tests run the mic 1 % fast or slow
  for thousands of blocks and assert that latency stays bounded.
* **Clips** (`ClipVoice`). By default a new clip replaces the one playing, with a 30 ms fade;
  overlapping can be turned on. Clip edges get a 5 ms fade so a trimmed clip never clicks.
* **Ducking** (off by default) lowers the mic by a set number of dB while a clip plays. The gain
  ramps over 40 ms so the level change doesn't produce zipper noise.
* **Limiter** (`SoftLimiter`): stereo-linked, instant attack, 150 ms release, −1 dBFS ceiling. It
  does nothing at normal levels and prevents harsh clipping when mic and clip are loud together.
* **Device format.** VB-CABLE often defaults to 44.1 kHz. `VirtualOutputService` converts to the
  device's own mix format (WDL resampler, channel adapter), so Windows never rejects or silently
  converts the stream.
* **Recovery.** `MicrophoneCaptureService` and `VirtualOutputService` share `SupervisedEndpoint`, a
  2 s supervisor that reopens a stream after a fault, a settings change, its own device disappearing,
  or a change of the Windows default device it follows. Plugging in an unrelated USB device does not
  glitch the stream. If the mic is lost, the output keeps running with clips plus silence, so
  Discord's input never disappears.
* **Safety.** A virtual cable is never opened as the microphone, even if it is the Windows default
  recording device, because that would feed Echodeck's output back into itself.

### Your side in replays, and hearing your clips

* **Own audio in replays.** `MixerEngine.OutputTap` copies every block sent to Discord into a
  `TimedAudioRecorder`, a rolling buffer kept aligned with the clock like the Discord one. A gap is
  filled with silence at the moment audio resumes, not at the end. `ReplayService.CaptureLast`
  snapshots both buffers for the same window, both ending "now", and sums them with a limiter
  (`AudioMixdown.SumEndAligned`). The own-audio snapshot drops its last 30 ms, because that audio
  was rendered but hasn't reached the cable yet, which keeps your voice in time with your friends'.
  Muted mic means it isn't recorded. The setting is on by default.
* **Hearing your clips.** `HeadphoneClipMonitorService` plays a second, clips-only `MixerEngine` on
  the headphones device. Every clip sent to Discord is also queued there, without the mic. Clips
  are only queued while that stream is actually running, and are dropped when it stops, so
  replugging the headset never releases a burst of old clips. Each mix is also capped at 16 voices.
  This playback comes from Echodeck's own process, so per-process Discord capture never records it.

### Setup check (`AudioSetupMonitor` + `AudioSetupRules`)

Every 5 s, and soon after any device or settings change, Echodeck compares the actual routing with
the expected one. Discord's **real** input and output devices are found through its active audio
sessions. Problems are shown as a banner with the fix:

* Windows output set to a CABLE device (you hear nothing)
* VB-CABLE missing
* Echodeck's Discord output not a cable
* Echodeck's mic is a cable
* previews going into the cable
* Discord's output on the cable
* Discord's input not the cable

Nothing is changed automatically. The rules are platform-neutral and unit-tested.

### Clip editor

`ClipEditorWindow` and `WaveformView` work on an in-memory copy of the clip:

* drag the markers, drag across the waveform to make a new selection, or click to move the nearest marker
* keys: Space, Enter, Ctrl+S, Esc, and arrow nudges
* Save writes once, in place; the clip keeps its creation date
* rename, Save as copy, and Save + Play are also available

The same editor opens a fresh capture of the replay buffer (**Edit last 30 s…**). Phase 3 attaches
that to a hotkey.

### Single instance

A named mutex (`Local\Echodeck.SingleInstance`) allows one copy per Windows session. A second
launch signals a named event so the running window comes to the front, then exits. Two copies would
both send your mic into the cable.

## Avoiding feedback loops

The loop you described (soundboard → Discord → Discord output → replay buffer → soundboard) can't
happen with per-process capture:

1. Clips go **Echodeck → CABLE Input → Discord's microphone input**. Discord sends your microphone to
   friends but **never plays your own microphone back to you**, so a clip never appears in
   Discord.exe's output, which is what we capture.
2. Local previews and mic monitoring play from **Echodeck's own process**. Process loopback on the
   Discord tree never includes them.
3. Echodeck plays nothing through Discord.exe itself.

Remaining cases:

* **A friend on open speakers.** Their mic can pick up your clip and send it back to you. Discord's echo
  cancellation usually removes this. If it still happens, the worst case is that one replayed clip
  contains a faint copy of an earlier clip. It isn't a self-sustaining loop, because a replay is only
  ever triggered by your key press.
* **Device-loopback fallback modes.** If previews or monitoring play on the device being captured,
  they will be recorded. Phase 2 adds "pause replay recording during local preview" for these modes,
  and the UI already warns when a fallback mode is active.

## 5. Libraries

| Package | Why |
|---|---|
| **NAudio 2.2.1** | Mature WASAPI wrappers: capture, loopback and output, device enumeration, `IMMNotificationClient`, audio sessions, the WDL resampler and WAV reading. Used everywhere except process loopback. |
| *(own interop)* | Process loopback activation: about 150 lines in `Audio/Interop`. NAudio has no dependable public API for it. |
| **CommunityToolkit.Mvvm** | `[ObservableProperty]` and `[RelayCommand]` source generators keep view models short. |
| **Microsoft.Extensions.DependencyInjection / Logging** | Standard DI container and logging abstractions. The file logger is our own small, bounded one, so Serilog isn't needed. |
| **xUnit** | Unit tests. |
| Later: **H.NotifyIcon.Wpf** | Tray icon. WPF has none built in. |
| *(dotnet publish)* | Releases ship as one self-contained, single-file `Echodeck.exe` (no installer, no .NET install), built by `.github/workflows/release.yml`. **Velopack** can add an installer and auto-update later if wanted. |

The global hotkeys in Phase 3 use `RegisterHotKey` through P/Invoke, with no package. It is the
supported global hotkey API, it works while CS2 has focus, and unlike a low-level keyboard hook it
doesn't trigger anti-cheat concerns.

## 6. Limitations and technical risks

| Risk | Mitigation |
|---|---|
| Process loopback needs Windows 10 2004+ and isn't exercised by many apps. | Format negotiation with fallbacks, automatic fallback to device loopback, clear UI warning. |
| Discord changes its process layout (for example, audio moves to a service process with a different parent). | Whole-tree capture is robust to child changes. The locator logic is unit-tested and easy to adjust. |
| Discord's Krisp or echo cancellation might alter how a replayed clip sounds to friends. | User-side Discord setting. Documented in the test checklist. |
| VB-CABLE adds about 10–20 ms. The mixer adds about 20 ms. | Total stays well below Discord's own jitter buffer. |
| Clock drift between the mic and the cable | Drift compensation in the mixer (Phase 2). |
| `RegisterHotKey` fails if another app owns the combination. | Detected and reported per hotkey (Phase 3). |
| Anti-cheat (CS2 / VAC) | Echodeck injects nothing and uses no keyboard hooks, only standard audio APIs and `RegisterHotKey`. |
| Sleep/resume produces a huge timeline gap. | Silence inserts are capped, and the buffer simply ends up silent. |

## 7. Solution structure

```
Echodeck.sln
Directory.Build.props
src/
  Echodeck.Core/          Audio/  Discord/  Infrastructure/  Settings/
  Echodeck.Audio/         Capture/  Devices/  Diagnostics/  Discord/  Interop/  Playback/  Replay/
  Echodeck.App/           App.xaml  MainWindow.xaml  ViewModels/
tests/
  Echodeck.Core.Tests/    buffer, timeline, Discord locator, WAV, meter
docs/
  ARCHITECTURE.md  TESTING.md
```

Runtime data lives in `%AppData%\Echodeck\`:
`settings.json`, `clips\`, `clips.json` (from Phase 4), and `logs\echodeck*.log`.

## 8. Phases

| Phase | Scope | Status |
|---|---|---|
| 1 | Discord detection, per-process capture with fallbacks, 30 s rolling buffer, save last N s as WAV, local preview, logging and diagnostics | done |
| 2 | Mic capture, VB-CABLE output, mixer (gain, limiter, meters, optional ducking), play clip to Discord, setup warnings. Brought forward: waveform editor with trimming, rename/delete, single instance, app icon | **this commit** |
| 3 | `HotkeyService` (`RegisterHotKey`), global hotkeys for quick replay (3/5/10 s straight to Discord) and for opening the editor | next |
| 4 | Soundboard library (`clips.json`), per-clip hotkeys, categories, search, sort, favourites | |
| 5 | Navigation UI, tray, start minimised or with Windows, Setup page, device-recovery polish, optional installer/auto-update (single-file exe releases already exist) | |
