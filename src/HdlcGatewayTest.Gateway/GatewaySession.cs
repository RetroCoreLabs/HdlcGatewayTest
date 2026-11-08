// GATEWAY SESSION - one pane: a gateway, its role, its frame log and its console log.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Text;
using HdlcGatewayTest.Core;

namespace HdlcGatewayTest.Gateway;

/// <summary>
/// Everything one pane of the GUI (and one gateway for MCP or the CLI) needs: connect a
/// transport, probe how to command the firmware, run commands and show replies and log lines in
/// a <see cref="ConsoleLog"/>, list every frame in a <see cref="FrameLog"/>, and act on the
/// pane's <see cref="GatewayRole"/>: echo, bridge to the peer pane, or forward to and from a TCP
/// peer. The session owns no thread; <see cref="GatewaySessionSet"/> pumps it.
/// </summary>
public sealed class GatewaySession : IDisposable
{
    private readonly object _pumpLock = new object();
    private readonly object _sendLock = new object();
    private readonly byte[] _tcpRx = new byte[8192];
    private GatewayLink? _link;
    private TcpListenerTransport? _tcp;
    private GatewayChannel? _tcpChannel;
    private FrameSource _sendSource = FrameSource.Console;
    private FrameSource _tcpSendSource = FrameSource.Serial;
    /* How frames on the pane's own link are labelled: Serial for a COM port, Tcp for a
     * host:port endpoint (seen in the bench 2026-11-08: a TCP pane listed its frames as "Serial"). */
    private FrameSource _linkSource = FrameSource.Serial;
    private GatewaySessionState _state = GatewaySessionState.Disconnected;
    private GatewayRole _role = GatewayRole.Console;
    private volatile bool _inTrafficTest;

    /// <summary>
    /// Create a session.
    /// </summary>
    /// <param name="label">Pane label, "A" or "B".</param>
    public GatewaySession(string label)
    {
        Label = label;
    }

    /// <summary>Pane label, "A" or "B".</summary>
    public string Label { get; }

    /// <summary>Connection state.</summary>
    public GatewaySessionState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this);
            }
        }
    }

    /// <summary>Raised when <see cref="State"/>, <see cref="Role"/> or the TCP peer changes.</summary>
    public event Action<GatewaySession>? StateChanged;

    /// <summary>The pane's job.</summary>
    public GatewayRole Role
    {
        get => _role;
        set
        {
            if (_role != value)
            {
                _role = value;
                Console.Add(ConsoleLineKind.Notice, "Role: " + value, FrameSource.Console);
                StateChanged?.Invoke(this);
            }
        }
    }

    /// <summary>The other pane, for the Bridge role; set by <see cref="GatewaySessionSet"/>.</summary>
    public GatewaySession? Peer { get; internal set; }

    /// <summary>The link while connected, else null.</summary>
    public GatewayLink? Link => _link;

    /// <summary>Transport name while connected ("COM10"), else empty.</summary>
    public string EndpointName => _link?.Name ?? string.Empty;

    /// <summary>What the firmware said to HELLO, when it did.</summary>
    public GatewayIdentity? Identity => _link?.Identity;

    /// <summary>Every frame in and out of this pane.</summary>
    public FrameLog Frames { get; } = new FrameLog();

    /// <summary>The console lines.</summary>
    public ConsoleLog Console { get; } = new ConsoleLog();

    /// <summary>The last STAT frame the firmware pushed, or null.</summary>
    public GatewayStatistics? LatestStatistics { get; private set; }

    /// <summary>Firmware log level the pane last set.</summary>
    public LogLevel LogLevel { get; private set; } = LogLevel.Off;

    /// <summary>TCP port the pane listens on in the TcpListen role, 0 when not listening.</summary>
    public int TcpPort => _tcp?.Port ?? 0;

    /// <summary>The TCP peer's address while one is connected.</summary>
    public string? TcpPeer => _tcp?.PeerName;

    /// <summary>True while the traffic test owns this session's link; the pump skips it then.</summary>
    public bool InTrafficTest
    {
        get => _inTrafficTest;
        internal set => _inTrafficTest = value;
    }

    /// <summary>
    /// Open a transport, probe the firmware and start listening. Blocks for the probe (up to
    /// about four seconds for a silent gateway); call it off the UI thread.
    /// </summary>
    /// <param name="transport">The transport to use; owned by the session from now on.</param>
    /// <param name="tryConsole">
    /// Default false: HELLO only, never an ESC byte (the firmware is driven with command frames,
    /// Ronny 2026-11-08). True also tries the ESC ESC ESC text console first, which a
    /// text-console-only gateway (RC422Dev) needs.
    /// </param>
    public void Connect(IGatewayTransport transport, bool tryConsole = false)
    {
        Disconnect();
        State = GatewaySessionState.Connecting;
        GatewayLink link = new GatewayLink(transport);
        try
        {
            link.Open();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
        {
            link.Dispose();
            Console.Add(ConsoleLineKind.Notice, "Could not open " + transport.Name + ": " + ex.Message, FrameSource.Console);
            State = GatewaySessionState.Faulted;
            return;
        }
        transport.Faulted += OnTransportFaulted;
        _linkSource = transport.FramesOnly ? FrameSource.Tcp : FrameSource.Serial;
        Console.Add(ConsoleLineKind.Notice, "Opened " + transport.Name + (transport.FramesOnly ? "." : ", probing..."),
                    FrameSource.Console);
        State = transport.FramesOnly ? GatewaySessionState.Connecting : GatewaySessionState.Probing;

        link.Channel.FrameObserved = OnSerialFrameObserved;
        link.Channel.TextReceived = OnSerialText;
        link.Console.LogReceived = OnFirmwareLog;
        link.Console.EventReceived = OnFirmwareEvent;
        link.Console.StatisticsReceived = s => LatestStatistics = s;

        if (transport.FramesOnly)
        {
            // No HELLO: it would be clocked onto the line as a junk frame. Frames in and out only.
            lock (_pumpLock)
            {
                _link = link;
            }
            AttachFrameHandlers();
            Console.Add(ConsoleLineKind.Notice, transport.Name + " carries HDLC frames only (a gateway over TCP, or "
                        + "nd100x); commands to a gateway go through its USB pane.", FrameSource.Console);
            State = GatewaySessionState.FramesOnly;
            return;
        }

        GatewayCommandPath path = link.Probe(500, out string transcript, tryConsole);
        lock (_pumpLock)
        {
            _link = link;
        }
        AttachFrameHandlers();
        switch (path)
        {
            case GatewayCommandPath.ControlFrames:
                Console.Add(ConsoleLineKind.Notice, link.Identity!.Name + " " + link.Identity.Version
                            + " (" + link.Identity.Build + "): control frames.", FrameSource.Console);
                State = GatewaySessionState.ControlFrames;
                break;
            case GatewayCommandPath.TextConsole:
                Console.Add(ConsoleLineKind.Notice, "No HELLO reply: legacy firmware, using the ESC ESC ESC text console. "
                            + "Each probe frame went onto the line and counts as one bad frame there.", FrameSource.Console);
                State = GatewaySessionState.TextConsole;
                break;
            default:
                Console.Add(ConsoleLineKind.Notice, "No gateway answered on " + transport.Name
                            + " (neither HELLO nor the text console). Raw terminal only. Got: "
                            + TrafficTest.Printable(transcript), FrameSource.Console);
                State = GatewaySessionState.Unknown;
                break;
        }
    }

    /// <summary>Close the transport and the TCP listener, if any.</summary>
    public void Disconnect()
    {
        GatewayLink? link;
        lock (_pumpLock)
        {
            link = _link;
            _link = null;
        }
        if (link != null)
        {
            link.Transport.Faulted -= OnTransportFaulted;
            link.Dispose();
            Console.Add(ConsoleLineKind.Notice, "Closed " + link.Name + ".", FrameSource.Console);
        }
        StopTcpListen();
        LatestStatistics = null;
        LogLevel = LogLevel.Off;
        State = GatewaySessionState.Disconnected;
    }

    /// <summary>
    /// Move received bytes into the channels and act on them. Called by the set's pump thread;
    /// does nothing while the traffic test owns the link.
    /// </summary>
    /// <returns>Bytes moved.</returns>
    public int Pump()
    {
        if (_inTrafficTest)
        {
            return 0;
        }
        lock (_pumpLock)
        {
            int n = _link?.Pump() ?? 0;
            if (_tcp != null && _tcpChannel != null)
            {
                // Bounded like GatewayLink.Pump, for the same reason.
                for (int rounds = 0; rounds < 8; rounds++)
                {
                    int m = _tcp.Read(_tcpRx);
                    if (m <= 0)
                    {
                        break;
                    }
                    _tcpChannel.Feed(new ReadOnlySpan<byte>(_tcpRx, 0, m));
                    n += m;
                }
            }
            return n;
        }
    }

    /// <summary>
    /// Run one console command and put the command and its reply in the console log.
    /// Control-frame path: the pump thread delivers the reply, so this only waits. Text-console
    /// path: takes the pump lock and drives the console itself (frames cannot flow meanwhile,
    /// as on the firmware side).
    /// </summary>
    /// <param name="command">The command line.</param>
    /// <param name="source">Who asked: Console or Mcp.</param>
    /// <param name="timeoutMilliseconds">How long to wait for the end of the reply.</param>
    /// <returns>The reply text, or a notice when it failed.</returns>
    public async Task<string> SendCommandAsync(string command, FrameSource source, int timeoutMilliseconds = 2000)
    {
        GatewayLink? link = _link;
        Console.Add(ConsoleLineKind.Command, command, source);
        if (link == null)
        {
            return Notice("Not connected: pick the port and press Connect first.");
        }
        switch (State)
        {
            case GatewaySessionState.ControlFrames:
            {
                lock (_sendLock)
                {
                    _sendSource = source;
                }
                ControlRequest request = link.Console.SendCommand(command);
                Task finished = await Task.WhenAny(request.Completion, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false);
                if (!ReferenceEquals(finished, request.Completion))
                {
                    request.MarkTimedOut();
                }
                string text = request.Text;
                Console.AddLines(ConsoleLineKind.Reply, text, FrameSource.Serial);
                switch (request.State)
                {
                    case ControlRequestState.Completed:
                        return text;
                    case ControlRequestState.Failed:
                        return text.Length > 0 ? text : Notice("The firmware refused the command"
                            + (request.Nak.HasValue ? " (" + request.Nak.Value + ")" : "") + ".");
                    case ControlRequestState.Incomplete:
                        return text + Notice("(a reply chunk was lost on the USB path)");
                    default:
                        return text + Notice("No reply within " + timeoutMilliseconds + " ms.");
                }
            }

            case GatewaySessionState.FramesOnly:
                return Notice("This endpoint carries frames only; send the command on the gateway's USB pane.");

            case GatewaySessionState.TextConsole:
            case GatewaySessionState.Unknown:
            {
                string reply = await Task.Run(() =>
                {
                    lock (_pumpLock)
                    {
                        if (State == GatewaySessionState.Unknown)
                        {
                            // Raw terminal: send text plus CR, show whatever comes back for a moment.
                            link.Text.WriteLine(command);
                            return link.Text.ReadTextFor(500);
                        }
                        bool ok = link.RunCommand(command, out string text, timeoutMilliseconds);
                        return ok ? text : text + "\r\n(no prompt followed)";
                    }
                }).ConfigureAwait(false);
                Console.AddLines(ConsoleLineKind.Reply, reply, FrameSource.Serial);
                return reply;
            }

            default:
                return Notice("Not connected.");
        }
    }

    /// <summary>
    /// Set the firmware's log level (control-frame path only; the text console has a LOG command
    /// too but the lines only reach a host as frames).
    /// </summary>
    /// <param name="level">The level.</param>
    /// <param name="source">Who asked.</param>
    /// <returns>The firmware's answer.</returns>
    public async Task<string> SetLogLevelAsync(LogLevel level, FrameSource source)
    {
        GatewayLink? link = _link;
        if (link == null || State != GatewaySessionState.ControlFrames)
        {
            return Notice("Log frames need a connected gateway with control frames.");
        }
        ControlRequest request = link.Console.SetLogLevel(level);
        await Task.WhenAny(request.Completion, Task.Delay(2000)).ConfigureAwait(false);
        if (request.State == ControlRequestState.Completed)
        {
            LogLevel = level;
            Console.Add(ConsoleLineKind.Notice, "Firmware log level " + level + ".", source);
            StateChanged?.Invoke(this);
            return request.Text;
        }
        request.MarkTimedOut();
        return Notice("The firmware did not confirm the log level (" + request.State + ").");
    }

    /// <summary>
    /// Send a raw frame on the serial port, as pasted in the Free role or from MCP.
    /// </summary>
    /// <param name="frame">Address, control, data, and the FCS unless <paramref name="addFcs"/>.</param>
    /// <param name="addFcs">Compute and append the FCS first.</param>
    /// <param name="source">Who asked.</param>
    /// <returns>True when sent; false when not connected.</returns>
    public bool SendRawFrame(ReadOnlySpan<byte> frame, bool addFcs, FrameSource source)
    {
        GatewayLink? link = _link;
        if (link == null)
        {
            return false;
        }
        if (addFcs)
        {
            byte[] withFcs = new byte[frame.Length + 2];
            frame.CopyTo(withFcs);
            Fcs.Append(withFcs);
            SendSerial(link, withFcs, source);
        }
        else
        {
            SendSerial(link, frame, source);
        }
        return true;
    }

    /// <summary>
    /// Start listening on a TCP port for a peer (nd100x) and switch to the TcpListen role.
    /// </summary>
    /// <param name="port">The port.</param>
    /// <param name="error">Why it failed, or null.</param>
    /// <returns>True when listening.</returns>
    public bool StartTcpListen(int port, out string? error)
    {
        error = null;
        StopTcpListen();
        TcpListenerTransport tcp = new TcpListenerTransport(port);
        try
        {
            tcp.Open();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            error = "Cannot listen on port " + port + ": " + ex.Message;
            Console.Add(ConsoleLineKind.Notice, error, FrameSource.Console);
            tcp.Dispose();
            return false;
        }
        GatewayChannel channel = new GatewayChannel(tcp.Write);
        channel.FrameObserved = OnTcpFrameObserved;
        channel.DataFrameReceived = OnTcpDataFrame;
        channel.BadFrameReceived = f => Console.Add(ConsoleLineKind.Notice,
            "TCP peer sent a frame with a bad FCS (" + f.Length + " bytes), dropped.", FrameSource.Tcp);
        tcp.PeerChanged += OnTcpPeerChanged;
        lock (_pumpLock)
        {
            _tcp = tcp;
            _tcpChannel = channel;
        }
        Console.Add(ConsoleLineKind.Notice, "Listening on TCP port " + port + " for nd100x (--hdlc=N:<this-pc>:" + port + ").",
                    FrameSource.Console);
        Role = GatewayRole.TcpListen;
        StateChanged?.Invoke(this);
        return true;
    }

    /// <summary>Stop the TCP listener; the role falls back to Console if it was TcpListen.</summary>
    public void StopTcpListen()
    {
        TcpListenerTransport? tcp;
        lock (_pumpLock)
        {
            tcp = _tcp;
            _tcp = null;
            _tcpChannel = null;
        }
        if (tcp != null)
        {
            tcp.PeerChanged -= OnTcpPeerChanged;
            tcp.Dispose();
            Console.Add(ConsoleLineKind.Notice, "Stopped listening on TCP port " + tcp.Port + ".", FrameSource.Console);
            if (Role == GatewayRole.TcpListen)
            {
                Role = GatewayRole.Console;
            }
        }
    }

    /// <summary>
    /// Return once no <see cref="Pump"/> is in progress. The traffic test calls it after setting
    /// <see cref="InTrafficTest"/>: the flag stops the NEXT pump, but a pump already inside the
    /// link would otherwise run alongside the engine's own pumping of the same link and feed one
    /// byte-frame decoder from two threads (the test-host crash of 2026-11-08).
    /// </summary>
    internal void WaitPumpIdle()
    {
        lock (_pumpLock)
        {
            // Taking the lock is the wait; nothing to do while holding it.
        }
    }

    /// <summary>
    /// Put this session's own handlers back on the serial channel. The traffic test replaces
    /// them while it runs.
    /// </summary>
    internal void AttachFrameHandlers()
    {
        GatewayLink? link = _link;
        if (link == null)
        {
            return;
        }
        link.Channel.DataFrameReceived = OnSerialDataFrame;
        link.Channel.BadFrameReceived = OnSerialBadFrame;
    }

    private string Notice(string text)
    {
        Console.Add(ConsoleLineKind.Notice, text, FrameSource.Console);
        return text;
    }

    private void SendSerial(GatewayLink link, ReadOnlySpan<byte> frame, FrameSource source)
    {
        lock (_sendLock)
        {
            _sendSource = source;
            link.Channel.Send(frame);
        }
    }

    private void OnSerialFrameObserved(bool sent, ReadOnlySpan<byte> frame)
    {
        FrameSource source;
        if (sent)
        {
            lock (_sendLock)
            {
                source = _sendSource;
            }
        }
        else
        {
            source = _linkSource;
        }
        Frames.Add(sent, source, frame);
    }

    private void OnTcpFrameObserved(bool sent, ReadOnlySpan<byte> frame)
    {
        Frames.Add(sent, sent ? _tcpSendSource : FrameSource.Tcp, frame);
    }

    private void OnSerialDataFrame(ReadOnlySpan<byte> frame)
    {
        RouteReceived(FrameSource.Serial, frame);
    }

    private void OnSerialBadFrame(ReadOnlySpan<byte> frame)
    {
        // Listed by the observer already; nothing is forwarded.
    }

    private void OnTcpDataFrame(ReadOnlySpan<byte> frame)
    {
        RouteReceived(FrameSource.Tcp, frame);
    }

    private void RouteReceived(FrameSource from, ReadOnlySpan<byte> frame)
    {
        FrameDestinations to = FrameRouter.Route(Role, from);
        if ((to & FrameDestinations.OwnSerial) != 0 && _link != null)
        {
            SendSerial(_link, frame, from == FrameSource.Tcp ? FrameSource.Tcp : FrameSource.Echo);
        }
        if ((to & FrameDestinations.PeerSerial) != 0 && Peer?._link != null)
        {
            Peer.SendSerial(Peer._link, frame, FrameSource.Bridge);
        }
        if ((to & FrameDestinations.OwnTcp) != 0 && _tcpChannel != null)
        {
            _tcpSendSource = FrameSource.Serial;
            _tcpChannel.Send(frame);
        }
    }

    private void OnSerialText(ReadOnlySpan<byte> bytes)
    {
        Console.AddLines(ConsoleLineKind.RawText, Encoding.Latin1.GetString(bytes), FrameSource.Serial);
    }

    private void OnFirmwareLog(LogLevel level, uint milliseconds, string text)
    {
        Console.Add(ConsoleLineKind.Log, "[" + level + " " + (milliseconds / 1000.0).ToString("F3") + "s] " + text,
                    FrameSource.Serial);
    }

    private void OnFirmwareEvent(GatewayEvent code, string text)
    {
        Console.Add(ConsoleLineKind.Event, code + (text.Length > 0 ? ": " + text : string.Empty), FrameSource.Serial);
    }

    private void OnTcpPeerChanged(string? peer)
    {
        Console.Add(ConsoleLineKind.Notice, peer == null ? "TCP peer disconnected." : "TCP peer connected: " + peer,
                    FrameSource.Tcp);
        StateChanged?.Invoke(this);
    }

    private void OnTransportFaulted(Exception ex)
    {
        Console.Add(ConsoleLineKind.Notice, "Port failed: " + ex.Message, FrameSource.Console);
        State = GatewaySessionState.Faulted;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Disconnect();
    }
}
