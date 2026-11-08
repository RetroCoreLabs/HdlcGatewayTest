// PROGRAM - entry point of hdlc-gateway-tool.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using Avalonia;

namespace HdlcGatewayTest.App;

/// <summary>
/// Starts the window. Command line: <c>--mcp [port]</c> turns the MCP server on for this run
/// (on the given port, else the remembered one) without changing the saved setting, so a
/// shortcut can launch an AI-ready window while the default stays off (Ronny, 2026-11-08).
/// </summary>
public static class Program
{
    /// <summary>Parsed command line, read by the main window on start-up.</summary>
    public static StartupOptions Options { get; private set; } = new StartupOptions();

    /// <summary>
    /// Entry point.
    /// </summary>
    /// <param name="args">Command line.</param>
    [STAThread]
    public static void Main(string[] args)
    {
        Options = StartupOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Avalonia configuration; also used by the designer.</summary>
    /// <returns>The app builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}

/// <summary>What the command line asked for.</summary>
public sealed class StartupOptions
{
    /// <summary>True when --mcp was given: start the MCP server for this run.</summary>
    public bool McpForThisRun { get; private set; }

    /// <summary>Port given after --mcp, or 0 for the remembered one.</summary>
    public int McpPort { get; private set; }

    /// <summary>
    /// Parse.
    /// </summary>
    /// <param name="args">Command line.</param>
    /// <returns>The options; unknown arguments are ignored.</returns>
    public static StartupOptions Parse(string[] args)
    {
        StartupOptions o = new StartupOptions();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--mcp", StringComparison.OrdinalIgnoreCase))
            {
                o.McpForThisRun = true;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int port) && port > 0 && port < 65536)
                {
                    o.McpPort = port;
                    i++;
                }
            }
        }
        return o;
    }
}
