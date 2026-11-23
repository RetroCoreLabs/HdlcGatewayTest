// GATEWAY PANE VIEW MODEL TESTS - the pane follows the session, with a fake gateway and an inline dispatcher.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using HdlcGatewayTest.App.Services;
using HdlcGatewayTest.App.ViewModels;
using HdlcGatewayTest.Core;
using HdlcGatewayTest.Core.Tests;
using HdlcGatewayTest.Gateway;
using NUnit.Framework;

namespace HdlcGatewayTest.App.Tests;

/// <summary>Endpoints GOOD, OLD and DEAD, like the MCP tests.</summary>
public sealed class FakeFactory : IGatewayTransportFactory
{
    public readonly Dictionary<string, FakeGateway> Gateways = new Dictionary<string, FakeGateway>();

    public string[] ListEndpoints() => new[] { "DEAD", "GOOD", "OLD" };

    public IGatewayTransport Create(string endpoint)
    {
        FakeTransport t = new FakeTransport { Name = endpoint };
        FakeGateway.Kind kind = endpoint switch
        {
            "GOOD" => FakeGateway.Kind.ControlFrames,
            "OLD" => FakeGateway.Kind.TextConsoleOnly,
            _ => FakeGateway.Kind.Dead,
        };
        Gateways[endpoint] = new FakeGateway(t, kind);
        return t;
    }
}

[TestFixture]
public sealed class GatewayPaneViewModelTests
{
    private static bool WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return condition();
    }

    [Test]
    public void Connect_ShowsControlFramesState_AndCommandsAppearInConsole()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        set.Start();
        FakeFactory factory = new FakeFactory();
        GatewayPaneViewModel vm = new GatewayPaneViewModel(set, set.A, factory, new InlineDispatcher(), 1362);

        Assert.That(vm.Endpoints, Is.EqualTo(new[] { "DEAD", "GOOD", "OLD" }));
        Assert.That(vm.StateText, Is.EqualTo("Disconnected"));
        Assert.That(vm.IsConnected, Is.False);

        vm.SelectedEndpoint = "GOOD";
        Assert.That(vm.ConnectCommand.CanExecute(null), Is.True);
        vm.ConnectCommand.Execute(null);
        Assert.That(WaitUntil(() => vm.IsConnected), Is.True);
        Assert.That(WaitUntil(() => vm.StateText == "CONTROL FRAMES"), Is.True);
        Assert.That(vm.IdentityText, Does.StartWith("FAKEGW 0.2.0"));
        Assert.That(vm.HasControlFrames, Is.True);

        vm.CommandText = "BAUD 9600";
        Assert.That(vm.SendCommand.CanExecute(null), Is.True);
        vm.SendCommand.Execute(null);
        Assert.That(WaitUntil(() => factory.Gateways["GOOD"].Baud == 9600), Is.True);
        Assert.That(WaitUntil(() => HasLine(vm, ConsoleLineKind.Reply, "set to 9600")), Is.True);
        Assert.That(HasLine(vm, ConsoleLineKind.Command, "BAUD 9600"), Is.True);
        Assert.That(vm.CommandText, Is.Empty, "the box is cleared after sending");

        vm.HistoryUp();
        Assert.That(vm.CommandText, Is.EqualTo("BAUD 9600"));
        vm.HistoryDown();
        Assert.That(vm.CommandText, Is.Empty);

        // Frames: HELLO out and in, COMMAND out and REPLY in.
        Assert.That(vm.Frames.Count, Is.GreaterThanOrEqualTo(4));
        vm.SelectedFrame = vm.Frames[0];
        Assert.That(vm.FrameDetail, Does.Contain("Control frame: Hello"));

        vm.DisconnectCommand.Execute(null);
        Assert.That(WaitUntil(() => !vm.IsConnected), Is.True);
        Assert.That(vm.StateText, Is.EqualTo("Disconnected"));
    }

    [Test]
    public void LegacyFirmware_ShowsTextConsole_AndLogLevelIsDisabled()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        set.Start();
        GatewayPaneViewModel vm = new GatewayPaneViewModel(set, set.B, new FakeFactory(), new InlineDispatcher(), 1363)
        {
            SelectedEndpoint = "OLD",
            UseTextConsole = true,
        };
        vm.ConnectCommand.Execute(null);
        Assert.That(WaitUntil(() => vm.StateText == "TEXT CONSOLE (legacy)"), Is.True);
        Assert.That(vm.HasControlFrames, Is.False);
        Assert.That(HasLine(vm, ConsoleLineKind.Notice, "legacy firmware"), Is.True);
    }

    [Test]
    public void DeadPort_ShowsNoGatewayAnswered()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        set.Start();
        GatewayPaneViewModel vm = new GatewayPaneViewModel(set, set.A, new FakeFactory(), new InlineDispatcher(), 1362)
        {
            SelectedEndpoint = "DEAD",
        };
        vm.ConnectCommand.Execute(null);
        Assert.That(WaitUntil(() => vm.StateText == "NO GATEWAY ANSWERED", 8000), Is.True);
    }

    [Test]
    public void Roles_FreeShowsHexBox_EchoWorks_BridgeNeedsBoth()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        set.Start();
        FakeFactory factory = new FakeFactory();
        GatewayPaneViewModel a = new GatewayPaneViewModel(set, set.A, factory, new InlineDispatcher(), 1362) { SelectedEndpoint = "GOOD" };
        GatewayPaneViewModel b = new GatewayPaneViewModel(set, set.B, factory, new InlineDispatcher(), 1363) { SelectedEndpoint = "OLD", UseTextConsole = true };
        a.ConnectCommand.Execute(null);
        Assert.That(WaitUntil(() => a.IsConnected), Is.True);

        a.SelectedRole = GatewayPaneViewModel.RoleName(GatewayRole.Free);
        Assert.That(a.ShowHexBox, Is.True);
        a.HexText = "01 3F";
        a.SendHexCommand.Execute(null);
        // Look for the sent frame anywhere in the list, not as the last row: the fake gateway
        // loops every data frame straight back, so the echo (RX, also 4 bytes) can already be
        // the last row when the test first looks. Waiting for "last row is the sent frame" then
        // never succeeds - that failed on the GitHub Windows runner on 21 November 2026.
        FrameRecordViewModel? sent = null;
        Assert.That(WaitUntil(() => (sent = FindFrame(a, sent: true, length: 4)) != null), Is.True, "the sent frame is listed");
        Assert.That(sent!.Text, Does.Contain("SABM"));

        a.HexText = "zz";
        a.SendHexCommand.Execute(null);
        Assert.That(HasLine(a, ConsoleLineKind.Notice, "not a hex digit"), Is.True);

        a.SelectedRole = GatewayPaneViewModel.RoleName(GatewayRole.Bridge);
        Assert.That(HasLine(a, ConsoleLineKind.Notice, "both panes"), Is.True);
        Assert.That(set.A.Role, Is.Not.EqualTo(GatewayRole.Bridge));

        b.ConnectCommand.Execute(null);
        Assert.That(WaitUntil(() => b.IsConnected), Is.True);
        a.SelectedRole = GatewayPaneViewModel.RoleName(GatewayRole.Bridge);
        Assert.That(set.A.Role, Is.EqualTo(GatewayRole.Bridge));
        Assert.That(WaitUntil(() => b.SelectedRole == GatewayPaneViewModel.RoleName(GatewayRole.Bridge)), Is.True, "pane B follows");
        a.SelectedRole = GatewayPaneViewModel.RoleName(GatewayRole.Console);
        Assert.That(set.B.Role, Is.EqualTo(GatewayRole.Console));
    }

    [Test]
    public void FrameFilter_NarrowsAndRestoresTheList()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        set.Start();
        GatewayPaneViewModel vm = new GatewayPaneViewModel(set, set.A, new FakeFactory(), new InlineDispatcher(), 1362)
        {
            SelectedEndpoint = "GOOD",
        };
        vm.ConnectCommand.Execute(null);
        Assert.That(WaitUntil(() => vm.IsConnected), Is.True);
        int all = vm.Frames.Count;
        Assert.That(all, Is.GreaterThanOrEqualTo(2), "HELLO out and reply in");

        vm.FrameFilter = "TX";
        Assert.That(vm.Frames.Count, Is.LessThan(all));
        for (int i = 0; i < vm.Frames.Count; i++)
        {
            Assert.That(vm.Frames[i].Record.Sent, Is.True);
        }
        vm.FrameFilter = "nothing matches this";
        Assert.That(vm.Frames.Count, Is.EqualTo(0));
        vm.FrameFilter = string.Empty;
        Assert.That(vm.Frames.Count, Is.EqualTo(all));
    }

    [Test]
    public void CommandOnDisconnectedPane_GivesANotice_NotSilence()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        set.Start();
        GatewayPaneViewModel vm = new GatewayPaneViewModel(set, set.A, new FakeFactory(), new InlineDispatcher(), 1362)
        {
            CommandText = "STAT",
        };
        Assert.That(vm.SendCommand.CanExecute(null), Is.True, "Enter must do something on a closed pane");
        vm.SendCommand.Execute(null);
        Assert.That(WaitUntil(() => HasLine(vm, ConsoleLineKind.Notice, "Not connected")), Is.True);
        Assert.That(HasLine(vm, ConsoleLineKind.Command, "STAT"), Is.True);
    }

    [Test]
    public void EveryConsoleLineKind_HasAVisibleBrush()
    {
        // A null Foreground paints nothing: the STAT reply was invisible on 2026-11-08.
        foreach (ConsoleLineKind kind in Enum.GetValues<ConsoleLineKind>())
        {
            ConsoleLineViewModel vm = new ConsoleLineViewModel(new ConsoleLine(1, DateTime.Now, kind, "x", FrameSource.Console));
            Assert.That(vm.Brush, Is.Not.Null, kind.ToString());
        }
        FrameRecordViewModel f = new FrameRecordViewModel(new FrameRecord(1, DateTime.Now, false, FrameSource.Serial,
                                                                          new byte[] { 0x01, 0x3F, 0xEB, 0xDF }));
        Assert.That(f.Brush, Is.Not.Null);
    }

    [Test]
    public void ParseHex_AcceptsCommonShapes()
    {
        Assert.That(GatewayPaneViewModel.TryParseHex("0x01, 0x3F", out byte[] b, out _), Is.True);
        Assert.That(b, Is.EqualTo(new byte[] { 0x01, 0x3F }));
        Assert.That(GatewayPaneViewModel.TryParseHex("013f48\n49", out b, out _), Is.True);
        Assert.That(b.Length, Is.EqualTo(4));
        Assert.That(GatewayPaneViewModel.TryParseHex("013", out _, out string? err), Is.False);
        Assert.That(err, Does.Contain("Odd"));
    }

    /// <summary>The first frame in the pane's list with this direction and length, or null.</summary>
    private static FrameRecordViewModel? FindFrame(GatewayPaneViewModel vm, bool sent, int length)
    {
        for (int i = 0; i < vm.Frames.Count; i++)
        {
            FrameRecordViewModel f = vm.Frames[i];
            if (f.Record.Sent == sent && f.Record.Bytes.Length == length)
            {
                return f;
            }
        }
        return null;
    }

    private static bool HasLine(GatewayPaneViewModel vm, ConsoleLineKind kind, string contains)
    {
        for (int i = 0; i < vm.ConsoleLines.Count; i++)
        {
            ConsoleLine l = vm.ConsoleLines[i].Line;
            if (l.Kind == kind && l.Text.Contains(contains, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    [Test]
    public void VersionFields_FollowTheHelloReply()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        set.Start();
        FakeFactory factory = new FakeFactory();
        GatewayPaneViewModel vm = new GatewayPaneViewModel(set, set.A, factory, new InlineDispatcher(), 1362);
        Assert.That(vm.FirmwareName, Is.EqualTo("-"));
        Assert.That(vm.VersionSummary, Does.Contain("no gateway identified"));

        vm.SelectedEndpoint = "GOOD";
        vm.ConnectCommand.Execute(null);
        Assert.That(WaitUntil(() => vm.IsConnected), Is.True);
        Assert.That(vm.FirmwareName, Is.EqualTo("FAKEGW"));
        Assert.That(vm.FirmwareVersion, Is.EqualTo("0.2.0"));
        Assert.That(vm.FirmwareBuild, Is.EqualTo("test"));
        Assert.That(vm.FirmwareChip, Is.EqualTo("FAKE1"));
        Assert.That(vm.FirmwareBoard, Is.EqualTo("Test bench"));
        Assert.That(vm.FirmwareMaxFrame, Is.EqualTo("1024 bytes"));
        Assert.That(vm.FirmwareRevision, Is.EqualTo("1.0"));
        Assert.That(vm.FirmwareSerial, Is.EqualTo("0011223344AA"));
        Assert.That(vm.FirmwareCpu, Is.EqualTo("2 cores, 240 MHz"));
        Assert.That(vm.FirmwareFlash, Is.EqualTo("4 MB on the chip, image built for 2 MB, 80 MHz, dio"));
        Assert.That(vm.FirmwareRam, Is.EqualTo("323,204 bytes, 216,156 bytes free, least 200,388 bytes, largest block 110,580 bytes"));
        Assert.That(vm.FirmwarePsram, Is.EqualTo("none"));
        Assert.That(vm.FirmwareUniqueId, Is.EqualTo("00112233445566778899AABBCCDDEEFF"));
        Assert.That(vm.FirmwareChipIds, Is.EqualTo("package 0, eFuse block 0.2, built-in flash 2, built-in PSRAM 1"));
        Assert.That(vm.FirmwareFlashId, Is.EqualTo("204016 (maker 20)"));
        Assert.That(vm.FirmwareFuses, Is.EqualTo(
            "USB off 0, download off 0, pins swapped 1, external PHY 0, VDD_SPI force 0, tieh 0, xpd 1"));
        Assert.That(vm.FirmwareCapabilities, Is.EqualTo("cmd, log, stat"));
        Assert.That(vm.FirmwarePort, Is.EqualTo("GOOD"));
        Assert.That(vm.VersionSummary, Is.EqualTo("Port A: FAKEGW 0.2.0 on FAKE1 (Test bench), built test"));
    }

    [Test]
    public void Setup_CopiesThePane_AndApplyChangesEndpointRoleAndTcpServer()
    {
        using GatewaySessionSet set = new GatewaySessionSet();
        set.Start();
        FakeFactory factory = new FakeFactory();
        GatewayPaneViewModel vm = new GatewayPaneViewModel(set, set.A, factory, new InlineDispatcher(), 1362);
        vm.SelectedEndpoint = "GOOD";
        vm.SelectedBaud = "57600";

        PaneSetupViewModel setup = PaneSetupViewModel.From(vm);
        Assert.That(setup.Title, Is.EqualTo("Setup gateway A"));
        Assert.That(setup.UseTcpClient, Is.False);
        Assert.That(setup.SelectedSerialPort, Is.EqualTo("GOOD"));
        Assert.That(setup.Baud, Is.EqualTo("57600"));
        Assert.That(setup.TcpServerEnabled, Is.False);
        Assert.That(setup.TcpServerPort, Is.EqualTo(1362));
        Assert.That(setup.Validate(), Is.True);

        // TCP client without a host is refused; with one it becomes a host:port endpoint.
        setup.UseTcpClient = true;
        Assert.That(setup.Validate(), Is.False);
        Assert.That(setup.Error, Does.Contain("host"));
        setup.TcpHost = "hdlcgw.local";
        setup.TcpClientPort = 1362;
        Assert.That(setup.Validate(), Is.True);
        Assert.That(setup.Endpoint, Is.EqualTo("hdlcgw.local:1362"));
        setup.SelectedRole = GatewayPaneViewModel.RoleName(GatewayRole.Echo);
        setup.Baud = "19200";
        vm.ApplySetup(setup);
        Assert.That(vm.SelectedEndpoint, Is.EqualTo("hdlcgw.local:1362"));
        Assert.That(vm.Endpoints, Does.Contain("hdlcgw.local:1362"));
        Assert.That(set.A.Role, Is.EqualTo(GatewayRole.Echo));
        Assert.That(vm.SelectedBaud, Is.EqualTo("19200"));
        Assert.That(vm.ConfigSummary, Does.Contain("TCP client to hdlcgw.local:1362"));
        Assert.That(vm.ConfigSummary, Does.Contain("Echo"));

        // TCP server on a connected pane: the role becomes TCP server on the chosen port.
        setup.UseTcpClient = false;
        setup.SelectedSerialPort = "GOOD";
        vm.ApplySetup(setup);
        vm.ConnectCommand.Execute(null);
        Assert.That(WaitUntil(() => vm.IsConnected), Is.True);
        setup.TcpServerEnabled = true;
        setup.TcpServerPort = 0;
        Assert.That(setup.Validate(), Is.False);
        setup.TcpServerPort = FreePort();
        Assert.That(setup.Validate(), Is.True);
        vm.ApplySetup(setup);
        Assert.That(WaitUntil(() => set.A.TcpPort == setup.TcpServerPort), Is.True, "listening on the chosen port");
        Assert.That(set.A.Role, Is.EqualTo(GatewayRole.TcpListen));
        Assert.That(vm.ConfigSummary, Does.Contain("TCP server on " + setup.TcpServerPort));
        setup.TcpServerEnabled = false;
        setup.SelectedRole = GatewayPaneViewModel.RoleName(GatewayRole.Console);
        vm.ApplySetup(setup);
        Assert.That(WaitUntil(() => set.A.TcpPort == 0), Is.True, "server stopped");
        Assert.That(set.A.Role, Is.EqualTo(GatewayRole.Console));
    }

    private static int FreePort()
    {
        System.Net.Sockets.TcpListener l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
