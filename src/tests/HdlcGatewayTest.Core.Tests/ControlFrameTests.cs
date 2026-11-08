// CONTROL FRAME TESTS - the codec, and the proof that control and data frames never collide.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class ControlFrameTests
{
    [TestCase(ControlFrameType.Hello, 0)]
    [TestCase(ControlFrameType.Command, 1)]
    [TestCase(ControlFrameType.Reply, 17)]
    [TestCase(ControlFrameType.Log, 200)]
    [TestCase(ControlFrameType.Stat, 68)]
    [TestCase(ControlFrameType.Nak, ControlFrame.MaxPayload)]
    public void Encode_ThenTryDecode_RoundTripsEveryField(ControlFrameType type, int payloadLength)
    {
        byte[] payload = new byte[payloadLength];
        Random rng = new Random(payloadLength + 1);
        rng.NextBytes(payload);
        byte[] frame = new byte[payloadLength + ControlFrame.Overhead];

        int n = ControlFrame.Encode(type, 0x2A, 0x31, payload, frame);

        Assert.That(n, Is.EqualTo(frame.Length));
        Assert.That(frame[0], Is.EqualTo(ControlFrame.Address));
        Assert.That(ControlFrame.TryDecode(frame, out ControlFrameHeader header, out ReadOnlySpan<byte> decoded), Is.True);
        Assert.That(header.Type, Is.EqualTo(type));
        Assert.That(header.Id, Is.EqualTo((byte)0x2A));
        Assert.That(header.Flags, Is.EqualTo((byte)0x31));
        Assert.That(header.IsLast, Is.True);
        Assert.That(header.IsError, Is.False);
        Assert.That(header.Chunk, Is.EqualTo(3));
        Assert.That(decoded.ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public void Encode_RefusesPayloadLongerThanMax()
    {
        byte[] payload = new byte[ControlFrame.MaxPayload + 1];
        byte[] frame = new byte[ByteFrame.MaxFrameLength + 8];
        Assert.That(() => ControlFrame.Encode(ControlFrameType.Command, 1, 0, payload, frame),
                    Throws.ArgumentException);
    }

    [Test]
    public void TryDecode_RejectsWrongAddress_WrongCheck_AndTooShort()
    {
        byte[] frame = new byte[10];
        ControlFrame.Encode(ControlFrameType.Ping, 5, 0, new byte[4], frame);

        byte[] wrongAddress = (byte[])frame.Clone();
        wrongAddress[0] = 0x01;
        Assert.That(ControlFrame.TryDecode(wrongAddress, out _, out _), Is.False);

        byte[] wrongCheck = (byte[])frame.Clone();
        wrongCheck[7] ^= 0x01;
        Assert.That(ControlFrame.TryDecode(wrongCheck, out _, out _), Is.False);

        Assert.That(ControlFrame.TryDecode(new byte[] { 0xFE, 0x01, 0x00, 0x00, 0x00 }, out _, out _), Is.False);
    }

    [Test]
    public void ControlFrame_NeverPassesTheHdlcFcsCheck()
    {
        // The second lock: a frame whose check is the un-inverted CRC leaves residue 0x0000, a
        // good HDLC frame leaves 0xF0B8. 10,000 random control frames, none may look like data.
        Random rng = new Random(1);
        byte[] frame = new byte[ByteFrame.MaxFrameLength];
        for (int i = 0; i < 10_000; i++)
        {
            int len = rng.Next(0, 64);
            byte[] payload = new byte[len];
            rng.NextBytes(payload);
            int n = ControlFrame.Encode((ControlFrameType)rng.Next(1, 7), (byte)rng.Next(256),
                                        (byte)rng.Next(256), payload, frame);
            Assert.That(Fcs.IsGood(new ReadOnlySpan<byte>(frame, 0, n)), Is.False, "iteration " + i);
        }
    }

    [Test]
    public void DataFrameWithGoodFcs_NeverDecodesAsControl_EvenWhenAddressedToFE()
    {
        // The address lock alone could be fooled by traffic that happens to use 0xFE; the CRC
        // lock cannot. 10,000 random frames to address 0xFE with a correct HDLC FCS.
        Random rng = new Random(2);
        for (int i = 0; i < 10_000; i++)
        {
            byte[] frame = new byte[rng.Next(4, 64)];
            rng.NextBytes(frame);
            frame[0] = ControlFrame.Address;
            Fcs.Append(frame);
            Assert.That(Fcs.IsGood(frame), Is.True);
            Assert.That(ControlFrame.TryDecode(frame, out _, out _), Is.False, "iteration " + i);
        }
    }

    [Test]
    public void EventCodes_MatchTheFirmwareHeader()
    {
        // control_frame.h in the HDLCGW sketch, 2026-11-08. A change there is a change here.
        Assert.That((byte)GatewayEvent.Boot, Is.EqualTo(1));
        Assert.That((byte)GatewayEvent.UsbHostLost, Is.EqualTo(7));
        Assert.That((byte)GatewayEvent.WifiConnecting, Is.EqualTo(16));
        Assert.That((byte)GatewayEvent.WifiUp, Is.EqualTo(17));
        Assert.That((byte)GatewayEvent.WifiDown, Is.EqualTo(18));
        Assert.That((byte)GatewayEvent.TcpListening, Is.EqualTo(19));
        Assert.That((byte)GatewayEvent.TcpPeerConnected, Is.EqualTo(20));
        Assert.That((byte)GatewayEvent.TcpPeerLost, Is.EqualTo(21));
        Assert.That((byte)GatewayEvent.TcpConnecting, Is.EqualTo(22));
        Assert.That((byte)GatewayEvent.TcpConnectFailed, Is.EqualTo(23));
    }

    [Test]
    public void Header_FlagBits()
    {
        ControlFrameHeader h = new ControlFrameHeader(ControlFrameType.Reply, 1, 0xF3);
        Assert.That(h.IsLast, Is.True);
        Assert.That(h.IsError, Is.True);
        Assert.That(h.Chunk, Is.EqualTo(15));
    }
}
