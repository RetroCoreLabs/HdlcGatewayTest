// WRITE METER - bytes written to a transport and the time its Write() calls took.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Diagnostics;

namespace HdlcGatewayTest.Core;

/// <summary>
/// Counts what went into a transport's <see cref="IGatewayTransport.Write"/> and how long the
/// calls blocked. A serial write returns only when the driver has taken the bytes, so a slow
/// reader at the far end shows up here as long writes; and because the traffic engine pumps
/// both directions from one thread, a long write on one port also stalls the other (HDLCGW
/// session's question, 2026-11-09, after A->B doubled when only the Pico's USB reading changed).
/// Thread safe for one writer and any number of readers.
/// </summary>
public sealed class WriteMeter
{
    private long _bytes;
    private long _calls;
    private long _maxTicks;
    private long _totalTicks;

    /// <summary>Bytes handed to Write() since the transport was created.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Write() calls.</summary>
    public long Calls => Interlocked.Read(ref _calls);

    /// <summary>The longest single Write(), in microseconds.</summary>
    public long MaxMicroseconds => ToMicroseconds(Interlocked.Read(ref _maxTicks));

    /// <summary>Time spent inside Write() in all, in microseconds.</summary>
    public long TotalMicroseconds => ToMicroseconds(Interlocked.Read(ref _totalTicks));

    /// <summary>
    /// Record one call. Call with the timestamp taken before the write and the byte count.
    /// </summary>
    /// <param name="startTimestamp">From <see cref="Stopwatch.GetTimestamp"/> before the write.</param>
    /// <param name="bytes">Bytes written.</param>
    public void Record(long startTimestamp, int bytes)
    {
        long ticks = Stopwatch.GetTimestamp() - startTimestamp;
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _totalTicks, ticks);
        long max = Interlocked.Read(ref _maxTicks);
        while (ticks > max)
        {
            long seen = Interlocked.CompareExchange(ref _maxTicks, ticks, max);
            if (seen == max)
            {
                break;
            }
            max = seen;
        }
    }

    /// <summary>One line: "written N bytes in M calls, longest X us, total Y ms".</summary>
    /// <returns>The text.</returns>
    public string Describe()
    {
        return "written " + Bytes + " bytes in " + Calls + " calls, longest write " + MaxMicroseconds
               + " us, total in Write() " + (TotalMicroseconds / 1000) + " ms";
    }

    private static long ToMicroseconds(long ticks)
    {
        return ticks * 1_000_000 / Stopwatch.Frequency;
    }
}
