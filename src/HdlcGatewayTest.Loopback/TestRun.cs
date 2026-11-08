// TEST RUN - one rate after the other, each for a fixed time, with a report at the end.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Diagnostics;
using System.Globalization;
using System.Text;
using HdlcGatewayTest.Core;

namespace HdlcGatewayTest.Loopback;

/// <summary>
/// Drives the whole test: for every rate, set the transmit clock on each gateway through its
/// console, run traffic for the given time, drain, and keep the counters for the final table.
/// </summary>
public sealed class TestRun
{
    private readonly Options _options;
    private readonly TextWriter _out;
    private readonly List<RateResult> _results = new List<RateResult>();
    private readonly int _statusWidth = StatusWidth();

    /// <summary>
    /// Create a run.
    /// </summary>
    /// <param name="options">The command line.</param>
    /// <param name="output">Where status lines and the report go.</param>
    public TestRun(Options options, TextWriter output)
    {
        _options = options;
        _out = output;
    }

    /// <summary>
    /// Run every rate.
    /// </summary>
    /// <param name="cancel">Set by Ctrl-C; the current rate finishes early and the report is printed.</param>
    /// <returns>True when every rate passed.</returns>
    public bool Run(CancellationToken cancel)
    {
        using GatewayPort a = new GatewayPort(_options.PortA!);
        GatewayPort? b = _options.PortB == null ? null : new GatewayPort(_options.PortB);
        try
        {
            a.Open();
            b?.Open();
            _out.WriteLine(b == null
                ? "Loopback on " + a.Name + ": frames out of the gateway's TX must come back on its RX."
                : "Two gateways: " + a.Name + " -> " + b.Name + " and " + b.Name + " -> " + a.Name + ".");
            _out.WriteLine("Frame " + _options.FrameLength + " bytes with FCS, window "
                           + _options.WindowBytes + " bytes, " + _options.SecondsPerRate
                           + " s per rate, timeout " + _options.TimeoutSeconds + " s.");
            _out.WriteLine();

            TrafficDirection[] directions = b == null
                ? new[] { new TrafficDirection("A->A", a, a, 0xA1, _options.FrameLength,
                                               _options.WindowBytes, _options.TimeoutSeconds) }
                : new[]
                {
                    new TrafficDirection("A->B", a, b, 0xA1, _options.FrameLength,
                                         _options.WindowBytes, _options.TimeoutSeconds),
                    new TrafficDirection("B->A", b, a, 0xB2, _options.FrameLength,
                                         _options.WindowBytes, _options.TimeoutSeconds),
                };

            for (int r = 0; r < _options.Rates.Length && !cancel.IsCancellationRequested; r++)
            {
                uint rate = _options.Rates[r];
                if (!_options.NoBaud)
                {
                    if (!SetBaud(a, rate) || (b != null && !SetBaud(b, rate)))
                    {
                        _results.Add(new RateResult(rate, directions, "could not set BAUD"));
                        break;
                    }
                }
                RunRate(rate, directions, cancel);
            }
        }
        finally
        {
            b?.Dispose();
        }

        PrintReport();
        bool passed = _results.Count > 0;
        for (int i = 0; i < _results.Count; i++)
        {
            passed &= _results[i].Passed;
        }
        return passed;
    }

    private bool SetBaud(GatewayPort port, uint rate)
    {
        if (!port.EnterConsole(out string transcript))
        {
            _out.WriteLine(port.Name + ": no console prompt after ESC ESC ESC. Is a gateway on this port?");
            _out.WriteLine("RC422Dev only opens its console before the first forwarded frame; reset it");
            _out.WriteLine("between rates, or run one rate at a time with --no-baud after setting BAUD");
            _out.WriteLine("by hand. Got: " + Printable(transcript));
            return false;
        }
        string cmd = "BAUD " + rate.ToString(CultureInfo.InvariantCulture);
        bool ok = port.Command(cmd, "set to", out transcript);
        if (!ok)
        {
            _out.WriteLine(port.Name + ": '" + cmd + "' was not accepted. Got: " + Printable(transcript));
        }
        port.ExitConsole();
        return ok;
    }

    private void RunRate(uint rate, TrafficDirection[] directions, CancellationToken cancel)
    {
        for (int d = 0; d < directions.Length; d++)
        {
            directions[d].Reset();
        }

        long start = Stopwatch.GetTimestamp();
        long end = start + Stopwatch.Frequency * _options.SecondsPerRate;
        long nextStatus = start + Stopwatch.Frequency;
        long now = start;

        while (now < end && !cancel.IsCancellationRequested)
        {
            bool busy = false;
            for (int d = 0; d < directions.Length; d++)
            {
                busy |= directions[d].Pump(now, true);
            }
            if (now >= nextStatus)
            {
                PrintStatus(rate, directions, now - start, "");
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
        long sendingTicks = now - start;

        // Drain: let what is in flight come home, up to the timeout.
        long drainEnd = now + Stopwatch.Frequency * _options.TimeoutSeconds;
        bool pending = true;
        while (pending && now < drainEnd)
        {
            pending = false;
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
            _ = directions[d].Pump(now + Stopwatch.Frequency * _options.TimeoutSeconds, false);
            // Throughput is measured over sending plus drain: frames that arrive during the
            // drain were on the line during it, so counting them against the sending time
            // alone would overstate the rate (11.6 kbit/s on a 9600 bit/s line, seen once).
            directions[d].Statistics.ElapsedTicks = now - start;
        }

        PrintStatus(rate, directions, now - start, " done");
        _out.WriteLine();
        _results.Add(new RateResult(rate, directions, null));
    }

    private void PrintStatus(uint rate, TrafficDirection[] directions, long elapsedTicks, string suffix)
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
        sb.Append(suffix);
        _out.Write('\r');
        _out.Write(sb.ToString().PadRight(_statusWidth));
        if (suffix.Length > 0)
        {
            _out.WriteLine();
        }
    }

    /// <summary>
    /// Width to pad the one-line status to, so a shorter line overwrites a longer one. Zero
    /// when the output is a file or a pipe, where there is no line to overwrite anyway.
    /// </summary>
    private static int StatusWidth()
    {
        if (Console.IsOutputRedirected)
        {
            return 0;
        }
        try
        {
            return Math.Max(0, Console.WindowWidth - 1);
        }
        catch (IOException)
        {
            return 0;   // a console handle that is not a real window
        }
    }

    private void PrintReport()
    {
        _out.WriteLine();
        _out.WriteLine("   Rate    Dir   Sent      OK   BadFCS  Corrupt   Lost   Late  Dropped   kbit/s   RTT ms (mean/max)  Result");
        _out.WriteLine("-------  -----  ------  ------  ------  -------  -----  -----  -------  -------  -----------------  ------");
        for (int i = 0; i < _results.Count; i++)
        {
            RateResult r = _results[i];
            for (int d = 0; d < r.Directions.Length; d++)
            {
                // The frozen copy, not the live object: the directions are reused per rate.
                TrafficStatistics s = r.Statistics[d];
                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0,7}  {1,-5}  {2,6}  {3,6}  {4,6}  {5,7}  {6,5}  {7,5}  {8,7}  {9,7:F1}  {10,8:F1}/{11,-8:F1}  {12}",
                    r.Rate, r.Directions[d].Name, s.FramesSent, s.FramesOk, s.FramesBadFcs,
                    s.FramesCorrupt, s.FramesLost, s.FramesLate, s.DecoderDropped, s.KilobitPerSecond,
                    s.RoundTripMeanMs, s.RoundTripMaxMs,
                    r.Error ?? (s.Passed ? "PASS" : "FAIL"));
                _out.WriteLine(line);
            }
        }
        _out.WriteLine();
    }

    private static string Printable(string text)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < text.Length && sb.Length < 120; i++)
        {
            char c = text[i];
            sb.Append(c >= ' ' && c < 127 ? c : '.');
        }
        return sb.ToString();
    }

    private sealed class RateResult
    {
        public RateResult(uint rate, TrafficDirection[] directions, string? error)
        {
            Rate = rate;
            Directions = new TrafficDirection[directions.Length];
            Statistics = new TrafficStatistics[directions.Length];
            for (int i = 0; i < directions.Length; i++)
            {
                Directions[i] = directions[i];
                // The direction objects are reused per rate, so freeze a copy of the numbers.
                Statistics[i] = Copy(directions[i].Statistics);
            }
            Error = error;
        }

        public uint Rate { get; }
        public TrafficDirection[] Directions { get; }
        public TrafficStatistics[] Statistics { get; }
        public string? Error { get; }

        public bool Passed
        {
            get
            {
                if (Error != null)
                {
                    return false;
                }
                bool ok = true;
                for (int i = 0; i < Statistics.Length; i++)
                {
                    ok &= Statistics[i].Passed;
                }
                return ok;
            }
        }

        private static TrafficStatistics Copy(TrafficStatistics s)
        {
            return new TrafficStatistics
            {
                FramesSent = s.FramesSent,
                BytesSent = s.BytesSent,
                EncodedBytesSent = s.EncodedBytesSent,
                FramesReceived = s.FramesReceived,
                FramesOk = s.FramesOk,
                BytesOk = s.BytesOk,
                FramesBadFcs = s.FramesBadFcs,
                FramesCorrupt = s.FramesCorrupt,
                FramesLost = s.FramesLost,
                FramesLate = s.FramesLate,
                DecoderDropped = s.DecoderDropped,
                RoundTripTicksTotal = s.RoundTripTicksTotal,
                RoundTripTicksMax = s.RoundTripTicksMax,
                ElapsedTicks = s.ElapsedTicks,
            };
        }
    }
}
