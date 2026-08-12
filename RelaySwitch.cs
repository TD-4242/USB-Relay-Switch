using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RelaySwitchApp
{
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

            return 0;
        }
    }
}
