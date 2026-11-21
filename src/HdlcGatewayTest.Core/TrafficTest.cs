// TRAFFIC TEST - one rate after the other, each for a fixed time, with frozen results.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Diagnostics;
using System.Globalization;

namespace HdlcGatewayTest.Core;

/// <summary>
/// The engine behind hdlc-loopback and the GUI's traffic test: for every rate, set the
/// transmit clock on each gateway (control frame or text console, whatever
/// <see cref="GatewayLink.Probe"/> found), run traffic for the given time, drain, and freeze
/// the counters. One gateway means loopback (A->A); two means A->B and B->A at once. Runs on
/// the calling thread; progress goes to an <see cref="ITrafficTestObserver"/>.
/// </summary>
public sealed class TrafficTest
{
    private readonly TrafficTestSettings _settings;
    private readonly ITrafficTestObserver _observer;
    private readonly List<RateResult> _results = new List<RateResult>();

    /// <summary>
    /// Create a test.
    /// </summary>
    /// <param name="settings">What to run.</param>
    /// <param name="observer">Who hears about it.</param>
    public TrafficTest(TrafficTestSettings settings, ITrafficTestObserver observer)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    /// <summary>The frozen results so far, one per rate that ran or failed to start.</summary>
    public IReadOnlyList<RateResult> Results => _results;

    /// <summary>True when at least one rate ran and every rate passed.</summary>
    public bool Passed
    {
        get
        {
            bool passed = _results.Count > 0;
            for (int i = 0; i < _results.Count; i++)
            {
                passed &= _results[i].Passed;
            }
            return passed;
        }
    }

    /// <summary>
    /// Run every rate on open links. Both links must already be probed
    /// (<see cref="GatewayLink.Probe"/>) unless <see cref="TrafficTestSettings.NoBaud"/> is set.
    /// </summary>
    /// <param name="a">Gateway A.</param>
    /// <param name="b">Gateway B, or null for loopback on <paramref name="a"/>.</param>
    /// <param name="cancel">Set to stop; the current rate finishes early and its result is kept.</param>
    /// <param name="loopbackName">The direction label for loopback: "A->A" or "B->B".</param>
    /// <returns><see cref="Passed"/>.</returns>
    /// <remarks>
    /// With two links the directions follow <see cref="TrafficTestSettings.Path"/>: both at once,
    /// or one way only (RC422Dev's maximum-stream test, 2026-11-09: nothing coming back the
    /// other way). A loopback path with two links means both ways, for the command-line tool,
    /// which never sets the path.
    /// </remarks>
    public bool Run(GatewayLink a, GatewayLink? b, CancellationToken cancel, string loopbackName = "A->A")
    {
        TrafficDirection[] directions;
        if (b == null)
        {
            directions = new[] { new TrafficDirection(loopbackName, a, a, 0xA1, _settings.FrameLength, _settings.WindowBytes) };
        }
        else if (_settings.Path == TrafficTestPath.OneWayAtoB)
        {
            directions = new[] { new TrafficDirection("A->B", a, b, 0xA1, _settings.FrameLength, _settings.WindowBytes) };
        }
        else if (_settings.Path == TrafficTestPath.OneWayBtoA)
        {
            directions = new[] { new TrafficDirection("B->A", b, a, 0xB2, _settings.FrameLength, _settings.WindowBytes) };
        }
        else
        {
            directions = new[]
            {
                new TrafficDirection("A->B", a, b, 0xA1, _settings.FrameLength, _settings.WindowBytes),
                new TrafficDirection("B->A", b, a, 0xB2, _settings.FrameLength, _settings.WindowBytes),
            };
        }
        for (int d = 0; d < directions.Length; d++)
        {
            directions[d].VerboseFrames = _settings.VerboseFrames;
            directions[d].Explainer = _observer.Message;
        }
        GatewayLink[] links = b == null ? new[] { a } : new[] { a, b };

        for (int r = 0; r < _settings.Rates.Length && !cancel.IsCancellationRequested; r++)
        {
            uint rate = _settings.Rates[r];
            if (!_settings.NoBaud)
            {
                if (!SetBaud(a, rate) || (b != null && !SetBaud(b, rate)))
                {
                    RateResult failed = new RateResult(rate, directions, "could not set BAUD");
                    _results.Add(failed);
                    _observer.RateFinished(failed);
                    break;
                }
            }
            int timeout = _settings.TimeoutFor(rate);
            for (int d = 0; d < directions.Length; d++)
            {
                directions[d].TimeoutSeconds = timeout;
            }
            if (!RunRate(rate, links, directions, timeout, cancel))
            {
                // A port is gone; no later rate can run on it either.
                break;
            }
        }

        for (int d = 0; d < directions.Length; d++)
        {
            // Give the channels back: the next user of the link (console, GUI frame list)
            // sets its own handlers.
            links[d % links.Length].Channel.DataFrameReceived = null;
            links[d % links.Length].Channel.BadFrameReceived = null;
        }
        return Passed;
    }

    /// <summary>
    /// Set the transmit clock with BAUD. Reports what went wrong through the observer.
    /// </summary>
    private bool SetBaud(GatewayLink link, uint rate)
    {
        string cmd = "BAUD " + rate.ToString(CultureInfo.InvariantCulture);
        if (link.CommandPath == GatewayCommandPath.None || link.CommandPath == GatewayCommandPath.Unknown)
        {
            _observer.Message(link.Name + ": no console prompt after ESC ESC ESC. Is a gateway on this port?");
            _observer.Message("RC422Dev only opens its console before the first forwarded frame; reset it");
            _observer.Message("between rates, or run one rate at a time with --no-baud after setting BAUD by hand.");
            return false;
        }
        bool ok = link.RunCommand(cmd, out string reply)
                  && reply.Contains("set to", StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            _observer.Message(link.Name + ": '" + cmd + "' was not accepted"
                              + (link.CommandPath == GatewayCommandPath.ControlFrames ? " (control frame)" : " (text console)")
                              + ". Got: " + Printable(reply));
        }
        return ok;
    }

    /// <summary>
    /// Run one rate: send for the configured time, then drain.
    /// </summary>
    /// <returns>
    /// False when a link's transport failed during the rate (the port vanished, the TCP peer
    /// went away): the rate's result carries the error and the run must not go on. Bench
    /// 2026-11-09: a Pico reflashed under a run took COM18 away, the engine kept queueing
    /// frames to the dead port for four minutes, counted them lost and the drain never ended
    /// while the queue kept changing.
    /// </returns>
    private bool RunRate(uint rate, GatewayLink[] links, TrafficDirection[] directions, int timeoutSeconds,
                         CancellationToken cancel)
    {
        for (int d = 0; d < directions.Length; d++)
        {
            directions[d].Reset();
        }
        if (timeoutSeconds != _settings.TimeoutSeconds)
        {
            _observer.Message(rate.ToString(CultureInfo.InvariantCulture) + " bit/s: timeout raised to "
                              + timeoutSeconds + " s, the window takes "
                              + _settings.WindowSeconds(rate).ToString("F1", CultureInfo.InvariantCulture)
                              + " s to cross the line.");
        }

        // The PC side's own behaviour during this rate, for the far ends' questions (HDLCGW
        // 2026-11-09: how long does the PC leave the gateway's output unfetched, and does the
        // garbage collector pause the reader?): the transports' read statistics start over here
        // and the GC pause total is remembered; both are reported after the drain.
        for (int l = 0; l < links.Length; l++)
        {
            links[l].Transport.ResetReadStatistics();
        }
        TimeSpan gcPauseBefore = GC.GetTotalPauseDuration();
        int gen0Before = GC.CollectionCount(0);
        int gen2Before = GC.CollectionCount(2);

        long start = Stopwatch.GetTimestamp();
        long end = start + (Stopwatch.Frequency * _settings.SecondsPerRate);
        long nextStatus = start + Stopwatch.Frequency;
        long now = start;

        GatewayLink? faulted = null;
        while (now < end && !cancel.IsCancellationRequested && (faulted = FaultedLink(links)) == null)
        {
            bool busy = false;
            for (int l = 0; l < links.Length; l++)
            {
                busy |= links[l].Pump() > 0;
            }
            for (int d = 0; d < directions.Length; d++)
            {
                busy |= directions[d].Pump(now, true);
            }
            if (now >= nextStatus)
            {
                _observer.Status(rate, directions, now - start, false);
                nextStatus += Stopwatch.Frequency;
            }
            // Sleep only when nothing moved. On Windows Thread.Sleep(1) can be a whole timer
            // tick (up to 15.6 ms), which would put a ceiling on the round trip and so on the
            // throughput a window allows.
            if (!busy)
            {
                Thread.Sleep(1);
            }
            now = Stopwatch.GetTimestamp();
        }

        // Drain: let what is in flight come home, up to the timeout. While a transport's write
        // queue is still shrinking the far end is still taking bytes, so the deadline moves with
        // that progress: those frames are neither home nor lost yet. Whatever is still queued
        // when the drain ends is written off as unsent, never as lost.
        long drainEnd = now + (Stopwatch.Frequency * timeoutSeconds);
        long lastQueued = -1;
        bool pending = true;
        while (pending && now < drainEnd && faulted == null && (faulted = FaultedLink(links)) == null)
        {
            pending = false;
            long queued = 0;
            for (int l = 0; l < links.Length; l++)
            {
                links[l].Pump();
                queued += links[l].Transport.QueuedWriteBytes;
            }
            if (queued > 0 && queued != lastQueued)
            {
                lastQueued = queued;
                drainEnd = now + (Stopwatch.Frequency * timeoutSeconds);
            }
            for (int d = 0; d < directions.Length; d++)
            {
                _ = directions[d].Pump(now, false);
                pending |= directions[d].Pending > 0;
            }
            Thread.Sleep(2);
            now = Stopwatch.GetTimestamp();
        }
        for (int d = 0; d < directions.Length; d++)
        {
            directions[d].WriteOffQueued();
            _ = directions[d].Pump(now + (Stopwatch.Frequency * timeoutSeconds), false);
            // Throughput is measured over sending plus drain: frames that arrive during the
            // drain were on the line during it, so counting them against the sending time
            // alone would overstate the rate (11.6 kbit/s on a 9600 bit/s line, seen once).
            directions[d].Statistics.ElapsedTicks = now - start;
        }

        for (int l = 0; l < links.Length; l++)
        {
            string reads = links[l].Transport.DescribeReads();
            if (reads.Length > 0)
            {
                _observer.Message("PC side, " + links[l].Transport.Name + " during this rate: " + reads);
            }
        }
        _observer.Message("PC side, GC during this rate: pauses " + (GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)
                          + " ms in all, gen0 collections " + (GC.CollectionCount(0) - gen0Before)
                          + ", gen2 collections " + (GC.CollectionCount(2) - gen2Before));

        _observer.Status(rate, directions, now - start, true);
        string? error = null;
        if (faulted != null)
        {
            error = faulted.Name + " failed during the rate: " + (faulted.Fault?.Message ?? "port gone")
                    + " (frames still queued are unsent; frames on the wire without an answer are lost)";
            _observer.Message(error);
        }
        RateResult result = new RateResult(rate, directions, error);
        _results.Add(result);
        _observer.RateFinished(result);
        return faulted == null;
    }

    /// <summary>
    /// The first link whose transport has reported an error, or null.
    /// </summary>
    /// <param name="links">The links in use.</param>
    /// <returns>The failed link, or null while all work.</returns>
    private static GatewayLink? FaultedLink(GatewayLink[] links)
    {
        for (int l = 0; l < links.Length; l++)
        {
            if (links[l].IsFaulted)
            {
                return links[l];
            }
        }
        return null;
    }

    /// <summary>
    /// A transcript with control characters replaced, cut to 120 characters, for a message.
    /// </summary>
    /// <param name="text">Raw text.</param>
    /// <returns>Printable text.</returns>
    public static string Printable(string text)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < text.Length && sb.Length < 120; i++)
        {
            char c = text[i];
            sb.Append(c >= ' ' && c < 127 ? c : '.');
        }
        return sb.ToString();
    }
}
