// TCP LISTENER TRANSPORT - waits for one TCP peer (nd100x) and moves raw bytes to and from it.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Net;
using System.Net.Sockets;

namespace HdlcGatewayTest.Core;

/// <summary>
/// An <see cref="IGatewayTransport"/> that listens on a TCP port for the nd100x emulator (or a
/// gateway in WiFi client mode). nd100x dials with <c>--hdlc=N:HOST:PORT</c> and sends the same
/// byte-stuffed HDLC stream the gateways speak on USB, FCS on the wire, nothing else. One peer
/// at a time: a new connection replaces the old one (logged through <see cref="PeerChanged"/>),
/// like nd100x's own server does. Writes while no peer is connected are dropped and counted.
/// Reads on a thread per connection into a queue, same shape as the serial transport.
/// </summary>
public sealed class TcpListenerTransport : IGatewayTransport
{
    private const int QueueSize = 1 << 20;

    private readonly byte[] _queue = new byte[QueueSize];
    private readonly object _lock = new object();
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _acceptThread;
    private int _queueHead;
    private int _queueCount;
    private long _queueOverflow;
    private long _droppedWrites;
    private volatile bool _closing;
    private long _bytesReceived;
    private int _generation;

    /// <summary>
    /// Prepare a listener. Nothing is opened until <see cref="Open"/>.
    /// </summary>
    /// <param name="port">TCP port to listen on, all interfaces.</param>
    public TcpListenerTransport(int port)
    {
        if (port < 1 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        Port = port;
        Name = "tcp:" + port;
    }

    /// <summary>The port.</summary>
    public int Port { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool IsOpen => _listener != null && !_closing;

    /// <summary>True while a peer is connected.</summary>
    public bool HasPeer => _stream != null;

    /// <summary>The connected peer's address, or null.</summary>
    public string? PeerName { get; private set; }

    /// <inheritdoc/>
    public long ReceiveOverflow => _queueOverflow;

    /// <summary>Writes thrown away because no peer was connected.</summary>
    public long DroppedWrites => _droppedWrites;

    /// <inheritdoc/>
    public bool FramesOnly => true;

    /// <inheritdoc/>
    public long DriverErrors => 0;

    /// <inheritdoc/>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <inheritdoc/>
    public event Action<Exception>? Faulted;

    /// <summary>A peer connected (name) or went away (null). Raised on the accept or reader thread.</summary>
    public event Action<string?>? PeerChanged;

    /// <inheritdoc/>
    public void Open()
    {
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start(1);
        _closing = false;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "accept " + Name };
        _acceptThread.Start();
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
        NetworkStream? stream = _stream;
        if (stream == null)
        {
            Interlocked.Increment(ref _droppedWrites);
            return;
        }
        try
        {
            stream.Write(data);
        }
        catch (IOException)
        {
            Interlocked.Increment(ref _droppedWrites);
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Increment(ref _droppedWrites);
        }
    }

    /// <summary>Drop the current peer, if any; the listener keeps accepting.</summary>
    public void DropPeer()
    {
        ClosePeer(Interlocked.Increment(ref _generation));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _closing = true;
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // Already gone.
        }
        ClosePeer(Interlocked.Increment(ref _generation));
        _acceptThread?.Join(1000);
        _listener = null;
    }

    private void AcceptLoop()
    {
        TcpListener listener = _listener!;
        while (!_closing)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (SocketException ex)
            {
                if (!_closing)
                {
                    Faulted?.Invoke(ex);
                }
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            int generation = Interlocked.Increment(ref _generation);
            ClosePeer(generation - 1);
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            lock (_lock)
            {
                _client = client;
                _stream = stream;
                _queueHead = 0;
                _queueCount = 0;
                PeerName = client.Client.RemoteEndPoint?.ToString();
            }
            PeerChanged?.Invoke(PeerName);
            Thread reader = new Thread(() => ReadLoop(stream, generation))
            {
                IsBackground = true,
                Name = "read " + Name,
            };
            reader.Start();
        }
    }

    private void ReadLoop(NetworkStream stream, int generation)
    {
        byte[] buf = new byte[4096];
        while (!_closing && generation == _generation)
        {
            int n;
            try
            {
                n = stream.Read(buf, 0, buf.Length);
            }
            catch (IOException)
            {
                n = 0;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (n <= 0)
            {
                if (generation == _generation)
                {
                    ClosePeer(generation);
                }
                return;
            }
            Interlocked.Add(ref _bytesReceived, n);
            lock (_lock)
            {
                if (generation != _generation)
                {
                    return;
                }
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

    /// <summary>Close the peer that belongs to a generation, if it is still the current one.</summary>
    private void ClosePeer(int generation)
    {
        TcpClient? client;
        lock (_lock)
        {
            if (_client == null || generation < _generation - 1)
            {
                return;
            }
            client = _client;
            _client = null;
            _stream = null;
            PeerName = null;
        }
        try
        {
            client.Close();
        }
        catch (SocketException)
        {
            // Nothing to do; it is gone.
        }
        PeerChanged?.Invoke(null);
    }
}
