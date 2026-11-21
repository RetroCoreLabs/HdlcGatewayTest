// MCP SERVER HOST - Kestrel on localhost, streamable HTTP at /mcp, inside the running program.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
    private readonly ConnectionCounter _connections;

    private McpServerHost(WebApplication app, string url, ConnectionCounter connections)
    {
        _app = app;
        Url = url;
        _connections = connections;
    }

    /// <summary>The URL the server is listening on (http://127.0.0.1:port/mcp).</summary>
    public string Url { get; }

    /// <summary>
    /// TCP connections open to the MCP port right now: the clients connected. This is counted
    /// at the connection, not at the MCP session, because the 2026-07-28 protocol revision
    /// removed sessions from streamable HTTP (the SDK's own notes on SessionMode) and a test
    /// showed the SDK client holds none after initialize. An HTTP client keeps its connection
    /// open between calls and closes it after about two minutes of silence, so a quiet client
    /// drops out of the count after that.
    /// </summary>
    public int ActiveConnections => _connections.Count;

    /// <summary>Raised on a server thread whenever <see cref="ActiveConnections"/> changes.</summary>
    public event Action<int>? ActiveConnectionsChanged
    {
        add => _connections.Changed += value;
        remove => _connections.Changed -= value;
    }

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

        ConnectionCounter connections = new ConnectionCounter(tools.Log);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();    // the hosting program owns its own logging
        // Localhost only, and every connection passes the counter on its way in and out.
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenLocalhost(port, listen => listen.Use(connections.Middleware)));

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
            {
                tools.Log.LogCall("tools/list", string.Empty);
                return ValueTask.FromResult(new ListToolsResult { Tools = tools.BuildToolList() });
            })
            .WithCallToolHandler((request, ct) =>
                new ValueTask<CallToolResult>(
                    tools.CallToolAsync(request.Params?.Name ?? string.Empty, request.Params?.Arguments, ct)));

        WebApplication app = builder.Build();
        app.MapMcp("/mcp");

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return new McpServerHost(app, "http://127.0.0.1:" + port + "/mcp", connections);
    }

    /// <summary>Stop the listener.</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the TCP connections Kestrel has open on the MCP port and writes each open and
    /// close to the log.
    /// </summary>
    private sealed class ConnectionCounter
    {
        private readonly McpTrafficLog _log;
        private int _count;

        public ConnectionCounter(McpTrafficLog log)
        {
            _log = log;
        }

        /// <summary>Connections open.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>Raised after every change, with the new count.</summary>
        public event Action<int>? Changed;

        /// <summary>
        /// Kestrel connection middleware: runs once per TCP connection for its whole life.
        /// </summary>
        /// <param name="next">The rest of the pipeline (HTTP).</param>
        /// <returns>The delegate Kestrel calls per connection.</returns>
        public ConnectionDelegate Middleware(ConnectionDelegate next)
        {
            return async context =>
            {
                string peer = context.RemoteEndPoint?.ToString() ?? "?";
                int now = Interlocked.Increment(ref _count);
                _log.LogEvent("client connected from " + peer + ", " + now + " connected");
                Changed?.Invoke(now);
                try
                {
                    await next(context).ConfigureAwait(false);
                }
                finally
                {
                    now = Interlocked.Decrement(ref _count);
                    _log.LogEvent("client " + peer + " disconnected, " + now + " connected");
                    Changed?.Invoke(now);
                }
            };
        }
    }
}
