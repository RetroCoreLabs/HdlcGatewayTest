// PENDING FRAMES - the frames sent and not yet seen again, in order.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// What happened when a received sequence number was matched against the pending list.
/// </summary>
public enum AcknowledgeResult
{
    /// <summary>It was the oldest pending frame. Nothing was skipped.</summary>
    InOrder,

    /// <summary>
    /// It was pending but not the oldest: every older pending frame is gone and has been
    /// counted as lost.
    /// </summary>
    SkippedOlder,

    /// <summary>Older than anything pending: a duplicate, or a frame already written off as lost.</summary>
    Late,

    /// <summary>Newer than anything sent, or nothing pending: cannot be one of ours.</summary>
    Unknown,
}

/// <summary>
/// A fixed-size queue of the frames in flight: sequence number, when it was sent and how many
/// bytes it took on the wire. Sequence numbers are handed in strictly increasing, which is
/// what lets a received number be classified in one pass from the oldest end.
/// </summary>
public sealed class PendingFrames
{
    private readonly uint[] _sequence;
    private readonly long[] _sentTicks;
    private readonly int[] _bytes;
    private int _head;
    private int _count;
    private long _bytesInFlight;
    private uint _lastAdded;
    private bool _anyAdded;

    /// <summary>
    /// Create the queue.
    /// </summary>
    /// <param name="capacity">Most frames in flight at once.</param>
    public PendingFrames(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _sequence = new uint[capacity];
        _sentTicks = new long[capacity];
        _bytes = new int[capacity];
    }

    /// <summary>Frames in flight.</summary>
    public int Count => _count;

    /// <summary>Room left.</summary>
    public bool IsFull => _count == _sequence.Length;

    /// <summary>Sum of the wire bytes of every frame in flight.</summary>
    public long BytesInFlight => _bytesInFlight;

    /// <summary>
    /// Record a sent frame.
    /// </summary>
    /// <param name="sequence">Its number; must be greater than the previous one.</param>
    /// <param name="sentTicks">When it was sent, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    /// <param name="bytes">Bytes it took on the wire (encoded length).</param>
    /// <exception cref="InvalidOperationException">The queue is full.</exception>
    /// <exception cref="ArgumentException">The number is not increasing.</exception>
    public void Add(uint sequence, long sentTicks, int bytes)
    {
        if (IsFull)
        {
            throw new InvalidOperationException("Pending queue is full.");
        }
        if (_anyAdded && sequence <= _lastAdded)
        {
            throw new ArgumentException("Sequence numbers must increase.", nameof(sequence));
        }
        int index = (_head + _count) % _sequence.Length;
        _sequence[index] = sequence;
        _sentTicks[index] = sentTicks;
        _bytes[index] = bytes;
        _count++;
        _bytesInFlight += bytes;
        _lastAdded = sequence;
        _anyAdded = true;
    }

    /// <summary>
    /// A frame with this number came back. Removes it and everything older.
    /// </summary>
    /// <param name="sequence">The number read from the received frame.</param>
    /// <param name="lost">How many older pending frames were skipped and are now counted lost.</param>
    /// <param name="roundTripTicks">Ticks between sending and now for the matched frame; 0 if not matched.</param>
    /// <param name="nowTicks">The current time in Stopwatch ticks.</param>
    /// <returns>How the number related to the queue.</returns>
    public AcknowledgeResult Acknowledge(uint sequence, long nowTicks, out int lost, out long roundTripTicks)
    {
        lost = 0;
        roundTripTicks = 0;
        if (_count == 0)
        {
            return AcknowledgeResult.Unknown;
        }
        if (sequence > _lastAdded)
        {
            return AcknowledgeResult.Unknown;
        }
        if (sequence < _sequence[_head])
        {
            return AcknowledgeResult.Late;
        }

        while (_count > 0 && _sequence[_head] < sequence)
        {
            RemoveHead();
            lost++;
        }
        // The head is now the frame itself (numbers increase, and it is <= _lastAdded).
        roundTripTicks = nowTicks - _sentTicks[_head];
        RemoveHead();
        return lost == 0 ? AcknowledgeResult.InOrder : AcknowledgeResult.SkippedOlder;
    }

    /// <summary>
    /// Give up on frames sent before <paramref name="deadlineTicks"/>.
    /// </summary>
    /// <param name="deadlineTicks">Frames sent at or before this time are removed.</param>
    /// <returns>How many were removed; the caller counts them as lost.</returns>
    public int ExpireSentBefore(long deadlineTicks)
    {
        int expired = 0;
        while (_count > 0 && _sentTicks[_head] <= deadlineTicks)
        {
            RemoveHead();
            expired++;
        }
        return expired;
    }

    /// <summary>Forget everything in flight and allow the numbering to start over.</summary>
    public void Clear()
    {
        _head = 0;
        _count = 0;
        _bytesInFlight = 0;
        _lastAdded = 0;
        _anyAdded = false;
    }

    private void RemoveHead()
    {
        _bytesInFlight -= _bytes[_head];
        _head = (_head + 1) % _sequence.Length;
        _count--;
    }
}
