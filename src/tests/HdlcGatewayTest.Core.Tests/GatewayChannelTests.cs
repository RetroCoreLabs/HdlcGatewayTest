// GATEWAY CHANNEL TESTS - every frame lands in exactly one place, however the bytes are split.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Text;
using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class GatewayChannelTests
{
    private static byte[] DataFrame(byte address, int length, int seed)
    {
        byte[] frame = new byte[length];
        new Random(seed).NextBytes(frame);
        frame[0] = address;
        Fcs.Append(frame);
        return frame;
    }

    private static byte[] Stuffed(byte[] frame)
    {
        byte[] buf = new byte[ByteFrame.MaxEncodedLength(frame.Length)];
        int n = ByteFrame.Encode(frame, buf);
        return new ArraySegment<byte>(buf, 0, n).ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            total += parts[i].Length;
        }
        byte[] all = new byte[total];
        int o = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            Array.Copy(parts[i], 0, all, o, parts[i].Length);
            o += parts[i].Length;
        }
        return all;
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(64)]
    [TestCase(100_000)]
    public void Feed_SortsTextDataControlAndBad_WhateverTheSplit(int chunk)
    {
        byte[] banner = Encoding.ASCII.GetBytes("HDLC GW version 0.2.0\r\n");
        byte[] data = DataFrame(0xA1, 40, 7);
        byte[] control = new byte[12 + ControlFrame.Overhead];
        ControlFrame.Encode(ControlFrameType.Reply, 9, 0x01, Encoding.ASCII.GetBytes("Hello world!"), control);
        byte[] bad = DataFrame(0x03, 20, 8);
        bad[5] ^= 0x40;
        byte[] stream = Concat(banner, Stuffed(data), Stuffed(control), Stuffed(bad), new byte[] { 0x7E, 0x7E });

        List<string> events = new List<string>();
        StringBuilder text = new StringBuilder();
        GatewayChannel channel = new GatewayChannel(_ => { });
        channel.TextReceived = b => text.Append(Encoding.ASCII.GetString(b));
        channel.DataFrameReceived = f => events.Add("data " + f.Length);
        channel.ControlFrameReceived = (in ControlFrameHeader h, ReadOnlySpan<byte> p) =>
            events.Add("control " + h.Type + " id " + h.Id + " " + Encoding.ASCII.GetString(p));
        channel.BadFrameReceived = f => events.Add("bad " + f.Length);

        for (int o = 0; o < stream.Length; o += chunk)
        {
            channel.Feed(new ReadOnlySpan<byte>(stream, o, Math.Min(chunk, stream.Length - o)));
        }

        Assert.That(text.ToString(), Is.EqualTo("HDLC GW version 0.2.0\r\n"));
        Assert.That(events, Is.EqualTo(new[] { "data 40", "control Reply id 9 Hello world!", "bad 20" }));
        Assert.That(channel.DataFrames, Is.EqualTo(1));
        Assert.That(channel.ControlFrames, Is.EqualTo(1));
        Assert.That(channel.BadFrames, Is.EqualTo(1));
    }

    [Test]
    public void Send_WritesOneStuffedFrame_AndSendControlBuildsTheFrame()
    {
        List<byte[]> writes = new List<byte[]>();
        GatewayChannel channel = new GatewayChannel(b => writes.Add(b.ToArray()));

        channel.SendControl(ControlFrameType.Command, 3, Encoding.ASCII.GetBytes("STAT"));

        Assert.That(writes.Count, Is.EqualTo(1));
        byte[] w = writes[0];
        Assert.That(w[0], Is.EqualTo(ByteFrame.Flag));
        Assert.That(w[w.Length - 1], Is.EqualTo(ByteFrame.Flag));

        ByteFrameDecoder dec = new ByteFrameDecoder();
        bool got = false;
        for (int i = 0; i < w.Length; i++)
        {
            if (dec.Feed(w[i]))
            {
                got = true;
                Assert.That(ControlFrame.TryDecode(dec.Frame, out ControlFrameHeader h, out ReadOnlySpan<byte> p), Is.True);
                Assert.That(h.Type, Is.EqualTo(ControlFrameType.Command));
                Assert.That(h.Id, Is.EqualTo((byte)3));
                Assert.That(Encoding.ASCII.GetString(p), Is.EqualTo("STAT"));
            }
        }
        Assert.That(got, Is.True);
    }

    [Test]
    public void Send_FromTwoThreads_NeverInterleavesFrames()
    {
        // 2 x 2000 frames through one writer; the far side must see every frame intact.
        MemoryStream wire = new MemoryStream();
        object wireLock = new object();
        GatewayChannel channel = new GatewayChannel(b =>
        {
            lock (wireLock)
            {
                wire.Write(b);
            }
        });
        byte[] a = DataFrame(0xA1, 100, 11);
        byte[] b2 = DataFrame(0xB2, 300, 12);

        Thread ta = new Thread(() => { for (int i = 0; i < 2000; i++) { channel.Send(a); } });
        Thread tb = new Thread(() => { for (int i = 0; i < 2000; i++) { channel.Send(b2); } });
        ta.Start();
        tb.Start();
        ta.Join();
        tb.Join();

        int countA = 0;
        int countB = 0;
        GatewayChannel reader = new GatewayChannel(_ => { });
        reader.DataFrameReceived = f =>
        {
            if (f.SequenceEqual(a)) { countA++; }
            else if (f.SequenceEqual(b2)) { countB++; }
            else { Assert.Fail("a frame that is neither A nor B"); }
        };
        reader.BadFrameReceived = f => Assert.Fail("bad frame of " + f.Length + " bytes");
        reader.Feed(wire.ToArray());

        Assert.That(countA, Is.EqualTo(2000));
        Assert.That(countB, Is.EqualTo(2000));
    }

    [Test]
    public void Reset_MakesFollowingBytesTextAgain()
    {
        StringBuilder text = new StringBuilder();
        GatewayChannel channel = new GatewayChannel(_ => { });
        channel.TextReceived = b => text.Append(Encoding.ASCII.GetString(b));

        channel.Feed(new byte[] { 0x7E, 0x41, 0x42 });    // inside a frame now
        Assert.That(text.Length, Is.EqualTo(0));
        channel.Reset();
        channel.Feed(Encoding.ASCII.GetBytes("# "));
        Assert.That(text.ToString(), Is.EqualTo("# "));
    }
}
