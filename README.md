# GoveeDreamView

The Govee desktop app has a "DreamView" mode that watches your screen and pushes
the colours onto a light strip behind the monitor. It's a nice effect. The app
around it is less nice: a heavy desktop client that wants an account and sits in
the background doing who-knows-what. I only ever used the screen-sync, so I pulled
apart the LAN protocol and wrote my own. This is the whole thing in a ~100 KB tray
exe that talks straight to the strip over UDP. No login, no cloud, nothing to install.

Run it and the edges of your screen show up on the strip. The tray icon doubles as
a colour meter so you can tell at a glance that it's alive.

## Screenshots

The tray icon is a little colour wheel around a screen. While streaming it tints to
whatever colour is dominant on your monitor; stopped, it goes grey:

![tray icon](docs/tray.png)

The screen gets carved into ten edge zones, walked clockwise from the top-left, and
each zone drives one DreamView segment:

![zone mapping](docs/zones.png)

## How it works

DreamView is just the "razer" command of Govee's LAN API. It's all UDP on your
local network, no internet hop:

| Step | Transport | Payload |
|------|-----------|---------|
| Discover | multicast `239.255.255.250:4001`, reply on `:4002` | `{"cmd":"scan"}`, reply has `ip`, `sku` |
| Power / brightness | UDP `<device>:4003` | `{"cmd":"turn","value":1}`, `{"cmd":"brightness","value":100}` |
| Enable stream | UDP `:4003` | `{"cmd":"razer","pt":"uwABsQEK"}` (`BB 00 01 B1 01 0A`) |
| Stream colours | UDP `:4003` | `{"cmd":"razer","pt":"<base64 packet>"}` |
| Disable stream | UDP `:4003` | `{"cmd":"razer","pt":"uwABsQAL"}` (`BB 00 01 B1 00 0B`) |

The colour payload is a raw byte packet, base64'd into that `pt` field:

```
BB 00 <len> B0 <stretch> <N> <R G B> x N <XOR-checksum>
 |  |    |   |     |       |   \___ N colour triples ___/   \_ XOR of all prior bytes
 |  |    |   |     |       └─ segment/zone count
 |  |    |   |     └─ 0x00 = per-segment, 0x01 = stretch N colours across all LEDs
 |  |    |   └─ 0xB0 constant
 |  |    └─ payload length = 2 + 3*N  (stretch byte + count byte + RGB bytes)
 └──┴─ 0xBB 0x00 constant
```

Watch the `<len>` byte. A lot of the reference code out there hard-codes it as some
magic "header variant" (`0xFA`, `0x20`, `0x0E`). It's nothing of the sort. It's the
payload length, and the `0x20` everyone copies is just `2 + 3*10`, because Govee's
own app streams ten segments. Get it wrong and the strip drops the packet without a
word, which cost me an evening. So this app computes it from `N`.

The loop itself is boring, which is the point: grab the framebuffer, box-average it
down to ten edge colours, apply gamma, saturation and a colour-correction matrix,
send the packet. A GDI `StretchBlt` does the downscale so there's no full-frame copy
per tick, and an exponential moving average smooths out the flicker that makes cheap
ambient lighting look twitchy. The strip is found by its MAC and re-resolved to a
current IP on every start (via an ARP sweep), so a new DHCP lease doesn't lose it.

Protocol credit goes to the people who did the original reverse engineering:
[LedFx](https://github.com/LedFx/LedFx),
[egold555](https://github.com/egold555/Govee-Reverse-Engineering) and
[wez/govee2mqtt](https://github.com/wez/govee2mqtt).

## Install

You need Windows 10 or 11. .NET Framework 4.x is already on it. No admin.

Grab `GoveeDreamView.exe` from the [latest release](../../releases/latest) and run
it, or build from source:

```cmd
build.cmd
```

That drops `bin\Release\net48\GoveeDreamView.exe`. It's a single file; put it wherever
you like. If you want it at login, stick a shortcut in `shell:startup`.

One catch on the Govee side: the strip has to support DreamView/Razer, be on Wi-Fi,
and have **LAN Control** switched on (Govee Home app, your device, settings, LAN
Control). Without that last toggle nothing on your network can talk to it.

## Run

Double-click it. A tray icon shows up and it starts streaming right away, finding the
strip on first launch and remembering it afterwards. Right-click for everything else.

## Tray menu

**Start / Stop** streaming, or just double-click the icon.

**Pause when locked** turns the strip off when you lock Windows and back on when you
return. Handy if you don't want it glowing at an empty desk.

**Settings** is live colour tuning. Changes apply instantly and get saved:

- Brightness (25/50/75/100), the device's own master brightness.
- Gamma (1.8/2.2/2.6/3.2). Higher means deeper blacks but dimmer mid-tones. Once
  brightness is maxed, this is the knob that actually makes the strip look brighter
  or darker.
- Saturation (1.0/1.25/1.5/1.75).

The rest: **Reload / Edit config**, **Rediscover device** if it moved, **Calibrate
colours** (below), and **Exit**.

## Configuration

First run writes a `config.ini` next to the exe. Edit it and hit Reload config, or
just use the Settings menu for the colour knobs.

| Key | Default | Meaning |
|-----|---------|---------|
| `DeviceIp` | *(blank)* | Strip IP. Blank means auto-discover and remember. |
| `DeviceMac` | *(blank)* | Fixed device MAC, re-resolved to the current IP each start so DHCP can't lose it. |
| `Fps` | `30` | Colour frames per second. |
| `Brightness` | `100` | Device master brightness, 0 to 100. |
| `StretchToFit` | `false` | `true` spreads the ten colours across all LEDs (some devices only). `false` is per-segment. |
| `Smoothing` | `0.45` | Temporal EMA. `0` is instant and jittery, `0.8` is glassy but laggy. |
| `Saturation` | `1.25` | Above 1 boosts vividness. |
| `Gamma` | `2.2` | Above 1 darkens dark and mid tones. Raise for deeper blacks, lower for brighter mids. |
| `MonitorIndex` | `0` | Which display to capture. 0 is primary. |
| `PauseOnLock` | `false` | `true` turns the strip off while Windows is locked. |
| `Ccm` | *identity* | 3x3 colour-correction matrix, filled in by Calibrate colours. |
| `CalibPort` | `8443` | HTTPS port for the phone calibration page. |

## Calibrate colours

The colour you set and the colour you actually see are not the same thing, because
the light bounces off your wall first and the wall has opinions. Calibrate colours
spins up a tiny HTTPS page, you open it on your phone and aim the camera at the wall
behind the strip, and it runs a colour sweep to solve a 3x3 matrix that corrects for
the difference. It never leaves your LAN.

## Files

| File | What's in it |
|------|--------------|
| `Program.cs` | The tray app: menu, streaming loop, animated icon, lock handling |
| `GoveeClient.cs` | The Govee LAN protocol: discover, power, stream, razer packets |
| `Net.cs` | MAC-to-IP via an ARP sweep, so DHCP changes don't lose the device |
| `ScreenCapture.cs` | GDI `StretchBlt` capture and per-zone box averaging |
| `Config.cs` | The flat `key=value` config next to the exe |
| `Calibration.cs`, `CameraServer.cs` | The phone-camera colour calibration |

## Troubleshooting

**"No Govee device found."** Almost always LAN Control being off, or the strip being
on a different Wi-Fi band or subnet than the PC. Check that first, then set `DeviceIp`
or `DeviceMac` by hand.

**Strip is stuck on one colour but the tray says Streaming.** It fell out of stream
mode, usually because something else grabbed it or it lost power for a moment. Stop
and Start from the tray re-arms it.

**Too dim.** Brightness is already the master level, so turning it up won't help. The
real lever is Gamma, lower it in Settings. A colour calibration also caps peak white
on purpose, which trades a bit of brightness for accuracy.

Errors go to `govee.log` next to the exe.

## Releases

Push a `v*` tag and GitHub Actions builds the exe and attaches it to a release:

```bash
git tag v1.0.0 && git push origin v1.0.0
```
