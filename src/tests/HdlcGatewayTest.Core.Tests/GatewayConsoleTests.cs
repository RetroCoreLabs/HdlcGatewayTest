// GATEWAY CONSOLE TESTS - replies find their request, chunks assemble, unsolicited frames go elsewhere.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Text;
using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class GatewayConsoleTests
{
    /// <summary>A channel whose writes are captured and whose input can be injected as frames.</summary>
    private sealed class Harness
    {
        public readonly List<byte[]> Sent = new List<byte[]>();
        public readonly GatewayChannel Channel;
        public readonly GatewayConsole Console;

        public Harness()
        {
            Channel = new GatewayChannel(b => Sent.Add(b.ToArray()));
            Console = new GatewayConsole(Channel);
        }

        /// <summary>Decode the last frame the console sent.</summary>
        public ControlFrameHeader LastSentHeader(out string payloadText)
        {
            ByteFrameDecoder dec = new ByteFrameDecoder();
            byte[] w = Sent[Sent.Count - 1];
            for (int i = 0; i < w.Length; i++)
            {
                if (dec.Feed(w[i]))
                {
                    Assert.That(ControlFrame.TryDecode(dec.Frame, out ControlFrameHeader h, out ReadOnlySpan<byte> p), Is.True);
                    payloadText = Encoding.UTF8.GetString(p);
                    return h;
                }
            }
            Assert.Fail("no frame in the last write");
            payloadText = "";
            return default;
        }

        /// <summary>Inject a control frame from the "firmware".</summary>
        public void Inject(ControlFrameType type, byte id, byte flags, byte[] payload)
        {
            byte[] frame = new byte[payload.Length + ControlFrame.Overhead];
            ControlFrame.Encode(type, id, flags, payload, frame);
            byte[] stuffed = new byte[ByteFrame.MaxEncodedLength(frame.Length)];
            int n = ByteFrame.Encode(frame, stuffed);
            Channel.Feed(new ReadOnlySpan<byte>(stuffed, 0, n));
        }

        public void InjectReply(byte id, int chunk, bool last, bool error, string text)
        {
            byte flags = (byte)(chunk << ControlFrame.ChunkShift);
            if (last) { flags |= (byte)ControlFrameFlags.Last; }
            if (error) { flags |= (byte)ControlFrameFlags.Error; }
            Inject(ControlFrameType.Reply, id, flags, Encoding.UTF8.GetBytes(text));
        }
    }

    [Test]
    public void SendCommand_SendsCommandFrame_AndAssemblesThreeChunks()
    {
        Harness h = new Harness();
        ControlRequest req = h.Console.SendCommand("STAT");
        ControlFrameHeader sent = h.LastSentHeader(out string text);
        Assert.That(sent.Type, Is.EqualTo(ControlFrameType.Command));
        Assert.That(sent.Id, Is.EqualTo(req.Id));
        Assert.That(text, Is.EqualTo("STAT"));
        Assert.That(req.State, Is.EqualTo(ControlRequestState.Pending));

        h.InjectReply(req.Id, 0, false, false, "line one\r\n");
        h.InjectReply(req.Id, 1, false, false, "line two\r\n");
        Assert.That(req.State, Is.EqualTo(ControlRequestState.Pending));
        h.InjectReply(req.Id, 2, true, false, "");

        Assert.That(req.State, Is.EqualTo(ControlRequestState.Completed));
        Assert.That(req.Text, Is.EqualTo("line one\r\nline two\r\n"));
        Assert.That(req.Wait(10), Is.EqualTo(ControlRequestState.Completed));
    }

    [Test]
    public void MissingChunk_EndsIncomplete()
    {
        Harness h = new Harness();
        ControlRequest req = h.Console.SendCommand("HELP");
        h.InjectReply(req.Id, 0, false, false, "a");
        h.InjectReply(req.Id, 2, true, false, "c");     // chunk 1 was lost on the USB path
        Assert.That(req.State, Is.EqualTo(ControlRequestState.Incomplete));
        Assert.That(req.Text, Is.EqualTo("ac"));
    }

    [Test]
    public void ErrorFlag_AndNak_EndFailed()
    {
        Harness h = new Harness();
        ControlRequest bad = h.Console.SendCommand("FOO");
        h.InjectReply(bad.Id, 0, true, true, "Unknown command 'FOO'.");
        Assert.That(bad.State, Is.EqualTo(ControlRequestState.Failed));
        Assert.That(bad.Text, Does.Contain("Unknown command"));

        ControlRequest nak = h.Console.SetLogLevel(LogLevel.Trace);
        h.Inject(ControlFrameType.Nak, nak.Id, 0x01, new byte[] { (byte)NakReason.BadLength });
        Assert.That(nak.State, Is.EqualTo(ControlRequestState.Failed));
        Assert.That(nak.Nak, Is.EqualTo(NakReason.BadLength));
    }

    [Test]
    public void UnsolicitedLogDuringPendingReply_GoesToLogHandler_NotToTheReply()
    {
        Harness h = new Harness();
        List<string> logs = new List<string>();
        h.Console.LogReceived = (level, ms, text) => logs.Add(level + "@" + ms + ":" + text);

        ControlRequest req = h.Console.SendCommand("BAUD 38400");
        h.InjectReply(req.Id, 0, false, false, "Baud rate ");
        byte[] log = new byte[5 + 9];
        log[0] = (byte)LogLevel.Warn;
        log[1] = 0x10; log[2] = 0x27; log[3] = 0; log[4] = 0;    // 10000 ms
        Encoding.ASCII.GetBytes("FCS BAD 7").CopyTo(log, 5);
        h.Inject(ControlFrameType.Log, 0, 0, log);
        h.InjectReply(req.Id, 1, true, false, "set to 38400\r\n");

        Assert.That(req.State, Is.EqualTo(ControlRequestState.Completed));
        Assert.That(req.Text, Is.EqualTo("Baud rate set to 38400\r\n"));
        Assert.That(logs, Is.EqualTo(new[] { "Warn@10000:FCS BAD 7" }));
    }

    [Test]
    public void Hello_ParsesIdentity_WithUnknownKeysKept()
    {
        Harness h = new Harness();
        ControlRequest req = h.Console.Hello();
        string text = "name=HDLCGW\nversion=0.2.0\nbuild=Nov  8 2026 12:00:00\nmaxframe=1024\ncaps=cmd,log,stat\ncolour=blue\n";
        byte[] payload = new byte[1 + Encoding.UTF8.GetByteCount(text)];
        payload[0] = 1;
        Encoding.UTF8.GetBytes(text, 0, text.Length, payload, 1);
        h.Inject(ControlFrameType.HelloReply, req.Id, 0x01, payload);

        Assert.That(req.State, Is.EqualTo(ControlRequestState.Completed));
        Assert.That(GatewayIdentity.TryParse(req.Payload, out GatewayIdentity? id), Is.True);
        Assert.That(id!.ProtocolVersion, Is.EqualTo((byte)1));
        Assert.That(id.Name, Is.EqualTo("HDLCGW"));
        Assert.That(id.Version, Is.EqualTo("0.2.0"));
        Assert.That(id.MaxFrame, Is.EqualTo(1024));
        Assert.That(id.Has("log"), Is.True);
        Assert.That(id.Has("wifi"), Is.False);
        Assert.That(id.Values["colour"], Is.EqualTo("blue"));
    }

    [Test]
    public void Statistics_ParseWith17Fields_AndWith10()
    {
        byte[] full = new byte[17 * 4];
        for (int i = 0; i < 17; i++)
        {
            full[i * 4] = (byte)(i + 1);
        }
        Assert.That(GatewayStatistics.TryParse(full, out GatewayStatistics? s), Is.True);
        Assert.That(s!.FieldCount, Is.EqualTo(17));
        Assert.That(s.SentTotal, Is.EqualTo(1u));
        Assert.That(s.ControlFramesDropped, Is.EqualTo(17u));

        Assert.That(GatewayStatistics.TryParse(new ReadOnlySpan<byte>(full, 0, 40), out GatewayStatistics? old), Is.True);
        Assert.That(old!.FieldCount, Is.EqualTo(10));
        Assert.That(old.LineIdleDropped, Is.EqualTo(10u));
        Assert.That(old.LineFifoOverruns, Is.Null);
        Assert.That(old.ControlFramesDropped, Is.Null);

        Assert.That(GatewayStatistics.TryParse(new byte[7], out _), Is.False);
    }

    [Test]
    public void StatPushAndEvents_ReachTheirHandlers()
    {
        Harness h = new Harness();
        GatewayStatistics? got = null;
        List<string> events = new List<string>();
        h.Console.StatisticsReceived = s => got = s;
        h.Console.EventReceived = (code, text) => events.Add(code + ":" + text);

        h.Console.SetStatisticsPush(1000);
        ControlFrameHeader sent = h.LastSentHeader(out _);
        Assert.That(sent.Type, Is.EqualTo(ControlFrameType.StatPush));

        h.Inject(ControlFrameType.Stat, 0, 0, new byte[8]);
        h.Inject(ControlFrameType.Event, 0, 0, Encoding.ASCII.GetBytes("\u0002FORWARDING"));

        Assert.That(got, Is.Not.Null);
        Assert.That(got!.FieldCount, Is.EqualTo(2));
        Assert.That(events, Is.EqualTo(new[] { "ModeChange:FORWARDING" }));
    }

    [Test]
    public void LateReply_AfterTimeout_IsStray()
    {
        Harness h = new Harness();
        ControlRequest req = h.Console.Ping();
        Assert.That(req.Wait(1), Is.EqualTo(ControlRequestState.TimedOut));
        h.InjectReply(req.Id, 0, true, false, "");
        Assert.That(h.Console.StrayReplies, Is.EqualTo(1));
        Assert.That(req.State, Is.EqualTo(ControlRequestState.TimedOut));
    }

    [Test]
    public void RequestIds_SkipZero_AndWrap()
    {
        Harness h = new Harness();
        ControlRequest first = h.Console.Ping();
        Assert.That(first.Id, Is.EqualTo((byte)1));
        for (int i = 0; i < 254; i++)
        {
            ControlRequest r = h.Console.Ping();
            h.InjectReply(r.Id, 0, true, false, "");
        }
        h.InjectReply(first.Id, 0, true, false, "");
        ControlRequest wrapped = h.Console.Ping();
        Assert.That(wrapped.Id, Is.EqualTo((byte)1));
    }
}
