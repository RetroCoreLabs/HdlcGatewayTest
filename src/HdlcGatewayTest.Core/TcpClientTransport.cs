// TCP CLIENT TRANSPORT - dials a gateway in WiFi mode (or any byte-stuffed HDLC server) over TCP.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Net.Sockets;

namespace HdlcGatewayTest.Core;

/// <summary>
/// An <see cref="IGatewayTransport"/> that connects to a host and port: the HDLCGW in WiFi mode
/// listening on 1362 (<c>hdlcgw.local:1362</c>), or nd100x started as a server. The stream is the
/// same byte-stuffed HDLC the gateways speak on USB. Reads on a thread into a queue, the same
/// shape as the serial transport; a dropped connection raises <see cref="Faulted"/> and reads
/// return nothing more. Reconnecting is the caller's business (Connect again).
/// </summary>
public sealed class TcpClientTransport : IGatewayTransport
{
    private const int QueueSize = 1 << 20;

    private readonly byte[] _queue = new byte[QueueSize];
    private readonly object _lock = new object();
    private readonly int _connectTimeoutMs;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _reader;
    private int _queueHead;
    private int _queueCount;
    private long _queueOverflow;
    private volatile bool _closing;
    private long _bytesReceived;

    /// <summary>
    /// Prepare a connection. Nothing is dialled until <see cref="Open"/>.
    /// </summary>
    /// <param name="host">Host name or address.</param>
    /// <param name="port">TCP port.</param>
    /// <param name="connectTimeoutMs">How long <see cref="Open"/> waits for the connection.</param>
    public TcpClientTransport(string host, int port, int connectTimeoutMs = 5000)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("A host is needed.", nameof(host));
        }
        if (port < 1 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        Host = host.Trim();
        Port = port;
        Name = Host + ":" + port;
        _connectTimeoutMs = connectTimeoutMs;
    }

    /// <summary>The host as given.</summary>
    public string Host { get; }

    /// <summary>The port.</summary>
    public int Port { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool IsOpen => _stream != null && !_closing;

    /// <inheritdoc/>
    public long ReceiveOverflow => _queueOverflow;

    /// <inheritdoc/>
    public bool FramesOnly => true;

    /// <inheritdoc/>
    public long DriverErrors => 0;

    /// <inheritdoc/>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <inheritdoc/>
    public event Action<Exception>? Faulted;

    /// <summary>
    /// Parse "host:port". IPv6 literals are not handled (no gateway here has one).
    /// </summary>
    /// <param name="endpoint">The text.</param>
    /// <param name="host">The host part.</param>
    /// <param name="port">The port part.</param>
    /// <returns>True when the text is host:port with a port 1..65535.</returns>
    public static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon == endpoint.Length - 1)
        {
            return false;
        }
        if (!int.TryParse(endpoint.Substring(colon + 1), out port) || port < 1 || port > 65535)
        {
            return false;
        }
        host = endpoint.Substring(0, colon).Trim();
        return host.Length > 0;
    }

    /// <inheritdoc/>
    /// <exception cref="IOException">The host did not answer within the timeout or refused.</exception>
    public void Open()
    {
        TcpClient client = new TcpClient { NoDelay = true };
        try
        {
            Task connect = client.ConnectAsync(Host, Port);
            if (!connect.Wait(_connectTimeoutMs))
            {
                client.Dispose();
                throw new IOException("No answer from " + Name + " within " + _connectTimeoutMs + " ms.");
            }
        }
        catch (AggregateException ex)
        {
            client.Dispose();
            throw new IOException("Cannot connect to " + Name + ": " + ex.GetBaseException().Message, ex.GetBaseException());
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new IOException("Cannot connect to " + Name + ": " + ex.Message, ex);
        }
        _client = client;
        _stream = client.GetStream();
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
        NetworkStream? stream = _stream;
        if (stream == null)
        {
            throw new IOException(Name + " is not connected.");
        }
        stream.Write(data);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _closing = true;
        NetworkStream? stream = _stream;
        _stream = null;
        try
        {
            stream?.Dispose();
            _client?.Close();
        }
        catch (IOException)
        {
            // Already gone.
        }
        _reader?.Join(1000);
        _client = null;
    }

    private void ReadLoop()
    {
        byte[] buf = new byte[4096];
        NetworkStream? stream = _stream;
        if (stream == null)
        {
            return;
        }
        while (!_closing)
        {
            int n;
            try
            {
                n = stream.Read(buf, 0, buf.Length);
            }
            catch (Exception ex) when (_closing || ex is ObjectDisposedException)
            {
                return;
            }
            catch (IOException ex)
            {
                _stream = null;
                Faulted?.Invoke(ex);
                return;
            }
            if (n <= 0)
            {
                _stream = null;
                Faulted?.Invoke(new IOException(Name + " closed the connection."));
                return;
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
