// TRAFFIC TEST TESTS - the engine against a fake loopback gateway: clean passes, loss and damage fail.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class TrafficTestTests
{
    private sealed class Recorder : ITrafficTestObserver
    {
        public readonly List<string> Messages = new List<string>();
        public readonly List<RateResult> Finished = new List<RateResult>();
        public int StatusCalls;
        public int DoneCalls;

        public void Message(string text) => Messages.Add(text);

        public void Status(uint rate, TrafficDirection[] directions, long elapsedTicks, bool done)
        {
            StatusCalls++;
            if (done) { DoneCalls++; }
        }

        public void RateFinished(RateResult result) => Finished.Add(result);
    }

    private static TrafficTestSettings QuickSettings()
    {
        return new TrafficTestSettings
        {
            Rates = new uint[] { 9600, 38400 },
            SecondsPerRate = 1,
            FrameLength = 64,
            WindowBytes = 600,
            TimeoutSeconds = 1,
            VerboseFrames = 2,
        };
    }

    [Test]
    public void CleanLoopback_PassesEveryRate_AndSetsBaudThroughFrames()
    {
        FakeTransport t = new FakeTransport();
        FakeGateway gw = new FakeGateway(t, FakeGateway.Kind.ControlFrames);
        using GatewayLink link = new GatewayLink(t);
        link.Open();
        link.Probe(50, out _);
        Recorder rec = new Recorder();

        TrafficTest test = new TrafficTest(QuickSettings(), rec);
        bool passed = test.Run(link, null, CancellationToken.None);

        Assert.That(passed, Is.True);
        Assert.That(test.Results.Count, Is.EqualTo(2));
        Assert.That(rec.Finished.Count, Is.EqualTo(2));
        Assert.That(rec.DoneCalls, Is.EqualTo(2));
        Assert.That(test.Results[0].Rate, Is.EqualTo(9600u));
        Assert.That(test.Results[0].DirectionNames, Is.EqualTo(new[] { "A->A" }));
        TrafficStatistics s = test.Results[1].Statistics[0];
        Assert.That(s.FramesSent, Is.GreaterThan(10));
        Assert.That(s.FramesOk, Is.EqualTo(s.FramesSent));
        Assert.That(s.FramesLost, Is.EqualTo(0));
        Assert.That(s.Passed, Is.True);
        Assert.That(gw.Commands, Is.EqualTo(new[] { "BAUD 9600", "BAUD 38400" }));
        Assert.That(gw.Baud, Is.EqualTo(38400u));
    }

    [Test]
    public void LossyLoopback_FailsWithLostFrames_AndExplainsDamage()
    {
        FakeTransport t = new FakeTransport();
        FakeGateway gw = new FakeGateway(t, FakeGateway.Kind.TextConsoleOnly) { DropEvery = 7, DamageEvery = 11 };
        using GatewayLink link = new GatewayLink(t);
        link.Open();
        Assert.That(link.Probe(20, out _, tryConsole: true), Is.EqualTo(GatewayCommandPath.TextConsole));
        Recorder rec = new Recorder();
        TrafficTestSettings settings = QuickSettings();
        settings.Rates = new uint[] { 115200 };

        TrafficTest test = new TrafficTest(settings, rec);
        bool passed = test.Run(link, null, CancellationToken.None);

        Assert.That(passed, Is.False);
        TrafficStatistics s = test.Results[0].Statistics[0];
        Assert.That(s.FramesLost, Is.GreaterThan(0));
        Assert.That(s.FramesBadFcs, Is.GreaterThan(0));
        Assert.That(s.FramesOk, Is.LessThan(s.FramesSent));
        Assert.That(gw.Commands, Does.Contain("BAUD 115200"));
        Assert.That(gw.DataFrames, Is.GreaterThan(0), "traffic flowed");
        Assert.That(gw.Commands[0], Is.EqualTo("LOG"), "the console was asked, no HELLO was sent to the legacy gateway");
        bool explained = false;
        for (int i = 0; i < rec.Messages.Count; i++)
        {
            explained |= rec.Messages[i].Contains("bad FCS", StringComparison.Ordinal);
        }
        Assert.That(explained, Is.True, "verbose explanation of a damaged frame");
    }

    [Test]
    public void DeadGateway_CannotSetBaud_OneFailedResult()
    {
        FakeTransport t = new FakeTransport();
        _ = new FakeGateway(t, FakeGateway.Kind.Dead);
        using GatewayLink link = new GatewayLink(t);
        link.Open();
        link.Probe(10, out _);
        Recorder rec = new Recorder();

        TrafficTest test = new TrafficTest(QuickSettings(), rec);
        bool passed = test.Run(link, null, CancellationToken.None);

        Assert.That(passed, Is.False);
        Assert.That(test.Results.Count, Is.EqualTo(1));
        Assert.That(test.Results[0].Error, Is.EqualTo("could not set BAUD"));
        Assert.That(test.Results[0].Passed, Is.False);
    }

    [Test]
    public void NoBaud_SkipsCommands_AndCancelEndsEarly()
    {
        FakeTransport t = new FakeTransport();
        FakeGateway gw = new FakeGateway(t, FakeGateway.Kind.ControlFrames);
        using GatewayLink link = new GatewayLink(t);
        link.Open();
        Recorder rec = new Recorder();
        TrafficTestSettings settings = QuickSettings();
        settings.NoBaud = true;
        settings.SecondsPerRate = 30;
        using CancellationTokenSource cts = new CancellationTokenSource(300);

        TrafficTest test = new TrafficTest(settings, rec);
        bool passed = test.Run(link, null, cts.Token);

        Assert.That(passed, Is.True);
        Assert.That(test.Results.Count, Is.EqualTo(1), "cancelled during the first rate");
        Assert.That(gw.Commands, Is.Empty);
    }

    [Test]
    public void TwoGateways_RunBothDirections()
    {
        FakeTransport ta = new FakeTransport { Name = "A" };
        FakeTransport tb = new FakeTransport { Name = "B" };
        // Wire A's output to B's input and B's to A's: a frame written on A comes back on B.
        FakeGateway ga = new FakeGateway(ta, FakeGateway.Kind.ControlFrames);
        FakeGateway gb = new FakeGateway(tb, FakeGateway.Kind.ControlFrames);
        Action<byte[]>? aLoop = ta.OnWrite;
        Action<byte[]>? bLoop = tb.OnWrite;
        ta.OnWrite = bytes => CrossWire(bytes, ta, tb, aLoop!);
        tb.OnWrite = bytes => CrossWire(bytes, tb, ta, bLoop!);

        using GatewayLink a = new GatewayLink(ta);
        using GatewayLink b = new GatewayLink(tb);
        a.Open();
        b.Open();
        Recorder rec = new Recorder();
        TrafficTestSettings settings = QuickSettings();
        settings.Rates = new uint[] { 38400 };
        settings.NoBaud = true;

        TrafficTest test = new TrafficTest(settings, rec);
        bool passed = test.Run(a, b, CancellationToken.None);

        Assert.That(passed, Is.True);
        Assert.That(test.Results[0].DirectionNames, Is.EqualTo(new[] { "A->B", "B->A" }));
        Assert.That(test.Results[0].Statistics[0].FramesOk, Is.GreaterThan(0));
        Assert.That(test.Results[0].Statistics[1].FramesOk, Is.GreaterThan(0));
        Assert.That(ga.DataFrames + gb.DataFrames, Is.EqualTo(0), "frames crossed the wire, nothing looped locally");
    }

    /// <summary>
    /// A data frame written on one transport is delivered on the other, as two gateways wired
    /// line to line would; control frames stay local (the fake gateway answers them itself).
    /// </summary>
    private static void CrossWire(byte[] bytes, FakeTransport self, FakeTransport other, Action<byte[]> localGateway)
    {
        ByteFrameDecoder dec = new ByteFrameDecoder();
        for (int i = 0; i < bytes.Length; i++)
        {
            if (dec.Feed(bytes[i]))
            {
                if (ControlFrame.TryDecode(dec.Frame, out _, out _))
                {
                    localGateway(bytes);
                    return;
                }
                other.Inject(bytes);
                return;
            }
        }
        localGateway(bytes);
    }
}
