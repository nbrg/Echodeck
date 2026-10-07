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

## Phase 2 checklist: mic + clips into Discord

Preparation: VB-CABLE installed. Windows output and Discord output on your headset. Discord input on
**CABLE Output**, input mode Voice Activity. Echodeck's Audio tab: your mic, "Auto — CABLE Input".

| # | Step | Expected |
|---|---|---|
| 2.1 | Start Echodeck. | All three status cards say **Active**. No orange or red banner. |
| 2.2 | Set Windows output to CABLE Input for a moment. | Within about 5 s a banner says you won't hear anything. Switch back and it disappears. |
| 2.3 | Set Discord's input back to your real mic and talk. | A banner says friends won't hear clips. Set it back to CABLE Output. |
| 2.4 | Talk. | The Microphone and Discord output meters move. Discord's input meter (Voice & Video → Mic Test) moves. Friends hear you, unchanged and without echo. |
| 2.5 | Click **▶ Discord** on a saved clip while talking. | Friends hear the clip **and** your voice; your mic never cuts out. You don't hear yourself. |
| 2.6 | Click **▶ Replay last 5 s to Discord** right after a friend speaks. | Friends hear their own sentence. |
| 2.7 | Straight after 2.6, click **💾 Save last 10 s** and preview it. | Contains friends only. Your replay from 2.6 isn't in it, so there is no feedback loop. |
| 2.8 | Play two clips quickly. | The second replaces the first. With "Let clips overlap" on, both play. |
| 2.9 | Turn on ducking at −8 dB and play a clip while talking. | Your voice dips while the clip plays, then returns. |
| 2.10 | Tick **Mute**. | Friends stop hearing you; clips still play. The card says MUTED. |
| 2.11 | Unplug the headset, wait, then plug it back in. | Microphone shows Inactive, then Active again by itself. Discord never loses its input. |
| 2.12 | **✂ Edit last 30 s…**: drag the markers, use Space to preview, Enter to play, Ctrl+S to save. | The playhead moves during preview. Enter plays only the selection into Discord. The saved file has the selected length. |
| 2.13 | Edit a saved clip, rename it in the editor and save. | The list shows the new name and length, in the same position. |
| 2.14 | Press F2 / Del on a clip, or use the Rename / 🗑 buttons. | Rename and delete work, delete asks for confirmation, and the files change in the clips folder. |
| 2.15 | Start Echodeck a second time. | The existing window comes to the front and no second copy runs (check Task Manager). |
| 2.16 | Check the taskbar and Alt+Tab. | The Echodeck icon is shown, not the default window icon. |
| 2.17a | Talk at the same time as a friend, then **💾 Save last 5 s** and preview it. | Both voices are in the clip, in sync. With "Include my side in replays" off, only the friend is in it. |
| 2.17b | **▶ Replay last 5 s to Discord**. | You hear the replay in your headphones and friends hear it in Discord. With "Hear clips… in my headphones" off, only friends hear it. You never hear your own live mic. |
| 2.17c | Unplug the headset, play two clips to Discord, then plug it back in. | No burst of old clips when the headset returns. |
| 2.17 | Talk for 2 hours, then **Copy diagnostics**. | The "Mic buffer" line shows a few underruns or drift corrections at most, with no growing latency. Memory is flat. |

## Phases 3–5 checklist: hotkeys, soundboard, tray, phone

| # | Step | Expected |
|---|---|---|
| 3.1 | Start CS2 (fullscreen and borderless), a friend talks, press **F8**. | Friends hear the last 5 s, CS2 stays focused, and you hear it in your headset. |
| 3.2 | In CS2, press **F9**. | The editor opens on top with the whole buffer. Drag to trim, **Enter** plays, **Ctrl+S** saves, **Esc** closes. |
| 3.3 | Hotkeys tab: set "Replay last 10 s" to F8. | Both rows show the conflict, and only the first one fires. |
| 3.4 | Set a hotkey that another app already uses (e.g. one Discord's keybinds own). | The row says it's taken by another program. |
| 3.5 | Try to set plain `A`. | Rejected: letters need Ctrl or Alt. |
| 3.6 | Mash F8 ten times quickly. | No crash or pile-up. A new replay replaces the playing one unless overlap is on. |
| 4.1 | Soundboard: give a clip Ctrl+NumPad1, a category and ★. Restart Echodeck. | All three are kept, and Ctrl+NumPad1 plays the clip in-game. |
| 4.2 | Import an MP3. | It shows up with the right length and plays into Discord. |
| 4.3a | Delete a clip in each of four ways: the row 🗑 button (Soundboard and Replay tabs), right-click → Delete, the **Del** key, and the details panel. | Each asks for confirmation once, then the clip disappears from both tabs, the clips folder and the phone. |
| 4.3b | Ctrl/Shift-click several clips (in either tab), then press Del. | One confirmation lists them; all are deleted. |
| 4.3 | Duplicate, trim, rename and delete clips. | The list, the files in the clips folder and the hotkeys all stay in sync. |
| 4.4 | Copy 300 WAVs into the clips folder and restart. | All are adopted; search, filter and scrolling stay instant. |
| 4.5 | Set a clip's volume to 50 %. | It plays quieter, both in Discord and in your headset. |
| 5.1 | Minimise. | Goes to the tray and shows a one-time balloon. Hotkeys still work. Double-click the icon restores it. |
| 5.2 | Tray menu: Mute, Record replay buffer, Exit. | The checkmarks match the app. Exit really quits (check Task Manager). |
| 5.3 | Settings: start with Windows plus start minimised, then reboot. | Echodeck starts in the tray. |
| 5.4 | Settings: untick "Record the replay buffer". | Capture shows **Paused**. Ticking it again resumes. |
| P.1 | Phone tab: enable, allow the firewall prompt, scan the QR code. | The page shows your clips. Tapping one plays it into Discord. Replay, Stop and Mute work. |
| P.2 | Add to Home Screen on the iPad. | Opens full-screen and stays paired. |
| P.3 | Click "New pairing code". | The old phone shows "Not paired" until it re-scans. |
| P.4 | Open the link without the `#t=` part on another device. | It shows "Not paired" and can't trigger anything. |

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
