// FCS TESTS - against values the gateways printed on the bench.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class FcsTests
{
    // Printed by the HDLCGW console on 2026-11-08: "F 01 03 48 49" -> Received CRC=0x5862,
    // "F 01 3F" -> 0xDFEB, "DECODE 01 11" -> 0x1797. The FCS goes on the wire low byte first.
    [TestCase(new byte[] { 0x01, 0x03, 0x48, 0x49 }, (ushort)0x5862)]
    [TestCase(new byte[] { 0x01, 0x3F }, (ushort)0xDFEB)]
    [TestCase(new byte[] { 0x01, 0x11 }, (ushort)0x1797)]
    public void Append_MatchesTheGatewayConsole(byte[] data, ushort expectedFcs)
    {
        byte[] frame = new byte[data.Length + 2];
        Array.Copy(data, frame, data.Length);

        Fcs.Append(frame);

        Assert.That(frame[data.Length], Is.EqualTo((byte)(expectedFcs & 0xFF)));
        Assert.That(frame[data.Length + 1], Is.EqualTo((byte)(expectedFcs >> 8)));
    }

    [Test]
    public void IsGood_TrueForAppendedFrame_FalseAfterOneBitFlip()
    {
        byte[] frame = new byte[30];
        for (int i = 0; i < 28; i++)
        {
            frame[i] = (byte)(i * 7);
        }
        Fcs.Append(frame);

        Assert.That(Fcs.IsGood(frame), Is.True);
        frame[13] ^= 0x10;
        Assert.That(Fcs.IsGood(frame), Is.False);
    }

    [Test]
    public void IsGood_FalseForTooShort()
    {
        Assert.That(Fcs.IsGood(new byte[] { 0x7E }), Is.False);
        Assert.That(Fcs.IsGood(ReadOnlySpan<byte>.Empty), Is.False);
    }

    [Test]
    public void Compute_OverGoodFrameLeavesResidue()
    {
        // RFC 1662 appendix C: the CRC over data plus FCS is always 0xF0B8.
        byte[] frame = new byte[] { 0x55, 0x00, 0x41, 0x42, 0x43, 0, 0 };
        Fcs.Append(frame);
        Assert.That(Fcs.Compute(frame), Is.EqualTo(Fcs.Good));
    }
}
