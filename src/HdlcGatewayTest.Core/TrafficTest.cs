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
    /// <param name="b">Gateway B, or null for loopback on A.</param>
    /// <param name="cancel">Set to stop; the current rate finishes early and its result is kept.</param>
    /// <returns><see cref="Passed"/>.</returns>
    public bool Run(GatewayLink a, GatewayLink? b, CancellationToken cancel)
    {
        TrafficDirection[] directions = b == null
            ? new[] { new TrafficDirection("A->A", a, a, 0xA1, _settings.FrameLength, _settings.WindowBytes) }
            : new[]
            {
                new TrafficDirection("A->B", a, b, 0xA1, _settings.FrameLength, _settings.WindowBytes),
                new TrafficDirection("B->A", b, a, 0xB2, _settings.FrameLength, _settings.WindowBytes),
            };
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
            RunRate(rate, links, directions, timeout, cancel);
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

    private void RunRate(uint rate, GatewayLink[] links, TrafficDirection[] directions, int timeoutSeconds,
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

        long start = Stopwatch.GetTimestamp();
        long end = start + (Stopwatch.Frequency * _settings.SecondsPerRate);
        long nextStatus = start + Stopwatch.Frequency;
        long now = start;

        while (now < end && !cancel.IsCancellationRequested)
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

        // Drain: let what is in flight come home, up to the timeout.
        long drainEnd = now + (Stopwatch.Frequency * timeoutSeconds);
        bool pending = true;
        while (pending && now < drainEnd)
        {
            pending = false;
            for (int l = 0; l < links.Length; l++)
            {
                links[l].Pump();
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
            _ = directions[d].Pump(now + (Stopwatch.Frequency * timeoutSeconds), false);
            // Throughput is measured over sending plus drain: frames that arrive during the
            // drain were on the line during it, so counting them against the sending time
            // alone would overstate the rate (11.6 kbit/s on a 9600 bit/s line, seen once).
            directions[d].Statistics.ElapsedTicks = now - start;
        }

        _observer.Status(rate, directions, now - start, true);
        RateResult result = new RateResult(rate, directions, null);
        _results.Add(result);
        _observer.RateFinished(result);
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
