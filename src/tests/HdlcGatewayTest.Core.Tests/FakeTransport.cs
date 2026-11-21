// FAKE TRANSPORT - an in-memory IGatewayTransport for tests: scripted answers, loopback, loss.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core.Tests;

/// <summary>
/// A transport whose far end is code. Bytes written go to <see cref="OnWrite"/>; whatever the
/// test wants the gateway to "send back" is queued with <see cref="Inject"/> and comes out of
/// <see cref="Read"/>. Thread safe, because the engine and a test may touch it from two threads.
/// </summary>
public sealed class FakeTransport : IGatewayTransport
{
    private readonly Queue<byte> _incoming = new Queue<byte>();
    private readonly object _lock = new object();

    /// <summary>Called with every write, on the writer's thread.</summary>
    public Action<byte[]>? OnWrite { get; set; }

    /// <summary>Everything written so far, for assertions.</summary>
    public List<byte[]> WrittenBlocks { get; } = new List<byte[]>();

    /// <inheritdoc/>
    public string Name { get; set; } = "FAKE";

    /// <inheritdoc/>
    public bool IsOpen { get; private set; }

    /// <inheritdoc/>
    public long ReceiveOverflow => 0;

    /// <summary>Settable: true imitates a TCP endpoint that carries frames only.</summary>
    public bool FramesOnly { get; set; }

    /// <inheritdoc/>
    public long DriverErrors => 0;

    /// <inheritdoc/>
    public long BytesReceived => _bytesReceived;

    /// <inheritdoc/>
    public WriteMeter Writes { get; } = new WriteMeter();

    private long _bytesReceived;

    /// <inheritdoc/>
    public event Action<Exception>? Faulted;

    /// <inheritdoc/>
    public void Open()
    {
        IsOpen = true;
    }

    /// <inheritdoc/>
    public int Read(Span<byte> destination)
    {
        lock (_lock)
        {
            int n = 0;
            while (n < destination.Length && _incoming.Count > 0)
            {
                destination[n++] = _incoming.Dequeue();
            }
            _bytesReceived += n;
            return n;
        }
    }

    /// <inheritdoc/>
    public void Write(ReadOnlySpan<byte> data)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[] copy = data.ToArray();
        lock (_lock)
        {
            WrittenBlocks.Add(copy);
        }
        OnWrite?.Invoke(copy);
        Writes.Record(start, copy.Length);
    }

    /// <summary>Queue bytes the "gateway" sends to the host.</summary>
    /// <param name="bytes">The bytes.</param>
    public void Inject(ReadOnlySpan<byte> bytes)
    {
        lock (_lock)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                _incoming.Enqueue(bytes[i]);
            }
        }
    }

    /// <summary>Queue text the "gateway" prints.</summary>
    /// <param name="text">Latin-1 text.</param>
    public void InjectText(string text)
    {
        Inject(System.Text.Encoding.Latin1.GetBytes(text));
    }

    /// <summary>Queue one byte-stuffed frame.</summary>
    /// <param name="frame">The frame, check bytes included.</param>
    public void InjectFrame(ReadOnlySpan<byte> frame)
    {
        byte[] buf = new byte[ByteFrame.MaxEncodedLength(frame.Length)];
        int n = ByteFrame.Encode(frame, buf);
        Inject(new ReadOnlySpan<byte>(buf, 0, n));
    }

    /// <summary>Queue one control frame from the "firmware".</summary>
    public void InjectControl(ControlFrameType type, byte id, byte flags, ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[payload.Length + ControlFrame.Overhead];
        int n = ControlFrame.Encode(type, id, flags, payload, frame);
        InjectFrame(new ReadOnlySpan<byte>(frame, 0, n));
    }

    /// <summary>Raise <see cref="Faulted"/>.</summary>
    /// <param name="ex">The error.</param>
    public void Fail(Exception ex)
    {
        IsOpen = false;
        Faulted?.Invoke(ex);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        IsOpen = false;
    }
}
