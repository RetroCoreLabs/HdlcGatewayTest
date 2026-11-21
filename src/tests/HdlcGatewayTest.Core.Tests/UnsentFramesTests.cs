// UNSENT FRAMES TESTS - frames still in the PC's write queue at the end are unsent, not lost.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class UnsentFramesTests
{
    [Test]
    public void PendingFrames_RemoveNewestWithin_TakesTheTailThatFits()
    {
        PendingFrames p = new PendingFrames(8);
        p.Add(0, 100, 50);
        p.Add(1, 101, 50);
        p.Add(2, 102, 50);
        p.Add(3, 103, 50);
        Assert.That(p.RemoveNewestWithin(120), Is.EqualTo(2), "two whole frames fit in 120 queued bytes");
        Assert.That(p.Count, Is.EqualTo(2));
        Assert.That(p.BytesInFlight, Is.EqualTo(100));
        Assert.That(p.RemoveNewestWithin(10), Is.EqualTo(0), "a frame bigger than the queued bytes stays");
        Assert.That(p.RemoveNewestWithin(1000), Is.EqualTo(2));
        Assert.That(p.Count, Is.EqualTo(0));
    }

    [Test]
    public void Statistics_Passed_IgnoresUnsentFrames()
    {
        TrafficStatistics s = new TrafficStatistics { FramesSent = 10, FramesOk = 7, FramesUnsent = 3 };
        Assert.That(s.Passed, Is.True, "7 of 7 frames that left the PC came back");
        s.FramesUnsent = 2;
        Assert.That(s.Passed, Is.False, "now one frame did leave and never came back");
        TrafficStatistics c = s.Clone();
        Assert.That(c.FramesUnsent, Is.EqualTo(2));
        c.Clear();
        Assert.That(c.FramesUnsent, Is.EqualTo(0));
    }

    [Test]
    public void TrafficTest_OverASlowWriter_CountsQueuedFramesAsUnsent()
    {
        // A loopback whose "driver" takes ten writes and then stops taking anything (a far end
        // that stalls): the window fills the queue at once, ten frames come back, and the rest
        // must come back as unsent, not lost, even though their clock would have run out.
        FakeTransport inner = new FakeTransport();
        ManualResetEventSlim stall = new ManualResetEventSlim(false);
        int writes = 0;
        inner.OnWrite = block =>
        {
            if (Interlocked.Increment(ref writes) > 10)
            {
                stall.Wait(20000);
                return;
            }
            inner.Inject(block);    // loopback: what was written comes straight back
        };
        QueuedWriteTransport transport = new QueuedWriteTransport(inner);
        using GatewayLink link = new GatewayLink(transport);
        transport.Open();
        TrafficTestSettings settings = new TrafficTestSettings
        {
            Rates = new uint[] { 9600 },
            SecondsPerRate = 1,
            FrameLength = 32,
            WindowBytes = 2000,
            TimeoutSeconds = 1,
            NoBaud = true,
        };
        RecordingObserver observer = new RecordingObserver();
        TrafficTest test = new TrafficTest(settings, observer);
        bool passed = test.Run(link, null, CancellationToken.None);
        TrafficStatistics s = observer.Results[0].Statistics[0];
        stall.Set();
        Assert.That(s.FramesOk, Is.EqualTo(10), "ten writes went through the stalled driver");
        Assert.That(s.FramesLost, Is.EqualTo(0), "nothing that left the PC was lost");
        Assert.That(s.FramesUnsent, Is.GreaterThan(0), "the rest never left the PC");
        Assert.That(s.FramesOk + s.FramesUnsent, Is.EqualTo(s.FramesSent));
        Assert.That(passed, Is.True);
    }

    [Test]
    public void PendingFrames_ExpireSentBefore_SparesTheQueuedTail()
    {
        PendingFrames p = new PendingFrames(8);
        p.Add(0, 100, 50);
        p.Add(1, 101, 50);
        p.Add(2, 102, 50);
        Assert.That(p.CountNewestWithin(100), Is.EqualTo(2));
        Assert.That(p.ExpireSentBefore(1000, 100), Is.EqualTo(1), "only the frame that left the PC expires");
        Assert.That(p.Count, Is.EqualTo(2));
        Assert.That(p.ExpireSentBefore(1000, 0), Is.EqualTo(2), "with the queue empty the rest expire");
    }

    private sealed class RecordingObserver : ITrafficTestObserver
    {
        public List<RateResult> Results { get; } = new List<RateResult>();

        public void Status(uint rate, TrafficDirection[] directions, long elapsedTicks, bool done)
        {
        }

        public void Message(string text)
        {
        }

        public void RateFinished(RateResult result)
        {
            Results.Add(result);
        }
    }
}
