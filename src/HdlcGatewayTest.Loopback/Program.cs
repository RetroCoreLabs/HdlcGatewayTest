// PROGRAM - entry point of hdlc-loopback.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using HdlcGatewayTest.Core;
using HdlcGatewayTest.Gateway;
using HdlcGatewayTest.Mcp;
using HdlcGatewayTest.Serial;

namespace HdlcGatewayTest.Loopback;

/// <summary>
/// Parses the command line, runs the test (or the given console commands), returns 0 on PASS
/// and 1 on anything else.
/// </summary>
public static class Program
{
    /// <summary>
    /// Entry point.
    /// </summary>
    /// <param name="args">Command line, see <see cref="Options.Usage"/>.</param>
    /// <returns>0 when every rate passed, 1 otherwise or on a usage error.</returns>
    public static int Main(string[] args)
    {
        Options? options = Options.Parse(args, out string? error);
        if (options == null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        if (options.Help)
        {
            Console.Write(Options.Usage);
            return 0;
        }
        if (options.ListPorts)
        {
            string[] names = SerialPortNames.List();
            for (int i = 0; i < names.Length; i++)
            {
                Console.WriteLine(names[i]);
            }
            return 0;
        }

        using CancellationTokenSource cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        TextWriter output = Console.Out;
        StreamWriter? log = null;
        if (options.LogFile != null)
        {
            log = new StreamWriter(options.LogFile, true);
            log.WriteLine();
            log.WriteLine("==== hdlc-loopback " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  "
                          + string.Join(' ', args));
            output = new TeeWriter(Console.Out, log);
        }

        try
        {
            if (options.McpPort != 0)
            {
                return ServeMcp(options.McpPort, output, cts.Token).GetAwaiter().GetResult();
            }
            if (options.PortA == null)
            {
                output.WriteLine("--port is required (or --list, --mcp, --help).");
                return 1;
            }
            if (options.Commands.Count > 0)
            {
                return RunCommands(options, output) ? 0 : 1;
            }
            TestRun run = new TestRun(options, output);
            return run.Run(cts.Token) ? 0 : 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            output.WriteLine("Port busy or not permitted: " + ex.Message);
            return 1;
        }
        catch (IOException ex)
        {
            output.WriteLine("Port error: " + ex.Message);
            return 1;
        }
        finally
        {
            output.Flush();
            log?.Dispose();
        }
    }

    /// <summary>
    /// --mcp: host the MCP server headless until Ctrl-C, printing every call and result so a
    /// person at the terminal sees what the AI does.
    /// </summary>
    private static async Task<int> ServeMcp(int port, TextWriter output, CancellationToken cancel)
    {
        using GatewaySessionSet sessions = new GatewaySessionSet();
        sessions.Start();
        McpTrafficLog log = new McpTrafficLog();
        log.Added += entry => output.WriteLine(entry.Time.ToString("HH:mm:ss.fff") + " "
                                               + (entry.IsResult ? (entry.IsError ? "ERR  " : "ok   ") : "call ")
                                               + entry.Tool + " " + entry.Text);
        HdlcToolProvider tools = new HdlcToolProvider(sessions, new EndpointTransportFactory(), log,
                                                      typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        McpServerHost server;
        try
        {
            server = await McpServerHost.StartAsync(tools, port, cancel).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            output.WriteLine("Cannot listen on 127.0.0.1:" + port + ": " + ex.Message);
            return 1;
        }
        await using (server)
        {
            output.WriteLine("MCP server at " + server.Url + " (Ctrl-C stops it).");
            output.WriteLine("Claude Code: claude mcp add --transport http hdlcgw " + server.Url);
            try
            {
                await Task.Delay(Timeout.Infinite, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ctrl-C.
            }
            output.WriteLine("Stopping.");
        }
        return 0;
    }

    /// <summary>
    /// --command: open the port, probe for control frames, run each command and print what the
    /// gateway answered.
    /// </summary>
    private static bool RunCommands(Options options, TextWriter output)
    {
        using GatewayLink link = new GatewayLink(new SerialTransport(options.PortA!));
        link.Open();
        GatewayCommandPath path = link.Probe(500, out string transcript, options.UseConsole);
        switch (path)
        {
            case GatewayCommandPath.ControlFrames:
                output.WriteLine(link.Name + ": " + link.Identity!.Name + " " + link.Identity.Version
                                 + " (" + link.Identity.Build + "), control frames.");
                break;
            case GatewayCommandPath.TextConsole:
                output.WriteLine(link.Name + ": no HELLO reply, using the text console.");
                break;
            default:
                output.WriteLine(link.Name + ": no HELLO reply and no console prompt. Got: "
                                 + TrafficTest.Printable(transcript));
                return false;
        }

        bool allOk = true;
        for (int i = 0; i < options.Commands.Count; i++)
        {
            string command = options.Commands[i];
            output.WriteLine("> " + command);
            bool ok = link.RunCommand(command, out string reply);
            output.Write(reply);
            if (!reply.EndsWith('\n'))
            {
                output.WriteLine();
            }
            if (!ok)
            {
                output.WriteLine("(reply did not complete cleanly)");
                allOk = false;
            }
        }
        return allOk;
    }
}
