# Virtual audio routing

## How GameDeck uses a virtual cable

A virtual cable is a driver that exposes two endpoints: a **render** device ("CABLE Input") and a **capture** device ("CABLE Output"). Audio written to the render side comes out of the capture side.

```
sounds ──┬─► Headphones bus ──► your headphones            (what you hear)
         └─► Virtual bus ─────► "CABLE Input"  ═ driver ═► "CABLE Output" ──► Discord / TeamSpeak mic
mic ───────► Virtual bus only   (never the headphones bus, so there is no self-feedback)
```

The engine only sees WASAPI endpoints. It does not know or care which driver backs the virtual bus, so VB-CABLE, another cable, or a future GameDeck driver can be swapped in without code changes.

Built-in guards:
- The virtual output cannot be the same device as the headphones.
- If the selected microphone looks like the capture side of the same virtual cable (same adapter name, and the name looks virtual), mic mixing is refused, because it would loop the output back into itself.

## Initial driver: VB-CABLE (evaluation)

- Vendor: VB-Audio Software, <https://vb-audio.com/Cable/>.
- Licensing: **donationware**. Free to use; the vendor asks for payment if you find it useful or use it professionally (<https://vb-audio.com/Services/licensing.htm>). Distribution or integration deals need an agreement with VB-Audio (see their shop's general terms, section 3.4).
- **Decision: GameDeck does not bundle or redistribute VB-CABLE.** Users install it themselves from the vendor's site. Revisit only with a written agreement from VB-Audio.
- Setup:
  1. Install VB-CABLE from vb-audio.com (needs admin and a reboot).
  2. GameDeck → Audio Mixer → **Virtual mic** → choose **CABLE Input**.
  3. Discord → Settings → Voice & Video → Input device → **CABLE Output**. TeamSpeak → Options → Capture → **CABLE Output**.
  4. For your voice to reach the call through GameDeck, choose your physical mic in GameDeck's **Microphone** strip and turn on mixing (Experimental).
  5. Turn off the voice app's noise suppression and echo cancellation if they swallow short sound effects.

Other options, not chosen: Voicemeeter (same vendor, heavier), and Virtual Audio Cable by Eugene Muzychenko (commercial). **Steam Streaming Microphone is a loopback, but unusable as a voice cable.** Measured on 2026-10-28: audio rendered to "Speakers (Steam Streaming Microphone)" does arrive on "Microphone (Steam Streaming Microphone)", continuously, after about 430 ms. But a 1000 Hz tone arrives as **~459 Hz** (= 1000 × 44100/48000 ÷ 2): the driver copies between a 48 kHz render format and a 44.1 kHz capture format without conversion. Voices would sound slowed and deep. GameDeck's *Test routing* detects this and reports it as "pitch-shifted" with the fix. (An earlier note here said this pair was "not a loopback"; that was a measurement bug in the first route test, since fixed.)

### Validation status

**Verified on real hardware (2026-10-28).** Test PC: a USB microphone. The virtual output was "Speakers (Steam Streaming Microphone)", recorded via WASAPI loopback, which is exactly the signal a cable would carry. `tools/GameDeck.HardwareCheck`, 15/15 checks, two runs:

| Check | Result |
| --- | --- |
| GameDeck output reaches the virtual endpoint | test tone at -12.0 dBFS (expected -12.04), ~43 ms |
| Mic continuous with no soundboard audio, 120 s | 0 silent 10 ms blocks, 0 underruns, 0 drops |
| Virtual output carries the mic | envelope correlation r = 1.00 |
| Mic under a playing sound | relative level change +0.2 dB (not muted or ducked) |
| Mic gain 50% | mic -6.0 dB, sound 0.0 dB |
| Sounds into the call 50% | sound -6.0 dB, mic 0.0 dB |
| Headphone volume 20% | call unchanged (0.0 dB / -0.1 dB) |
| Mic in the headphone bus | -180 dBFS (digital silence) |
| Sounds in the headphone bus | once, at the expected level; nothing else above -99 dBFS |
| Guards | headphones = virtual refused; same-adapter mic refused; missing device gives actionable error |
| Through a real third-party loopback driver (Steam pair) | mic continuous for 60 s with 0 gaps/underruns/drops (r = 0.96); route test correctly diagnoses its pitch corruption (1000 → 459 Hz). Mixing levels can't be measured through it because it corrupts the signal |

**Still requires manual validation** (VB-CABLE isn't installed on this PC, and Discord's settings are the user's): see [DISCORD_TEST.md](DISCORD_TEST.md).

- [ ] VB-CABLE: *Test routing* passes (tone arrives on CABLE Output)
- [ ] Discord mic test: voice and sounds, independent volumes, no dropouts
- [ ] Real call heard by a second account, in Discord and in TeamSpeak
- [ ] 2+ hours while gaming without dropouts; check the mic underrun count afterwards
- [ ] Physically unplug and replug the mic and headphones during a session

## Own virtual microphone driver: research (no implementation)

**Starting point.** Microsoft's **SysVAD** sample (<https://learn.microsoft.com/en-us/samples/microsoft/windows-driver-samples/sysvad-virtual-audio-device-driver-sample/>) is a WDM/PortCls (WaveRT) audio driver that exposes several virtual endpoints. A GameDeck cable would turn it into one render and one capture endpoint that share a ring buffer. Microsoft also documents **ACX** (Audio Class eXtensions, KMDF-based) as the newer framework for Windows 11 audio drivers, with its own samples. Evaluate ACX before committing to the PortCls design.

What it would take:
1. **Tooling:** Visual Studio with the Windows Driver Kit (WDK) and SDK matching the targeted Windows versions.
2. **Engineering:** a render pin that writes into a lock-free ring buffer, a capture pin that reads it, format negotiation (48 kHz float/16-bit), clock and timestamp handling, power management, a minimal INF, and no glitches under load.
3. **Testing:** on a **dedicated test machine or VM, never the gaming PC.** Test-signed drivers need `bcdedit /set TESTSIGNING ON` and a test certificate, and Microsoft's sample docs note BitLocker/Secure Boot may need suspending first. Run Driver Verifier, the HLK audio tests, sleep/resume, and hot-plug of other devices.
4. **Compatibility:** must work with Memory Integrity (HVCI), which is on by default on many Windows 11 PCs. Check behaviour alongside kernel anti-cheat (Vanguard, EAC, BattlEye), which can block drivers that aren't properly signed.
5. **Signing for real users:** an EV code-signing certificate and a Microsoft Partner Center (Hardware Dev Center) account, then attestation signing (Windows 10/11 client) or full WHQL/HLK certification for broader distribution. *Check the current Partner Center rules before planning; requirements change.*
6. **Licence:** confirm the Windows-driver-samples repository licence permits shipping derived code.
7. **Maintenance:** driver updates through an installer, Windows feature updates, and supporting users who can't install drivers (managed PCs).

**Gate:** per the project brief, no driver work starts until the app and VB-CABLE routing pass the validation checklist above. No experimental kernel drivers get installed on this PC, and no Windows security features get disabled here.
