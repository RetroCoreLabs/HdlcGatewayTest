// SERIAL TRANSPORT - one gateway's USB serial port as an IGatewayTransport.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.IO.Ports;
using HdlcGatewayTest.Core;

namespace HdlcGatewayTest.Serial;

/// <summary>
/// Opens a gateway's serial port and reads it continuously on its own thread into a queue.
/// <para>
/// Why DTR and RTS are set before Open: the ESP32 Arduino core treats DTR/RTS changes as
/// signals. Both high together means "a host is connected" and lets the firmware write to
/// the port; a particular dance of the two lines (USBCDC::_onLineState) reboots the board
/// into its ROM bootloader. Setting both before the port opens gives the firmware one clean
/// transition and nothing it can mistake for that dance.
/// </para>
/// <para>
/// Why a blocking read on a dedicated thread: on Windows the async read of a quiet serial
/// port can hang forever (RetroTerm's SerialConnection documents it); a blocking
/// <c>BaseStream.Read</c> on its own thread, stopped by closing the port, does not.
/// </para>
/// </summary>
public sealed class SerialTransport : IGatewayTransport
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
    private long _driverErrors;
    private long _bytesReceived;
    private string _lastDriverError = string.Empty;

    /// <summary>
    /// The driver reported a receive error. RXOver = its own receive buffer overflowed (the PC
    /// read too slowly), Overrun = a character was lost in the hardware, Frame/RXParity = line
    /// errors (not possible on USB CDC). Counted; the frame they belong to shows up as missing
    /// or as a bad FCS.
    /// </summary>
    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        Interlocked.Increment(ref _driverErrors);
        _lastDriverError = e.EventType.ToString();
    }

    /// <summary>
    /// Prepare the port. Nothing is opened until <see cref="Open"/>.
    /// </summary>
    /// <param name="name">The port name, for example COM10.</param>
    /// <param name="baud">USB CDC ignores it, but a real UART would not; 115200 by default.</param>
    public SerialTransport(string name, int baud = 115200)
    {
        Name = name;
        _port = new SerialPort(name, baud, Parity.None, 8, StopBits.One)
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

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool IsOpen => _port.IsOpen && !_closing;

    /// <inheritdoc/>
    public long ReceiveOverflow => _queueOverflow;

    /// <inheritdoc/>
    public bool FramesOnly => false;

    /// <inheritdoc/>
    public long DriverErrors => _driverErrors;

    /// <inheritdoc/>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>The last driver error kind seen (RXOver, Overrun, Frame, ...), or empty.</summary>
    public string LastDriverError => _lastDriverError;

    /// <inheritdoc/>
    public event Action<Exception>? Faulted;

    /// <inheritdoc/>
    public void Open()
    {
        _port.ErrorReceived += OnErrorReceived;
        _port.Open();
        _port.DiscardInBuffer();
        _closing = false;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "read " + Name };
        _reader.Start();
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public void Write(ReadOnlySpan<byte> data)
    {
        _port.BaseStream.Write(data);
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
            catch (IOException ex)
            {
                Faulted?.Invoke(ex);    // port vanished; the main loop will notice nothing arrives
                return;
            }
            catch (InvalidOperationException ex)
            {
                Faulted?.Invoke(ex);
                return;
            }
            if (n <= 0)
            {
                continue;
            }
            Interlocked.Add(ref _bytesReceived, n);
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
