// TEST RUN - the console face of the traffic test: status lines while it runs, a table at the end.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Diagnostics;
using System.Globalization;
using System.Text;
using HdlcGatewayTest.Core;
using HdlcGatewayTest.Serial;

namespace HdlcGatewayTest.Loopback;

/// <summary>
/// Opens the ports, probes each gateway, runs the shared <see cref="TrafficTest"/> engine and
/// prints what it reports in the exact shape hdlc-loopback always printed: a one-line status
/// per second that overwrites itself on a terminal, and the PASS/FAIL table at the end.
/// </summary>
public sealed class TestRun : ITrafficTestObserver
{
    private readonly Options _options;
    private readonly TextWriter _out;
    private readonly int _statusWidth = StatusWidth();
    private readonly List<RateResult> _results = new List<RateResult>();

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
        TrafficTestSettings settings = new TrafficTestSettings
        {
            Rates = _options.Rates,
            SecondsPerRate = _options.SecondsPerRate,
            FrameLength = _options.FrameLength,
            WindowBytes = _options.WindowBytes,
            TimeoutSeconds = _options.TimeoutSeconds,
            NoBaud = _options.NoBaud,
            VerboseFrames = _options.Verbose,
        };

        using GatewayLink a = new GatewayLink(new SerialTransport(_options.PortA!));
        GatewayLink? b = _options.PortB == null ? null : new GatewayLink(new SerialTransport(_options.PortB));
        bool passed;
        try
        {
            a.Open();
            b?.Open();
            _out.WriteLine(b == null
                ? "Loopback on " + a.Name + ": frames out of the gateway's TX must come back on its RX."
                : "Two gateways: " + a.Name + " -> " + b.Name + " and " + b.Name + " -> " + a.Name + ".");
            _out.WriteLine("Frame " + _options.FrameLength + " bytes with FCS, window "
                           + _options.WindowBytes + " bytes, " + _options.SecondsPerRate
                           + " s per rate, timeout " + _options.TimeoutSeconds + " s or more.");
            if (!_options.NoBaud)
            {
                Probe(a);
                if (b != null)
                {
                    Probe(b);
                }
            }
            _out.WriteLine();

            TrafficTest test = new TrafficTest(settings, this);
            passed = test.Run(a, b, cancel);
        }
        finally
        {
            b?.Dispose();
        }

        PrintReport();
        return passed;
    }

    /// <summary>
    /// Find out whether the gateway speaks control frames and say so, because the two paths
    /// behave differently on the bench.
    /// </summary>
    public void Probe(GatewayLink link)
    {
        GatewayCommandPath path = link.Probe(500, out string transcript, _options.UseConsole);
        switch (path)
        {
            case GatewayCommandPath.ControlFrames:
                _out.WriteLine(link.Name + ": " + link.Identity!.Name + " " + link.Identity.Version
                               + ", control frames (BAUD is set without leaving forwarding mode).");
                break;
            case GatewayCommandPath.TextConsole:
                _out.WriteLine(link.Name + ": no HELLO reply, using the ESC ESC ESC text console "
                               + "(each probe frame went onto the line and counts as one bad frame there).");
                break;
            default:
                _out.WriteLine(link.Name + ": no HELLO reply and no console prompt. Is a gateway on this port? Got: "
                               + TrafficTest.Printable(transcript));
                break;
        }
    }

    /// <inheritdoc/>
    public void Message(string text)
    {
        _out.WriteLine(text);
    }

    /// <inheritdoc/>
    public void Status(uint rate, TrafficDirection[] directions, long elapsedTicks, bool done)
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
              .Append(" unsent ").Append(s.FramesUnsent)
              .Append(' ').Append(kbit.ToString("F1", CultureInfo.InvariantCulture)).Append(" kbit/s");
        }
        if (done)
        {
            sb.Append(" done");
        }
        _out.Write('\r');
        _out.Write(sb.ToString().PadRight(_statusWidth));
        if (done)
        {
            _out.WriteLine();
            _out.WriteLine();
        }
    }

    /// <inheritdoc/>
    public void RateFinished(RateResult result)
    {
        _results.Add(result);
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
        _out.WriteLine("   Rate    Dir   Sent      OK   BadFCS  Corrupt   Lost   Late  Unsent  Dropped   kbit/s   RTT ms (mean/max)  Result");
        _out.WriteLine("-------  -----  ------  ------  ------  -------  -----  -----  ------  -------  -------  -----------------  ------");
        for (int i = 0; i < _results.Count; i++)
        {
            RateResult r = _results[i];
            for (int d = 0; d < r.Statistics.Length; d++)
            {
                TrafficStatistics s = r.Statistics[d];
                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0,7}  {1,-5}  {2,6}  {3,6}  {4,6}  {5,7}  {6,5}  {7,5}  {8,6}  {9,7}  {10,7:F1}  {11,8:F1}/{12,-8:F1}  {13}",
                    r.Rate, r.DirectionNames[d], s.FramesSent, s.FramesOk, s.FramesBadFcs,
                    s.FramesCorrupt, s.FramesLost, s.FramesLate, s.FramesUnsent, s.DecoderDropped, s.KilobitPerSecond,
                    s.RoundTripMeanMs, s.RoundTripMaxMs,
                    r.Error ?? (s.Passed ? "PASS" : "FAIL"));
                _out.WriteLine(line);
            }
        }
        _out.WriteLine();
    }
}
