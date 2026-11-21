// TRAFFIC DIRECTION - frames out of one gateway, checked as they come in on another (or the same).
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Diagnostics;
using System.Text;

namespace HdlcGatewayTest.Core;

/// <summary>
/// One direction of traffic. Keeps the window full on the sending side, takes the frames the
/// receiving link's channel delivers, and matches them by sequence number. In loopback the
/// two links are the same object; in a two-gateway test they are A and B. The direction
/// attaches itself to the sink channel's data-frame and bad-frame handlers, so control frames
/// (log lines, replies) on the same port still reach the link's console while traffic runs.
/// </summary>
public sealed class TrafficDirection
{
    private readonly GatewayLink _source;
    private readonly GatewayLink _sink;
    private readonly byte _address;
    private readonly int _frameLength;
    private readonly int _windowBytes;
    private long _timeoutTicks = Stopwatch.Frequency * 5;
    private readonly PendingFrames _pending;
    private readonly byte[] _frame;
    private uint _nextSequence;
    private int _lastDecoderDropped;
    private int _explained;

    /// <summary>
    /// Set up a direction and attach it to the sink's channel.
    /// </summary>
    /// <param name="name">Shown in the status line, for example "A->B".</param>
    /// <param name="source">Link the frames are written to.</param>
    /// <param name="sink">Link the frames are expected back on.</param>
    /// <param name="address">Address byte of this direction's frames.</param>
    /// <param name="frameLength">Frame length including FCS.</param>
    /// <param name="windowBytes">Most encoded bytes in flight.</param>
    public TrafficDirection(string name, GatewayLink source, GatewayLink sink, byte address,
                            int frameLength, int windowBytes)
    {
        Name = name;
        _source = source;
        _sink = sink;
        _address = address;
        _frameLength = frameLength;
        _windowBytes = windowBytes;
        // Enough slots that the window, not the slot count, is what limits traffic.
        _pending = new PendingFrames(Math.Max(16, (windowBytes / TestFrame.MinLength) + 2));
        _frame = new byte[frameLength];
        _sink.Channel.DataFrameReceived = OnDataFrame;
        _sink.Channel.BadFrameReceived = OnBadFrame;
    }

    /// <summary>Label for reports.</summary>
    public string Name { get; }

    /// <summary>The counters.</summary>
    public TrafficStatistics Statistics { get; } = new TrafficStatistics();

    /// <summary>Frames still in flight.</summary>
    public int Pending => _pending.Count;

    /// <summary>Seconds before a missing frame counts as lost. Set per rate by the run.</summary>
    public int TimeoutSeconds
    {
        get => (int)(_timeoutTicks / Stopwatch.Frequency);
        set => _timeoutTicks = Stopwatch.Frequency * value;
    }

    /// <summary>How many bad frames to describe through <see cref="Explainer"/> before the counter alone has to do.</summary>
    public int VerboseFrames { get; set; }

    /// <summary>Where bad-frame descriptions go; null for none.</summary>
    public Action<string>? Explainer { get; set; }

    /// <summary>Start a new run: counters to zero, sequence numbers from 0.</summary>
    public void Reset()
    {
        Statistics.Clear();
        _pending.Clear();
        _nextSequence = 0;
        _explained = 0;
        _lastDecoderDropped = _sink.Channel.DroppedFrames;
    }

    /// <summary>
    /// One step: write off what timed out and top up the window. Receiving happens when the
    /// sink link is pumped (<see cref="GatewayLink.Pump"/>), which the engine does first.
    /// </summary>
    /// <param name="nowTicks">Current Stopwatch timestamp.</param>
    /// <param name="sending">False during the drain phase, when nothing new goes out.</param>
    /// <returns>True when something was sent.</returns>
    public bool Pump(long nowTicks, bool sending)
    {
        int dropped = _sink.Channel.DroppedFrames;
        if (dropped != _lastDecoderDropped)
        {
            Statistics.DecoderDropped += dropped - _lastDecoderDropped;
            _lastDecoderDropped = dropped;
        }

        // Frames still in the PC's write queue have not been on the line: they cannot be lost yet.
        int expired = _pending.ExpireSentBefore(nowTicks - _timeoutTicks, _source.Transport.QueuedWriteBytes);
        Statistics.FramesLost += expired;

        if (!sending)
        {
            return false;
        }

        bool busy = false;
        while (!_pending.IsFull)
        {
            Span<byte> frame = TestFrame.Build(_nextSequence, _frameLength, _address, _frame);
            int encodedLength = EncodedLength(frame);
            if (_pending.BytesInFlight + encodedLength > _windowBytes && _pending.Count > 0)
            {
                break;
            }
            _source.Channel.Send(frame);
            _pending.Add(_nextSequence, Stopwatch.GetTimestamp(), encodedLength);
            Statistics.FramesSent++;
            Statistics.BytesSent += frame.Length;
            Statistics.EncodedBytesSent += encodedLength;
            _nextSequence++;
            busy = true;
        }
        return busy;
    }

    /// <summary>Bytes the source transport still holds unwritten.</summary>
    public long QueuedWriteBytes => _source.Transport.QueuedWriteBytes;

    /// <summary>
    /// End of the drain: frames whose bytes are still in the source transport's write queue
    /// never left the PC. Take them out of the pending list and count them as unsent, so the
    /// final expiry does not call them lost.
    /// </summary>
    public void WriteOffQueued()
    {
        long queued = _source.Transport.QueuedWriteBytes;
        if (queued <= 0)
        {
            return;
        }
        Statistics.FramesUnsent += _pending.RemoveNewestWithin(queued);
    }

    /// <summary>Bytes the frame takes on the wire: flags plus escapes.</summary>
    private static int EncodedLength(ReadOnlySpan<byte> frame)
    {
        int n = 2;
        for (int i = 0; i < frame.Length; i++)
        {
            byte b = frame[i];
            n += (b == ByteFrame.Flag || b == ByteFrame.Escape || b < 0x20) ? 2 : 1;
        }
        return n;
    }

    private void OnBadFrame(ReadOnlySpan<byte> frame)
    {
        Statistics.FramesReceived++;
        Statistics.FramesBadFcs++;
        TestFrame.Verify(frame, out uint sequence, out byte address);
        Explain(frame, sequence, address, "bad FCS");
    }

    private void OnDataFrame(ReadOnlySpan<byte> frame)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        Statistics.FramesReceived++;
        TestFrameVerdict verdict = TestFrame.Verify(frame, out uint sequence, out byte address);

        if (verdict == TestFrameVerdict.BadFcs)
        {
            Statistics.FramesBadFcs++;
            Explain(frame, sequence, address, "bad FCS");
            return;
        }
        if (verdict != TestFrameVerdict.Ok || address != _address)
        {
            Statistics.FramesCorrupt++;
            Explain(frame, sequence, address, verdict.ToString());
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

    /// <summary>
    /// Say what is wrong with a bad frame: how long it is against what its header claims, and
    /// where it first differs from the frame the sequence number should have produced.
    /// </summary>
    private void Explain(ReadOnlySpan<byte> frame, uint sequence, byte address, string what)
    {
        if (Explainer == null || _explained >= VerboseFrames)
        {
            return;
        }
        _explained++;

        int claimed = frame.Length >= 8 ? (frame[6] << 8) | frame[7] : -1;
        byte[] expected = new byte[Math.Max(claimed, TestFrame.MinLength)];
        int firstDiff = -1;
        int diffs = 0;
        if (claimed >= TestFrame.MinLength && claimed <= expected.Length)
        {
            TestFrame.Build(sequence, claimed, address, expected);
            int n = Math.Min(claimed, frame.Length);
            for (int i = 0; i < n; i++)
            {
                if (frame[i] != expected[i])
                {
                    diffs++;
                    if (firstDiff < 0)
                    {
                        firstDiff = i;
                    }
                }
            }
        }

        StringBuilder sb = new StringBuilder();
        sb.Append(Name).Append(": ").Append(what).Append(", seq ").Append(sequence)
          .Append(", addr 0x").Append(address.ToString("X2"))
          .Append(", got ").Append(frame.Length).Append(" bytes, header says ").Append(claimed);
        if (claimed >= TestFrame.MinLength)
        {
            sb.Append(", ").Append(diffs).Append(" byte(s) differ from expected");
            if (firstDiff >= 0)
            {
                sb.Append(", first at offset ").Append(firstDiff)
                  .Append(" (got 0x").Append(frame[firstDiff].ToString("X2"))
                  .Append(" want 0x").Append(expected[firstDiff].ToString("X2")).Append(')');
            }
        }
        sb.Append(". First 24 bytes:");
        for (int i = 0; i < Math.Min(24, frame.Length); i++)
        {
            sb.Append(' ').Append(frame[i].ToString("X2"));
        }
        Explainer(sb.ToString());
    }
}
