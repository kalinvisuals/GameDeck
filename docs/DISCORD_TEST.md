# End-to-end test: microphone + soundboard → Discord / TeamSpeak

This is the procedure that marks the voice-chat integration as **working**. Until someone completes it on a real PC with a real cable, treat the integration as unvalidated.

## The routing being tested

```
Your mic ───────────► GameDeck mic input ──(mic gain)──────────────┐
                                                                   ├─► mixer + limiter ─► "CABLE Input"  (render)
soundboard sounds ──(per-button volume)─► sound bus ─(sounds→call)─┘                             │  VB-CABLE driver
                  └─(per-button volume)─► headphone bus ─(sounds in headphones)─► your headphones ▼
                                                                    Discord input = "CABLE Output" (capture)
```

- Your voice only goes to the call, never to your headphones, so you never hear yourself through GameDeck.
- Sounds go to both: you hear them once in your headphones, and the call hears them once through the cable.
- The three volumes are independent: mic gain, sounds into the call, sounds in headphones.
- **Discord must NOT also use your physical mic.** Its input is the cable; GameDeck delivers your voice through it.

## 1. One-time setup

1. Install **VB-CABLE** from <https://vb-audio.com/Cable/> (run the installer as administrator), then **restart Windows**.
2. Check that Windows shows **CABLE Input** under Playback and **CABLE Output** under Recording (Win+R → `mmsys.cpl`). Both must be *enabled*.
3. Don't make CABLE Input your Windows default output. If it is, GameDeck refuses to use "Windows default" as your headphones and says why.
4. GameDeck: right-click the tray icon → **Audio setup (Discord / TeamSpeak)…**
   - Virtual mic output: **CABLE Input (VB-Audio Virtual Cable)**
   - Microphone: your physical mic; tick **Mix my microphone into the virtual mic**
   - Headphones: your real headphones
   - Every check should be green or grey. Click **Test routing**: it should report the tone arriving at "CABLE Output" with **correct pitch** and latency in milliseconds. *(Not during a call.)*
   - If it reports **pitch-shifted**, the cable's two sides use different formats. In `mmsys.cpl`, open CABLE Input (Playback) and CABLE Output (Recording) → Properties → Advanced, set both to the same rate (e.g. 48000 Hz), then test again.

## 2. Discord microphone test

1. Discord → User Settings → **Voice & Video**.
2. **Input Device: CABLE Output (VB-Audio Virtual Cable)**. **Output Device:** your headphones.
3. Turn **off**, for now: Noise Suppression (Krisp), Echo Cancellation, Automatic Gain Control, and *Automatically determine input sensitivity*. Krisp in particular removes music and sound effects. Set Input Sensitivity low (left) or use Push-to-Talk.
4. Under **Mic Test**, click **Let's Check**. Discord plays back what it receives, into your headphones.

| # | Do this | Expect | Pass? |
| - | --- | --- | --- |
| 1 | Talk normally, no sounds playing | You hear your voice in Discord's test playback, clear, with no dropouts or robot noise. The input meter moves. | |
| 2 | Keep talking for 60 s | No clicks, gaps or rising delay. | |
| 3 | Tap a soundboard button while talking | You hear the sound **twice**: once from GameDeck right away, once slightly later from Discord's playback. Your voice continues under the sound, not muted or cut. | |
| 4 | GameDeck mixer: drag *Mic gain* down | Only your voice gets quieter in the test playback. | |
| 5 | Drag *Sounds into the call* down, tap a sound | Only the sound gets quieter in the test playback; your headphone copy is unchanged. | |
| 6 | Drag *Sounds in headphones* down, tap a sound | Only your immediate (GameDeck) copy gets quieter; the Discord playback copy is unchanged. | |
| 7 | Stop talking, no sounds | Silence in the test playback. No hum, no echo of the soundboard. | |
| 8 | Stop All while a long sound plays | The sound stops immediately in both copies. | |

The double sound in step 3 only happens during Discord's mic test, because Discord is playing your input back to you. In a real call you hear the sound once.

## 3. Real call (second account, or a friend)

1. Join a voice channel with a second device logged into another account (the phone app works).
2. Talk, then play sounds while talking. On the second device: voice and sounds are both clear, sounds don't cut your voice, and loud sounds don't distort. If they do, lower *Sounds into the call*.
3. Play for 15–30 minutes while gaming. Then open GameDeck → Audio setup → Checks. It shows how many mic buffer underruns/drops occurred. A few over 30 minutes is clock drift and acceptable; dozens means raise the output buffer to 60 ms.

## 4. TeamSpeak

Options → **Capture** → Device: **CABLE Output**. Use *Voice Activation Detection* with a low threshold or Push-to-Talk, and turn off *Remove background noise* and *Echo reduction*. Click **Begin Test**: you hear your voice and sounds just as in the Discord table.

## 5. Unplug / recovery (manual)

| Do this | Expect |
| --- | --- |
| Unplug the USB mic mid-test | GameDeck shows *Microphone '…' is unplugged*; sounds still reach Discord. Plug it back in: the voice resumes within about 3 s without restarting anything. |
| Unplug the headphones | Headphone bus shows an error; the call still gets sounds and voice. Reconnect: it resumes. |
| Disable CABLE Output in `mmsys.cpl` | Audio setup shows *recording side 'CABLE Output' is disabled* with the fix. |
| Uninstall/disable CABLE Input | *Virtual mic output … is disabled / not present* with the fix. |

## If something fails

- **Discord hears nothing:** Discord's input isn't CABLE Output, or Krisp/auto sensitivity is gating the signal. Run *Test routing* in GameDeck: if it passes, the problem is on Discord's side.
- **Discord hears only sounds, no voice:** mic mixing is off or set to the wrong mic. The Checks list says which.
- **You hear yourself:** something other than GameDeck is doing it. Windows "Listen to this device" on your mic or the cable (`mmsys.cpl` → Recording → Properties → Listen), or Discord's mic test playback.
- **Echo of the soundboard in the call:** your headphones are speakers and your mic hears them. Use headphones, or keep Discord's echo cancellation on (it may also soften sounds).

## Automated real-hardware check (no Discord needed)

`tools/GameDeck.HardwareCheck` runs the same mixing checks against real devices, numerically:

```powershell
dotnet run --project tools/GameDeck.HardwareCheck -- devices
dotnet run --project tools/GameDeck.HardwareCheck -- check --virtual "CABLE Input" --capture "CABLE Output" `
    --mic "Microphone (USB" --monitor "<an output you can't hear>" --seconds 60
```

It records the cable's recording side (what Discord reads), checks continuity, mic-under-sound and the independence of each volume, and checks that the mic never reaches the headphone bus. Microphone audio is analysed in memory only.
