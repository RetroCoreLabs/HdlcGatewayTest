// GATEWAY LINK TESTS - the probe finds the right command path and commands work on both.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class GatewayLinkTests
{
    [Test]
    public void Probe_FindsControlFrames_AndRunsCommandFramed()
    {
        FakeTransport t = new FakeTransport();
        FakeGateway gw = new FakeGateway(t, FakeGateway.Kind.ControlFrames);
        using GatewayLink link = new GatewayLink(t);
        link.Open();

        Assert.That(link.Probe(200, out _), Is.EqualTo(GatewayCommandPath.ControlFrames));
        Assert.That(link.Identity!.Name, Is.EqualTo("FAKEGW"));
        Assert.That(gw.Commands, Is.Empty, "the default probe sends HELLO only, no ESC, no EXIT");

        Assert.That(link.RunCommand("BAUD 9600", out string reply), Is.True);
        Assert.That(reply, Does.Contain("set to 9600"));
        Assert.That(gw.Baud, Is.EqualTo(9600u));
        Assert.That(gw.Commands, Is.EqualTo(new[] { "BAUD 9600" }));

        Assert.That(link.RunCommand("BOGUS", out reply), Is.False);
        Assert.That(reply, Does.Contain("Unknown command"));
    }

    [Test]
    public void Probe_FallsBackToTextConsole_ForLegacyFirmware()
    {
        FakeTransport t = new FakeTransport();
        FakeGateway gw = new FakeGateway(t, FakeGateway.Kind.TextConsoleOnly);
        using GatewayLink link = new GatewayLink(t);
        link.Open();

        Assert.That(link.Probe(50, out _, tryConsole: true), Is.EqualTo(GatewayCommandPath.TextConsole));
        Assert.That(link.Identity, Is.Null);
        // The console answered "Unknown command" to LOG, so this is a legacy gateway and NO frame
        // at all was sent to it (RC422Dev locks its console after the first forwarded frame).
        Assert.That(gw.DataFrames, Is.EqualTo(0));
        Assert.That(gw.Commands[0], Is.EqualTo("LOG"), "the console was asked whether it has frames");
        Assert.That(gw.Commands[1], Is.EqualTo("EXIT"));

        Assert.That(link.RunCommand("BAUD 19200", out string reply), Is.True);
        Assert.That(reply, Does.Contain("set to 19200"));
        Assert.That(reply, Does.Not.Contain("# "), "the prompt is stripped");
        Assert.That(gw.Baud, Is.EqualTo(19200u));
        Assert.That(gw.Commands, Is.EqualTo(new[] { "LOG", "EXIT", "BAUD 19200", "EXIT" }));
    }

    [Test]
    public void Probe_RecoversABoardLeftInDebugMode()
    {
        FakeTransport t = new FakeTransport();
        FakeGateway gw = new FakeGateway(t, FakeGateway.Kind.ControlFramesButInDebug);
        using GatewayLink link = new GatewayLink(t);
        link.Open();

        Assert.That(link.Probe(50, out _, tryConsole: true), Is.EqualTo(GatewayCommandPath.ControlFrames));
        Assert.That(link.Identity!.Version, Is.EqualTo("0.2.0"));
        // The console was found (the ESCs were typed into the line, CR ran that junk line and a
        // prompt came), LOG said "Log level", EXIT left it, then HELLO succeeded.
        Assert.That(gw.Commands[gw.Commands.Count - 1], Is.EqualTo("EXIT"));
        Assert.That(gw.Commands, Does.Contain("LOG"));
    }

    [Test]
    public void Probe_WithConsole_SendsNoFrameToALegacyGateway()
    {
        // The bench lesson of 2026-11-08, twice: RC422Dev forwards any frame it does not understand
        // and then locks its console, so a gateway whose console says it has no LOG command must
        // never be sent a frame. Check every write.
        FakeTransport t = new FakeTransport();
        _ = new FakeGateway(t, FakeGateway.Kind.TextConsoleOnly);
        using GatewayLink link = new GatewayLink(t);
        link.Open();
        Assert.That(link.Probe(50, out _, tryConsole: true), Is.EqualTo(GatewayCommandPath.TextConsole));

        Assert.That(t.WrittenBlocks.Count, Is.GreaterThanOrEqualTo(3));
        Assert.That(t.WrittenBlocks[0], Is.EqualTo(new byte[] { 0x1B, 0x1B, 0x1B }));
        for (int i = 0; i < t.WrittenBlocks.Count; i++)
        {
            Assert.That(t.WrittenBlocks[i][0], Is.Not.EqualTo(ByteFrame.Flag), "write " + i + " must not be a frame");
        }
    }

    [Test]
    public void Probe_WithConsole_HelloComesAfterLogAndExit_OnHdlcgw()
    {
        FakeTransport t = new FakeTransport();
        FakeGateway gw = new FakeGateway(t, FakeGateway.Kind.ControlFrames);
        using GatewayLink link = new GatewayLink(t);
        link.Open();
        Assert.That(link.Probe(50, out _, tryConsole: true), Is.EqualTo(GatewayCommandPath.ControlFrames));
        Assert.That(gw.Commands, Is.EqualTo(new[] { "LOG", "EXIT" }));
        bool exitSeen = false;
        for (int i = 0; i < t.WrittenBlocks.Count; i++)
        {
            if (t.WrittenBlocks[i][0] == ByteFrame.Flag && t.WrittenBlocks[i].Length > 4)
            {
                Assert.That(exitSeen, Is.True, "the HELLO frame must come after EXIT");
            }
            if (System.Text.Encoding.ASCII.GetString(t.WrittenBlocks[i]).StartsWith("EXIT", StringComparison.Ordinal))
            {
                exitSeen = true;
            }
        }
    }

    [Test]
    public void Probe_Default_NeverSendsAnEscByte_AndLegacyIsNone()
    {
        // Ronny, 2026-11-08: the firmware is driven with command frames; by default no ESC goes out.
        FakeTransport t = new FakeTransport();
        _ = new FakeGateway(t, FakeGateway.Kind.TextConsoleOnly);
        using GatewayLink link = new GatewayLink(t);
        link.Open();
        Assert.That(link.Probe(50, out _), Is.EqualTo(GatewayCommandPath.None));
        for (int i = 0; i < t.WrittenBlocks.Count; i++)
        {
            Assert.That(t.WrittenBlocks[i][0], Is.EqualTo(ByteFrame.Flag), "only frames were written");
        }
    }

    [Test]
    public void Probe_DeadGateway_IsNone_AndRunCommandSaysSo()
    {
        FakeTransport t = new FakeTransport();
        _ = new FakeGateway(t, FakeGateway.Kind.Dead);
        using GatewayLink link = new GatewayLink(t);
        link.Open();

        Assert.That(link.Probe(20, out _), Is.EqualTo(GatewayCommandPath.None));
        Assert.That(link.RunCommand("STAT", out string reply), Is.False);
        Assert.That(reply, Does.Contain("neither"));
    }

    [Test]
    public void TextConsole_IsPrompt()
    {
        Assert.That(TextConsole.IsPrompt("banner\r\n# "), Is.True);
        Assert.That(TextConsole.IsPrompt("[12:0]@> "), Is.True);
        Assert.That(TextConsole.IsPrompt("# x"), Is.False);
    }
}
