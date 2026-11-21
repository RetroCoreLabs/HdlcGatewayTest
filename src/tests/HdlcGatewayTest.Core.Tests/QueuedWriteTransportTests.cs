// QUEUED WRITE TRANSPORT TESTS - writes return at once, arrive in order, and drain on dispose.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class QueuedWriteTransportTests
{
    [Test]
    public void Writes_DoNotBlockTheCaller_AndReachTheInnerTransportInOrder()
    {
        FakeTransport inner = new FakeTransport();
        ManualResetEventSlim release = new ManualResetEventSlim(false);
        inner.OnWrite = _ => release.Wait(5000);    // the "driver" is slow
        QueuedWriteTransport t = new QueuedWriteTransport(inner);
        t.Open();

        long start = Environment.TickCount64;
        t.Write(new byte[] { 1 });
        t.Write(new byte[] { 2, 2 });
        t.Write(new byte[] { 3, 3, 3 });
        Assert.That(Environment.TickCount64 - start, Is.LessThan(500), "Write() returned without waiting for the slow inner write");
        Assert.That(t.QueuedBytes, Is.GreaterThan(0));

        release.Set();
        Assert.That(t.Flush(5000), Is.True, "the queue drained once the inner transport took the bytes");
        lock (inner.WrittenBlocks)
        {
            Assert.That(inner.WrittenBlocks.Count, Is.EqualTo(3));
            Assert.That(inner.WrittenBlocks[0], Is.EqualTo(new byte[] { 1 }));
            Assert.That(inner.WrittenBlocks[1], Is.EqualTo(new byte[] { 2, 2 }));
            Assert.That(inner.WrittenBlocks[2], Is.EqualTo(new byte[] { 3, 3, 3 }));
        }
        Assert.That(t.Writes.Bytes, Is.EqualTo(6), "the meter is the inner transport's");
        t.Dispose();
    }

    [Test]
    public void ReadsAndCounters_PassThrough_AndDisposeDrains()
    {
        FakeTransport inner = new FakeTransport();
        QueuedWriteTransport t = new QueuedWriteTransport(inner);
        t.Open();
        inner.Inject(new byte[] { 9, 8, 7 });
        byte[] buf = new byte[8];
        Assert.That(t.Read(buf), Is.EqualTo(3));
        Assert.That(t.BytesReceived, Is.EqualTo(3));
        t.Write(new byte[] { 4, 5 });
        t.Dispose();
        lock (inner.WrittenBlocks)
        {
            Assert.That(inner.WrittenBlocks.Count, Is.EqualTo(1), "dispose wrote what was still queued");
        }
        Assert.That(inner.IsOpen, Is.False, "dispose closed the inner transport");
    }

    [Test]
    public void Write_BeyondTheBound_IsDroppedAndCounted()
    {
        FakeTransport inner = new FakeTransport();
        ManualResetEventSlim release = new ManualResetEventSlim(false);
        inner.OnWrite = _ => release.Wait(5000);
        QueuedWriteTransport t = new QueuedWriteTransport(inner, maxQueuedBytes: 1500);
        t.Open();
        t.Write(new byte[600]);    // taken by the writer thread, which then blocks; still counted until written
        Thread.Sleep(50);
        t.Write(new byte[600]);    // queued: 1200 bytes not yet written
        t.Write(new byte[600]);    // would make 1800, over the bound: dropped
        Assert.That(t.DroppedWrites, Is.EqualTo(1));
        release.Set();
        t.Dispose();
    }
}
