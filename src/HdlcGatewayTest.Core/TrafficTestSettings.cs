// TRAFFIC TEST SETTINGS - what a traffic test run is told to do.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// Which gateways the test frames travel through. Ronny, 2026-11-09: "Loopback on A" alone
/// was not understandable, and there was no loopback on B.
/// </summary>
public enum TrafficTestPath
{
    /// <summary>Gateway A alone, its transmit wired back to its own receive; frames go A to A.</summary>
    LoopbackA,

    /// <summary>Gateway B alone, wired back to itself; frames go B to B.</summary>
    LoopbackB,

    /// <summary>A and B wired to each other; frames go A to B and B to A at the same time.</summary>
    TwoGateways,

    /// <summary>A and B wired to each other; only A sends, B only receives (one-way maximum stream).</summary>
    OneWayAtoB,

    /// <summary>A and B wired to each other; only B sends, A only receives.</summary>
    OneWayBtoA,
}

/// <summary>
/// The knobs of a <see cref="TrafficTest"/>; the same ones the hdlc-loopback command line
/// exposes, with its defaults.
/// </summary>
public sealed class TrafficTestSettings
{
    /// <summary>Transmit clock rates to run, one after the other.</summary>
    public uint[] Rates = { 9600, 19200, 38400, 115200, 307200, 983000 };

    /// <summary>Seconds of traffic per rate.</summary>
    public int SecondsPerRate = 120;

    /// <summary>Frame length including FCS, 10..ByteFrame.MaxFrameLength (4112).</summary>
    public int FrameLength = 126;

    /// <summary>Most encoded bytes in flight per direction.</summary>
    public int WindowBytes = 3000;

    /// <summary>Seconds before an unseen frame counts as lost (raised per rate when the window needs longer).</summary>
    public int TimeoutSeconds = 5;

    /// <summary>Do not set BAUD on the gateways; keep whatever clock they have.</summary>
    public bool NoBaud;

    /// <summary>Which gateways carry the test frames.</summary>
    public TrafficTestPath Path = TrafficTestPath.LoopbackA;

    /// <summary>How many bad frames per direction to describe in detail.</summary>
    public int VerboseFrames;

    /// <summary>
    /// Seconds before a frame counts as lost, for one rate: <see cref="TimeoutSeconds"/>, or
    /// longer when the window itself takes longer than that to cross the line. With 12000
    /// bytes in flight at 9600 bit/s a frame needs 10 s just to get its turn; a 5 s timeout
    /// then wrote off every frame as lost and the test failed a gateway that had done nothing
    /// wrong (bench, 2026-11-08). Three window times plus two seconds leaves room for the USB
    /// path.
    /// </summary>
    /// <param name="rate">The line rate in bit/s.</param>
    /// <returns>Seconds.</returns>
    public int TimeoutFor(uint rate)
    {
        double windowSeconds = WindowBytes * 8.0 / rate;
        int needed = (int)Math.Ceiling((3.0 * windowSeconds) + 2.0);
        return Math.Max(TimeoutSeconds, needed);
    }

    /// <summary>
    /// Seconds the window takes to cross the line at a rate.
    /// </summary>
    /// <param name="rate">The line rate in bit/s.</param>
    /// <returns>Seconds.</returns>
    public double WindowSeconds(uint rate)
    {
        return WindowBytes * 8.0 / rate;
    }
}
