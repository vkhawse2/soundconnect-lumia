# Sound Connect — for Windows 10 Mobile

An unofficial controller for the **Sony WH-1000XM4** running on a
**Lumia 540 (Windows 10 Mobile, build 14393)** — the app Sony never made
for this phone.

Name and logo as chosen by the owner (personal, sideloaded build).

## What it does

- Battery percentage + charging state, current codec (SBC/AAC/LDAC/aptX), firmware version
- Noise cancelling / Ambient sound (levels 1–20) / Off, Focus on Voice
- Equalizer: Sony presets (Off, Bright, Excited, Mellow, Relaxed, Vocal,
  Treble Boost, Bass Boost, Speech) + custom 5-band EQ (400 Hz, 1 kHz,
  2.5 kHz, 6.3 kHz, 16 kHz) + Clear Bass
- DSEE on/off, Speak-to-Chat on/off
- Auto power off (the two values the XM4 honors: *Do not turn off* /
  *When taken off*)
- Power off headphones

## How it works

The XM4 takes settings over a Bluetooth **RFCOMM** data channel —
Sony Protocol V1, service UUID `96CC203E-50F8-4944-9C22-EDCB48F6216F`.
Windows 10 Mobile apps are allowed to use RFCOMM (unlike the audio
profiles, which is why no app can change Bluetooth codecs on this phone).

The protocol code in `SoundConnect/Sony/` is a C# port of our own
xm4mini protocol library — itself built on the open SonyHeadphonesClient
research — with byte layouts verified against a WH-1000XM4 on
firmware 3.0.1. Only APIs present in build 14393 are used
(`BluetoothDevice.GetRfcommServicesAsync`, `StreamSocket`).

**One controller at a time:** the headphones accept a single control
connection. Close Sound Connect / xm4mini / Sony's app on other devices
before connecting here, or the connection will fail.

## Building

A Windows machine is required (UWP ARM packages cannot be built on Linux).

**Route A — GitHub Actions (no installs):** push this repo to GitHub,
open the *Actions* tab, run **Build Sound Connect (Windows 10 Mobile
ARM)**. Download the `SoundConnect-W10M` artifact: one **`.appxbundle`**
plus `SoundConnect.cer`.

**Route B — Visual Studio on your PC:** see `BUILD-ON-YOUR-PC.md`
(in the wizard, leave the bundle choice on **Always**).

## Installing on the phone — the bundle way (see INSTALL.txt)

The build produces an **`.appxbundle`**: the app *and all its dependency
packages inside one file*. No dependency juggling, no 0x80073CF3.

1. Developer Mode ON (Settings → Update & security → For developers).
2. Copy **`.cer`** + **`.appxbundle`** to the phone's **Downloads** folder.
3. File Explorer → tap the **`.cer`** → Install.
4. Tap the **`.appxbundle`** → Install. Done.
5. Pair the XM4 in phone Settings (for music), open Sound Connect,
   pick the headphones, tap **Connect**.

## Not included (by design)

- Firmware updates and Sony account features — those live on Sony's
  servers, not in the headphones.
- Codec switching — impossible for any app on Windows 10 Mobile
  (audio profiles are system-only).
