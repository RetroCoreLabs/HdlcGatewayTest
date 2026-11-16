// HDLC TOOL PROVIDER - the MCP tool list and what each tool does, over the shared session layer.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HdlcGatewayTest.Core;
using HdlcGatewayTest.Gateway;
using ModelContextProtocol.Protocol;

namespace HdlcGatewayTest.Mcp;

/// <summary>
/// Builds the MCP tool list and dispatches calls. Every tool is a thin wrapper over
/// <see cref="GatewaySessionSet"/> and <see cref="GatewaySession"/>, the same objects the GUI's
/// panes use, so a person watching the window sees what the AI does (commands are tagged Mcp).
/// Tool descriptions are the documentation the AI reads; keep them plain and complete.
/// </summary>
public sealed class HdlcToolProvider
{
    private readonly GatewaySessionSet _sessions;
    private readonly IGatewayTransportFactory _transports;
    private readonly TrafficTestRecorder _recorder = new TrafficTestRecorder();
    private Task<bool>? _test;

    /// <summary>
    /// Create the provider.
    /// </summary>
    /// <param name="sessions">The two panes.</param>
    /// <param name="transports">How to open an endpoint.</param>
    /// <param name="log">Where calls and results are recorded.</param>
    /// <param name="buildVersion">Version string reported to clients.</param>
    public HdlcToolProvider(GatewaySessionSet sessions, IGatewayTransportFactory transports, McpTrafficLog log,
                            string buildVersion)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _transports = transports ?? throw new ArgumentNullException(nameof(transports));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        BuildVersion = buildVersion;
    }

    /// <summary>Calls and results.</summary>
    public McpTrafficLog Log { get; }

    /// <summary>Version string reported in the MCP handshake and by hdlc_build.</summary>
    public string BuildVersion { get; }

    /// <summary>The recorder of the running or last traffic test.</summary>
    public TrafficTestRecorder TestRecorder => _recorder;

    /// <summary>Every tool name, in the order the list shows them. The "every command has a tool" test uses it.</summary>
    public static readonly string[] ToolNames =
    {
        "hdlc_ports", "hdlc_open", "hdlc_close", "hdlc_list", "hdlc_status", "hdlc_command", "hdlc_readnew",
        "hdlc_loglevel", "hdlc_stat", "hdlc_send_frame", "hdlc_frames", "hdlc_decode", "hdlc_role",
        "hdlc_test_start", "hdlc_test_status", "hdlc_test_stop", "hdlc_build",
    };

    /// <summary>
    /// The tool list for tools/list.
    /// </summary>
    /// <returns>The tools.</returns>
    public IList<Tool> BuildToolList()
    {
        List<Tool> tools = new List<Tool>
        {
            new Tool
            {
                Name = "hdlc_ports",
                Description = "List the serial ports on this PC. The ESP32-S2 gateway (HDLCGW) and the Pico gateway "
                              + "(RC422Dev) each show as one COM port.",
                InputSchema = Schema(),
            },
            new Tool
            {
                Name = "hdlc_open",
                Description = "Connect pane A or B to a gateway and find out how to talk to it: sends HELLO; a "
                              + "firmware with control frames answers with its name and version, an older one falls "
                              + "back to the ESC ESC ESC text console. Takes up to four seconds for a silent port. "
                              + "Returns the resulting state.",
                InputSchema = Schema(
                    ("pane", "string", true, "A or B"),
                    ("endpoint", "string", true, "Serial port name, for example COM10, or host:port for a gateway over WiFi"),
                    ("console", "boolean", false, "Default false: HELLO only, never an ESC byte; commands go as control "
                                                   + "frames (HDLCGW 0.2.0 and later). True: also try the ESC ESC ESC text "
                                                   + "console first, needed for a text-console-only gateway such as RC422Dev.")),
            },
            new Tool
            {
                Name = "hdlc_close",
                Description = "Disconnect a pane and free its port.",
                InputSchema = Schema(("pane", "string", true, "A or B")),
            },
            new Tool
            {
                Name = "hdlc_list",
                Description = "Both panes in one line each: state, endpoint, firmware, role, TCP port and peer.",
                InputSchema = Schema(),
            },
            new Tool
            {
                Name = "hdlc_status",
                Description = "Everything about one pane: state, firmware identity, role, log level, TCP listener, "
                              + "frame and console line counts, and the last STAT the firmware pushed if any.",
                InputSchema = Schema(("pane", "string", true, "A or B")),
            },
            new Tool
            {
                Name = "hdlc_command",
                Description = "Run one firmware console command on a pane and return its reply text. Commands: HELP, "
                              + "STAT, INFO, BAUD <rate>|MAX|DEFAULT (transmit clock), LOG 0..5 (firmware log level), "
                              + "F [hex] (send a frame with FCS added; F alone sends the 24-byte test frame), INSPECT (last "
                              + "frame off the line), DECODE <hex>, CLOCK ON|OFF, LOOPBACK ON|OFF, ECHO, HDLC, DUMP, "
                              + "CLEAR, TASK, RXDMA, RESET. With a control-frame firmware traffic keeps flowing; with a "
                              + "legacy firmware the port leaves forwarding mode for the duration of the command.",
                InputSchema = Schema(
                    ("pane", "string", true, "A or B"),
                    ("command", "string", true, "The command line, for example 'BAUD 38400'"),
                    ("timeout_ms", "integer", false, "How long to wait for the reply; default 2000")),
            },
            new Tool
            {
                Name = "hdlc_readnew",
                Description = "Console lines of a pane since a cursor: commands, replies, firmware log lines, events "
                              + "and notices, each with its kind and time. Returns the lines and the next cursor; pass "
                              + "it back as 'since' to poll. since=0 (default) returns everything kept.",
                InputSchema = Schema(
                    ("pane", "string", true, "A or B"),
                    ("since", "integer", false, "Cursor from the previous call; 0 for everything"),
                    ("max", "integer", false, "Most lines to return; default 200")),
            },
            new Tool
            {
                Name = "hdlc_loglevel",
                Description = "Set the firmware log level of a pane: 0 off, 1 error, 2 warn (bad FCS, aborts), 3 info, "
                              + "4 debug (one line per good frame), 5 trace. Log lines then arrive as console lines of "
                              + "kind Log; read them with hdlc_readnew. Needs a control-frame firmware.",
                InputSchema = Schema(
                    ("pane", "string", true, "A or B"),
                    ("level", "integer", true, "0..5")),
            },
            new Tool
            {
                Name = "hdlc_stat",
                Description = "Run STAT on a pane and return the firmware's counter table (frames sent and received, "
                              + "line receiver counters, USB drops, control-frame counters).",
                InputSchema = Schema(("pane", "string", true, "A or B")),
            },
            new Tool
            {
                Name = "hdlc_send_frame",
                Description = "Send a raw HDLC frame on a pane's port: hex bytes (spaces optional) of address, control "
                              + "and data; with add_fcs=true the FCS is computed and appended, otherwise the last two "
                              + "bytes you give are sent as the FCS. The frame goes onto the synchronous line as-is.",
                InputSchema = Schema(
                    ("pane", "string", true, "A or B"),
                    ("hex", "string", true, "For example '01 3F' or '0103484900'"),
                    ("add_fcs", "boolean", false, "Append the FCS; default true")),
            },
            new Tool
            {
                Name = "hdlc_frames",
                Description = "Frames seen on a pane since a cursor, oldest first: sequence, time, TX or RX, source "
                              + "(Serial, Tcp, Echo, Bridge, Console, Mcp, Traffic), decoded address and control field, "
                              + "FCS verdict and the bytes as hex. Returns the next cursor.",
                InputSchema = Schema(
                    ("pane", "string", true, "A or B"),
                    ("since", "integer", false, "Cursor from the previous call; 0 for everything kept"),
                    ("max", "integer", false, "Most frames to return; default 100")),
            },
            new Tool
            {
                Name = "hdlc_decode",
                Description = "Decode a frame given as hex without sending it: address, control field (I/S/U, N(S), "
                              + "N(R), P/F, SABM/UA/RR/...), info length, FCS check and the correct FCS if it is wrong.",
                InputSchema = Schema(("hex", "string", true, "The whole frame including its two FCS bytes")),
            },
            new Tool
            {
                Name = "hdlc_role",
                Description = "Set what a pane does with received frames. Console: list only (default). Free: list "
                              + "only, hex entry in the GUI. Echo: send every received frame straight back on the same "
                              + "port. Bridge: pair A and B, a frame received on one is sent on the other (both panes "
                              + "connected). TcpListen: also listen on tcp_port for the nd100x emulator "
                              + "(--hdlc=N:<this-pc>:<port>) and forward frames between it and the serial line; A and B "
                              + "need different ports. Omit role to read the current one.",
                InputSchema = Schema(
                    ("pane", "string", true, "A or B"),
                    ("role", "string", false, "Console, Free, Echo, Bridge or TcpListen"),
                    ("tcp_port", "integer", false, "For TcpListen; default 1362 for A, 1363 for B")),
            },
            new Tool
            {
                Name = "hdlc_test_start",
                Description = "Start the traffic test in the background: numbered frames out of a gateway, checked as "
                              + "they come back (loopback on A, or A->B and B->A with two gateways wired line to line). "
                              + "Sets BAUD on each rate unless no_baud. Read progress and the PASS/FAIL table with "
                              + "hdlc_test_status; stop early with hdlc_test_stop.",
                InputSchema = Schema(
                    ("two_gateways", "boolean", false, "A->B and B->A instead of loopback on A; default false"),
                    ("rates", "string", false, "Comma-separated bit rates; default 9600,19200,38400,115200,307200,983000"),
                    ("seconds", "integer", false, "Seconds per rate; default 120"),
                    ("size", "integer", false, "Frame length including FCS, 10..4112; default 126 (both gateways take up to 4105, their HELLO maxframe; longer frames test that they refuse them)"),
                    ("window", "integer", false, "Encoded bytes in flight per direction; default 3000"),
                    ("timeout", "integer", false, "Seconds before a frame counts as lost; default 5"),
                    ("no_baud", "boolean", false, "Do not set BAUD; keep the gateways' clocks"),
                    ("verbose", "integer", false, "Bad frames to explain in the messages; default 3")),
            },
            new Tool
            {
                Name = "hdlc_test_status",
                Description = "Whether a traffic test runs, its latest status line, its messages and the result table "
                              + "of the rates finished so far.",
                InputSchema = Schema(),
            },
            new Tool
            {
                Name = "hdlc_test_stop",
                Description = "Stop the running traffic test after the current rate drains; its results stay readable.",
                InputSchema = Schema(),
            },
            new Tool
            {
                Name = "hdlc_build",
                Description = "Which build of the tool answers: version and the process hosting it.",
                InputSchema = Schema(),
            },
        };
        return tools;
    }

    /// <summary>
    /// Dispatch one call.
    /// </summary>
    /// <param name="name">Tool name.</param>
    /// <param name="arguments">Arguments, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result; errors go back as results with IsError, never as exceptions.</returns>
    public async Task<CallToolResult> CallToolAsync(string name, IDictionary<string, JsonElement>? arguments,
                                                    CancellationToken cancellationToken)
    {
        Log.LogCall(name, DescribeArguments(arguments));
        Stopwatch stopwatch = Stopwatch.StartNew();
        CallToolResult result;
        try
        {
            result = name switch
            {
                "hdlc_ports" => Ports(),
                "hdlc_open" => await OpenAsync(arguments).ConfigureAwait(false),
                "hdlc_close" => Close(arguments),
                "hdlc_list" => ListPanes(),
                "hdlc_status" => Status(arguments),
                "hdlc_command" => await CommandAsync(arguments).ConfigureAwait(false),
                "hdlc_readnew" => ReadNew(arguments),
                "hdlc_loglevel" => await LogLevelAsync(arguments).ConfigureAwait(false),
                "hdlc_stat" => await StatAsync(arguments).ConfigureAwait(false),
                "hdlc_send_frame" => SendFrame(arguments),
                "hdlc_frames" => Frames(arguments),
                "hdlc_decode" => Decode(arguments),
                "hdlc_role" => Role(arguments),
                "hdlc_test_start" => TestStart(arguments),
                "hdlc_test_status" => TestStatus(),
                "hdlc_test_stop" => TestStop(),
                "hdlc_build" => Ok("HdlcGatewayTest " + BuildVersion + ", process " + Process.GetCurrentProcess().ProcessName
                                   + " pid " + Environment.ProcessId),
                _ => Error("Unknown tool '" + name + "'."),
            };
        }
        catch (OperationCanceledException)
        {
            Log.LogResult(name, true, "cancelled", stopwatch.Elapsed);
            throw;
        }
        catch (Exception ex)
        {
            result = Error(name + " failed: " + ex.Message);
        }
        Log.LogResult(name, result.IsError == true, FirstText(result), stopwatch.Elapsed);
        return result;
    }

    private CallToolResult Ports()
    {
        string[] names = _transports.ListEndpoints();
        return Ok(names.Length == 0 ? "No serial ports." : string.Join("\n", names));
    }

    private async Task<CallToolResult> OpenAsync(IDictionary<string, JsonElement>? args)
    {
        if (!TryPane(args, out GatewaySession? pane, out CallToolResult? error))
        {
            return error!;
        }
        string endpoint = Str(args, "endpoint");
        if (endpoint.Length == 0)
        {
            return Error("endpoint is required, for example COM10.");
        }
        if (_sessions.TestRunning)
        {
            return Error("A traffic test is running; stop it first.");
        }
        IGatewayTransport transport;
        try
        {
            transport = _transports.Create(endpoint);
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
        bool tryConsole = Bool(args, "console", false);
        await Task.Run(() => pane!.Connect(transport, tryConsole)).ConfigureAwait(false);
        return pane!.State == GatewaySessionState.Faulted ? Error(DescribePane(pane)) : Ok(DescribePane(pane));
    }

    private CallToolResult Close(IDictionary<string, JsonElement>? args)
    {
        if (!TryPane(args, out GatewaySession? pane, out CallToolResult? error))
        {
            return error!;
        }
        if (_sessions.TestRunning)
        {
            return Error("A traffic test is running; stop it first.");
        }
        pane!.Disconnect();
        return Ok(DescribePane(pane));
    }

    private CallToolResult ListPanes()
    {
        return Ok(DescribePane(_sessions.A) + "\n" + DescribePane(_sessions.B));
    }

    private CallToolResult Status(IDictionary<string, JsonElement>? args)
    {
        if (!TryPane(args, out GatewaySession? pane, out CallToolResult? error))
        {
            return error!;
        }
        StringBuilder sb = new StringBuilder();
        sb.Append(DescribePane(pane!)).Append('\n');
        sb.Append("log level: ").Append(pane!.LogLevel).Append('\n');
        sb.Append("frames logged: ").Append(pane.Frames.Total).Append(", console lines: ").Append(pane.Console.Total).Append('\n');
        if (pane.Link != null)
        {
            // What the channel itself counted, to tell "nothing arrived" from "arrived but not listed".
            sb.Append("channel: data ").Append(pane.Link.Channel.DataFrames)
              .Append(", control ").Append(pane.Link.Channel.ControlFrames)
              .Append(", bad ").Append(pane.Link.Channel.BadFrames)
              .Append(", decoder dropped ").Append(pane.Link.Channel.DroppedFrames)
              .Append(", transport receive overflow ").Append(pane.Link.Transport.ReceiveOverflow)
              .Append(", driver errors ").Append(pane.Link.Transport.DriverErrors)
              .Append(", raw bytes received ").Append(pane.Link.Transport.BytesReceived).Append('\n');
        }
        if (pane.Identity != null)
        {
            sb.Append("identity: ").Append(pane.Identity.Text.Replace('\n', ' ').TrimEnd()).Append('\n');
        }
        GatewayStatistics? s = pane.LatestStatistics;
        if (s != null)
        {
            sb.Append("last STAT push:\n").Append(s.Describe(pane.Identity?.Name));
        }
        return Ok(sb.ToString().TrimEnd());
    }

    private async Task<CallToolResult> CommandAsync(IDictionary<string, JsonElement>? args)
    {
        if (!TryPane(args, out GatewaySession? pane, out CallToolResult? error))
        {
            return error!;
        }
        string command = Str(args, "command");
        if (command.Length == 0)
        {
            return Error("command is required.");
        }
        if (pane!.Link == null)
        {
            return Error("Pane " + pane.Label + " is not connected; hdlc_open first.");
        }
        if (pane.InTrafficTest)
        {
            return Error("Pane " + pane.Label + " is busy with the traffic test.");
        }
        int timeout = Int(args, "timeout_ms", 2000);
        string reply = await pane.SendCommandAsync(command, FrameSource.Mcp, timeout).ConfigureAwait(false);
        string path = pane.State == GatewaySessionState.ControlFrames ? "control frame" : "text console";
        return Ok("[" + path + "] " + command + "\n" + reply.TrimEnd());
    }

    private CallToolResult ReadNew(IDictionary<string, JsonElement>? args)
    {
        if (!TryPane(args, out GatewaySession? pane, out CallToolResult? error))
        {
            return error!;
        }
        long since = Long(args, "since", 0);
        int max = Int(args, "max", 200);
        List<ConsoleLine> lines = pane!.Console.Since(since, max);
        StringBuilder sb = new StringBuilder();
        long next = since;
        for (int i = 0; i < lines.Count; i++)
        {
            ConsoleLine l = lines[i];
            sb.Append(l.Sequence).Append(' ').Append(l.Time.ToString("HH:mm:ss.fff")).Append(' ')
              .Append(l.Kind.ToString().PadRight(7)).Append(' ').Append(l.Text).Append('\n');
            next = l.Sequence;
        }
        sb.Append("next cursor: ").Append(Math.Max(next, pane.Console.Total < since ? since : next));
        return Ok(sb.ToString());
    }

    private async Task<CallToolResult> LogLevelAsync(IDictionary<string, JsonElement>? args)
    {
        if (!TryPane(args, out GatewaySession? pane, out CallToolResult? error))
        {
            return error!;
        }
        int level = Int(args, "level", -1);
        if (level < 0 || level > 5)
        {
            return Error("level must be 0..5.");
        }
        string reply = await pane!.SetLogLevelAsync((LogLevel)level, FrameSource.Mcp).ConfigureAwait(false);
        return pane.LogLevel == (LogLevel)level ? Ok(reply.TrimEnd()) : Error(reply.TrimEnd());
    }

    private async Task<CallToolResult> StatAsync(IDictionary<string, JsonElement>? args)
    {
        if (!TryPane(args, out GatewaySession? pane, out CallToolResult? error))
        {
            return error!;
        }
        if (pane!.Link == null)
        {
            return Error("Pane " + pane.Label + " is not connected.");
        }
        string reply = await pane.SendCommandAsync("STAT", FrameSource.Mcp).ConfigureAwait(false);
        return Ok(reply.TrimEnd());
    }

    private CallToolResult SendFrame(IDictionary<string, JsonElement>? args)
    {
        if (!TryPane(args, out GatewaySession? pane, out CallToolResult? error))
        {
            return error!;
        }
        if (!TryHex(Str(args, "hex"), out byte[] bytes, out string? hexError))
        {
            return Error(hexError!);
        }
        bool addFcs = Bool(args, "add_fcs", true);
        if (bytes.Length < (addFcs ? 2 : 4))
        {
            return Error("A frame needs at least address and control" + (addFcs ? "." : " plus two FCS bytes."));
        }
        if (!pane!.SendRawFrame(bytes, addFcs, FrameSource.Mcp))
        {
            return Error("Pane " + pane.Label + " is not connected.");
        }
        byte[] sent = bytes;
        if (addFcs)
        {
            sent = new byte[bytes.Length + 2];
            bytes.CopyTo(sent, 0);
            Fcs.Append(sent);
        }
        return Ok("Sent " + sent.Length + " bytes on " + pane.EndpointName + ": " + Hex(sent));
    }

    private CallToolResult Frames(IDictionary<string, JsonElement>? args)
    {
        if (!TryPane(args, out GatewaySession? pane, out CallToolResult? error))
        {
            return error!;
        }
        long since = Long(args, "since", 0);
        int max = Int(args, "max", 100);
        List<FrameRecord> frames = pane!.Frames.Since(since, max);
        StringBuilder sb = new StringBuilder();
        long next = since;
        for (int i = 0; i < frames.Count; i++)
        {
            FrameRecord f = frames[i];
            sb.Append(f.Sequence).Append(' ').Append(f.Time.ToString("HH:mm:ss.fff")).Append(' ')
              .Append(f.Sent ? "TX" : "RX").Append(' ').Append(f.Source.ToString().PadRight(7)).Append(' ')
              .Append(f.Summary()).Append(" | ").Append(Hex(f.Bytes)).Append('\n');
            next = f.Sequence;
        }
        sb.Append("next cursor: ").Append(next);
        return Ok(sb.ToString());
    }

    private static CallToolResult Decode(IDictionary<string, JsonElement>? args)
    {
        if (!TryHex(Str(args, "hex"), out byte[] bytes, out string? hexError))
        {
            return Error(hexError!);
        }
        FrameRecord record = new FrameRecord(0, DateTime.Now, false, FrameSource.Console, bytes);
        return Ok(record.Describe() + record.HexDump());
    }

    private CallToolResult Role(IDictionary<string, JsonElement>? args)
    {
        if (!TryPane(args, out GatewaySession? pane, out CallToolResult? error))
        {
            return error!;
        }
        string roleText = Str(args, "role");
        if (roleText.Length == 0)
        {
            return Ok("Pane " + pane!.Label + " role: " + pane.Role + (pane.TcpPort != 0 ? " on TCP port " + pane.TcpPort : ""));
        }
        if (!Enum.TryParse(roleText, true, out GatewayRole role))
        {
            return Error("role must be Console, Free, Echo, Bridge or TcpListen.");
        }
        if (_sessions.TestRunning)
        {
            return Error("A traffic test is running; stop it first.");
        }
        switch (role)
        {
            case GatewayRole.Bridge:
                if (_sessions.A.Link == null || _sessions.B.Link == null)
                {
                    return Error("Bridge needs both panes connected.");
                }
                _sessions.SetBridge(true);
                return Ok("A and B bridged: frames received on one go out on the other.");

            case GatewayRole.TcpListen:
            {
                int port = Int(args, "tcp_port", ReferenceEquals(pane, _sessions.A) ? 1362 : 1363);
                if (pane!.Role == GatewayRole.Bridge)
                {
                    _sessions.SetBridge(false);
                }
                return _sessions.TryStartTcpListen(pane, port, out string? tcpError)
                    ? Ok("Pane " + pane.Label + " listens on TCP port " + port + "; start nd100x with --hdlc=N:<this-pc>:" + port)
                    : Error(tcpError ?? "could not listen");
            }

            default:
                if (pane!.Role == GatewayRole.Bridge)
                {
                    _sessions.SetBridge(false);
                }
                if (pane.Role == GatewayRole.TcpListen)
                {
                    pane.StopTcpListen();
                }
                pane.Role = role;
                return Ok("Pane " + pane.Label + " role: " + role);
        }
    }

    private CallToolResult TestStart(IDictionary<string, JsonElement>? args)
    {
        if (_sessions.TestRunning)
        {
            return Error("A traffic test is already running.");
        }
        bool twoGateways = Bool(args, "two_gateways", false);
        TrafficTestSettings settings = new TrafficTestSettings
        {
            SecondsPerRate = Int(args, "seconds", 120),
            FrameLength = Int(args, "size", 126),
            WindowBytes = Int(args, "window", 3000),
            TimeoutSeconds = Int(args, "timeout", 5),
            NoBaud = Bool(args, "no_baud", false),
            VerboseFrames = Int(args, "verbose", 3),
        };
        string rates = Str(args, "rates");
        if (rates.Length > 0)
        {
            string[] parts = rates.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            uint[] values = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!uint.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]) || values[i] == 0)
                {
                    return Error("rates: '" + parts[i] + "' is not a bit rate.");
                }
            }
            settings.Rates = values;
        }
        if (settings.FrameLength < TestFrame.MinLength || settings.FrameLength > ByteFrame.MaxFrameLength)
        {
            return Error("size must be " + TestFrame.MinLength + ".." + ByteFrame.MaxFrameLength + ".");
        }
        _recorder.Clear();
        try
        {
            _test = _sessions.RunTrafficTestAsync(settings, _recorder, twoGateways);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
        return Ok("Traffic test started: " + (twoGateways ? "A->B and B->A" : "loopback on A") + ", rates "
                  + string.Join(",", settings.Rates) + ", " + settings.SecondsPerRate + " s per rate, frames of "
                  + settings.FrameLength + " bytes. Poll hdlc_test_status.");
    }

    private CallToolResult TestStatus()
    {
        StringBuilder sb = new StringBuilder();
        Task<bool>? test = _test;
        if (test == null)
        {
            sb.Append("No traffic test has run.\n");
        }
        else if (!test.IsCompleted)
        {
            sb.Append("RUNNING\n");
        }
        else if (test.IsFaulted)
        {
            sb.Append("FAILED: ").Append(test.Exception?.GetBaseException().Message).Append('\n');
        }
        else
        {
            sb.Append(test.Result ? "FINISHED: PASS\n" : "FINISHED: FAIL\n");
        }
        string status = _recorder.StatusLine;
        if (status.Length > 0)
        {
            sb.Append(status).Append('\n');
        }
        List<string> messages = _recorder.Messages;
        for (int i = 0; i < messages.Count; i++)
        {
            sb.Append(messages[i]).Append('\n');
        }
        List<RateResult> results = _recorder.Results;
        if (results.Count > 0)
        {
            sb.Append(TrafficTestRecorder.FormatReport(results));
        }
        return Ok(sb.ToString().TrimEnd());
    }

    private CallToolResult TestStop()
    {
        if (!_sessions.TestRunning)
        {
            return Ok("No traffic test is running.");
        }
        _sessions.StopTrafficTest();
        return Ok("Stop requested; the current rate drains first. Poll hdlc_test_status.");
    }

    private static string DescribePane(GatewaySession pane)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("Pane ").Append(pane.Label).Append(": ").Append(pane.State);
        if (pane.EndpointName.Length > 0)
        {
            sb.Append(" on ").Append(pane.EndpointName);
        }
        if (pane.Identity != null)
        {
            sb.Append(", ").Append(pane.Identity.Name).Append(' ').Append(pane.Identity.Version);
        }
        sb.Append(", role ").Append(pane.Role);
        if (pane.TcpPort != 0)
        {
            sb.Append(", TCP port ").Append(pane.TcpPort).Append(pane.TcpPeer != null ? " peer " + pane.TcpPeer : " no peer");
        }
        if (pane.InTrafficTest)
        {
            sb.Append(", in traffic test");
        }
        return sb.ToString();
    }

    private bool TryPane(IDictionary<string, JsonElement>? args, out GatewaySession? pane, out CallToolResult? error)
    {
        pane = _sessions.ByLabel(Str(args, "pane"));
        error = pane == null ? Error("pane must be A or B.") : null;
        return pane != null;
    }

    private static bool TryHex(string text, out byte[] bytes, out string? error)
    {
        List<byte> list = new List<byte>();
        int nibbles = 0;
        int value = 0;
        string s = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Replace("0X", "");
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == ' ' || c == ',' || c == '\t' || c == '\r' || c == '\n' || c == ':')
            {
                continue;
            }
            int d = c >= '0' && c <= '9' ? c - '0'
                  : c >= 'a' && c <= 'f' ? c - 'a' + 10
                  : c >= 'A' && c <= 'F' ? c - 'A' + 10
                  : -1;
            if (d < 0)
            {
                bytes = Array.Empty<byte>();
                error = "hex: '" + c + "' is not a hex digit.";
                return false;
            }
            value = (value << 4) | d;
            if (++nibbles == 2)
            {
                list.Add((byte)value);
                nibbles = 0;
                value = 0;
            }
        }
        if (nibbles != 0)
        {
            bytes = Array.Empty<byte>();
            error = "hex: odd number of digits.";
            return false;
        }
        bytes = list.ToArray();
        error = null;
        return true;
    }

    private static string Hex(byte[] bytes)
    {
        StringBuilder sb = new StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }

    private static string Str(IDictionary<string, JsonElement>? args, string name)
    {
        if (args != null && args.TryGetValue(name, out JsonElement e))
        {
            return e.ValueKind == JsonValueKind.String ? (e.GetString() ?? string.Empty) : e.ToString();
        }
        return string.Empty;
    }

    private static int Int(IDictionary<string, JsonElement>? args, string name, int fallback)
    {
        if (args != null && args.TryGetValue(name, out JsonElement e))
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int n))
            {
                return n;
            }
            if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out int m))
            {
                return m;
            }
        }
        return fallback;
    }

    private static long Long(IDictionary<string, JsonElement>? args, string name, long fallback)
    {
        if (args != null && args.TryGetValue(name, out JsonElement e))
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long n))
            {
                return n;
            }
            if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out long m))
            {
                return m;
            }
        }
        return fallback;
    }

    private static bool Bool(IDictionary<string, JsonElement>? args, string name, bool fallback)
    {
        if (args != null && args.TryGetValue(name, out JsonElement e))
        {
            if (e.ValueKind == JsonValueKind.True)
            {
                return true;
            }
            if (e.ValueKind == JsonValueKind.False)
            {
                return false;
            }
            if (e.ValueKind == JsonValueKind.String && bool.TryParse(e.GetString(), out bool b))
            {
                return b;
            }
        }
        return fallback;
    }

    private static string DescribeArguments(IDictionary<string, JsonElement>? args)
    {
        if (args == null || args.Count == 0)
        {
            return string.Empty;
        }
        StringBuilder sb = new StringBuilder();
        IEnumerator<KeyValuePair<string, JsonElement>> e = args.GetEnumerator();
        while (e.MoveNext())
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(e.Current.Key).Append('=').Append(e.Current.Value.ToString());
        }
        return sb.ToString();
    }

    private static string FirstText(CallToolResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentBlock text)
        {
            return text.Text;
        }
        return string.Empty;
    }

    private static JsonElement Schema(params (string Name, string Type, bool Required, string Description)[] parameters)
    {
        StringBuilder sb = new StringBuilder(256);
        sb.Append("{\"type\":\"object\",\"properties\":{");
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(JsonSerializer.Serialize(parameters[i].Name))
              .Append(":{\"type\":\"").Append(parameters[i].Type).Append("\",\"description\":")
              .Append(JsonSerializer.Serialize(parameters[i].Description))
              .Append('}');
        }
        sb.Append('}');
        bool any = false;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].Required)
            {
                sb.Append(any ? "," : ",\"required\":[");
                sb.Append(JsonSerializer.Serialize(parameters[i].Name));
                any = true;
            }
        }
        if (any)
        {
            sb.Append(']');
        }
        sb.Append('}');
        using JsonDocument doc = JsonDocument.Parse(sb.ToString());
        return doc.RootElement.Clone();
    }

    private static CallToolResult Ok(string text)
    {
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = text } },
            IsError = false,
        };
    }

    private static CallToolResult Error(string text)
    {
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = text } },
            IsError = true,
        };
    }
}
