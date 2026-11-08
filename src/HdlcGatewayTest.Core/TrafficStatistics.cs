// TRAFFIC STATISTICS - the counters one direction of a test run keeps.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// Counters for one direction (one sending port, one receiving port). Plain fields updated
/// from one thread; the report reads them when the run is over.
/// </summary>
public sealed class TrafficStatistics
{
    /// <summary>Frames handed to the sending port.</summary>
    public long FramesSent;

    /// <summary>Frame bytes sent, FCS included, before byte stuffing.</summary>
    public long BytesSent;

    /// <summary>Bytes actually written to the port, byte stuffing and flags included.</summary>
    public long EncodedBytesSent;

    /// <summary>Complete frames the decoder delivered from the receiving port.</summary>
    public long FramesReceived;

    /// <summary>Received frames that matched a pending frame byte for byte.</summary>
    public long FramesOk;

    /// <summary>Frame bytes in the good frames, FCS included.</summary>
    public long BytesOk;

    /// <summary>Received frames whose FCS did not match.</summary>
    public long FramesBadFcs;

    /// <summary>FCS good, but length or payload wrong, or not one of ours.</summary>
    public long FramesCorrupt;

    /// <summary>Sent frames never seen again: skipped by a later frame, or timed out.</summary>
    public long FramesLost;

    /// <summary>Received frames older than anything pending (duplicates or already written off).</summary>
    public long FramesLate;

    /// <summary>Frames the byte-frame decoder dropped (too long, or ended inside an escape).</summary>
    public long DecoderDropped;

    /// <summary>Sum of round-trip times of the good frames, in Stopwatch ticks.</summary>
    public long RoundTripTicksTotal;

    /// <summary>Longest round trip of a good frame, in Stopwatch ticks.</summary>
    public long RoundTripTicksMax;

    /// <summary>How long the sending phase ran, in Stopwatch ticks.</summary>
    public long ElapsedTicks;

    /// <summary>True when nothing went wrong and at least one frame made it.</summary>
    public bool Passed =>
        FramesOk > 0 && FramesBadFcs == 0 && FramesCorrupt == 0 && FramesLost == 0
        && DecoderDropped == 0 && FramesOk == FramesSent;

    /// <summary>Good frame bytes per second over the sending phase, in kilobit per second.</summary>
    public double KilobitPerSecond
    {
        get
        {
            if (ElapsedTicks <= 0)
            {
                return 0;
            }
            double seconds = (double)ElapsedTicks / System.Diagnostics.Stopwatch.Frequency;
            return BytesOk * 8.0 / seconds / 1000.0;
        }
    }

    /// <summary>Mean round trip of the good frames in milliseconds, 0 when none.</summary>
    public double RoundTripMeanMs
    {
        get
        {
            if (FramesOk == 0)
            {
                return 0;
            }
            return TicksToMs(RoundTripTicksTotal / FramesOk);
        }
    }

    /// <summary>Longest round trip of a good frame in milliseconds.</summary>
    public double RoundTripMaxMs => TicksToMs(RoundTripTicksMax);

    /// <summary>Set every counter to zero.</summary>
    public void Clear()
    {
        FramesSent = 0;
        BytesSent = 0;
        EncodedBytesSent = 0;
        FramesReceived = 0;
        FramesOk = 0;
        BytesOk = 0;
        FramesBadFcs = 0;
        FramesCorrupt = 0;
        FramesLost = 0;
        FramesLate = 0;
        DecoderDropped = 0;
        RoundTripTicksTotal = 0;
        RoundTripTicksMax = 0;
        ElapsedTicks = 0;
    }

    private static double TicksToMs(long ticks)
    {
        return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }
}
