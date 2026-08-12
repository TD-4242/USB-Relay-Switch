using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RelaySwitchApp
{
    /// <summary>
    /// Serial protocol and wiring semantics for the LCUS-style relay board.
    /// This class is the ONLY place that knows the load is on the NC contacts.
    /// </summary>
    partial class RelayPort
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
    }

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
            if (side <= 0) return;
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
                       new RectangleF(18f, 7f, 64f, 78f),
                       Color.FromArgb(70, 70, 74), Color.FromArgb(40, 40, 44),
                       LinearGradientMode.Vertical))
            using (Pen pen = new Pen(Color.FromArgb(20, 20, 22), 1.2f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
        }

        /// <summary>
        /// Two trapezoid halves fake the tilt: the raised half is lit and taller,
        /// the pressed half is recessed and dark.
        /// </summary>
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

            // Port-name parsing is pure string work and is tested without hardware.
            AssertTrue("parses COM3 from CH340 friendly name",
                RelayPort.ExtractComPort("USB-SERIAL CH340 (COM3)") == "COM3");
            AssertTrue("parses two-digit port",
                RelayPort.ExtractComPort("USB-SERIAL CH340 (COM17)") == "COM17");
            AssertTrue("returns null when no port present",
                RelayPort.ExtractComPort("Some Other Device") == null);

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

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        /// <summary>
        /// A /target:winexe build has no console of its own, so console output is
        /// discarded by default. Attach to the caller's console, then rebind
        /// Console.Out to the real standard-output handle -- without the rebind,
        /// AttachConsole leaves Console.Out pointing at a null writer and output
        /// vanishes, including when stdout is redirected to a file or a pipe.
        /// </summary>
        static void UseCallersConsole()
        {
            // Order matters: if stdout is already redirected to a file or pipe we
            // must bind it BEFORE attaching, because AttachConsole replaces the
            // inherited standard handles and the redirected output is lost.
            if (TryBindStdout()) return;

            AttachConsole(-1);
            TryBindStdout();
        }

        static bool TryBindStdout()
        {
            try
            {
                Stream stdout = Console.OpenStandardOutput();
                if (stdout == Stream.Null) return false;
                StreamWriter writer = new StreamWriter(stdout);
                writer.AutoFlush = true;
                Console.SetOut(writer);
                return true;
            }
            catch (IOException)
            {
                // Launched from Explorer with no console at all; exit codes still
                // convey the result.
                return false;
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            bool selfTest = false;
            for (int i = 0; i < args.Length; i++)
                if (String.Equals(args[i], "--selftest", StringComparison.OrdinalIgnoreCase))
                    selfTest = true;

            if (selfTest)
            {
                UseCallersConsole();
                return SelfTest.Run() == 0 ? 0 : 1;
            }

            bool probe = false;
            for (int i = 0; i < args.Length; i++)
                if (String.Equals(args[i], "--probe", StringComparison.OrdinalIgnoreCase))
                    probe = true;

            if (probe)
            {
                // Reads device metadata only; sends nothing to the board, so the
                // relay does not move and the live load is untouched.
                UseCallersConsole();
                string[] ports = RelayPort.FindAllPorts();
                if (ports.Length == 0) Console.WriteLine("No relay board found.");
                else foreach (string p in ports) Console.WriteLine("Found relay board on " + p);
                return ports.Length > 0 ? 0 : 1;
            }

            bool preview = false;
            for (int i = 0; i < args.Length; i++)
                if (String.Equals(args[i], "--preview", StringComparison.OrdinalIgnoreCase))
                    preview = true;

            if (preview)
            {
                // Renders the widget with NO serial port attached, so the graphic
                // can be reviewed without touching the live load.
                SetProcessDPIAware();
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

            return 0;
        }
    }
}
