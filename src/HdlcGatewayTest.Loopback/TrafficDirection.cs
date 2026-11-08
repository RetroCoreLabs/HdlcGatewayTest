// TRAFFIC DIRECTION - frames out of one port, checked as they come in on another (or the same).
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Diagnostics;
using HdlcGatewayTest.Core;

namespace HdlcGatewayTest.Loopback;

/// <summary>
/// One direction of traffic. Keeps the window full on the sending side, decodes everything
/// the receiving side delivers, and matches frames by sequence number. In loopback the two
/// ports are the same object; in a two-gateway test they are A and B.
/// </summary>
public sealed class TrafficDirection
{
    private readonly GatewayPort _source;
    private readonly GatewayPort _sink;
    private readonly byte _address;
    private readonly int _frameLength;
    private readonly int _windowBytes;
    private readonly long _timeoutTicks;
    private readonly ByteFrameDecoder _decoder;
    private readonly PendingFrames _pending;
    private readonly byte[] _frame;
    private readonly byte[] _encoded;
    private readonly byte[] _rx = new byte[8192];
    private uint _nextSequence;
    private long _lastDecoderDropped;

    /// <summary>
    /// Set up a direction.
    /// </summary>
    /// <param name="name">Shown in the status line, for example "A->B".</param>
    /// <param name="source">Port the frames are written to.</param>
    /// <param name="sink">Port the frames are expected back on.</param>
    /// <param name="address">Address byte of this direction's frames.</param>
    /// <param name="frameLength">Frame length including FCS.</param>
    /// <param name="windowBytes">Most encoded bytes in flight.</param>
    /// <param name="timeoutSeconds">Seconds before a missing frame counts as lost.</param>
    public TrafficDirection(string name, GatewayPort source, GatewayPort sink, byte address,
                            int frameLength, int windowBytes, int timeoutSeconds)
    {
        Name = name;
        _source = source;
        _sink = sink;
        _address = address;
        _frameLength = frameLength;
        _windowBytes = windowBytes;
        _timeoutTicks = Stopwatch.Frequency * timeoutSeconds;
        _decoder = new ByteFrameDecoder();
        // Enough slots that the window, not the slot count, is what limits traffic.
        _pending = new PendingFrames(Math.Max(16, (windowBytes / TestFrame.MinLength) + 2));
        _frame = new byte[frameLength];
        _encoded = new byte[ByteFrame.MaxEncodedLength(frameLength)];
    }

    /// <summary>Label for reports.</summary>
    public string Name { get; }

    /// <summary>The counters.</summary>
    public TrafficStatistics Statistics { get; } = new TrafficStatistics();

    /// <summary>Frames still in flight.</summary>
    public int Pending => _pending.Count;

    /// <summary>Start a new run: counters to zero, sequence numbers from 0.</summary>
    public void Reset()
    {
        Statistics.Clear();
        _pending.Clear();
        _decoder.Reset();
        _nextSequence = 0;
        _lastDecoderDropped = _decoder.DroppedFrames;
    }

    /// <summary>
    /// One step: take in what arrived, write off what timed out, and top up the window.
    /// </summary>
    /// <param name="nowTicks">Current Stopwatch timestamp.</param>
    /// <param name="sending">False during the drain phase, when nothing new goes out.</param>
    /// <returns>True when something was received or sent; false when there was nothing to do.</returns>
    public bool Pump(long nowTicks, bool sending)
    {
        bool busy = Receive(nowTicks);

        int expired = _pending.ExpireSentBefore(nowTicks - _timeoutTicks);
        Statistics.FramesLost += expired;

        if (!sending)
        {
            return busy;
        }

        while (!_pending.IsFull)
        {
            Span<byte> frame = TestFrame.Build(_nextSequence, _frameLength, _address, _frame);
            int encodedLength = ByteFrame.Encode(frame, _encoded);
            if (_pending.BytesInFlight + encodedLength > _windowBytes && _pending.Count > 0)
            {
                break;
            }
            _source.Write(new ReadOnlySpan<byte>(_encoded, 0, encodedLength));
            _pending.Add(_nextSequence, Stopwatch.GetTimestamp(), encodedLength);
            Statistics.FramesSent++;
            Statistics.BytesSent += frame.Length;
            Statistics.EncodedBytesSent += encodedLength;
            _nextSequence++;
            busy = true;
        }
        return busy;
    }

    private bool Receive(long nowTicks)
    {
        bool any = false;
        int n;
        while ((n = _sink.Read(_rx)) > 0)
        {
            any = true;
            for (int i = 0; i < n; i++)
            {
                if (_decoder.Feed(_rx[i]))
                {
                    Classify(_decoder.Frame, nowTicks);
                }
            }
        }
        long dropped = _decoder.DroppedFrames;
        if (dropped != _lastDecoderDropped)
        {
            Statistics.DecoderDropped += dropped - _lastDecoderDropped;
            _lastDecoderDropped = dropped;
        }
        return any;
    }

    private void Classify(ReadOnlySpan<byte> frame, long nowTicks)
    {
        Statistics.FramesReceived++;
        TestFrameVerdict verdict = TestFrame.Verify(frame, out uint sequence, out byte address);

        if (verdict == TestFrameVerdict.BadFcs)
        {
            Statistics.FramesBadFcs++;
            return;
        }
        if (verdict != TestFrameVerdict.Ok || address != _address)
        {
            Statistics.FramesCorrupt++;
            return;
        }

        AcknowledgeResult ack = _pending.Acknowledge(sequence, nowTicks, out int lost, out long rtt);
        switch (ack)
        {
        case AcknowledgeResult.InOrder:
        case AcknowledgeResult.SkippedOlder:
            Statistics.FramesLost += lost;
            Statistics.FramesOk++;
            Statistics.BytesOk += frame.Length;
            Statistics.RoundTripTicksTotal += rtt;
            if (rtt > Statistics.RoundTripTicksMax)
            {
                Statistics.RoundTripTicksMax = rtt;
            }
            break;
        case AcknowledgeResult.Late:
            Statistics.FramesLate++;
            break;
        case AcknowledgeResult.Unknown:
        default:
            Statistics.FramesCorrupt++;
            break;
        }
    }
}
