# HdlcGatewayTest

HdlcGatewayTest is a traffic test tool that proves an HDLC gateway (the ESP32-S2 **HDLCGW** or the
Raspberry Pi Pico **RC422Dev**) sends and receives frames correctly, by pushing numbered frames
through it for minutes at a time and checking every byte that comes back.

[![build](https://github.com/RetroCoreLabs/HdlcGatewayTest/actions/workflows/build.yml/badge.svg)](https://github.com/RetroCoreLabs/HdlcGatewayTest/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/RetroCoreLabs/HdlcGatewayTest)](https://github.com/RetroCoreLabs/HdlcGatewayTest/releases)
[![licence](https://img.shields.io/github/license/RetroCoreLabs/HdlcGatewayTest)](LICENSE)
[![downloads](https://img.shields.io/github/downloads/RetroCoreLabs/HdlcGatewayTest/total)](https://github.com/RetroCoreLabs/HdlcGatewayTest/releases)

**Download** · [What it does](#what-you-can-use-it-for) · [Quick start](#quick-start) · [Building](#building-from-source) · [About HDLC](#about-hdlc-and-these-gateways)

**Latest build for Windows:** https://github.com/RetroCoreLabs/HdlcGatewayTest/releases/latest

## Why it exists

A gateway between a PC and a synchronous HDLC line has a lot of stages a single test frame
cannot see through: USB framing, bit stuffing, a transmit clock, a receiver on somebody else's
clock, DMA, ring buffers, and the USB path back. Each one worked on its own and the first
multi-frame run still lost every second frame, because the transmit task could catch the ring
buffer half filled.

The way to find those faults is traffic: many frames in flight, every one numbered, every byte
checked on the way back, at every clock rate, for long enough that a rare event happens. That
is what this tool does, and the only hardware it needs is the gateway and two wires.

## What you can use it for

- **Loopback of one gateway**: wire its transmit pins to its receive pins and every frame must
  come back intact. Exit code 0 means it did, at every rate asked for.
- **Two gateways wired to each other**, HDLCGW and RC422Dev or two of the same, frames both
  ways at once, each direction judged on its own.
- **Throughput and round trip** per clock rate, as payload kilobit per second and milliseconds,
  so a change in the firmware shows as a number.
- **Finding the stage that fails**: the result table separates bad FCS, corrupt payload, lost,
  late and dropped frames, and the gateway's own `STAT` counters say whether a buffer or the USB
  path was the cause.

## Status

Version 0.1.0.

- 30 unit tests cover the FCS, the byte stuffing, the test frames and the sequence tracking.
- Verified 2026-11-08 against HDLCGW on a LOLIN S2 Mini in loopback, two minutes per rate
  with 1024-byte frames: 9600, 19200, 38400, 115200, 307200 and 983000 bit/s, 9894 frames in
  all, 0 lost, 0 bad. The board's own counters agreed: 5747 sent, 5747 received, 0 bad since
  its last reset.
- Measured on that board: 95 percent of the clock rate comes through as payload up to 38400
  bit/s; above about 270 kbit/s the USB path between board and PC is the ceiling, not the
  line.
- It found two firmware bugs on the way (frames split by idle bytes; a padding byte stuck on
  the front of frames), both fixed in HDLCGW the same day.
- Not yet run against RC422Dev (the Pico was not powered when this was written).

## Prerequisites

| # | Requirement | Needed for | How to check |
|---|---|---|---|
| 1 | Windows 10 or 11, 64-bit | the released exe | |
| 2 | A gateway on a USB serial port, in FORWARDING mode | everything | `hdlc-loopback --list` shows the port |
| 3 | Two jumper wires | loopback: transmit clock to receive clock, transmit data to receive data | |
| 4 | .NET SDK as pinned in `src/global.json` | building from source only | `dotnet --info` |

## Quick start

Wire the gateway's transmit clock to its receive clock and its transmit data to its receive
data (on HDLCGW: GPIO 7 to GPIO 12 and GPIO 11 to GPIO 9). Then:

```powershell
hdlc-loopback --list                                   # which COM port is the gateway
hdlc-loopback --port COM10 --rates 38400 --seconds 10  # a ten-second look
hdlc-loopback --port COM10                             # six rates, two minutes each
```

The last line of the run is a table with one row per rate; `PASS` in every row and exit code 0
is the result you want. `hdlc-loopback --help` lists every option.

## Building from source

```powershell
git clone https://github.com/RetroCoreLabs/HdlcGatewayTest.git
cd HdlcGatewayTest
.\src\build.ps1
.\src\publish\win-x64\hdlc-loopback.exe --help
```

`build.ps1` builds, runs the unit tests and publishes one self-contained `hdlc-loopback.exe`.
For a plain build, `dotnet build -nodeReuse:false` in `src`.

## Current limitations

- **RC422Dev opens its console only before the first forwarded frame.** The tool sets the
  clock rate through that console at the start of every rate, so against RC422Dev run one rate
  per invocation, or reset the Pico between rates.
- **RC422Dev's frame limit is 128 bytes.** Use `--size 126` or smaller with it (the default).
- **Throughput above about 270 kbit/s is not measured by this tool** on HDLCGW, because the
  gateway's USB path caps there. The line itself runs to 983000 bit/s without loss.
- Windows only has been tried. The code uses `System.IO.Ports`, which also runs on Linux and
  macOS, but nobody has done it yet.

## About HDLC and these gateways

HDLC is the 1970s bit-level framing that X.25, SDLC and many minicomputer links used: frames
start and end with the flag byte 0x7E, a zero bit is stuffed after any five ones so the flag
cannot appear inside the data, and a 16-bit CRC at the end lets the receiver check the frame.
The line is synchronous: a clock travels beside the data, one clock per direction.

A modern PC has no synchronous port, so a gateway does the clocked side and talks to the PC over
USB in a simpler framing from RFC 1662 (PPP in HDLC-like framing): the same 0x7E flag, but with
0x7D as an escape byte instead of bit stuffing. HDLCGW does the line side with an ESP32-S2's two
SPI peripherals; RC422Dev does it with the Pico's PIO. Both forward the frame's CRC untouched, so
this tool can check a frame end to end. The HDLCGW repository's `LOOPBACK-TEST.md` is the
step-by-step bench guide, wiring diagrams included.

## Documentation

- `LOOPBACK-TEST.md` in the HDLCGW repository: wiring, console commands, running the test, how
  to read the results, measured numbers.
- `src/HdlcGatewayTest.Core`: the testable parts, each file's header says what it is for.
- [CONTRIBUTING.md](.github/CONTRIBUTING.md), [SECURITY.md](.github/SECURITY.md).

## Contributing

Fixes and additions are welcome. [CONTRIBUTING.md](.github/CONTRIBUTING.md) has the build steps,
the coding rules (no LINQ, unit tests only, keep the comments) and what a pull request should say
about how it was verified.

## Licence

MIT, see [LICENSE](LICENSE).
