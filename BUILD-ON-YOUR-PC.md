# Build Sound Connect on your Fujitsu (Route B)

You only do this once. At the end you get an `.appx` file to install on
the Lumia through Device Portal — the same way as VLC.

## Step 1 — Install Visual Studio (one time, ~20–40 min on download)

1. Download **Visual Studio Community 2022** (free) from Microsoft:
   https://visualstudio.microsoft.com/downloads/
2. Run the installer. On the **Workloads** screen, tick:
   **Universal Windows Platform development**
3. On the right side (Installation details), make sure these are ticked:
   - **Windows 10 SDK (10.0.19041.0)**
   - **C# and Visual Basic** language support (under the UWP workload)
4. Click Install. Restart the PC if it asks.

## Step 2 — Open the project

1. Unzip `SoundConnect-Lumia-v1-source.zip`.
2. Double-click **SoundConnect.sln**.
3. At the top of Visual Studio set:
   - Configuration: **Release**
   - Platform: **ARM**

## Step 3 — Create the install package

1. Menu: **Project → Publish → Create App Packages…**
2. Choose **Sideloading** → Next.
3. Signing: choose **Create…** a test certificate (publisher name can be
   `Vishal`) — or pick an existing one. → Next.
4. **Bundle: choose "Always"** when the wizard asks (the project is
   already set this way). Version: leave 1.0.0.0. Architecture: tick
   **ARM** only. → Create.
5. Wait for "Package creation finished". Click the output folder link.

Inside the folder you will find:
- `SoundConnect_1.0.0.0_ARM.appxbundle` — ONE file with the app and all
  its helper (dependency) packages inside it
- a **`.cer`** certificate file next to it

## Step 4 — Install on the Lumia (2 taps, from Downloads)

1. Phone: **Settings → Update & security → For developers → Developer Mode ON**
   (already done on your phone).
2. Copy the **`.cer`** and the **`.appxbundle`** to the phone's
   **Downloads** folder (USB).
3. File Explorer → Downloads → tap the **`.cer`** → **Install**.
4. Tap the **`.appxbundle`** → **Install**. Done — "Sound Connect"
   appears in the app list with your logo.

> No dependency files to add one by one: the bundle carries them
> inside, so the **0x80073CF3** error cannot happen with this file.

## Step 5 — First test

1. Pair the XM4 with the Lumia in Settings → Bluetooth (for music as usual).
2. Make sure Sony's app / xm4mini on other devices is NOT connected to the XM4.
3. Open **Sound Connect** → pick **WH-1000XM4** → **Connect**.
4. Battery %, codec and firmware should appear. Try Noise Cancelling →
   Ambient → Off, then an EQ preset. Send a photo of the Log section if
   anything fails — the log lines say exactly which command failed.
