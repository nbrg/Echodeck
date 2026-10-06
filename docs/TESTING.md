# Testing

## Automated

```
dotnet test
```

Unit tests cover the platform-neutral core:

* the rolling buffer: wrap-around, bounded memory, resize, concurrent write/snapshot
* timeline gap filling
* Discord root-process selection
* the WAV writer
* the peak meter

They run on any OS. CI builds the whole solution on `windows-latest`.

WASAPI behaviour can only be checked on a real Windows machine with Discord. Use the checklists
below. **Copy diagnostics** (bottom right of the window) gives you everything needed to report a
failure.

## Phase 1 checklist: "friend speaks → saved WAV"

Preparation: Windows 10 2004+ or Windows 11, Discord desktop app in a voice channel with at least one
friend (a second account on a phone works too).

| # | Step | Expected |
|---|---|---|
| 1.1 | Start Echodeck **before** Discord. | Status shows **Waiting**, Discord shows "Not detected". |
| 1.2 | Start Discord. | Within about 2 s: Discord shows "Discord (PID n)", Status shows **Active**, the summary says "Capturing Discord only (per-process)". |
| 1.3 | Friend talks. | The "Discord incoming" meter moves. |
| 1.4 | Play Spotify, YouTube and a CS2 match on the same headphones; friend stays silent. | **The meter stays flat.** |
| 1.5 | Friend says a sentence; within 2 s click **Save last 5 seconds**. | A new row appears. ▶ Preview plays the friend's voice and none of the step 1.4 audio. |
| 1.6 | Wait 20 s in silence, then save 5 s. | The clip is 5.0 s of silence, not older speech. This checks timeline gap filling. |
| 1.7 | Open the clips folder. | `Replay yyyy-MM-dd HH-mm-ss.wav`, 48 kHz 16-bit stereo, plays in any media player. No `.tmp` files. |
| 1.8 | Fully quit Discord (tray → Quit), wait, then restart it and rejoin voice. | Status goes Waiting → Active with a new PID, with no Echodeck restart. Saving still works. |
| 1.9 | Change Discord's output device (Settings → Voice & Video). | Capture continues uninterrupted, because process loopback is device-independent. |
| 1.10 | Unplug and replug the USB headset. | No crash. Capture continues, or recovers within a few seconds. |
| 1.11 | Change the buffer length to 10 s, then save 30 s. | The clip is at most 10 s. |
| 1.12 | Force each capture method in Settings. | Each one reaches **Active**. Device modes show the orange "may include other apps" warning. |
| 1.13 | Leave it running for 2 h while gaming. Watch the "Working set" line in Copy diagnostics, or Task Manager. | Memory is flat after the first minute: about 23 MB for a 30 s buffer plus runtime. CPU is about 0–1 %. The logs folder stays at 5 MB or less. |
| 1.14 | Press Save repeatedly (10× fast). | Ten distinct files, no errors, UI stays responsive. |

If step 1.2 shows a device-loopback method instead of per-process, copy the diagnostics and look at the
"Capture method ProcessLoopback failed" log line. The HRESULT in that line identifies the cause.

## Full routing checklist (Phases 2–5)

| # | Scenario | Pass criteria |
|---|---|---|
| 1 | Talk normally | Friends hear you, unchanged. The Discord input meter (CABLE Output) moves. |
| 2 | Play a clip while talking | Friends hear the clip **and** your voice, with no mic dropout. |
| 3 | Capture a friend and replay | Friends hear their own sentence back. |
| 4 | CS2 running | CS2 audio is absent from replay clips. |
| 5 | Spotify or browser playing | Absent from replay clips. |
| 6 | Play a clip, then immediately replay the last 10 s | The new clip contains only friends, not your clip (no feedback loop). |
| 7 | 2 h session | Memory and latency are flat, no growth in `%AppData%\Echodeck`. |
| 8 | Restart Discord with the app open | Capture recovers automatically and the virtual mic stays working. |
| 9 | Disconnect and reconnect the USB headset | Mic and preview recover, with a clear message while they are missing. |
| 10 | Change Discord's output device | Capture continues. |
| 11 | Hotkeys with CS2 fullscreen or borderless | Every hotkey fires and nothing alt-tabs. |
| 12 | Mash several hotkeys quickly | No overlapping clips unless overlap is enabled, and no crash. |
| 13 | Library with 500 clips | Search and list stay instant. Startup takes less than 1 s extra. |
