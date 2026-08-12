# RelaySwitch GUI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `RelaySwitch.exe`, a portable single-file Windows app showing one large industrial rocker switch that cuts and restores power to a device wired through the NC contacts of a CH340 USB relay board.

**Architecture:** One C# source file compiled by the in-box `csc.exe` into a ~20 KB WinForms executable. Three classes with clean boundaries — `RelayPort` (serial + device discovery, owns the NC inversion), `RockerSwitch` (a custom-painted `Control` that knows nothing about serial), `MainForm` (composition, status, resize). A fourth class, `SelfTest`, runs hardware-free assertions when the exe is launched with `--selftest`.

**Tech Stack:** C# on the .NET Framework 4.0 API surface, WinForms, GDI+, `System.IO.Ports.SerialPort`, WMI via `System.Management`.

## Global Constraints

Every task's requirements implicitly include this section.

- **API floor: .NET Framework 4.0.** Must run on all Windows 10 releases and Windows 11 with nothing installed. No language or library feature newer than 4.0.
- **Forbidden syntax** (not available on the 4.0 compiler): string interpolation `$"..."`, `nameof`, null-conditional `?.`, expression-bodied members, auto-property initializers, `async`/`await`. Use `String.Format` instead of interpolation.
- **Single source file.** All classes live in `RelaySwitch.cs`. This deviates from the usual "small focused files" guidance and is deliberate — portability is the product requirement. Class boundaries carry the separation instead.
- **Build flags:** `/target:winexe /optimize+ /out:RelaySwitch.exe`
- **References:** `System.dll`, `System.Drawing.dll`, `System.Windows.Forms.dll`, `System.Management.dll` — all in-box.
- **Serial parameters:** 9600 baud, 8 data bits, no parity, 1 stop bit.
- **Frame format:** `A0 <channel> <coilState> <checksum>`, checksum = sum of first three bytes, masked to a byte. Never hardcode the checksum.
- **NC wiring:** the load is on the normally-closed contacts. `deviceOn == true` means the coil is **de-energized**. The inversion appears exactly once, in `RelayPort`.
- **Live load attached.** No step may toggle the physical relay without the user's explicit go-ahead at that moment.

---

### Task 1: Build harness, frame construction, and the NC inversion

The riskiest logic in the project is the inversion, so it goes first and gets asserted on raw bytes.

**Files:**
- Create: `C:\Users\billw\git\switch\build.cmd`
- Create: `C:\Users\billw\git\switch\RelaySwitch.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `static byte[] RelayPort.BuildFrame(int channel, bool energized)`
  - `const bool RelayPort.NormallyClosed = true`
  - `static bool RelayPort.CoilStateFor(bool devicePowered)`
  - `static int SelfTest.Run()` — returns the failure count.

- [ ] **Step 1: Write `build.cmd`**

```bat
@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo ERROR: csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /out:RelaySwitch.exe ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll ^
  RelaySwitch.cs
if errorlevel 1 exit /b 1
echo Build OK: RelaySwitch.exe
```

- [ ] **Step 2: Write the failing test**

Create `RelaySwitch.cs` containing only the test harness and an intentionally
absent implementation. The assertions encode the known-good bytes confirmed
against real hardware.

```csharp
using System;
using System.Runtime.InteropServices;

namespace RelaySwitchApp
{
    static class SelfTest
    {
        static int failures;

        static string Hex(byte[] b)
        {
            string[] parts = new string[b.Length];
            for (int i = 0; i < b.Length; i++) parts[i] = b[i].ToString("X2");
            return String.Join(" ", parts);
        }

        static void AssertBytes(string label, byte[] actual, byte[] expected)
        {
            bool ok = actual != null && actual.Length == expected.Length;
            if (ok)
                for (int i = 0; i < expected.Length; i++)
                    if (actual[i] != expected[i]) { ok = false; break; }

            if (ok)
            {
                Console.WriteLine("PASS  " + label + "  [" + Hex(expected) + "]");
            }
            else
            {
                failures++;
                Console.WriteLine("FAIL  " + label +
                    "  expected [" + Hex(expected) + "]" +
                    "  got [" + (actual == null ? "null" : Hex(actual)) + "]");
            }
        }

        static void AssertTrue(string label, bool condition)
        {
            if (condition) { Console.WriteLine("PASS  " + label); }
            else { failures++; Console.WriteLine("FAIL  " + label); }
        }

        public static int Run()
        {
            failures = 0;

            // Known-good frames, verified against the physical board on 2026-08-11.
            byte[] coilOn  = new byte[] { 0xA0, 0x01, 0x01, 0xA2 };
            byte[] coilOff = new byte[] { 0xA0, 0x01, 0x00, 0xA1 };

            AssertBytes("BuildFrame(1, energized:true)",  RelayPort.BuildFrame(1, true),  coilOn);
            AssertBytes("BuildFrame(1, energized:false)", RelayPort.BuildFrame(1, false), coilOff);

            // Checksum must be computed, not hardcoded to channel 1.
            AssertBytes("BuildFrame(2, energized:true)", RelayPort.BuildFrame(2, true),
                        new byte[] { 0xA0, 0x02, 0x01, 0xA3 });

            // The NC inversion: powering the DEVICE on means DE-energizing the coil.
            AssertTrue("NC wiring declared", RelayPort.NormallyClosed);
            AssertTrue("device on  => coil de-energized", RelayPort.CoilStateFor(true) == false);
            AssertTrue("device off => coil energized",    RelayPort.CoilStateFor(false) == true);

            // End-to-end byte check through the inversion, the thing most likely
            // to ship backwards.
            AssertBytes("device ON  emits coil-off frame",
                        RelayPort.BuildFrame(1, RelayPort.CoilStateFor(true)), coilOff);
            AssertBytes("device OFF emits coil-on frame",
                        RelayPort.BuildFrame(1, RelayPort.CoilStateFor(false)), coilOn);

            Console.WriteLine(failures == 0
                ? "\nAll self-tests passed."
                : "\n" + failures + " self-test(s) FAILED.");
            return failures;
        }
    }

    static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        static int Main(string[] args)
        {
            bool selfTest = false;
            for (int i = 0; i < args.Length; i++)
                if (String.Equals(args[i], "--selftest", StringComparison.OrdinalIgnoreCase))
                    selfTest = true;

            if (selfTest)
            {
                // /target:winexe has no console of its own; borrow the caller's.
                AttachConsole(-1);
                return SelfTest.Run() == 0 ? 0 : 1;
            }

            return 0;
        }
    }
}
```

- [ ] **Step 3: Run the build to verify it fails**

Run: `cd C:\Users\billw\git\switch && build.cmd`
Expected: FAIL — `error CS0103: The name 'RelayPort' does not exist in the current context` (several occurrences). This confirms the tests genuinely depend on code that does not yet exist.

- [ ] **Step 4: Write the minimal implementation**

Add this class to `RelaySwitch.cs`, inside the `RelaySwitchApp` namespace:

```csharp
    /// <summary>
    /// Serial protocol and wiring semantics for the LCUS-style relay board.
    /// This class is the ONLY place that knows the load is on the NC contacts.
    /// </summary>
    static partial class RelayPort
    {
        /// <summary>
        /// True when the load is wired to the normally-closed contacts, so a
        /// de-energized coil leaves the circuit CLOSED and the device POWERED.
        /// Flip this single constant if the load is ever moved to NO.
        /// </summary>
        public const bool NormallyClosed = true;

        /// <summary>Maps desired DEVICE power to the required COIL state.</summary>
        public static bool CoilStateFor(bool devicePowered)
        {
            return NormallyClosed ? !devicePowered : devicePowered;
        }

        /// <summary>Builds the 4-byte command frame. Speaks COIL state, not device state.</summary>
        public static byte[] BuildFrame(int channel, bool energized)
        {
            byte a = 0xA0;
            byte b = (byte)channel;
            byte c = energized ? (byte)0x01 : (byte)0x00;
            byte checksum = (byte)((a + b + c) & 0xFF);
            return new byte[] { a, b, c, checksum };
        }
    }
```

`partial` is used so later tasks can extend the class without re-editing this block.

- [ ] **Step 5: Run the build and self-test to verify they pass**

Run: `cd C:\Users\billw\git\switch && build.cmd && RelaySwitch.exe --selftest`
Expected: `Build OK`, then 8 `PASS` lines and `All self-tests passed.` No hardware is touched.

- [ ] **Step 6: Commit**

```bash
git add build.cmd RelaySwitch.cs
git commit -m "feat: add build harness, frame construction, and NC inversion"
```

---

### Task 2: Port discovery

**Files:**
- Modify: `C:\Users\billw\git\switch\RelaySwitch.cs`

**Interfaces:**
- Consumes: `RelayPort` from Task 1.
- Produces:
  - `static string[] RelayPort.FindAllPorts()`
  - `static string RelayPort.FindPort()` — first match, or `null`
  - `static string RelayPort.ExtractComPort(string friendlyName)`
  - `--probe` command-line flag

- [ ] **Step 1: Write the failing test**

Add to `SelfTest.Run()`, immediately before the final `Console.WriteLine`:

```csharp
            // Port-name parsing is pure string work and is tested without hardware.
            AssertTrue("parses COM3 from CH340 friendly name",
                RelayPort.ExtractComPort("USB-SERIAL CH340 (COM3)") == "COM3");
            AssertTrue("parses two-digit port",
                RelayPort.ExtractComPort("USB-SERIAL CH340 (COM17)") == "COM17");
            AssertTrue("returns null when no port present",
                RelayPort.ExtractComPort("Some Other Device") == null);
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd C:\Users\billw\git\switch && build.cmd`
Expected: FAIL — `error CS0117: 'RelayPort' does not contain a definition for 'ExtractComPort'`.

- [ ] **Step 3: Write the implementation**

Add `using System.Collections.Generic;`, `using System.Management;`, and
`using System.Text.RegularExpressions;` to the top of the file, then add to the
`RelayPort` class:

```csharp
        /// <summary>Hardware ID prefix of the WCH CH340 bridge used by these boards.</summary>
        public const string HardwareIdPrefix = @"USB\VID_1A86&PID_7523";

        static readonly Regex ComPattern = new Regex(@"\((COM\d+)\)", RegexOptions.IgnoreCase);

        /// <summary>Pulls "COM3" out of a PnP friendly name, or returns null.</summary>
        public static string ExtractComPort(string friendlyName)
        {
            if (String.IsNullOrEmpty(friendlyName)) return null;
            Match m = ComPattern.Match(friendlyName);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        /// <summary>
        /// Finds every serial port belonging to a CH340 relay board.
        /// Filtering is done in C# rather than WQL to avoid backslash-escaping
        /// pitfalls in the DeviceID LIKE clause.
        /// </summary>
        public static string[] FindAllPorts()
        {
            List<string> found = new List<string>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                {
                    foreach (ManagementBaseObject obj in searcher.Get())
                    {
                        object idObj = obj["DeviceID"];
                        object nameObj = obj["Name"];
                        if (idObj == null || nameObj == null) continue;

                        string id = idObj.ToString();
                        if (id.IndexOf(HardwareIdPrefix, StringComparison.OrdinalIgnoreCase) != 0)
                            continue;

                        string port = ExtractComPort(nameObj.ToString());
                        if (port != null && !found.Contains(port)) found.Add(port);
                    }
                }
            }
            catch (Exception)
            {
                // WMI can fail on locked-down machines; treat as "no boards found"
                // rather than crashing the app before the window appears.
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found.ToArray();
        }

        /// <summary>First detected relay board, or null if none is present.</summary>
        public static string FindPort()
        {
            string[] ports = FindAllPorts();
            return ports.Length > 0 ? ports[0] : null;
        }
```

- [ ] **Step 4: Add the `--probe` flag**

In `Program.Main`, add alongside the `--selftest` handling:

```csharp
            bool probe = false;
            for (int i = 0; i < args.Length; i++)
                if (String.Equals(args[i], "--probe", StringComparison.OrdinalIgnoreCase))
                    probe = true;

            if (probe)
            {
                AttachConsole(-1);
                string[] ports = RelayPort.FindAllPorts();
                if (ports.Length == 0) Console.WriteLine("No relay board found.");
                else foreach (string p in ports) Console.WriteLine("Found relay board on " + p);
                return ports.Length > 0 ? 0 : 1;
            }
```

- [ ] **Step 5: Run to verify both pass**

Run: `cd C:\Users\billw\git\switch && build.cmd && RelaySwitch.exe --selftest && RelaySwitch.exe --probe`
Expected: all self-tests pass, then `Found relay board on COM3`. `--probe` only reads device metadata; it sends nothing to the board and does not move the relay.

- [ ] **Step 6: Commit**

```bash
git add RelaySwitch.cs
git commit -m "feat: detect CH340 relay board via WMI"
```

---

### Task 3: Serial write path

**Files:**
- Modify: `C:\Users\billw\git\switch\RelaySwitch.cs`

**Interfaces:**
- Consumes: `RelayPort.BuildFrame`, `RelayPort.CoilStateFor` from Task 1.
- Produces:
  - `class RelayPort` instance members: `RelayPort(string portName)`, `string PortName { get; }`, `void SetDevicePower(int channel, bool powered)`

Because `RelayPort` is currently `static partial`, this task converts it to a
normal `partial class` with static *and* instance members. The static members
from Tasks 1–2 are unchanged; only the `static` keyword on the class declaration
is removed.

- [ ] **Step 1: Change the class declaration**

In `RelaySwitch.cs`, change:

```csharp
    static partial class RelayPort
```

to:

```csharp
    partial class RelayPort
```

- [ ] **Step 2: Write the implementation**

Add `using System.IO.Ports;` to the top of the file, then add these instance
members to `RelayPort`:

```csharp
        public const int BaudRate = 9600;
        public const int DefaultChannel = 1;

        readonly string portName;

        public RelayPort(string portName)
        {
            if (String.IsNullOrEmpty(portName)) throw new ArgumentNullException("portName");
            this.portName = portName;
        }

        public string PortName { get { return portName; } }

        /// <summary>
        /// Sets DEVICE power, applying the NC inversion. This is the only public
        /// way to change relay state; callers never deal in coil state or frames.
        /// Throws on failure so the caller can revert the UI.
        /// </summary>
        public void SetDevicePower(int channel, bool powered)
        {
            byte[] frame = BuildFrame(channel, CoilStateFor(powered));

            // Opened per command and closed immediately, so relay.ps1 and other
            // tools stay usable while this app is running, and an unplug between
            // clicks surfaces as a clean error rather than a stale handle.
            using (SerialPort port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One))
            {
                port.WriteTimeout = 1000;
                port.Open();
                port.Write(frame, 0, frame.Length);
            }
        }
```

- [ ] **Step 3: Verify the build and that existing tests still pass**

Run: `cd C:\Users\billw\git\switch && build.cmd && RelaySwitch.exe --selftest`
Expected: `Build OK` and all self-tests still pass. The static tests from Tasks 1–2 must be unaffected by the class-shape change — that is what this step checks.

No hardware step here. `SetDevicePower` moves a live load and is exercised only in Task 6, with the user present.

- [ ] **Step 4: Commit**

```bash
git add RelaySwitch.cs
git commit -m "feat: add serial write path with NC-aware device power control"
```

---

### Task 4: The rocker switch control

A self-contained widget with no serial dependency, so it can be judged purely on
how it looks.

**Files:**
- Modify: `C:\Users\billw\git\switch\RelaySwitch.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks — deliberately decoupled.
- Produces:
  - `class RockerSwitch : Control`
  - `bool RockerSwitch.On { get; set; }` — means **device powered**, never coil state. The setter repaints without raising `Toggled`.
  - `event EventHandler RockerSwitch.Toggled`
  - `--preview` command-line flag

- [ ] **Step 1: Write the implementation**

Add `using System.Drawing;`, `using System.Drawing.Drawing2D;`, and
`using System.Windows.Forms;` to the top of the file, then add this class:

```csharp
    /// <summary>
    /// A large industrial rocker/breaker switch. Knows nothing about serial ports:
    /// On == true means the DEVICE is powered.
    /// </summary>
    class RockerSwitch : Control
    {
        bool on;
        public event EventHandler Toggled;

        public RockerSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(28, 28, 30);
            Cursor = Cursors.Hand;
        }

        /// <summary>Device power. Setting this repaints WITHOUT raising Toggled,
        /// so the form can revert the display after a failed write.</summary>
        public bool On
        {
            get { return on; }
            set { if (on != value) { on = value; Invalidate(); } }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            on = !on;
            Invalidate();
            if (Toggled != null) Toggled(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(BackColor);

            // Draw against a normalised 100x100 model, then scale to the control,
            // so the switch stays crisp at any window size.
            float side = Math.Min(Width, Height);
            float unit = side / 100f;
            float ox = (Width - side) / 2f;
            float oy = (Height - side) / 2f;

            GraphicsState state = g.Save();
            g.TranslateTransform(ox, oy);
            g.ScaleTransform(unit, unit);

            bool live = Enabled;
            DrawBezel(g);
            DrawRocker(g, live);
            DrawLamp(g, live && on);

            g.Restore(state);
        }

        static void DrawBezel(Graphics g)
        {
            using (GraphicsPath path = RoundedRect(18f, 8f, 64f, 76f, 6f))
            using (LinearGradientBrush brush = new LinearGradientBrush(
                       new RectangleF(18f, 8f, 64f, 76f),
                       Color.FromArgb(70, 70, 74), Color.FromArgb(40, 40, 44),
                       LinearGradientMode.Vertical))
            using (Pen pen = new Pen(Color.FromArgb(20, 20, 22), 1.2f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
        }

        // DrawRocker and DrawLamp are written in Step 2. They are instance methods
        // (not static) because they read `on`. Declare them empty for now so the
        // file compiles and the bezel can be checked on its own:
        void DrawRocker(Graphics g, bool live) { }
        void DrawLamp(Graphics g, bool lit) { }

        static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath path = new GraphicsPath();
            float d = r * 2f;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
```

- [ ] **Step 2: Fill in the rocker and lamp bodies**

Replace the two empty stubs with these instance methods (note: change
`static void DrawRocker(Graphics g, bool live)` to an instance method so it can
read `on`, and likewise for the lamp):

```csharp
        void DrawRocker(Graphics g, bool live)
        {
            Color upFace   = on && live ? Color.FromArgb(226, 226, 230) : Color.FromArgb(120, 120, 126);
            Color downFace = on ? Color.FromArgb(70, 70, 76) : Color.FromArgb(210, 210, 214);
            if (!live) { upFace = Color.FromArgb(96, 96, 100); downFace = Color.FromArgb(80, 80, 84); }

            // Upper half: raised when the device is powered.
            using (GraphicsPath upper = new GraphicsPath())
            using (GraphicsPath lower = new GraphicsPath())
            {
                float inset = on ? 0f : 3f;
                upper.AddPolygon(new PointF[] {
                    new PointF(26f, 20f + inset), new PointF(74f, 20f + inset),
                    new PointF(70f, 46f), new PointF(30f, 46f) });
                lower.AddPolygon(new PointF[] {
                    new PointF(30f, 46f), new PointF(70f, 46f),
                    new PointF(74f, 72f - (3f - inset)), new PointF(26f, 72f - (3f - inset)) });

                using (SolidBrush ub = new SolidBrush(upFace))
                using (SolidBrush lb = new SolidBrush(downFace))
                using (Pen edge = new Pen(Color.FromArgb(24, 24, 26), 1f))
                {
                    g.FillPath(ub, upper); g.DrawPath(edge, upper);
                    g.FillPath(lb, lower); g.DrawPath(edge, lower);
                }
            }

            using (Font f = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                using (SolidBrush tb = new SolidBrush(on ? Color.FromArgb(30, 30, 34) : Color.FromArgb(210, 210, 214)))
                    g.DrawString("ON", f, tb, new RectangleF(26f, 24f, 48f, 18f), sf);
                using (SolidBrush tb = new SolidBrush(on ? Color.FromArgb(200, 200, 204) : Color.FromArgb(40, 40, 44)))
                    g.DrawString("OFF", f, tb, new RectangleF(26f, 50f, 48f, 18f), sf);
            }
        }

        void DrawLamp(Graphics g, bool lit)
        {
            RectangleF lamp = new RectangleF(45f, 88f, 10f, 10f);
            if (lit)
            {
                using (GraphicsPath glow = new GraphicsPath())
                {
                    glow.AddEllipse(RectangleF.Inflate(lamp, 7f, 7f));
                    using (PathGradientBrush pg = new PathGradientBrush(glow))
                    {
                        pg.CenterColor = Color.FromArgb(160, 255, 60, 40);
                        pg.SurroundColors = new Color[] { Color.FromArgb(0, 255, 60, 40) };
                        g.FillPath(pg, glow);
                    }
                }
            }
            using (SolidBrush b = new SolidBrush(lit ? Color.FromArgb(255, 70, 50) : Color.FromArgb(70, 26, 24)))
            using (Pen p = new Pen(Color.FromArgb(20, 20, 22), 1f))
            {
                g.FillEllipse(b, lamp);
                g.DrawEllipse(p, lamp);
            }
        }
```

The call sites in `OnPaint` are unchanged — `DrawRocker(g, live)` and
`DrawLamp(g, live && on)` were already instance calls.

- [ ] **Step 3: Add the `--preview` flag**

In `Program.Main`, before the `return 0;`:

```csharp
            bool preview = false;
            for (int i = 0; i < args.Length; i++)
                if (String.Equals(args[i], "--preview", StringComparison.OrdinalIgnoreCase))
                    preview = true;

            if (preview)
            {
                // Renders the widget with NO serial port attached, so the graphic
                // can be reviewed without touching the live load.
                Application.EnableVisualStyles();
                Form f = new Form();
                f.Text = "RockerSwitch preview";
                f.ClientSize = new Size(320, 320);
                f.BackColor = Color.FromArgb(28, 28, 30);
                RockerSwitch rs = new RockerSwitch();
                rs.Dock = DockStyle.Fill;
                f.Controls.Add(rs);
                Application.Run(f);
                return 0;
            }
```

- [ ] **Step 4: Build and review the graphic**

Run: `cd C:\Users\billw\git\switch && build.cmd && RelaySwitch.exe --preview`
Expected: a window showing the rocker. Click it and confirm the upper half rises, the ON/OFF label contrast flips, and the lamp lights red with a glow. Resize the window and confirm the switch scales smoothly without pixelation.

**No serial port is constructed in preview mode, so the live load is untouched.** Screenshot both states for the record.

- [ ] **Step 5: Commit**

```bash
git add RelaySwitch.cs
git commit -m "feat: add scalable industrial rocker switch control"
```

---

### Task 5: Main window, status, square resize

**Files:**
- Modify: `C:\Users\billw\git\switch\RelaySwitch.cs`

**Interfaces:**
- Consumes: `RelayPort` (Task 3), `RockerSwitch` (Task 4).
- Produces: `class MainForm : Form`

- [ ] **Step 1: Write the implementation**

```csharp
    class MainForm : Form
    {
        const int WM_SIZING = 0x0214;
        const int MinSide = 220;

        readonly RockerSwitch rocker = new RockerSwitch();
        readonly Label status = new Label();
        readonly ComboBox portPicker = new ComboBox();
        RelayPort relay;

        public MainForm()
        {
            Text = "Relay Switch";
            BackColor = Color.FromArgb(28, 28, 30);
            ClientSize = new Size(340, 340);
            MinimumSize = new Size(MinSide, MinSide);
            StartPosition = FormStartPosition.CenterScreen;

            status.Dock = DockStyle.Bottom;
            status.Height = 34;
            status.TextAlign = ContentAlignment.MiddleCenter;
            status.ForeColor = Color.FromArgb(190, 190, 196);
            status.Font = new Font("Segoe UI", 9f);

            portPicker.Dock = DockStyle.Bottom;
            portPicker.DropDownStyle = ComboBoxStyle.DropDownList;
            portPicker.Visible = false;
            portPicker.SelectedIndexChanged += delegate
            {
                if (portPicker.SelectedItem != null)
                {
                    relay = new RelayPort(portPicker.SelectedItem.ToString());
                    ShowReady();
                }
            };

            rocker.Dock = DockStyle.Fill;
            rocker.Toggled += OnToggled;

            Controls.Add(rocker);
            Controls.Add(portPicker);
            Controls.Add(status);

            BuildContextMenu();
            Discover();
        }

        void BuildContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem top = new ToolStripMenuItem("Always on top");
            top.CheckOnClick = true;
            top.Click += delegate { TopMost = top.Checked; };
            menu.Items.Add(top);

            ToolStripMenuItem retry = new ToolStripMenuItem("Retry / rescan ports");
            retry.Click += delegate { Discover(); };
            menu.Items.Add(retry);

            ContextMenuStrip = menu;
            rocker.ContextMenuStrip = menu;
        }

        /// <summary>Locks the window to a square while the user drags any edge or corner.</summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SIZING)
            {
                RECT r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                int edge = m.WParam.ToInt32();
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                // Dragging a horizontal edge changes height, so height drives the
                // square; dragging a vertical edge changes width, so width drives it.
                int side = Math.Max(MinSide, edge == 3 || edge == 6 ? h   // top / bottom
                                          : edge == 1 || edge == 2 ? w   // left / right
                                          : Math.Max(w, h));             // corners

                // Grow away from whichever edge is anchored.
                if (edge == 1 || edge == 4 || edge == 7) r.Left = r.Right - side; else r.Right = r.Left + side;
                if (edge == 3 || edge == 4 || edge == 5) r.Top = r.Bottom - side; else r.Bottom = r.Top + side;

                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
    }
```

- [ ] **Step 2: Add discovery, startup reset, and the toggle handler**

Add these members to `MainForm`:

```csharp
        void Discover()
        {
            string[] ports = RelayPort.FindAllPorts();

            portPicker.Visible = ports.Length > 1;
            portPicker.Items.Clear();
            foreach (string p in ports) portPicker.Items.Add(p);

            if (ports.Length == 0)
            {
                relay = null;
                rocker.Enabled = false;
                rocker.On = false;
                status.Text = "No relay board found  —  right-click to retry";
                return;
            }

            if (ports.Length > 1) portPicker.SelectedIndex = 0;
            relay = new RelayPort(ports[0]);
            rocker.Enabled = true;
            ResetToPowered();
        }

        /// <summary>
        /// Sends the coil-de-energize frame once so the graphic and the hardware
        /// are guaranteed to agree. Under NC wiring this RESTORES power.
        /// </summary>
        void ResetToPowered()
        {
            try
            {
                relay.SetDevicePower(RelayPort.DefaultChannel, true);
                rocker.On = true;
                ShowReady();
            }
            catch (Exception ex)
            {
                rocker.Enabled = false;
                status.Text = "Error: " + ex.Message;
            }
        }

        void ShowReady()
        {
            status.Text = String.Format("Device {0}  —  relay 1 on {1}",
                rocker.On ? "ON" : "OFF",
                relay == null ? "—" : relay.PortName);
        }

        void OnToggled(object sender, EventArgs e)
        {
            bool desired = rocker.On;
            if (relay == null) { rocker.On = !desired; return; }

            try
            {
                relay.SetDevicePower(RelayPort.DefaultChannel, desired);
                ShowReady();
            }
            catch (Exception ex)
            {
                // Never claim a state the hardware did not accept.
                rocker.On = !desired;
                status.Text = "Error: " + ex.Message;
            }
        }
```

- [ ] **Step 3: Wire up `Main`**

Replace the trailing `return 0;` in `Program.Main` with:

```csharp
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
```

And add the P/Invoke beside `AttachConsole`:

```csharp
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
```

- [ ] **Step 4: Build and verify the non-hardware behaviour**

Run: `cd C:\Users\billw\git\switch && build.cmd && RelaySwitch.exe --selftest && RelaySwitch.exe --preview`
Expected: build clean, self-tests pass, preview still works.

**Do not launch the full GUI yet** — startup sends a frame to a live load. That happens in Task 6 with the user's go-ahead.

- [ ] **Step 5: Commit**

```bash
git add RelaySwitch.cs
git commit -m "feat: add main window with square resize and port handling"
```

---

### Task 6: End-to-end verification against hardware

**Files:**
- Modify: `C:\Users\billw\git\switch\README.md` (create)

**Interfaces:**
- Consumes: everything.
- Produces: a verified `RelaySwitch.exe` and usage documentation.

- [ ] **Step 1: Ask the user for explicit go-ahead**

A live load is attached to the NC contacts. Before running the full GUI, confirm with the user that it is safe to power their device off and on now. **Do not proceed to Step 2 without an explicit yes.**

- [ ] **Step 2: Run the app and verify end-to-end**

Run: `cd C:\Users\billw\git\switch && RelaySwitch.exe`

Confirm each:
- On launch the switch is up/ON, the lamp is lit, and the device **has power** (startup restores it under NC).
- Status line reads `Device ON — relay 1 on COM3`.
- One click: rocker drops, lamp goes dark, device **loses power**, relay clicks.
- Second click: rocker rises, lamp lights, device **powers back on**.
- Right-click → Always on top pins the window.
- Dragging any edge and any corner keeps the window square.

- [ ] **Step 3: Verify the error path**

With the user's agreement, unplug the relay board while the app is running, then click the switch.
Expected: the switch snaps back, the status line shows an error, and the app does not crash. Note that unplugging also powers the device on, by design. Right-click → Retry after replugging should recover.

- [ ] **Step 4: Write `README.md`**

```markdown
# Relay Switch

Portable single-file GUI controlling a device wired through the NC contacts of an
LCUS-style CH340 USB relay board.

## Wiring

The load is on the **normally-closed** contacts, so the device is powered whenever
the relay coil is de-energized. Losing control — app exit, sleep, USB reset, unplug —
restores power rather than stranding the device off. This setup can cut power on
demand but **cannot hold a device off unattended**.

## Usage

Double-click `RelaySwitch.exe`. The switch opens in the ON position, restoring power.
Click to cut power, click again to restore it. Right-click for "Always on top" and
"Retry / rescan ports".

| Flag | Effect |
|---|---|
| `--selftest` | Runs hardware-free assertions on frame bytes and the NC inversion; exit code 0 = pass |
| `--probe` | Lists detected relay boards; sends nothing to the hardware |
| `--preview` | Shows the switch graphic with no serial port attached |

## Building

Run `build.cmd`. Uses the `csc.exe` built into Windows; nothing to install.
Targets the .NET Framework 4.0 API surface, so the output runs on all Windows 10
releases and Windows 11.

`relay.ps1` controls the same board from the command line and works while the GUI
is running, since the port is opened per command rather than held open.
```

- [ ] **Step 5: Commit**

```bash
git add README.md RelaySwitch.cs
git commit -m "docs: add README and verify end-to-end against hardware"
```

---

## Residual Risk

Windows 10 execution cannot be verified from this machine, which runs Windows 11. The .NET Framework 4.0 API-surface constraint is the mitigation and is well-founded, but it is reasoning rather than a passing test. If a Windows 10 machine is available, running the built `RelaySwitch.exe` there is the real check.
