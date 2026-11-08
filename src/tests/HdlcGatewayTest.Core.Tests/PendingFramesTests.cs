// PENDING FRAMES TESTS - in order, skipped, late, unknown, expired.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class PendingFramesTests
{
    [Test]
    public void InOrder_RemovesOneAndReportsRoundTrip()
    {
        PendingFrames p = new PendingFrames(8);
        p.Add(0, 100, 50);
        p.Add(1, 200, 50);

        AcknowledgeResult r = p.Acknowledge(0, 1100, out int lost, out long rtt);

        Assert.That(r, Is.EqualTo(AcknowledgeResult.InOrder));
        Assert.That(lost, Is.EqualTo(0));
        Assert.That(rtt, Is.EqualTo(1000));
        Assert.That(p.Count, Is.EqualTo(1));
        Assert.That(p.BytesInFlight, Is.EqualTo(50));
    }

    [Test]
    public void SkippedOlder_CountsTheGapAsLost()
    {
        PendingFrames p = new PendingFrames(8);
        p.Add(0, 1, 10);
        p.Add(1, 2, 10);
        p.Add(2, 3, 10);
        p.Add(3, 4, 10);

        AcknowledgeResult r = p.Acknowledge(2, 10, out int lost, out _);

        Assert.That(r, Is.EqualTo(AcknowledgeResult.SkippedOlder));
        Assert.That(lost, Is.EqualTo(2));
        Assert.That(p.Count, Is.EqualTo(1));
        Assert.That(p.BytesInFlight, Is.EqualTo(10));
    }

    [Test]
    public void Late_WhenOlderThanOldestPending()
    {
        PendingFrames p = new PendingFrames(8);
        p.Add(5, 1, 10);
        p.Add(6, 2, 10);

        Assert.That(p.Acknowledge(4, 10, out _, out _), Is.EqualTo(AcknowledgeResult.Late));
        Assert.That(p.Count, Is.EqualTo(2));
    }

    [Test]
    public void Unknown_WhenNewerThanAnythingSent_OrNothingPending()
    {
        PendingFrames p = new PendingFrames(8);
        Assert.That(p.Acknowledge(0, 10, out _, out _), Is.EqualTo(AcknowledgeResult.Unknown));
        p.Add(0, 1, 10);
        Assert.That(p.Acknowledge(1, 10, out _, out _), Is.EqualTo(AcknowledgeResult.Unknown));
        Assert.That(p.Count, Is.EqualTo(1));
    }

    [Test]
    public void ExpireSentBefore_RemovesOldOnes()
    {
        PendingFrames p = new PendingFrames(8);
        p.Add(0, 100, 10);
        p.Add(1, 200, 10);
        p.Add(2, 300, 10);

        Assert.That(p.ExpireSentBefore(200), Is.EqualTo(2));
        Assert.That(p.Count, Is.EqualTo(1));
        Assert.That(p.Acknowledge(2, 400, out _, out _), Is.EqualTo(AcknowledgeResult.InOrder));
    }

    [Test]
    public void WrapsAroundTheRing()
    {
        PendingFrames p = new PendingFrames(3);
        for (uint i = 0; i < 10; i++)
        {
            p.Add(i, i, 1);
            if (p.IsFull)
            {
                Assert.That(p.Acknowledge(i - 2, 100, out int lost, out _), Is.EqualTo(AcknowledgeResult.InOrder));
                Assert.That(lost, Is.EqualTo(0));
            }
        }
        Assert.That(p.Count, Is.EqualTo(2));
    }

    [Test]
    public void Clear_LetsTheNumberingStartFromZeroAgain()
    {
        // Each rate of a run starts its sequence numbers at 0; the second rate crashed on the
        // bench until Clear() also forgot the last number added.
        PendingFrames p = new PendingFrames(4);
        p.Add(0, 1, 1);
        p.Add(1, 2, 1);
        p.Clear();
        Assert.DoesNotThrow(() => p.Add(0, 3, 1));
        Assert.That(p.Count, Is.EqualTo(1));
        Assert.That(p.BytesInFlight, Is.EqualTo(1));
    }

    [Test]
    public void Add_RejectsNonIncreasingSequence_AndFullQueue()
    {
        PendingFrames p = new PendingFrames(1);
        p.Add(3, 1, 1);
        Assert.Throws<InvalidOperationException>(() => p.Add(4, 1, 1));
        p.Acknowledge(3, 2, out _, out _);
        Assert.Throws<ArgumentException>(() => p.Add(3, 1, 1));
    }
}
