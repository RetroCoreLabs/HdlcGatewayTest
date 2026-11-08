// TEST FRAME TESTS - a frame verifies against itself and fails on every kind of damage.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class TestFrameTests
{
    [TestCase(TestFrame.MinLength)]
    [TestCase(126)]
    [TestCase(1024)]
    public void Build_ThenVerify_IsOk(int length)
    {
        byte[] buf = new byte[length];
        Span<byte> frame = TestFrame.Build(1234567u, length, 0xA1, buf);

        TestFrameVerdict v = TestFrame.Verify(frame, out uint seq, out byte addr);

        Assert.That(v, Is.EqualTo(TestFrameVerdict.Ok));
        Assert.That(seq, Is.EqualTo(1234567u));
        Assert.That(addr, Is.EqualTo((byte)0xA1));
        Assert.That(frame[1], Is.EqualTo(TestFrame.Control));
    }

    [Test]
    public void Verify_BadFcs_WhenAPayloadBitFlips()
    {
        byte[] buf = new byte[126];
        TestFrame.Build(7u, 126, 0xA1, buf);
        buf[50] ^= 0x01;

        Assert.That(TestFrame.Verify(buf, out _, out _), Is.EqualTo(TestFrameVerdict.BadFcs));
    }

    [Test]
    public void Verify_BadPayload_WhenPayloadChangedAndFcsRecomputed()
    {
        // What a gateway that corrupts data and re-stamps the FCS would produce. Ours never
        // touch the FCS, but the check must still catch it.
        byte[] buf = new byte[126];
        TestFrame.Build(7u, 126, 0xA1, buf);
        buf[50] ^= 0x01;
        Fcs.Append(buf);

        Assert.That(TestFrame.Verify(buf, out _, out _), Is.EqualTo(TestFrameVerdict.BadPayload));
    }

    [Test]
    public void Verify_BadLength_WhenTruncatedAndFcsRecomputed()
    {
        byte[] buf = new byte[126];
        TestFrame.Build(7u, 126, 0xA1, buf);
        byte[] shorter = new byte[100];
        Array.Copy(buf, shorter, 98);
        Fcs.Append(shorter);

        Assert.That(TestFrame.Verify(shorter, out _, out _), Is.EqualTo(TestFrameVerdict.BadLength));
    }

    [Test]
    public void Verify_TooShort()
    {
        Assert.That(TestFrame.Verify(new byte[5], out _, out _), Is.EqualTo(TestFrameVerdict.TooShort));
    }

    [Test]
    public void Build_DifferentSequenceOrAddress_GivesDifferentPayload()
    {
        byte[] a = new byte[64];
        byte[] b = new byte[64];
        byte[] c = new byte[64];
        TestFrame.Build(1u, 64, 0xA1, a);
        TestFrame.Build(2u, 64, 0xA1, b);
        TestFrame.Build(1u, 64, 0xB2, c);

        Assert.That(new ArraySegment<byte>(a, 8, 54), Is.Not.EqualTo(new ArraySegment<byte>(b, 8, 54)));
        Assert.That(new ArraySegment<byte>(a, 8, 54), Is.Not.EqualTo(new ArraySegment<byte>(c, 8, 54)));
    }

    [Test]
    public void Build_RejectsTooShortLength()
    {
        byte[] buf = new byte[64];
        Assert.Throws<ArgumentOutOfRangeException>(() => TestFrame.Build(0, TestFrame.MinLength - 1, 1, buf));
    }
}
