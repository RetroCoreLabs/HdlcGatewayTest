// GATEWAY PORT - one gateway's USB serial port: raw bytes, plus the debug console on top.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using HdlcGatewayTest.Core;

namespace HdlcGatewayTest.Loopback;

/// <summary>
/// Opens a gateway's serial port, reads it continuously on its own thread into a queue, and
/// knows the three-ESC console well enough to set the transmit clock and leave again.
/// <para>
/// Why DTR and RTS are set before Open: the ESP32 Arduino core treats DTR/RTS changes as
/// signals. Both high together means "a host is connected" and lets the firmware write to
/// the port; a particular dance of the two lines (USBCDC::_onLineState) reboots the board
/// into its ROM bootloader. Setting both before the port opens gives the firmware one clean
/// transition and nothing it can mistake for that dance.
/// </para>
/// </summary>
public sealed class GatewayPort : IDisposable
{
    private const int QueueSize = 1 << 20;

    private readonly SerialPort _port;
    private readonly byte[] _queue = new byte[QueueSize];
    private readonly object _lock = new object();
    private int _queueHead;
    private int _queueCount;
    private long _queueOverflow;
    private Thread? _reader;
    private volatile bool _closing;

    /// <summary>
    /// Prepare the port. Nothing is opened until <see cref="Open"/>.
    /// </summary>
    /// <param name="name">The port name, for example COM10.</param>
    public GatewayPort(string name)
    {
        Name = name;
        _port = new SerialPort(name, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 5000,
            ReadBufferSize = 1 << 16,
            WriteBufferSize = 1 << 16,
        };
    }

    /// <summary>The port name.</summary>
    public string Name { get; }

    /// <summary>Bytes that arrived while the queue was full and were thrown away.</summary>
    public long QueueOverflow => _queueOverflow;

    /// <summary>Open the port and start the reader thread.</summary>
    public void Open()
    {
        _port.Open();
        _port.DiscardInBuffer();
        _closing = false;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "read " + Name };
        _reader.Start();
    }

    /// <summary>
    /// Take up to <paramref name="destination"/>.Length bytes received so far. Never blocks.
    /// </summary>
    /// <param name="destination">Where the bytes go.</param>
    /// <returns>How many were copied, 0 when nothing is waiting.</returns>
    public int Read(Span<byte> destination)
    {
        lock (_lock)
        {
            int n = Math.Min(destination.Length, _queueCount);
            for (int i = 0; i < n; i++)
            {
                destination[i] = _queue[_queueHead];
                _queueHead = (_queueHead + 1) % QueueSize;
            }
            _queueCount -= n;
            return n;
        }
    }

    /// <summary>
    /// Write bytes to the port. Blocks until the driver has taken them.
    /// </summary>
    /// <param name="data">The bytes.</param>
    public void Write(ReadOnlySpan<byte> data)
    {
        _port.BaseStream.Write(data);
    }

    /// <summary>
    /// Write a text line to the console: the text followed by CR, which both gateways take as
    /// the end of a command.
    /// </summary>
    /// <param name="text">The command without line ending.</param>
    public void WriteLine(string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text + "\r");
        Write(bytes);
    }

    /// <summary>
    /// Read whatever arrives for a while and return it as text. Used to drain console output
    /// before the binary traffic starts.
    /// </summary>
    /// <param name="milliseconds">How long to keep reading.</param>
    /// <returns>Everything read, bytes mapped one to one to characters.</returns>
    public string ReadTextFor(int milliseconds)
    {
        StringBuilder sb = new StringBuilder();
        byte[] buf = new byte[4096];
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * milliseconds / 1000;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            int n = Read(buf);
            if (n > 0)
            {
                sb.Append(Encoding.Latin1.GetString(buf, 0, n));
            }
            else
            {
                Thread.Sleep(5);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Read until the text contains <paramref name="expected"/> or the time is up.
    /// </summary>
    /// <param name="expected">Text to wait for, compared case-insensitively.</param>
    /// <param name="milliseconds">How long to wait at most.</param>
    /// <param name="transcript">Everything read while waiting.</param>
    /// <returns>True when the text appeared.</returns>
    public bool WaitForText(string expected, int milliseconds, out string transcript)
    {
        StringBuilder sb = new StringBuilder();
        byte[] buf = new byte[4096];
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * milliseconds / 1000;
        bool found = false;
        while (!found && Stopwatch.GetTimestamp() < deadline)
        {
            int n = Read(buf);
            if (n > 0)
            {
                sb.Append(Encoding.Latin1.GetString(buf, 0, n));
                found = sb.ToString().Contains(expected, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Thread.Sleep(5);
            }
        }
        transcript = sb.ToString();
        return found;
    }

    /// <summary>
    /// Open the debug console: three ESC bytes while the gateway is idle in forwarding mode.
    /// Both gateways answer with a banner line and a prompt ending in '#' or '@'.
    /// </summary>
    /// <param name="transcript">What the gateway printed.</param>
    /// <returns>True when a prompt appeared.</returns>
    public bool EnterConsole(out string transcript)
    {
        // Anything the gateway printed earlier (a version banner, a stale prompt) is not ours.
        _ = ReadTextFor(200);

        // The gateway may be in any of its three modes from an earlier session, and the
        // firmware only looks for ESC ESC ESC while its byte-frame decoder is between frames.
        // Any stray byte (even a CR) counts as the start of a frame and hides the ESCs, which
        // is why the probe below starts with a flag: a flag always puts the decoder back to
        // "between frames". In DEBUG the flag and the ESCs are just echoed keystrokes.
        //
        //   FORWARDING: flag, ESC ESC ESC -> prompt.
        //   DEBUG:      the bytes land in the input line; CR runs that junk line
        //               ("Unknown command") and prints a prompt.
        //   PACKET_GEN: ESC ESC ESC switches to FORWARDING; the CR then pollutes the
        //               decoder, so the last step repeats flag + ESC ESC ESC.
        Write(new byte[] { ByteFrame.Flag, 0x1B, 0x1B, 0x1B });
        if (WaitForPrompt(1000, out transcript))
        {
            return true;
        }
        Write(new byte[] { 0x0D });
        if (WaitForPrompt(600, out transcript))
        {
            return true;
        }
        Write(new byte[] { ByteFrame.Flag, 0x1B, 0x1B, 0x1B });
        return WaitForPrompt(2000, out transcript);
    }

    /// <summary>
    /// Wait for a console prompt. HDLCGW prints "# " or "@ " (HDLC decode on), RC422Dev the
    /// same with a '>' before the space when echo is on; either may be preceded by "[rx:tx]".
    /// </summary>
    /// <param name="milliseconds">How long to wait at most.</param>
    /// <param name="transcript">Everything read while waiting.</param>
    /// <returns>True when a prompt appeared.</returns>
    public bool WaitForPrompt(int milliseconds, out string transcript)
    {
        StringBuilder sb = new StringBuilder();
        byte[] buf = new byte[4096];
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * milliseconds / 1000;
        bool found = false;
        while (!found && Stopwatch.GetTimestamp() < deadline)
        {
            int n = Read(buf);
            if (n > 0)
            {
                sb.Append(Encoding.Latin1.GetString(buf, 0, n));
                string s = sb.ToString();
                found = s.EndsWith("# ", StringComparison.Ordinal) || s.EndsWith("@ ", StringComparison.Ordinal)
                        || s.EndsWith("#> ", StringComparison.Ordinal) || s.EndsWith("@> ", StringComparison.Ordinal);
            }
            else
            {
                Thread.Sleep(5);
            }
        }
        transcript = sb.ToString();
        return found;
    }

    /// <summary>
    /// Run one console command and wait for a text in the answer.
    /// </summary>
    /// <param name="command">The command line without CR.</param>
    /// <param name="expected">Text that marks success.</param>
    /// <param name="transcript">What the gateway printed.</param>
    /// <returns>True when <paramref name="expected"/> appeared within two seconds.</returns>
    public bool Command(string command, string expected, out string transcript)
    {
        WriteLine(command);
        return WaitForText(expected, 2000, out transcript);
    }

    /// <summary>
    /// Leave the console with EXIT and throw away the text the gateway prints on the way out,
    /// so the first byte the traffic decoder sees is a flag.
    /// </summary>
    public void ExitConsole()
    {
        WriteLine("EXIT");
        _ = ReadTextFor(300);
    }

    /// <summary>Close the port and stop the reader thread.</summary>
    public void Dispose()
    {
        _closing = true;
        try
        {
            if (_port.IsOpen)
            {
                _port.Close();
            }
        }
        catch (IOException)
        {
            // The port may already be gone (board reset); nothing left to close.
        }
        _reader?.Join(1000);
        _port.Dispose();
    }

    private void ReadLoop()
    {
        byte[] buf = new byte[4096];
        Stream stream = _port.BaseStream;
        while (!_closing)
        {
            int n;
            try
            {
                n = stream.Read(buf, 0, buf.Length);
            }
            catch (Exception) when (_closing)
            {
                return;
            }
            catch (IOException)
            {
                return;     // port vanished; the main loop will notice nothing arrives
            }
            catch (InvalidOperationException)
            {
                return;
            }
            if (n <= 0)
            {
                continue;
            }
            lock (_lock)
            {
                for (int i = 0; i < n; i++)
                {
                    if (_queueCount == QueueSize)
                    {
                        _queueOverflow += n - i;
                        break;
                    }
                    _queue[(_queueHead + _queueCount) % QueueSize] = buf[i];
                    _queueCount++;
                }
            }
        }
    }
}
