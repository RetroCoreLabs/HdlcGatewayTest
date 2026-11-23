// FAKE GATEWAY - a scripted firmware behind a FakeTransport: forwards frames, answers HELLO and BAUD.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Text;

namespace HdlcGatewayTest.Core.Tests;

/// <summary>
/// Behaves like a gateway in loopback wiring: every data frame written comes straight back
/// (optionally every Nth one dropped or damaged), and depending on <see cref="Flavour"/> it
/// answers control frames like HDLCGW 0.2.0 or only the ESC ESC ESC text console like
/// RC422Dev. Wired to a <see cref="FakeTransport"/> through its <see cref="FakeTransport.OnWrite"/>.
/// </summary>
public sealed class FakeGateway
{
    /// <summary>Which firmware to imitate.</summary>
    public enum Kind
    {
        /// <summary>Answers HELLO and COMMAND frames; forwards everything else.</summary>
        ControlFrames,

        /// <summary>Ignores control frames (forwards them as junk); has the text console.</summary>
        TextConsoleOnly,

        /// <summary>Starts in the DEBUG console: frames are typed into the input line until EXIT.</summary>
        ControlFramesButInDebug,

        /// <summary>Answers nothing at all.</summary>
        Dead,
    }

    private readonly FakeTransport _transport;
    private readonly ByteFrameDecoder _decoder = new ByteFrameDecoder();
    private readonly StringBuilder _line = new StringBuilder();
    private bool _inConsole;
    private int _escapes;
    private long _dataFrames;

    /// <summary>
    /// Attach to a transport.
    /// </summary>
    /// <param name="transport">The fake transport the host side writes to.</param>
    /// <param name="flavour">Which firmware to imitate.</param>
    public FakeGateway(FakeTransport transport, Kind flavour)
    {
        _transport = transport;
        Flavour = flavour;
        _inConsole = flavour == Kind.ControlFramesButInDebug;
        transport.OnWrite = OnHostBytes;
    }

    /// <summary>Which firmware this imitates.</summary>
    public Kind Flavour { get; }

    /// <summary>Drop every Nth data frame instead of looping it back; 0 = none.</summary>
    public int DropEvery { get; set; }

    /// <summary>Flip a bit in every Nth data frame before looping it back; 0 = none.</summary>
    public int DamageEvery { get; set; }

    /// <summary>Commands received through COMMAND frames or the console, in order.</summary>
    public List<string> Commands { get; } = new List<string>();

    /// <summary>Data frames that were forwarded (looped back).</summary>
    public long DataFrames => _dataFrames;

    /// <summary>
    /// The HELLO reply text, key=value lines. The screenshot test sets one that names a real
    /// firmware so the pictures read like the bench.
    /// </summary>
    public string HelloText { get; set; } =
        "name=FAKEGW\nversion=0.2.0\nbuild=test\nchip=FAKE1\nboard=Test bench\nmaxframe=1024\ncaps=cmd,log,stat\n"
        + "rev=1.0\nserial=0011223344AA\ncores=2\nmhz=240\nflash=4194304\nflashimage=2097152\nflashmhz=80\nflashmode=dio\n"
        + "ram=323204\nramfree=216156\nramleast=200388\nramblock=110580\npsram=0\n"
        + "pkg=0\nblkver=0.2\nembflash=2\nembpsram=1\nuid=00112233445566778899AABBCCDDEEFF\nflashid=204016\n"
        + "usbfuse=0010\nvddspi=001\n";

    /// <summary>Current transmit clock, set by BAUD.</summary>
    public uint Baud { get; private set; } = 38400;

    private void OnHostBytes(byte[] bytes)
    {
        if (Flavour == Kind.Dead)
        {
            return;
        }
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (_inConsole)
            {
                OnConsoleByte(b);
                continue;
            }
            if (_decoder.IsIdle)
            {
                if (b == 0x1B)
                {
                    if (++_escapes >= 3)
                    {
                        _escapes = 0;
                        _inConsole = true;
                        _transport.InjectText("\r\n*** Debug console. EXIT returns to forwarding. ***\r\n# ");
                    }
                    continue;
                }
                _escapes = 0;
            }
            if (_decoder.Feed(b))
            {
                OnFrame(_decoder.Frame);
            }
        }
    }

    private void OnConsoleByte(byte b)
    {
        if (b == '\r' || b == '\n')
        {
            string line = _line.ToString();
            _line.Clear();
            if (line.Length > 0)
            {
                RunCommand(line, framed: false);
            }
            if (_inConsole)
            {
                _transport.InjectText("# ");
            }
            return;
        }
        _line.Append((char)b);
    }

    private void OnFrame(ReadOnlySpan<byte> frame)
    {
        if (Flavour != Kind.TextConsoleOnly
            && ControlFrame.TryDecode(frame, out ControlFrameHeader header, out ReadOnlySpan<byte> payload))
        {
            OnControl(in header, payload);
            return;
        }
        _dataFrames++;
        if (DropEvery > 0 && _dataFrames % DropEvery == 0)
        {
            return;
        }
        if (DamageEvery > 0 && _dataFrames % DamageEvery == 0)
        {
            byte[] damaged = frame.ToArray();
            damaged[damaged.Length / 2] ^= 0x01;
            _transport.InjectFrame(damaged);
            return;
        }
        _transport.InjectFrame(frame);
    }

    private void OnControl(in ControlFrameHeader header, ReadOnlySpan<byte> payload)
    {
        switch (header.Type)
        {
            case ControlFrameType.Hello:
            {
                string text = HelloText;
                byte[] reply = new byte[1 + text.Length];
                reply[0] = 1;
                Encoding.ASCII.GetBytes(text, 0, text.Length, reply, 1);
                _transport.InjectControl(ControlFrameType.HelloReply, header.Id, (byte)ControlFrameFlags.Last, reply);
                break;
            }

            case ControlFrameType.Command:
                RunCommand(Encoding.ASCII.GetString(payload), framed: true, header.Id);
                break;

            case ControlFrameType.Ping:
                _transport.InjectControl(ControlFrameType.Reply, header.Id, (byte)ControlFrameFlags.Last, ReadOnlySpan<byte>.Empty);
                break;

            default:
                _transport.InjectControl(ControlFrameType.Nak, header.Id, (byte)ControlFrameFlags.Last,
                                         new byte[] { (byte)NakReason.UnknownType });
                break;
        }
    }

    private void RunCommand(string line, bool framed, byte id = 0)
    {
        Commands.Add(line);
        string reply;
        bool error = false;
        if (line.StartsWith("BAUD ", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(line.Substring(5), out uint rate))
        {
            Baud = rate;
            reply = "Baud rate set to " + rate + "\r\n";
        }
        else if (string.Equals(line, "EXIT", StringComparison.OrdinalIgnoreCase))
        {
            reply = "Exiting debug mode...\r\n";
            _inConsole = false;
            _decoder.Reset();
        }
        else if (string.Equals(line, "LOG", StringComparison.OrdinalIgnoreCase) && Flavour != Kind.TextConsoleOnly)
        {
            reply = "Log level 0 (OFF)\r\n";   // HDLCGW has LOG; RC422Dev answers Unknown command
        }
        else if (string.Equals(line, "STAT", StringComparison.OrdinalIgnoreCase))
        {
            reply = "HDLC frames   Total#   OK    BAD     BYTES\r\nSENT " + _dataFrames + "\r\n";
        }
        else
        {
            reply = "Unknown command '" + line + "'. Type HELP for a list of commands.\r\n";
            error = true;
        }

        if (framed)
        {
            byte flags = (byte)ControlFrameFlags.Last;
            if (error)
            {
                flags |= (byte)ControlFrameFlags.Error;
            }
            _transport.InjectControl(ControlFrameType.Reply, id, flags, Encoding.ASCII.GetBytes(reply));
        }
        else
        {
            _transport.InjectText(line + "\r\n" + reply);
        }
    }
}
