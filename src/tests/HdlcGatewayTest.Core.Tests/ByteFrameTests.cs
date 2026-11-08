// BYTE FRAME TESTS - encoder and decoder agree, and the decoder matches the firmware's corner cases.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class ByteFrameTests
{
    [Test]
    public void Encode_EscapesFlagEscapeAndControlBytes()
    {
        byte[] frame = { 0x7E, 0x7D, 0x1F, 0x20, 0x41 };
        byte[] dest = new byte[ByteFrame.MaxEncodedLength(frame.Length)];

        int n = ByteFrame.Encode(frame, dest);

        byte[] expected = { 0x7E, 0x7D, 0x5E, 0x7D, 0x5D, 0x7D, 0x3F, 0x20, 0x41, 0x7E };
        Assert.That(new ArraySegment<byte>(dest, 0, n), Is.EqualTo(expected));
    }

    [Test]
    public void Decoder_RoundTripsEveryByteValue()
    {
        byte[] frame = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            frame[i] = (byte)i;
        }
        byte[] encoded = new byte[ByteFrame.MaxEncodedLength(frame.Length)];
        int n = ByteFrame.Encode(frame, encoded);

        ByteFrameDecoder dec = new ByteFrameDecoder();
        int delivered = 0;
        for (int i = 0; i < n; i++)
        {
            if (dec.Feed(encoded[i]))
            {
                delivered++;
                Assert.That(dec.Frame.ToArray(), Is.EqualTo(frame));
            }
        }
        Assert.That(delivered, Is.EqualTo(1));
        Assert.That(dec.DroppedFrames, Is.EqualTo(0));
    }

    [Test]
    public void Decoder_IgnoresTextBeforeFirstFlag_AndBackToBackFlags()
    {
        ByteFrameDecoder dec = new ByteFrameDecoder();
        byte[] noise = System.Text.Encoding.ASCII.GetBytes("Exiting debug mode...\r\n");
        for (int i = 0; i < noise.Length; i++)
        {
            Assert.That(dec.Feed(noise[i]), Is.False);
        }
        Assert.That(dec.Feed(0x7E), Is.False);
        Assert.That(dec.Feed(0x7E), Is.False);
        Assert.That(dec.Feed(0x7E), Is.False);
        Assert.That(dec.Feed(0x01), Is.False);
        Assert.That(dec.Feed(0x02), Is.False);
        Assert.That(dec.Feed(0x7E), Is.True);
        Assert.That(dec.Frame.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02 }));
        Assert.That(dec.IsIdle, Is.True);
    }

    [Test]
    public void Decoder_SharedFlagEndsOneFrameAndStartsTheNext()
    {
        ByteFrameDecoder dec = new ByteFrameDecoder();
        byte[] stream = { 0x7E, 0xAA, 0x7E, 0xBB, 0xCC, 0x7E };
        List<byte[]> frames = new List<byte[]>();
        for (int i = 0; i < stream.Length; i++)
        {
            if (dec.Feed(stream[i]))
            {
                frames.Add(dec.Frame.ToArray());
            }
        }
        Assert.That(frames.Count, Is.EqualTo(2));
        Assert.That(frames[0], Is.EqualTo(new byte[] { 0xAA }));
        Assert.That(frames[1], Is.EqualTo(new byte[] { 0xBB, 0xCC }));
    }

    [Test]
    public void Decoder_DropsFrameEndingInsideEscape_AndCountsIt()
    {
        ByteFrameDecoder dec = new ByteFrameDecoder();
        byte[] stream = { 0x7E, 0x41, 0x7D, 0x7E, 0x42, 0x7E };
        List<byte[]> frames = new List<byte[]>();
        for (int i = 0; i < stream.Length; i++)
        {
            if (dec.Feed(stream[i]))
            {
                frames.Add(dec.Frame.ToArray());
            }
        }
        Assert.That(frames.Count, Is.EqualTo(1));
        Assert.That(frames[0], Is.EqualTo(new byte[] { 0x42 }));
        Assert.That(dec.DroppedFrames, Is.EqualTo(1));
    }

    [Test]
    public void Decoder_DropsOverlongFrame_AndRecovers()
    {
        ByteFrameDecoder dec = new ByteFrameDecoder(4);
        Assert.That(dec.Feed(0x7E), Is.False);
        for (int i = 0; i < 5; i++)
        {
            Assert.That(dec.Feed(0x11), Is.False);
        }
        Assert.That(dec.Feed(0x7E), Is.False);
        Assert.That(dec.DroppedFrames, Is.EqualTo(1));
        Assert.That(dec.Feed(0x22), Is.False);
        Assert.That(dec.Feed(0x7E), Is.True);
        Assert.That(dec.Frame.ToArray(), Is.EqualTo(new byte[] { 0x22 }));
    }
}
