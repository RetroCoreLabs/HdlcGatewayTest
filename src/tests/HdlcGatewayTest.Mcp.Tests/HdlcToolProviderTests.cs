// HDLC TOOL PROVIDER TESTS - every tool exists and does its job over fake gateways.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Text.Json;
using HdlcGatewayTest.Core;
using HdlcGatewayTest.Core.Tests;
using HdlcGatewayTest.Gateway;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace HdlcGatewayTest.Mcp.Tests;

/// <summary>A factory whose endpoints are fake gateways: "GOOD" speaks control frames, "OLD" text only, "DEAD" nothing.</summary>
public sealed class FakeTransportFactory : IGatewayTransportFactory
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
            "DEAD" => FakeGateway.Kind.Dead,
            _ => throw new ArgumentException("unknown endpoint " + endpoint),
        };
        Gateways[endpoint] = new FakeGateway(t, kind);
        return t;
    }
}

[TestFixture]
public sealed class HdlcToolProviderTests
{
    private GatewaySessionSet _set = null!;
    private FakeTransportFactory _factory = null!;
    private HdlcToolProvider _tools = null!;

    [SetUp]
    public void SetUp()
    {
        _set = new GatewaySessionSet();
        _set.Start();
        _factory = new FakeTransportFactory();
        _tools = new HdlcToolProvider(_set, _factory, new McpTrafficLog(), "test");
    }

    [TearDown]
    public void TearDown()
    {
        _set.Dispose();
    }

    private static IDictionary<string, JsonElement> Args(params (string, object)[] pairs)
    {
        Dictionary<string, JsonElement> d = new Dictionary<string, JsonElement>();
        for (int i = 0; i < pairs.Length; i++)
        {
            d[pairs[i].Item1] = JsonSerializer.SerializeToElement(pairs[i].Item2);
        }
        return d;
    }

    private async Task<string> Call(string tool, params (string, object)[] args)
    {
        CallToolResult r = await _tools.CallToolAsync(tool, Args(args), CancellationToken.None);
        Assert.That(r.IsError, Is.Not.True, tool + " failed: " + Text(r));
        return Text(r);
    }

    private async Task<string> CallExpectingError(string tool, params (string, object)[] args)
    {
        CallToolResult r = await _tools.CallToolAsync(tool, Args(args), CancellationToken.None);
        Assert.That(r.IsError, Is.True, tool + " should have failed");
        return Text(r);
    }

    private static string Text(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    [Test]
    public void ToolList_HasEveryNamedTool_WithDescriptionAndSchema()
    {
        IList<Tool> tools = _tools.BuildToolList();
        Assert.That(tools.Count, Is.EqualTo(HdlcToolProvider.ToolNames.Length));
        for (int i = 0; i < HdlcToolProvider.ToolNames.Length; i++)
        {
            bool found = false;
            for (int j = 0; j < tools.Count; j++)
            {
                if (tools[j].Name == HdlcToolProvider.ToolNames[i])
                {
                    found = true;
                    Assert.That(tools[j].Description, Is.Not.Null.And.Length.GreaterThan(20), tools[j].Name);
                    Assert.That(tools[j].InputSchema.GetProperty("type").GetString(), Is.EqualTo("object"));
                }
            }
            Assert.That(found, Is.True, HdlcToolProvider.ToolNames[i]);
        }
    }

    [Test]
    public async Task EveryTool_Dispatches_NoneIsUnknown()
    {
        for (int i = 0; i < HdlcToolProvider.ToolNames.Length; i++)
        {
            CallToolResult r = await _tools.CallToolAsync(HdlcToolProvider.ToolNames[i], Args(), CancellationToken.None);
            Assert.That(Text(r), Does.Not.Contain("Unknown tool"), HdlcToolProvider.ToolNames[i]);
        }
        CallToolResult unknown = await _tools.CallToolAsync("hdlc_nope", null, CancellationToken.None);
        Assert.That(unknown.IsError, Is.True);
    }

    [Test]
    public async Task Open_Command_ReadNew_Stat_Close()
    {
        Assert.That(await Call("hdlc_ports"), Does.Contain("GOOD"));
        string opened = await Call("hdlc_open", ("pane", "A"), ("endpoint", "GOOD"));
        Assert.That(opened, Does.Contain("ControlFrames").And.Contain("FAKEGW 0.2.0"));

        string reply = await Call("hdlc_command", ("pane", "A"), ("command", "BAUD 9600"));
        Assert.That(reply, Does.Contain("[control frame]").And.Contain("set to 9600"));
        Assert.That(_factory.Gateways["GOOD"].Baud, Is.EqualTo(9600u));

        string lines = await Call("hdlc_readnew", ("pane", "A"));
        Assert.That(lines, Does.Contain("Command BAUD 9600").And.Contain("Reply").And.Contain("next cursor:"));

        string stat = await Call("hdlc_stat", ("pane", "A"));
        Assert.That(stat, Does.Contain("HDLC frames"));

        string status = await Call("hdlc_status", ("pane", "A"));
        Assert.That(status, Does.Contain("frames logged:"));

        string list = await Call("hdlc_list");
        Assert.That(list, Does.Contain("Pane A: ControlFrames").And.Contain("Pane B: Disconnected"));

        Assert.That(await Call("hdlc_close", ("pane", "A")), Does.Contain("Disconnected"));
    }

    [Test]
    public async Task LegacyGateway_CommandsGoThroughTextConsole()
    {
        await Call("hdlc_open", ("pane", "B"), ("endpoint", "OLD"), ("console", true));
        string reply = await Call("hdlc_command", ("pane", "B"), ("command", "BAUD 19200"));
        Assert.That(reply, Does.Contain("[text console]").And.Contain("set to 19200"));
        string err = await CallExpectingError("hdlc_loglevel", ("pane", "B"), ("level", 2));
        Assert.That(err, Does.Contain("control frames"));
    }

    [Test]
    public async Task BadArguments_AreErrors_NotExceptions()
    {
        Assert.That(await CallExpectingError("hdlc_open", ("pane", "C"), ("endpoint", "GOOD")), Does.Contain("A or B"));
        Assert.That(await CallExpectingError("hdlc_open", ("pane", "A")), Does.Contain("endpoint"));
        Assert.That(await CallExpectingError("hdlc_open", ("pane", "A"), ("endpoint", "WHAT")), Does.Contain("unknown endpoint"));
        Assert.That(await CallExpectingError("hdlc_command", ("pane", "A"), ("command", "STAT")), Does.Contain("not connected"));
        Assert.That(await CallExpectingError("hdlc_send_frame", ("pane", "A"), ("hex", "0G")), Does.Contain("hex digit"));
        Assert.That(await CallExpectingError("hdlc_role", ("pane", "A"), ("role", "Pirate")), Does.Contain("Console, Free"));
    }

    [Test]
    public async Task Decode_SendFrame_Frames()
    {
        string decoded = await Call("hdlc_decode", ("hex", "01 3F EB DF"));
        Assert.That(decoded, Does.Contain("SABM").And.Contain("[OK]"));
        string wrong = await Call("hdlc_decode", ("hex", "01 3F 00 00"));
        Assert.That(wrong, Does.Contain("NOT OK, correct 0xDFEB"));

        await Call("hdlc_open", ("pane", "A"), ("endpoint", "GOOD"));
        string sent = await Call("hdlc_send_frame", ("pane", "A"), ("hex", "01 3F"));
        Assert.That(sent, Does.Contain("Sent 4 bytes").And.Contain("01 3F EB DF"));
        string frames = await Call("hdlc_frames", ("pane", "A"));
        Assert.That(frames, Does.Contain("TX Mcp").And.Contain("SABM"));
    }

    [Test]
    public async Task Role_Echo_Bridge_TcpListen()
    {
        await Call("hdlc_open", ("pane", "A"), ("endpoint", "GOOD"));
        Assert.That(await Call("hdlc_role", ("pane", "A")), Does.Contain("role: Console"));
        Assert.That(await Call("hdlc_role", ("pane", "A"), ("role", "echo")), Does.Contain("role: Echo"));
        Assert.That(await CallExpectingError("hdlc_role", ("pane", "A"), ("role", "Bridge")), Does.Contain("both panes"));

        await Call("hdlc_open", ("pane", "B"), ("endpoint", "OLD"), ("console", true));
        Assert.That(await Call("hdlc_role", ("pane", "A"), ("role", "Bridge")), Does.Contain("bridged"));
        Assert.That(_set.B.Role, Is.EqualTo(GatewayRole.Bridge));

        int port = FreePort();
        string tcp = await Call("hdlc_role", ("pane", "A"), ("role", "TcpListen"), ("tcp_port", port));
        Assert.That(tcp, Does.Contain("listens on TCP port " + port));
        Assert.That(_set.A.Role, Is.EqualTo(GatewayRole.TcpListen));
        Assert.That(_set.B.Role, Is.EqualTo(GatewayRole.Console), "the bridge was undone");
        Assert.That(await CallExpectingError("hdlc_role", ("pane", "B"), ("role", "TcpListen"), ("tcp_port", port)),
                    Does.Contain("different ports"));
        Assert.That(await Call("hdlc_role", ("pane", "A"), ("role", "Console")), Does.Contain("role: Console"));
        Assert.That(_set.A.TcpPort, Is.EqualTo(0));
    }

    [Test]
    public async Task TrafficTest_StartStatusStop()
    {
        await Call("hdlc_open", ("pane", "A"), ("endpoint", "GOOD"));
        Assert.That(await Call("hdlc_test_status"), Does.Contain("No traffic test"));
        string started = await Call("hdlc_test_start", ("rates", "38400"), ("seconds", 1), ("size", 32), ("window", 300), ("timeout", 1));
        Assert.That(started, Does.Contain("started"));
        Assert.That(await CallExpectingError("hdlc_test_start"), Does.Contain("already running"));
        Assert.That(await CallExpectingError("hdlc_command", ("pane", "A"), ("command", "STAT")), Does.Contain("busy"));

        string status = string.Empty;
        for (int i = 0; i < 100 && !status.Contains("FINISHED"); i++)
        {
            await Task.Delay(100);
            status = await Call("hdlc_test_status");
        }
        Assert.That(status, Does.Contain("FINISHED: PASS").And.Contain("38400  A->A").And.Contain("PASS"));
        Assert.That(await Call("hdlc_test_stop"), Does.Contain("No traffic test is running"));

        // The pane is usable again afterwards.
        Assert.That(await Call("hdlc_command", ("pane", "A"), ("command", "STAT")), Does.Contain("HDLC frames"));
    }

    [Test]
    public async Task TrafficLog_RecordsCallsAndResults()
    {
        McpTrafficLog log = _tools.Log;
        await Call("hdlc_build");
        List<McpTrafficEntry> entries = log.Snapshot();
        Assert.That(entries.Count, Is.EqualTo(2));
        Assert.That(entries[0].IsResult, Is.False);
        Assert.That(entries[1].IsResult, Is.True);
        Assert.That(entries[1].Text, Does.Contain("HdlcGatewayTest test"));
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
