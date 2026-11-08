// PROGRAM - entry point of hdlc-loopback.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.IO.Ports;

namespace HdlcGatewayTest.Loopback;

/// <summary>
/// Parses the command line, runs the test, returns 0 on PASS and 1 on anything else.
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
            string[] names = SerialPort.GetPortNames();
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
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

        try
        {
            TestRun run = new TestRun(options, Console.Out);
            return run.Run(cts.Token) ? 0 : 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine("Port busy or not permitted: " + ex.Message);
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("Port error: " + ex.Message);
            return 1;
        }
    }
}
