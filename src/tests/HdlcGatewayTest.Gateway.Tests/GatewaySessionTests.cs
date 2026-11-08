// GATEWAY SESSION TESTS - connect, probe, command, roles, frame log, bridge and TCP listen with fakes.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Net.Sockets;
using HdlcGatewayTest.Core;
using HdlcGatewayTest.Core.Tests;
using NUnit.Framework;

namespace HdlcGatewayTest.Gateway.Tests;

[TestFixture]
public sealed class GatewaySessionTests
{
    private static byte[] DataFrame(byte address, int length, int seed)
    {
        byte[] frame = new byte[length];
        new Random(seed).NextBytes(frame);
        frame[0] = address;
        frame[1] = 0x00;
        Fcs.Append(frame);
        return frame;
    }

    private static bool WaitUntil(Func<bool> condition, int milliseconds = 2000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(5);
        }
        return condition();
    }

    [Test]
    public async Task Connect_Probes_AndCommandsGoFramed_WithConsoleLines()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        FakeTransport t = new FakeTransport { Name = "FAKE-A" };
        FakeGateway gw = new FakeGateway(t, FakeGateway.Kind.ControlFrames);
        set.Start();

        set.A.Connect(t);

        Assert.That(set.A.State, Is.EqualTo(GatewaySessionState.ControlFrames));
        Assert.That(set.A.Identity!.Name, Is.EqualTo("FAKEGW"));

        string reply = await set.A.SendCommandAsync("BAUD 9600", FrameSource.Console);
        Assert.That(reply, Does.Contain("set to 9600"));
        Assert.That(gw.Baud, Is.EqualTo(9600u));

        List<ConsoleLine> lines = set.A.Console.Since(0);
        int commands = 0;
        int replies = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == ConsoleLineKind.Command) { commands++; }
            if (lines[i].Kind == ConsoleLineKind.Reply) { replies++; }
        }
        Assert.That(commands, Is.EqualTo(1));
        Assert.That(replies, Is.EqualTo(1));

        // The HELLO and the command and their replies are all in the frame log as control frames.
        List<FrameRecord> frames = set.A.Frames.Since(0);
        Assert.That(frames.Count, Is.EqualTo(4));
        Assert.That(frames[0].IsControl, Is.True);
        Assert.That(frames[0].Sent, Is.True);
        Assert.That(frames[1].IsControl, Is.True);
        Assert.That(frames[1].Sent, Is.False);
    }

    [Test]
    public async Task LegacyFirmware_UsesTextConsole()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        FakeTransport t = new FakeTransport();
        FakeGateway gw = new FakeGateway(t, FakeGateway.Kind.TextConsoleOnly);
        set.Start();

        set.A.Connect(t, tryConsole: true);
        Assert.That(set.A.State, Is.EqualTo(GatewaySessionState.TextConsole));

        string reply = await set.A.SendCommandAsync("BAUD 19200", FrameSource.Mcp);
        Assert.That(reply, Does.Contain("set to 19200"));
        Assert.That(gw.Baud, Is.EqualTo(19200u));
    }

    [Test]
    public void Echo_SendsReceivedFramesBack_AndListsBoth()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        FakeTransport t = new FakeTransport();
        _ = new FakeGateway(t, FakeGateway.Kind.ControlFrames);
        set.Start();
        set.A.Connect(t);
        set.A.Role = GatewayRole.Echo;
        int writesBefore = t.Writes.Count;

        byte[] frame = DataFrame(0x01, 20, 3);
        t.InjectFrame(frame);           // "the line delivered a frame"

        Assert.That(WaitUntil(() => t.Writes.Count > writesBefore), Is.True, "echoed within 2 s");
        // The fake loops the echo back, so this is an echo storm from here on; stop it before
        // reading the log, otherwise the records keep moving.
        set.A.Role = GatewayRole.Console;
        List<FrameRecord> frames = set.A.Frames.Since(0);
        FrameRecord? rx = null;
        FrameRecord? tx = null;
        for (int i = 0; i < frames.Count && tx == null; i++)
        {
            if (frames[i].FcsGood && !frames[i].Sent && rx == null)
            {
                rx = frames[i];
            }
            else if (frames[i].FcsGood && frames[i].Sent && rx != null)
            {
                tx = frames[i];
            }
        }
        Assert.That(rx, Is.Not.Null);
        Assert.That(tx, Is.Not.Null);
        Assert.That(rx!.Source, Is.EqualTo(FrameSource.Serial));
        Assert.That(rx.Bytes, Is.EqualTo(frame));
        Assert.That(tx!.Source, Is.EqualTo(FrameSource.Echo));
        Assert.That(tx.Bytes, Is.EqualTo(frame));
    }

    [Test]
    public void Bridge_ForwardsBetweenPanes_BothWays()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        FakeTransport ta = new FakeTransport { Name = "A" };
        FakeTransport tb = new FakeTransport { Name = "B" };
        _ = new FakeGateway(ta, FakeGateway.Kind.ControlFrames);
        _ = new FakeGateway(tb, FakeGateway.Kind.ControlFrames);
        set.Start();
        set.A.Connect(ta);
        set.B.Connect(tb);
        set.SetBridge(true);
        Assert.That(set.A.Role, Is.EqualTo(GatewayRole.Bridge));
        Assert.That(set.B.Role, Is.EqualTo(GatewayRole.Bridge));
        int aWrites = ta.Writes.Count;
        int bWrites = tb.Writes.Count;

        byte[] toB = DataFrame(0xA1, 30, 4);
        byte[] toA = DataFrame(0xB2, 40, 5);
        ta.InjectFrame(toB);
        tb.InjectFrame(toA);

        Assert.That(WaitUntil(() => tb.Writes.Count > bWrites && ta.Writes.Count > aWrites), Is.True);
        // The fakes loop every frame back, so the bridged frames ping-pong between the panes from
        // here on; stop that before reading the logs, then look the records up by content.
        set.SetBridge(false);
        Assert.That(FindSent(set.B.Frames, FrameSource.Bridge, toB), Is.Not.Null, "A's frame went out on B as Bridge");
        Assert.That(FindSent(set.A.Frames, FrameSource.Bridge, toA), Is.Not.Null, "B's frame went out on A as Bridge");

        Assert.That(set.A.Role, Is.EqualTo(GatewayRole.Console));
        Assert.That(set.A.Peer, Is.Null);
    }

    [Test]
    public void TcpListen_ForwardsPeerFramesToSerial_AndSerialToPeer()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        FakeTransport t = new FakeTransport();
        _ = new FakeGateway(t, FakeGateway.Kind.ControlFrames);
        set.Start();
        set.A.Connect(t);

        int port = FreePort();
        Assert.That(set.TryStartTcpListen(set.A, port, out string? error), Is.True, error);
        Assert.That(set.A.Role, Is.EqualTo(GatewayRole.TcpListen));
        Assert.That(set.TryStartTcpListen(set.B, port, out error), Is.False, "same port refused");
        Assert.That(error, Does.Contain("different ports"));

        using TcpClient peer = new TcpClient();
        peer.Connect("127.0.0.1", port);
        NetworkStream stream = peer.GetStream();
        Assert.That(WaitUntil(() => set.A.TcpPeer != null), Is.True);

        // nd100x sends a frame: it must go out on the serial port.
        int writesBefore = t.Writes.Count;
        byte[] fromPeer = DataFrame(0x01, 24, 6);
        byte[] stuffed = new byte[ByteFrame.MaxEncodedLength(fromPeer.Length)];
        int n = ByteFrame.Encode(fromPeer, stuffed);
        stream.Write(stuffed, 0, n);
        Assert.That(WaitUntil(() => t.Writes.Count > writesBefore), Is.True);
        FrameRecord? rxTcp = FindReceived(set.A.Frames, FrameSource.Tcp, fromPeer);
        Assert.That(rxTcp, Is.Not.Null, "listed as received from the TCP peer");
        Assert.That(FindSent(set.A.Frames, FrameSource.Tcp, fromPeer), Is.Not.Null, "listed as sent to the line for the TCP peer");

        // The line delivers a frame: it must reach the TCP peer, byte-stuffed. (The fake gateway
        // also loops the peer's own frame back onto the "line", so that one arrives first.)
        byte[] fromLine = DataFrame(0x03, 16, 7);
        t.InjectFrame(fromLine);
        byte[] rx = new byte[256];
        ByteFrameDecoder dec = new ByteFrameDecoder();
        bool got = false;
        peer.ReceiveTimeout = 2000;
        int framesSeen = 0;
        while (!got && framesSeen < 10)
        {
            int r = stream.Read(rx, 0, rx.Length);
            Assert.That(r, Is.GreaterThan(0));
            for (int i = 0; i < r && !got; i++)
            {
                if (dec.Feed(rx[i]))
                {
                    framesSeen++;
                    got = dec.Frame.SequenceEqual(fromLine);
                }
            }
        }
        Assert.That(got, Is.True, "the line's frame reached the TCP peer");

        set.A.StopTcpListen();
        Assert.That(set.A.TcpPort, Is.EqualTo(0));
        Assert.That(set.A.Role, Is.EqualTo(GatewayRole.Console));
    }

    [Test]
    public void SendRawFrame_AddsFcsWhenAsked_AndListsIt()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        FakeTransport t = new FakeTransport();
        _ = new FakeGateway(t, FakeGateway.Kind.ControlFrames);
        set.Start();
        set.A.Connect(t);
        set.A.Role = GatewayRole.Free;

        Assert.That(set.A.SendRawFrame(new byte[] { 0x01, 0x3F }, true, FrameSource.Console), Is.True);
        List<FrameRecord> frames = set.A.Frames.Since(0);
        FrameRecord tx = frames[frames.Count - 1];
        Assert.That(tx.Bytes.Length, Is.EqualTo(4));
        Assert.That(tx.FcsGood, Is.True);
        Assert.That(tx.Source, Is.EqualTo(FrameSource.Console));
        Assert.That(tx.Control!.Value.Name, Is.EqualTo("SABM"));
        Assert.That(tx.Summary(), Does.Contain("SABM"));
        Assert.That(tx.Describe(), Does.Contain("[OK]"));
    }

    [Test]
    public async Task TrafficTest_BorrowsTheLink_ThenHandlersComeBack()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        FakeTransport t = new FakeTransport();
        _ = new FakeGateway(t, FakeGateway.Kind.ControlFrames);
        set.Start();
        set.A.Connect(t);
        TrafficTestSettings settings = new TrafficTestSettings
        {
            Rates = new uint[] { 38400 }, SecondsPerRate = 1, FrameLength = 32, WindowBytes = 300, TimeoutSeconds = 1,
        };
        Recorder rec = new Recorder();

        bool passed = await set.RunTrafficTestAsync(settings, rec, useB: false);

        Assert.That(passed, Is.True);
        Assert.That(set.TestRunning, Is.False);
        Assert.That(set.A.InTrafficTest, Is.False);
        Assert.That(set.A.Frames.Total, Is.GreaterThan(10), "the frame log listed traffic frames too");

        // Echo still works afterwards: the session's handlers are back.
        set.A.Role = GatewayRole.Echo;
        int writes = t.Writes.Count;
        t.InjectFrame(DataFrame(0x01, 12, 9));
        Assert.That(WaitUntil(() => t.Writes.Count > writes), Is.True);
        set.A.Role = GatewayRole.Console;   // end the echo storm the fake's loopback makes
    }

    [Test]
    public async Task FramesOnlyEndpoint_SkipsHello_ListsFrames_RefusesCommands()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        FakeTransport t = new FakeTransport { Name = "hdlcgw.local:1362", FramesOnly = true };
        set.Start();

        set.A.Connect(t);

        Assert.That(set.A.State, Is.EqualTo(GatewaySessionState.FramesOnly));
        Assert.That(t.Writes.Count, Is.EqualTo(0), "no HELLO went out onto the frames-only link");
        byte[] frame = DataFrame(0x01, 20, 12);
        t.InjectFrame(frame);
        Assert.That(WaitUntil(() => set.A.Frames.Total > 0), Is.True);
        Assert.That(set.A.Frames.Since(0)[0].Bytes, Is.EqualTo(frame));
        Assert.That(set.A.Frames.Since(0)[0].Source, Is.EqualTo(FrameSource.Tcp), "a TCP pane labels its frames Tcp");

        string reply = await set.A.SendCommandAsync("STAT", FrameSource.Console);
        Assert.That(reply, Does.Contain("frames only"));
        Assert.That(t.Writes.Count, Is.EqualTo(0), "the command was not sent either");

        Assert.That(set.A.SendRawFrame(new byte[] { 0x01, 0x3F }, true, FrameSource.Console), Is.True);
        Assert.That(t.Writes.Count, Is.EqualTo(1), "raw frames do go out");
    }

    [Test]
    public void MirroredLineFrames_BetweenEvents_AreListedOnTheUsbPane()
    {
        // Replay of what the HDLCGW session captured on the raw USB stream with MIRROR ON
        // (2026-11-08): EVENT LineRxActive, three stuffed data frames 7E 7D 21 7D 23 48 49 62 58 7E,
        // EVENT LineRxIdle, with a framed STAT reply arriving in the middle as it did live.
        using GatewaySessionSet set = new GatewaySessionSet();
        FakeTransport t = new FakeTransport { Name = "COM10" };
        FakeGateway gw = new FakeGateway(t, FakeGateway.Kind.ControlFrames);
        set.Start();
        set.A.Connect(t);
        Assert.That(set.A.State, Is.EqualTo(GatewaySessionState.ControlFrames));
        long framesBefore = set.A.Frames.Total;

        byte[] stuffed = { 0x7E, 0x7D, 0x21, 0x7D, 0x23, 0x48, 0x49, 0x62, 0x58, 0x7E };
        t.InjectControl(ControlFrameType.Event, 0, 0, new byte[] { 3, (byte)'l', (byte)'i', (byte)'n', (byte)'e' });
        t.Inject(stuffed);
        t.Inject(stuffed);
        Task<string> stat = set.A.SendCommandAsync("STAT", FrameSource.Console);
        t.Inject(stuffed);
        t.InjectControl(ControlFrameType.Event, 0, 0, new byte[] { 4 });
        Assert.That(stat.Wait(3000), Is.True);
        Assert.That(stat.Result, Does.Contain("HDLC frames"));

        Assert.That(WaitUntil(() => set.A.Frames.Total >= framesBefore + 3), Is.True, "three data frames listed");
        List<FrameRecord> frames = set.A.Frames.Since(framesBefore);
        int data = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            if (!frames[i].IsControl && !frames[i].Sent)
            {
                data++;
                Assert.That(frames[i].FcsGood, Is.True);
                Assert.That(frames[i].Bytes, Is.EqualTo(new byte[] { 0x01, 0x03, 0x48, 0x49, 0x62, 0x58 }));
                Assert.That(frames[i].Source, Is.EqualTo(FrameSource.Serial));
            }
        }
        Assert.That(data, Is.EqualTo(3));
        List<ConsoleLine> lines = set.A.Console.Since(0);
        int events = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == ConsoleLineKind.Event) { events++; }
        }
        Assert.That(events, Is.EqualTo(2));
    }

    [Test]
    public void Router_Table()
    {
        Assert.That(FrameRouter.Route(GatewayRole.Console, FrameSource.Serial), Is.EqualTo(FrameDestinations.None));
        Assert.That(FrameRouter.Route(GatewayRole.Free, FrameSource.Serial), Is.EqualTo(FrameDestinations.None));
        Assert.That(FrameRouter.Route(GatewayRole.Echo, FrameSource.Serial), Is.EqualTo(FrameDestinations.OwnSerial));
        Assert.That(FrameRouter.Route(GatewayRole.Echo, FrameSource.Tcp), Is.EqualTo(FrameDestinations.None));
        Assert.That(FrameRouter.Route(GatewayRole.Bridge, FrameSource.Serial), Is.EqualTo(FrameDestinations.PeerSerial));
        Assert.That(FrameRouter.Route(GatewayRole.TcpListen, FrameSource.Serial), Is.EqualTo(FrameDestinations.OwnTcp));
        Assert.That(FrameRouter.Route(GatewayRole.TcpListen, FrameSource.Tcp), Is.EqualTo(FrameDestinations.OwnSerial));
    }

    [Test]
    public void Logs_KeepSequenceNumbers_AndSinceWorks()
    {
        ConsoleLog log = new ConsoleLog(16);
        for (int i = 0; i < 40; i++)
        {
            log.Add(ConsoleLineKind.Reply, "line " + i);
        }
        Assert.That(log.Total, Is.EqualTo(40));
        List<ConsoleLine> all = log.Since(0);
        Assert.That(all.Count, Is.EqualTo(16));
        Assert.That(all[0].Sequence, Is.EqualTo(25));
        Assert.That(all[15].Text, Is.EqualTo("line 39"));
        Assert.That(log.Since(38).Count, Is.EqualTo(2));
        Assert.That(log.Since(40).Count, Is.EqualTo(0));

        FrameLog frames = new FrameLog(16);
        frames.Add(false, FrameSource.Serial, new byte[] { 0x01, 0x3F, 0xEB, 0xDF });
        Assert.That(frames.Since(0)[0].FcsGood, Is.True);
        Assert.That(frames.Since(0)[0].HexDump(), Does.Contain("01 3F EB DF"));
    }

    private static FrameRecord? FindSent(FrameLog log, FrameSource source, byte[] bytes)
    {
        List<FrameRecord> frames = log.Since(0);
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Sent && frames[i].Source == source && frames[i].Bytes.AsSpan().SequenceEqual(bytes))
            {
                return frames[i];
            }
        }
        return null;
    }

    private static FrameRecord? FindReceived(FrameLog log, FrameSource source, byte[] bytes)
    {
        List<FrameRecord> frames = log.Since(0);
        for (int i = 0; i < frames.Count; i++)
        {
            if (!frames[i].Sent && frames[i].Source == source && frames[i].Bytes.AsSpan().SequenceEqual(bytes))
            {
                return frames[i];
            }
        }
        return null;
    }

    private static int FreePort()
    {
        System.Net.Sockets.TcpListener l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Recorder : ITrafficTestObserver
    {
        public void Message(string text) { }
        public void Status(uint rate, TrafficDirection[] directions, long elapsedTicks, bool done) { }
        public void RateFinished(RateResult result) { }
    }
}
