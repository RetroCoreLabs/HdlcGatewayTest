// OPTIONS - the command line of hdlc-loopback.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Globalization;

namespace HdlcGatewayTest.Loopback;

/// <summary>
/// Everything the command line can set, with the defaults that make a plain
/// <c>hdlc-loopback --port COM10</c> a sensible test.
/// </summary>
public sealed class Options
{
    /// <summary>Default transmit clock rates, bit/s, one run each.</summary>
    public static readonly uint[] DefaultRates = { 9600, 19200, 38400, 115200, 307200, 983000 };

    /// <summary>Serial port of gateway A. Required unless <see cref="ListPorts"/>.</summary>
    public string? PortA;

    /// <summary>Serial port of gateway B. Null means loopback on A alone.</summary>
    public string? PortB;

    /// <summary>Transmit clock rates to run, in order.</summary>
    public uint[] Rates = DefaultRates;

    /// <summary>Seconds of traffic per rate.</summary>
    public int SecondsPerRate = 120;

    /// <summary>
    /// Frame length including FCS. 126 fits RC422Dev's 128-byte limit; HDLCGW takes up to 1024.
    /// </summary>
    public int FrameLength = 126;

    /// <summary>Most encoded bytes in flight per direction before sending pauses.</summary>
    public int WindowBytes = 3000;

    /// <summary>Seconds a sent frame may stay unseen before it is counted lost.</summary>
    public int TimeoutSeconds = 5;

    /// <summary>Skip the console step that sets BAUD; use whatever clock the gateways have.</summary>
    public bool NoBaud;

    /// <summary>How many bad frames per direction to describe on stderr.</summary>
    public int Verbose;

    /// <summary>Print the serial ports on this machine and exit.</summary>
    public bool ListPorts;

    /// <summary>Print the usage text and exit.</summary>
    public bool Help;

    /// <summary>
    /// Parse the command line.
    /// </summary>
    /// <param name="args">The arguments as given to Main.</param>
    /// <param name="error">Why parsing failed, or null.</param>
    /// <returns>The options, or null when <paramref name="error"/> is set.</returns>
    public static Options? Parse(string[] args, out string? error)
    {
        Options o = new Options();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
            case "--help":
            case "-h":
            case "/?":
                o.Help = true;
                break;
            case "--list":
                o.ListPorts = true;
                break;
            case "--no-baud":
                o.NoBaud = true;
                break;
            case "--verbose":
                if (!TakeInt(args, ref i, 0, 100000, out o.Verbose, out error))
                {
                    return null;
                }
                break;
            case "--port":
                if (!TakeValue(args, ref i, out o.PortA, out error))
                {
                    return null;
                }
                break;
            case "--port-b":
                if (!TakeValue(args, ref i, out o.PortB, out error))
                {
                    return null;
                }
                break;
            case "--rates":
                if (!TakeValue(args, ref i, out string? rates, out error)
                    || !ParseRates(rates!, out o.Rates, out error))
                {
                    return null;
                }
                break;
            case "--seconds":
                if (!TakeInt(args, ref i, 1, 86400, out o.SecondsPerRate, out error))
                {
                    return null;
                }
                break;
            case "--size":
                if (!TakeInt(args, ref i, Core.TestFrame.MinLength, Core.ByteFrame.MaxFrameLength,
                             out o.FrameLength, out error))
                {
                    return null;
                }
                break;
            case "--window":
                if (!TakeInt(args, ref i, 64, 100000, out o.WindowBytes, out error))
                {
                    return null;
                }
                break;
            case "--timeout":
                if (!TakeInt(args, ref i, 1, 600, out o.TimeoutSeconds, out error))
                {
                    return null;
                }
                break;
            default:
                error = "Unknown option '" + a + "'. Try --help.";
                return null;
            }
        }

        if (!o.Help && !o.ListPorts && string.IsNullOrEmpty(o.PortA))
        {
            error = "--port is required. --list shows the ports on this machine.";
            return null;
        }
        return o;
    }

    /// <summary>The usage text.</summary>
    public static string Usage =>
        "hdlc-loopback - traffic test for the HDLC gateways (HDLCGW on ESP32-S2, RC422Dev on Pico)\r\n" +
        "\r\n" +
        "  hdlc-loopback --port COM10                       loopback: one gateway, TX wired to RX\r\n" +
        "  hdlc-loopback --port COM10 --port-b COM8         two gateways wired to each other\r\n" +
        "  hdlc-loopback --list                             show serial ports\r\n" +
        "\r\n" +
        "Options:\r\n" +
        "  --rates 9600,19200,...   transmit clock rates to run, one after the other\r\n" +
        "                           (default 9600,19200,38400,115200,307200,983000)\r\n" +
        "  --seconds N              seconds of traffic per rate (default 120)\r\n" +
        "  --size N                 frame length including FCS, 10..1024 (default 126;\r\n" +
        "                           RC422Dev accepts at most 128, HDLCGW up to 1024)\r\n" +
        "  --window N               encoded bytes in flight per direction (default 3000)\r\n" +
        "  --timeout N              seconds before an unseen frame counts as lost (default 5)\r\n" +
        "  --no-baud                do not set BAUD through the console; keep the gateways' clocks\r\n" +
        "  --verbose N              describe the first N bad frames per direction on stderr\r\n" +
        "\r\n" +
        "Exit code 0 when every rate passed: every frame sent came back in order with a good FCS\r\n" +
        "and the exact payload, nothing lost, nothing extra. Otherwise 1.\r\n";

    private static bool TakeValue(string[] args, ref int i, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (i + 1 >= args.Length)
        {
            error = args[i] + " needs a value.";
            return false;
        }
        i++;
        value = args[i];
        return true;
    }

    private static bool TakeInt(string[] args, ref int i, int min, int max, out int value, out string? error)
    {
        value = 0;
        string name = args[i];
        if (!TakeValue(args, ref i, out string? text, out error))
        {
            return false;
        }
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            || value < min || value > max)
        {
            error = name + " must be a number " + min + ".." + max + ".";
            return false;
        }
        return true;
    }

    private static bool ParseRates(string text, out uint[] rates, out string? error)
    {
        string[] parts = text.Split(',');
        rates = new uint[parts.Length];
        error = null;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!uint.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                               out rates[i]) || rates[i] == 0)
            {
                error = "--rates: '" + parts[i] + "' is not a bit rate.";
                return false;
            }
        }
        return true;
    }
}
