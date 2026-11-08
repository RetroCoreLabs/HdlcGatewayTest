// MCP SERVER HOST - Kestrel on localhost, streamable HTTP at /mcp, inside the running program.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace HdlcGatewayTest.Mcp;

/// <summary>
/// Hosts the MCP server inside the running application, the way RetroTerm does: a Kestrel
/// listener bound to 127.0.0.1 ONLY, speaking the MCP streamable-HTTP transport at /mcp. An AI
/// client such as Claude Code connects with
/// <c>claude mcp add --transport http hdlcgw http://127.0.0.1:2525/mcp</c>.
/// The bind address is hard-coded to localhost: this tool opens serial ports and sends frames
/// onto real machines' lines and must never listen on an external interface.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    /// <summary>Default port, Ronny's choice 2026-11-08 (RetroTerm uses 5715).</summary>
    public const int DefaultPort = 2525;

    private readonly WebApplication _app;

    private McpServerHost(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    /// <summary>The URL the server is listening on (http://127.0.0.1:port/mcp).</summary>
    public string Url { get; }

    /// <summary>
    /// Build and start the server. The tool provider decides what the AI can do; this class
    /// only owns transport and lifetime.
    /// </summary>
    /// <param name="tools">The tools.</param>
    /// <param name="port">TCP port on 127.0.0.1.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    /// <returns>The running host.</returns>
    /// <exception cref="IOException">The port is in use (Kestrel throws on start).</exception>
    public static async Task<McpServerHost> StartAsync(HdlcToolProvider tools, int port,
                                                       CancellationToken cancellationToken = default)
    {
        if (tools == null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();    // the hosting program owns its own logging
        builder.WebHost.UseUrls("http://127.0.0.1:" + port);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "HdlcGatewayTest",
                    Version = tools.BuildVersion,
                };
                options.ServerInstructions =
                    "HDLC gateway control (HDLCGW on ESP32-S2, RC422Dev on Pico) over two serial ports, panes A and B. "
                    + "Start with hdlc_ports, then hdlc_open pane=A endpoint=COMx. hdlc_command runs a firmware console "
                    + "command (HELP, STAT, BAUD n, LOG n, F, INSPECT, ...) and returns the reply; hdlc_readnew reads "
                    + "console lines (replies AND asynchronous firmware log lines) since a cursor; hdlc_frames lists the "
                    + "frames seen on a pane; hdlc_role sets Console/Free/Echo/Bridge/TcpListen; hdlc_test_start runs the "
                    + "loopback or two-gateway traffic test and hdlc_test_status reads its table. The server is "
                    + tools.BuildVersion + ".";
            })
            .WithHttpTransport()
            .WithListToolsHandler((request, ct) =>
                ValueTask.FromResult(new ListToolsResult { Tools = tools.BuildToolList() }))
            .WithCallToolHandler((request, ct) =>
                new ValueTask<CallToolResult>(
                    tools.CallToolAsync(request.Params?.Name ?? string.Empty, request.Params?.Arguments, ct)));

        WebApplication app = builder.Build();
        app.MapMcp("/mcp");

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return new McpServerHost(app, "http://127.0.0.1:" + port + "/mcp");
    }

    /// <summary>Stop the listener.</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
