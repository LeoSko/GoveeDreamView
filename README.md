# GoveeDreamView

The Govee desktop app has a DreamView mode that watches your screen and mirrors the
colours onto a strip behind the monitor. Nice effect, heavy app: it wants an account
and a background client for what turns out to be a few UDP packets. I reverse-engineered
the packets and put them in a ~100 KB tray exe. No login, no cloud, nothing to install.

Run it and the edges of your screen land on the strip.

## Screenshots

Tray icon: a colour wheel around a screen that tints to your dominant on-screen colour
while streaming, grey when stopped.

![tray icon](docs/tray.png)

The screen is split into ten edge zones, clockwise from top-left, one per strip segment.

![zone mapping](docs/zones.png)

## How it works

DreamView is the "razer" command of Govee's LAN API, all UDP on your network. You
discover the strip by multicast, switch it into stream mode, then fire colour packets
at `<device>:4003`. Each one is a raw byte frame, base64'd into the payload:

```
BB 00 <len> B0 <stretch> <N> <R G B> x N <XOR-checksum>
 |  |    |   |     |       |   \___ N colour triples ___/   \_ XOR of all prior bytes
 |  |    |   |     |       └─ segment/zone count
 |  |    |   |     └─ 0x00 = per-segment, 0x01 = stretch across all LEDs
 |  |    |   └─ 0xB0 constant
 |  |    └─ payload length = 2 + 3*N
 └──┴─ 0xBB 0x00 constant
```

Watch that `<len>` byte. Most of the reference code out there hard-codes it as a magic
"header variant" like `0x20`. It isn't. It's the payload length, and the `0x20` everyone
copies is just `2 + 3*10`, because Govee streams ten segments. Get it wrong and the strip
drops the packet without a word, which cost me an evening.

The loop itself is deliberately boring: grab the framebuffer, box-average it down to ten
edge colours, apply gamma and saturation, send. A GDI StretchBlt does the downscale so
there's no full-frame copy per tick, and a moving average smooths the flicker. The strip
is found by MAC and re-resolved to its current IP on every start, so a new DHCP lease
can't lose it.

Protocol reverse-engineering credit: [LedFx](https://github.com/LedFx/LedFx),
[egold555](https://github.com/egold555/Govee-Reverse-Engineering) and
[wez/govee2mqtt](https://github.com/wez/govee2mqtt).

## Install

Windows 10 or 11, with the built-in .NET Framework 4.x. No admin. Grab
`GoveeDreamView.exe` from the [latest release](../../releases/latest), or run `build.cmd`.
Double-click it and streaming starts on its own, finding the strip on first launch and
remembering it after.

One thing on the Govee side: the strip needs **LAN Control** switched on (Govee Home app,
your device, settings). Without it nothing on your network can reach it.

## Tray menu

- **Start / Stop**, or double-click the icon.
- **Settings** tunes brightness, gamma and saturation live. Once brightness is maxed,
  gamma is the knob that actually changes how bright it looks: higher means deeper blacks
  but dimmer mid-tones. It also holds **Start with Windows** and the two Govee Desktop
  switches below.
- **Kill Govee Desktop** clears the way when Govee's own app has the strip. Both stream
  the same protocol, so together they make it blink; by default this app steps aside and
  skips auto-start while GoveeDesktop.exe runs.
- **Calibrate colours** points your phone camera at the wall behind the strip and solves
  a colour matrix so the reflected light matches the screen. Never leaves your LAN.
- Plus Reload/Edit config and Rediscover device.

Lock Windows and the strip goes off; unlock and it comes back. Everything gets saved to a
`config.ini` next to the exe, each key commented.

## Files

| File | What's in it |
|------|--------------|
| `Program.cs` | Tray app: menu, streaming loop, animated icon, lock handling |
| `GoveeClient.cs` | The Govee LAN protocol: discover, power, stream, razer packets |
| `Net.cs` | MAC-to-IP via an ARP sweep, so DHCP doesn't lose the strip |
| `ScreenCapture.cs` | GDI StretchBlt capture and per-zone averaging |
| `Config.cs` | The flat `key=value` config |
| `Calibration.cs`, `CameraServer.cs` | The phone-camera colour calibration |

## Releases

Push a `v*` tag and GitHub Actions builds the exe and attaches it to a release:

```bash
git tag v1.0.0 && git push origin v1.0.0
```
