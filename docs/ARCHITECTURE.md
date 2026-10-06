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

## 4. Mixing microphone and soundboard into Discord (Phase 2)

* **Microphone:** `WasapiCapture` in shared, event-driven mode with 10 ms buffers, converted to 48 kHz
  stereo float and written into a small SPSC ring buffer.
* **Output:** `WasapiOut` on CABLE Input, shared and event-driven, with about 20 ms latency. Its render
  callback **pulls** from `MixerSampleProvider`:
  * mic samples from the ring buffer, then mic gain
  * active clip voices, then soundboard gain. One voice at a time by default: a new clip replaces the
    current one. Overlap can be turned on.
  * optional ducking, which lowers the mic while a clip plays (off by default)
  * a soft limiter on the sum, so mic and clip together never clip
  * peak meters for the mic and for Discord outgoing
* **Clock drift.** The mic and VB-CABLE run on different clocks, so the mic ring buffer slowly fills
  or drains. The mixer aims for about 20 ms of fill and drops or repeats single frames when it
  drifts past that window. Without this, latency would creep up over hours.
* If the mic is lost, the mixer outputs clips plus silence and the device service reopens the mic. The
  output to Discord never stops, so Discord never sees the virtual mic disappear.
* Your voice is untouched: no processing apart from your own gain setting, which defaults to 1.0.

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
| Later: **Velopack** (or WiX) | Installer and auto-update. |

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
| 1 | Discord detection, per-process capture with fallbacks, 30 s rolling buffer, save last N s as WAV, local preview, logging and diagnostics | **this commit** |
| 2 | Mic capture, VB-CABLE output, mixer (gain, limiter, meters, optional ducking), play clip to Discord | next |
| 3 | `HotkeyService` (`RegisterHotKey`), quick replay actions (3/5/10 s straight to Discord), replay editor with waveform, markers and keyboard control | |
| 4 | Soundboard library (`clips.json`), per-clip hotkeys, categories, search, sort, favourites | |
| 5 | Navigation UI, tray, start minimised or with Windows, Setup page, device-recovery polish, installer | |
