// TRAFFIC TEST RECORDER - keeps what a running traffic test reports, for a status read at any time.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Diagnostics;
using System.Globalization;
using System.Text;
using HdlcGatewayTest.Core;

namespace HdlcGatewayTest.Gateway;

/// <summary>
/// An <see cref="ITrafficTestObserver"/> that stores messages, the latest status line and the
/// finished rates, so the GUI's traffic panel and the MCP <c>hdlc_test_status</c> tool can read
/// the state of a test whenever they like. Thread safe. Raises <see cref="Changed"/> on the
/// engine's thread after every update.
/// </summary>
public sealed class TrafficTestRecorder : ITrafficTestObserver
{
    private readonly object _lock = new object();
    private readonly List<string> _messages = new List<string>();
    private readonly List<RateResult> _results = new List<RateResult>();
    private string _status = string.Empty;

    /// <summary>Raised after every update.</summary>
    public event Action? Changed;

    /// <summary>The latest one-line status, same text as the console tool prints.</summary>
    public string StatusLine
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>Messages so far (probe results, raised timeouts, bad-frame explanations).</summary>
    public List<string> Messages
    {
        get
        {
            lock (_lock)
            {
                return new List<string>(_messages);
            }
        }
    }

    /// <summary>Rates finished so far.</summary>
    public List<RateResult> Results
    {
        get
        {
            lock (_lock)
            {
                return new List<RateResult>(_results);
            }
        }
    }

    /// <summary>Forget everything, before a new run.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
            _results.Clear();
            _status = string.Empty;
        }
        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public void Message(string text)
    {
        lock (_lock)
        {
            _messages.Add(text);
        }
        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public void Status(uint rate, TrafficDirection[] directions, long elapsedTicks, bool done)
    {
        string line = FormatStatus(rate, directions, elapsedTicks, done);
        lock (_lock)
        {
            _status = line;
        }
        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public void RateFinished(RateResult result)
    {
        lock (_lock)
        {
            _results.Add(result);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The status line in the console tool's shape.
    /// </summary>
    /// <param name="rate">Line rate.</param>
    /// <param name="directions">Live directions.</param>
    /// <param name="elapsedTicks">Ticks since the rate started.</param>
    /// <param name="done">Final status of the rate.</param>
    /// <returns>The line.</returns>
    public static string FormatStatus(uint rate, TrafficDirection[] directions, long elapsedTicks, bool done)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(rate.ToString(CultureInfo.InvariantCulture).PadLeft(7)).Append(" bit/s ");
        sb.Append(((long)(elapsedTicks / Stopwatch.Frequency)).ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append(" s");
        for (int d = 0; d < directions.Length; d++)
        {
            TrafficStatistics s = directions[d].Statistics;
            double seconds = (double)elapsedTicks / Stopwatch.Frequency;
            double kbit = seconds > 0 ? s.BytesOk * 8.0 / seconds / 1000.0 : 0;
            sb.Append("  ").Append(directions[d].Name).Append(" sent ").Append(s.FramesSent)
              .Append(" ok ").Append(s.FramesOk)
              .Append(" bad ").Append(s.FramesBadFcs + s.FramesCorrupt)
              .Append(" lost ").Append(s.FramesLost)
              .Append(' ').Append(kbit.ToString("F1", CultureInfo.InvariantCulture)).Append(" kbit/s");
        }
        if (done)
        {
            sb.Append(" done");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The report table in the console tool's shape.
    /// </summary>
    /// <param name="results">Finished rates.</param>
    /// <returns>The table, header included.</returns>
    public static string FormatReport(IReadOnlyList<RateResult> results)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("   Rate    Dir   Sent      OK   BadFCS  Corrupt   Lost   Late  Dropped   kbit/s   RTT ms (mean/max)  Result\r\n");
        sb.Append("-------  -----  ------  ------  ------  -------  -----  -----  -------  -------  -----------------  ------\r\n");
        for (int i = 0; i < results.Count; i++)
        {
            RateResult r = results[i];
            for (int d = 0; d < r.Statistics.Length; d++)
            {
                TrafficStatistics s = r.Statistics[d];
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "{0,7}  {1,-5}  {2,6}  {3,6}  {4,6}  {5,7}  {6,5}  {7,5}  {8,7}  {9,7:F1}  {10,8:F1}/{11,-8:F1}  {12}",
                    r.Rate, r.DirectionNames[d], s.FramesSent, s.FramesOk, s.FramesBadFcs,
                    s.FramesCorrupt, s.FramesLost, s.FramesLate, s.DecoderDropped, s.KilobitPerSecond,
                    s.RoundTripMeanMs, s.RoundTripMaxMs,
                    r.Error ?? (s.Passed ? "PASS" : "FAIL"))).Append("\r\n");
            }
        }
        return sb.ToString();
    }
}
