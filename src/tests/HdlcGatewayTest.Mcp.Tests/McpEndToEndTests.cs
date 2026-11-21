// MCP END-TO-END TESTS - a real MCP client over HTTP to the real server, down to a fake gateway.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Net;
using System.Net.Sockets;
using HdlcGatewayTest.Gateway;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace HdlcGatewayTest.Mcp.Tests;

/// <summary>
/// Exactly what Claude Code does when it connects, minus the window: streamable HTTP to
/// 127.0.0.1, tools/list, tools/call.
/// </summary>
[TestFixture]
public sealed class McpEndToEndTests
{
    [Test]
    public async Task Client_ListsTools_OpensGateway_RunsCommand()
    {
        int port = FreePort();
        using GatewaySessionSet set = new GatewaySessionSet();
        set.Start();
        HdlcToolProvider tools = new HdlcToolProvider(set, new FakeTransportFactory(), new McpTrafficLog(), "e2e");
        await using McpServerHost server = await McpServerHost.StartAsync(tools, port);
        Assert.That(server.Url, Is.EqualTo("http://127.0.0.1:" + port + "/mcp"));

        HttpClientTransport transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Url),
        });
        await using McpClient client = await McpClient.CreateAsync(transport);

        IList<McpClientTool> listed = await client.ListToolsAsync();
        Assert.That(listed.Count, Is.EqualTo(HdlcToolProvider.ToolNames.Length));

        CallToolResult opened = await client.CallToolAsync("hdlc_open",
            new Dictionary<string, object?> { ["pane"] = "A", ["endpoint"] = "GOOD" });
        Assert.That(opened.IsError, Is.Not.True);
        Assert.That(((TextContentBlock)opened.Content[0]).Text, Does.Contain("ControlFrames"));

        CallToolResult reply = await client.CallToolAsync("hdlc_command",
            new Dictionary<string, object?> { ["pane"] = "A", ["command"] = "BAUD 38400" });
        Assert.That(((TextContentBlock)reply.Content[0]).Text, Does.Contain("set to 38400"));

        CallToolResult bad = await client.CallToolAsync("hdlc_command",
            new Dictionary<string, object?> { ["pane"] = "Q", ["command"] = "STAT" });
        Assert.That(bad.IsError, Is.True);
    }

    [Test]
    public async Task Server_CountsConnectedClients()
    {
        int port = FreePort();
        using GatewaySessionSet set = new GatewaySessionSet();
        set.Start();
        McpTrafficLog log = new McpTrafficLog();
        HdlcToolProvider tools = new HdlcToolProvider(set, new FakeTransportFactory(), log, "e2e");
        await using McpServerHost server = await McpServerHost.StartAsync(tools, port);
        Assert.That(server.ActiveConnections, Is.EqualTo(0));

        HttpClientTransport transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Url),
        });
        McpClient client = await McpClient.CreateAsync(transport);
        Assert.That(server.ActiveConnections, Is.GreaterThanOrEqualTo(1), "the connection is open after initialize");
        await client.ListToolsAsync();
        Assert.That(server.ActiveConnections, Is.GreaterThanOrEqualTo(1), "still open across a call");

        await client.DisposeAsync();
        await transport.DisposeAsync();    // disposes the HttpClient it owns: its connection closes
        Assert.That(await WaitForAsync(() => server.ActiveConnections == 0), Is.True, "the connection closed with the client");

        List<McpTrafficEntry> entries = log.Snapshot();
        bool opened = false;
        bool closed = false;
        bool listed = false;
        for (int i = 0; i < entries.Count; i++)
        {
            opened |= entries[i].Tool == McpTrafficLog.ServerTool && entries[i].Text.Contains("connected from");
            closed |= entries[i].Tool == McpTrafficLog.ServerTool && entries[i].Text.Contains("disconnected");
            listed |= entries[i].Tool == "tools/list";
        }
        Assert.That(opened && closed && listed, Is.True, "the log names the connect, the tools/list call and the disconnect");
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int milliseconds = 5000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(20);
        }
        return condition();
    }

    private static int FreePort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
