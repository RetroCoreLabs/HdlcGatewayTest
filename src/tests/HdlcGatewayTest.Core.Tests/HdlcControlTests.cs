// HDLC CONTROL TESTS - the decoder agrees with the firmware's table.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class HdlcControlTests
{
    // Control bytes the HDLCGW console decoded on the bench: "F 01 3F" -> U SABM P/F=1
    // (M1 3, M2 1); "DECODE 01 11" -> S RR P/F=1 N(R)=0; 0x00 -> I N(S)=0 N(R)=0.
    [TestCase((byte)0x3F, HdlcFrameClass.Unnumbered, "SABM", true, 0, 0)]
    [TestCase((byte)0x11, HdlcFrameClass.Supervisory, "RR", true, 0, 0)]
    [TestCase((byte)0x00, HdlcFrameClass.Information, "", false, 0, 0)]
    [TestCase((byte)0xA6, HdlcFrameClass.Information, "", false, 3, 5)]
    [TestCase((byte)0x09, HdlcFrameClass.Supervisory, "REJ", false, 0, 0)]
    [TestCase((byte)0x0D, HdlcFrameClass.Supervisory, "SREJ", false, 0, 0)]
    [TestCase((byte)0x05, HdlcFrameClass.Supervisory, "RNR", false, 0, 0)]
    [TestCase((byte)0x63, HdlcFrameClass.Unnumbered, "UA", false, 0, 0)]
    [TestCase((byte)0x43, HdlcFrameClass.Unnumbered, "DISC", false, 0, 0)]
    // 0x0F is both SARM (command) and DM (response); the table's first match wins, as in the firmware.
    [TestCase((byte)0x0F, HdlcFrameClass.Unnumbered, "SARM", false, 0, 0)]
    [TestCase((byte)0x87, HdlcFrameClass.Unnumbered, "FRMR", false, 0, 0)]
    [TestCase((byte)0x03, HdlcFrameClass.Unnumbered, "UI", false, 0, 0)]
    public void Decode_MatchesTheFirmwareTable(byte control, HdlcFrameClass cls, string name, bool pf, int ns, int nr)
    {
        HdlcControl c = HdlcControl.Decode(control);
        Assert.That(c.Class, Is.EqualTo(cls));
        Assert.That(c.Name, Is.EqualTo(name));
        Assert.That(c.PollFinal, Is.EqualTo(pf));
        Assert.That(c.SendSequence, Is.EqualTo(ns));
        Assert.That(c.ReceiveSequence, Is.EqualTo(nr));
    }

    [Test]
    public void Summary_ReadsLikeTheFrameList()
    {
        Assert.That(HdlcControl.Decode(0xA6).Summary(), Is.EqualTo("I N(S)=3 N(R)=5"));
        Assert.That(HdlcControl.Decode(0x51).Summary(), Is.EqualTo("RR N(R)=2 P/F"));
        Assert.That(HdlcControl.Decode(0x3F).Summary(), Is.EqualTo("SABM P/F"));
        Assert.That(HdlcControl.Decode(0xFF).Summary(), Is.EqualTo("UIH P/F"));
    }

    [Test]
    public void EveryByteDecodes_WithoutThrowing()
    {
        for (int b = 0; b < 256; b++)
        {
            HdlcControl c = HdlcControl.Decode((byte)b);
            Assert.That(c.ClassLetter, Is.AnyOf("I", "S", "U"));
            Assert.That(c.Summary(), Is.Not.Empty);
        }
    }
}
