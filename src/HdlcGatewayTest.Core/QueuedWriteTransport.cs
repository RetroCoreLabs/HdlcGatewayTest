// QUEUED WRITE TRANSPORT - a transport whose writes go through a queue and its own thread.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// Wraps a transport so that <see cref="Write"/> never blocks the caller: the bytes are queued
/// and a thread of this transport's own writes them to the inner transport in order.
/// <para>
/// Why: a serial write returns only when the driver has taken the bytes, and with a slow
/// reader at the far end one write took up to 74 ms (Pico at 983000 bit/s, bench 2026-11-09).
/// The traffic engine and the window pump both ports from one thread, so a blocked write to
/// one port stopped the reading of the other, the ESP32's USB ring filled and it dropped 160
/// frames in one run (its "USB TX frames dropped" counter). With the queue the pump thread
/// keeps reading both ports while each port's writer waits on its own.
/// </para>
/// <para>
/// Reads, counters and events pass straight through; <see cref="Writes"/> is the inner
/// transport's meter, so "time in Write()" still measures the real blocking writes.
/// <see cref="QueuedBytes"/> says how much is waiting for the writer thread.
/// </para>
/// </summary>
public sealed class QueuedWriteTransport : IGatewayTransport
{
    private readonly IGatewayTransport _inner;
    private readonly Queue<byte[]> _queue = new Queue<byte[]>();
    private readonly object _lock = new object();
    private readonly int _maxQueuedBytes;
    private Thread? _writer;
    private long _queuedBytes;
    private long _droppedWrites;
    private volatile bool _closing;

    /// <summary>
    /// Wrap a transport.
    /// </summary>
    /// <param name="inner">The transport that does the real reading and writing.</param>
    /// <param name="maxQueuedBytes">
    /// Most bytes waiting for the writer thread; a write beyond that is dropped whole and
    /// counted in <see cref="DroppedWrites"/>. The traffic engine's window keeps the queue far
    /// below this; the bound only protects memory when the far end stops reading for good.
    /// </param>
    public QueuedWriteTransport(IGatewayTransport inner, int maxQueuedBytes = 1 << 20)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxQueuedBytes = Math.Max(1024, maxQueuedBytes);
    }

    /// <summary>The wrapped transport.</summary>
    public IGatewayTransport Inner => _inner;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public bool IsOpen => _inner.IsOpen;

    /// <inheritdoc/>
    public long ReceiveOverflow => _inner.ReceiveOverflow;

    /// <inheritdoc/>
    public long DriverErrors => _inner.DriverErrors;

    /// <inheritdoc/>
    public long BytesReceived => _inner.BytesReceived;

    /// <inheritdoc/>
    public WriteMeter Writes => _inner.Writes;

    /// <inheritdoc/>
    public bool FramesOnly => _inner.FramesOnly;

    /// <summary>Bytes queued and not yet handed to the inner transport.</summary>
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    /// <inheritdoc/>
    public long QueuedWriteBytes => QueuedBytes;

    /// <inheritdoc/>
    public string DescribeReads() => _inner.DescribeReads();

    /// <inheritdoc/>
    public void ResetReadStatistics() => _inner.ResetReadStatistics();

    /// <summary>Writes dropped because the queue held <c>maxQueuedBytes</c> already.</summary>
    public long DroppedWrites => Interlocked.Read(ref _droppedWrites);

    /// <inheritdoc/>
    public event Action<Exception>? Faulted
    {
        add => _inner.Faulted += value;
        remove => _inner.Faulted -= value;
    }

    /// <inheritdoc/>
    public void Open()
    {
        _inner.Open();
        _closing = false;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "write " + Name };
        _writer.Start();
    }

    /// <inheritdoc/>
    public int Read(Span<byte> destination)
    {
        return _inner.Read(destination);
    }

    /// <summary>
    /// Queue the bytes for the writer thread. Returns at once. Order is kept.
    /// </summary>
    /// <param name="data">The bytes.</param>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }
        if (_writer == null)
        {
            // Not opened through this wrapper (tests, or a transport used before Open): write
            // straight through so nothing is silently lost.
            _inner.Write(data);
            return;
        }
        byte[] copy = data.ToArray();
        lock (_lock)
        {
            if (_queuedBytes + copy.Length > _maxQueuedBytes)
            {
                Interlocked.Increment(ref _droppedWrites);
                return;
            }
            _queue.Enqueue(copy);
            Interlocked.Add(ref _queuedBytes, copy.Length);
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>
    /// Wait until everything queued so far has been written, or the time runs out.
    /// </summary>
    /// <param name="milliseconds">How long to wait at most.</param>
    /// <returns>True when the queue drained.</returns>
    public bool Flush(int milliseconds)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (QueuedBytes > 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(1);
        }
        return QueuedBytes == 0;
    }

    /// <summary>Drain what is queued (up to a second), stop the writer, close the inner transport.</summary>
    public void Dispose()
    {
        Flush(1000);
        _closing = true;
        lock (_lock)
        {
            Monitor.PulseAll(_lock);
        }
        _writer?.Join(1000);
        _inner.Dispose();
    }

    private void WriteLoop()
    {
        while (!_closing)
        {
            byte[]? block;
            lock (_lock)
            {
                while (_queue.Count == 0 && !_closing)
                {
                    Monitor.Wait(_lock);
                }
                if (_closing && _queue.Count == 0)
                {
                    return;
                }
                block = _queue.Dequeue();
            }
            try
            {
                _inner.Write(block);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                // The port is gone; the inner transport's reader raises Faulted for the session.
                // Nothing more can be written, so stop here and let the queue be dropped.
                Interlocked.Exchange(ref _queuedBytes, 0);
                return;
            }
            finally
            {
                Interlocked.Add(ref _queuedBytes, -block.Length);
            }
        }
    }
}
