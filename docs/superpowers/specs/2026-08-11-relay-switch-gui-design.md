# RelaySwitch GUI — Design

**Date:** 2026-08-11
**Status:** Approved (pending spec review)

## Purpose

A portable, single-file Windows application presenting one large industrial rocker
switch that controls power to a device wired through relay 1 of the LCUS-style USB
relay board. Double-click to run; no install, no dependencies, no internet.

The device is normally powered; the switch exists to cut power and restore it.

## Hardware Context

The board is an LCUS-style USB relay using a WCH CH340 USB-serial bridge
(`USB\VID_1A86&PID_7523`), currently enumerated as COM3.

- 9600 baud, 8 data bits, no parity, 1 stop bit
- Command frame: `A0 <channel> <state> <checksum>`, checksum = sum of first 3 bytes
- Relay 1 ON = `A0 01 01 A2`; relay 1 OFF = `A0 01 00 A1`
- The board echoes command frames back on the serial line
- **The board cannot report relay state.** It is write-only for state purposes.

Verified working 2026-08-11 via `relay.ps1` (audible click confirmed by user).

### Wiring: normally-closed (NC)

**The load is wired to the NC contacts.** This inverts the relay's vocabulary relative
to the user's:

| Frame | Coil | NC contacts | Device |
|---|---|---|---|
| `A0 01 00 A1` | de-energized | closed | **powered ON** |
| `A0 01 01 A2` | energized | open | **powered OFF** |

Consequences that shape the design:

- **Fail-safe.** If the app exits, the PC sleeps, the USB bus resets, or the board is
  unplugged, the coil drops and the device powers back on. Loss of control restores
  power rather than stranding the device unpowered. This is the reason for NC wiring.
- **A live load is now attached.** Toggling during development requires the user's
  explicit go-ahead each time; it is no longer a free action.

## Deliverables

Three files in `C:\Users\billw\git\switch`:

| File | Purpose |
|---|---|
| `RelaySwitch.cs` | The entire application, one source file |
| `build.cmd` | Invokes the `csc.exe` built into Windows |
| `RelaySwitch.exe` | ~20 KB build output — the portable artifact |

`RelaySwitch.exe` is the thing the user copies around. The other two exist so it can be
rebuilt and modified.

## Platform Target

Must run on **Windows 10 (all releases) and Windows 11**, 64-bit.

.NET Framework 4.8 ships in-box only from Windows 10 version 1903 onward; earlier
Windows 10 releases carry 4.6 or 4.7. Therefore:

- Code targets the **.NET Framework 4.0 API surface**. No language or library feature
  newer than 4.0 may be used. This runs on every Windows 10 release and Windows 11
  without installing anything.
- Compiled with `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\`,
  present on both OS versions. `build.cmd` falls back to the 32-bit `Framework`
  directory if the 64-bit one is absent.
- Build flags: `/target:winexe` (no console window), `/optimize+`, `/out:RelaySwitch.exe`
- References: `System.dll`, `System.Drawing.dll`, `System.Windows.Forms.dll`,
  `System.Management.dll` — all in-box.

`System.IO.Ports.SerialPort` lives in `System.dll` on .NET Framework, so no extra
reference is needed for serial access.

DPI awareness is obtained by calling `SetProcessDPIAware()` via P/Invoke at startup
rather than shipping an application manifest, preserving the single-source-file design.

## Architecture

Three components within the single source file, each independently understandable:

### 1. `RelayPort` — serial layer

Owns all serial and device-discovery concerns. Knows nothing about the UI.

- `static string FindPort()` — WMI query over `Win32_PnPEntity` for devices whose
  `DeviceID` starts with `USB\VID_1A86&PID_7523`, parsing `COMn` out of the friendly
  name. Returns the port name, or `null` if none found.
- `static string[] FindAllPorts()` — same query, all matches, for the multi-board case.
- `static byte[] BuildFrame(int channel, bool energized)` — returns
  `{0xA0, channel, energized ? 1 : 0, checksum}` with checksum computed, not hardcoded.
  Speaks in terms of the **coil**, matching the wire protocol.
- `void SetDevicePower(int channel, bool powered)` — the **only** public way to change
  state. Applies the NC inversion (`energized = !powered`) and writes the frame.
  Throws on failure.

**The NC inversion lives here and nowhere else.** `RockerSwitch` and `MainForm` speak
exclusively in terms of device power and never reference coil state or raw frames. A
single named constant documents the wiring so that re-wiring to NO contacts is a
one-line change rather than a hunt through the UI.

**The port is opened per command and closed immediately, not held open.** This keeps
`relay.ps1` and any other tool usable while the GUI is running, and means an unplug
between clicks surfaces as a clean error rather than a stale handle.

### 2. `RockerSwitch` — the widget

A custom `Control` that paints the breaker and raises `Toggled`. Contains no serial
code, so it can be exercised without hardware.

- Double-buffered, anti-aliased GDI+ path rendering.
- Drawn against a normalised coordinate space, then scaled to the control bounds, so
  it stays crisp at any size instead of pixelating.
- Visual states: rocker tilted up (device powered) or down (device unpowered);
  indicator lamp bright red with a soft radial glow when the device is powered, dark
  red when it is not.
- Exposes `bool On { get; set; }` — meaning **device powered**, never coil state.
  Setting it updates the graphic without raising `Toggled`, so the form can correct
  the display after a failed write.
- A separate disabled appearance (greyed, lamp dark) for when no board is present.

### 3. `MainForm` — composition

Hosts the rocker, owns the status line and the always-on-top option, and wires the
widget's `Toggled` event to `RelayPort`.

- Status line shows `Relay 1 — COM3`, or the current error.
- Always-on-top exposed as a right-click context menu item toggling `TopMost`.
- **Square aspect ratio:** overrides `WndProc` to handle `WM_SIZING` (0x0214),
  clamping the drag rectangle to a square. Works from any edge or corner: dragging a
  vertical edge drives width from height, a horizontal edge drives height from width,
  and corners take the larger delta. A minimum size prevents label collision.

## Behaviour

### Startup

1. Call `SetProcessDPIAware()`.
2. Discover the port.
   - Exactly one match → use it.
   - Multiple matches → show a `ComboBox` docked directly above the status line,
     listing each port; the first is selected by default.
   - No match → disable the rocker, status reads `No relay board found`, and a
     `Retry` item is added to the right-click context menu which re-runs discovery.
3. **Send the coil-de-energize frame once** (`A0 01 00 A1`), i.e. `SetDevicePower(1,
   true)`. The graphic and the hardware are then guaranteed to agree, and because of
   the NC wiring this **restores power** rather than cutting it. The switch opens in
   the up/powered position with the lamp lit.

   Under NO wiring this startup reset would have been a real trade-off; under NC it
   is benign — the worst case is that a device the user had deliberately switched off
   gets powered back on when the app launches, which is both visible and recoverable
   with one click.

### Toggling

On click, the widget flips its graphic optimistically and the form sends the frame.
On success, the status line stays clean. On failure the switch **snaps back to its
previous position**, the lamp goes grey, and the error appears in the status line —
the UI never claims a state the hardware did not accept.

### Error handling

All port access is wrapped in try/catch. No exception may reach the message loop and
kill the window. Unplugging the board mid-session degrades to the same disabled
state described under Startup — rocker greyed, `Retry` in the context menu — rather
than crashing.

## Out of Scope

Deliberately excluded, per the user's scope decisions:

- Keyboard control (Space/Enter activation)
- Multi-channel support (2/4/8-channel boards)
- A dedicated power-cycle / reboot action. The user wants manual control over how long
  the device stays unpowered, so cutting and restoring power is two deliberate clicks.
- Persisting window position or state between runs
- Reading relay state back from the board — the hardware cannot do this

## Testing

| Concern | Method |
|---|---|
| Frame construction | Verify `BuildFrame` output equals the known-good `A0 01 01 A2` / `A0 01 00 A1` |
| **NC inversion** | Verify `SetDevicePower(1, true)` emits `A0 01 00 A1` and `SetDevicePower(1, false)` emits `A0 01 01 A2` — the inversion is the single most likely thing to ship backwards, so it is asserted directly on the bytes |
| Port discovery | Confirm `FindPort()` returns COM3 on this machine |
| Rendering, both states | Run the app, screenshot ON and OFF |
| Square resize | Drag each edge and a corner; confirm the window stays square |
| End-to-end | Physical relay click, as with `relay.ps1` — **requires the user's explicit go-ahead, since a live load is attached** |
| Error path | Unplug the board while running; confirm disabled state and no crash. Note this also powers the device on, by design |
| Build portability | Confirm `build.cmd` resolves `csc.exe` and compiles clean |

Windows 10 execution cannot be verified from this machine (Windows 11 only). The 4.0
API-surface constraint is the mitigation; this residual risk is noted rather than
tested.

## Related

- `relay.ps1` — existing CLI control script, same protocol. Remains useful and keeps
  working alongside the GUI because the port is not held open.
