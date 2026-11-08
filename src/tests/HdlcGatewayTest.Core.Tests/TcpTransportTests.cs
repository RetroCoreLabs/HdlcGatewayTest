// TCP TRANSPORT TESTS - client and listener transports against each other on localhost.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Net;
using System.Net.Sockets;
using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class TcpTransportTests
{
    private static int FreePort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static bool WaitUntil(Func<bool> condition, int milliseconds = 3000)
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
    public void ClientAndListener_MoveBytesBothWays_AndReportTheDrop()
    {
        int port = FreePort();
        using TcpListenerTransport server = new TcpListenerTransport(port);
        server.Open();
        string? peerSeen = null;
        server.PeerChanged += p => peerSeen = p ?? "gone";

        using TcpClientTransport client = new TcpClientTransport("127.0.0.1", port, 2000);
        Assert.That(client.Name, Is.EqualTo("127.0.0.1:" + port));
        client.Open();
        Assert.That(client.IsOpen, Is.True);
        // HasPeer turns true a moment before PeerChanged fires (the accept thread sets the
        // stream, then raises the event), so wait for the event itself.
        Assert.That(WaitUntil(() => peerSeen != null), Is.True);
        Assert.That(server.HasPeer, Is.True);
        Assert.That(peerSeen, Does.StartWith("127.0.0.1"));

        byte[] frame = new byte[] { 0x7E, 0x01, 0x3F, 0xEB, 0xDF, 0x7E };
        client.Write(frame);
        byte[] rx = new byte[64];
        int got = 0;
        Assert.That(WaitUntil(() => (got += server.Read(rx.AsSpan(got))) >= frame.Length), Is.True);
        Assert.That(rx.AsSpan(0, frame.Length).ToArray(), Is.EqualTo(frame));

        server.Write(new byte[] { 0x7E, 0xA1, 0x7E });
        got = 0;
        Assert.That(WaitUntil(() => (got += client.Read(rx.AsSpan(got))) >= 3), Is.True);
        Assert.That(rx[1], Is.EqualTo((byte)0xA1));

        Exception? fault = null;
        client.Faulted += ex => fault = ex;
        server.DropPeer();
        Assert.That(WaitUntil(() => fault != null), Is.True, "the client notices the server dropped it");
        Assert.That(client.IsOpen, Is.False);
        Assert.That(() => client.Write(frame), Throws.InstanceOf<IOException>());
    }

    [Test]
    public void Client_RefusedPort_ThrowsIOException()
    {
        int port = FreePort();
        using TcpClientTransport client = new TcpClientTransport("127.0.0.1", port, 2000);
        Assert.That(() => client.Open(), Throws.InstanceOf<IOException>());
        Assert.That(client.IsOpen, Is.False);
    }

    [Test]
    public void Listener_NewPeerReplacesOld()
    {
        int port = FreePort();
        using TcpListenerTransport server = new TcpListenerTransport(port);
        server.Open();
        using TcpClientTransport first = new TcpClientTransport("127.0.0.1", port);
        first.Open();
        Assert.That(WaitUntil(() => server.HasPeer), Is.True);
        Exception? firstFault = null;
        first.Faulted += ex => firstFault = ex;

        using TcpClientTransport second = new TcpClientTransport("127.0.0.1", port);
        second.Open();
        Assert.That(WaitUntil(() => firstFault != null), Is.True, "the first client was dropped");
        server.Write(new byte[] { 0x55 });
        byte[] rx = new byte[8];
        Assert.That(WaitUntil(() => second.Read(rx) > 0), Is.True);
        Assert.That(rx[0], Is.EqualTo((byte)0x55));
    }

    [TestCase("hdlcgw.local:1362", true, "hdlcgw.local", 1362)]
    [TestCase("192.168.1.40:1362", true, "192.168.1.40", 1362)]
    [TestCase("COM10", false, "", 0)]
    [TestCase("host:", false, "", 0)]
    [TestCase(":1362", false, "", 0)]
    [TestCase("host:70000", false, "", 0)]
    public void TryParseEndpoint(string text, bool ok, string host, int port)
    {
        Assert.That(TcpClientTransport.TryParseEndpoint(text, out string h, out int p), Is.EqualTo(ok));
        if (ok)
        {
            Assert.That(h, Is.EqualTo(host));
            Assert.That(p, Is.EqualTo(port));
        }
    }
}
