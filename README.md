# Relay Switch

Portable single-file GUI controlling a device wired through the NC contacts of an
LCUS-style CH340 USB relay board.

<p align="center">
  <img src="docs/interface.png" alt="The relay switch interface: an industrial rocker switch in the ON position with a lit red indicator lamp" width="362">
</p>

## Download

Grab `RelaySwitch.exe` from the [latest release](https://github.com/TD-4242/USB-Relay-Switch/releases/latest)
and double-click it. No install, no runtime, no internet — a single ~18 KB file that
runs on Windows 10 and 11.

## Wiring

The load is on the **normally-closed** contacts, so the device is powered whenever
the relay coil is de-energized:

| Frame sent | Coil | NC contacts | Device |
|---|---|---|---|
| `A0 01 00 A1` | de-energized | closed | **powered ON** |
| `A0 01 01 A2` | energized | open | **powered OFF** |

Losing control — app exit, sleep, USB reset, unplug — restores power rather than
stranding the device off. This setup can cut power on demand but **cannot hold a
device off unattended.**

The inversion lives in exactly one place: `RelayPort.NormallyClosed`. Flip that single
constant if the load is ever moved to the NO contacts.

## Usage

Double-click `RelaySwitch.exe`. The switch opens in the ON position, restoring power.
Click to cut power, click again to restore it. The window keeps a square aspect ratio
at any size. Right-click for "Always on top" and "Retry / rescan ports".

| Flag | Effect |
|---|---|
| `--selftest` | Runs hardware-free assertions on frame bytes and the NC inversion; exit code 0 = pass |
| `--probe` | Lists detected relay boards; sends nothing to the hardware |
| `--preview` | Shows the switch graphic with no serial port attached |

## Building

Run `build.cmd`. It uses the `csc.exe` built into Windows, so there is nothing to
install and no internet access needed. The code targets the .NET Framework 4.0 API
surface, so the ~18 KB output runs on all Windows 10 releases and Windows 11.

## Releasing

Pushing a version tag builds the exe on a Windows runner, gates it on `--selftest`,
and publishes a GitHub Release with the binary attached:

```bash
git tag v0.0.2
git push origin v0.0.2
```

## Related

`relay.ps1` controls the same board from the command line and keeps working while the
GUI is running, because the port is opened per command rather than held open.

```powershell
.\relay.ps1 -State on
.\relay.ps1 -State pulse -DurationMs 500
```

Note that `relay.ps1` speaks **coil** state, not device state — it predates the NC
wiring, so `-State on` energizes the coil and therefore powers the device **off**.
