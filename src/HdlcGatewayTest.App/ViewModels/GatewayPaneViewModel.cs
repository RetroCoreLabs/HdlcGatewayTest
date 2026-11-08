// GATEWAY PANE VIEW MODEL - what one pane shows and does; no Avalonia controls in here.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Collections.ObjectModel;
using HdlcGatewayTest.App.Services;
using HdlcGatewayTest.Core;
using HdlcGatewayTest.Gateway;

namespace HdlcGatewayTest.App.ViewModels;

/// <summary>
/// One pane: endpoint picker and connect, state badge, role, line rate, firmware log level, the
/// console (lines, command box with history), the hex box of the Free role, the frame list with
/// a detail panel, and the TCP listen port. Everything comes from a <see cref="GatewaySession"/>;
/// the session's events are marshalled to the UI thread through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed class GatewayPaneViewModel : ObservableObject
{
    private const int MaxConsoleLines = 5000;
    private const int MaxFrameRows = 2000;

    private readonly GatewaySessionSet _set;
    private readonly IGatewayTransportFactory _transports;
    private readonly IUiDispatcher _ui;
    private readonly List<string> _history = new List<string>();
    private int _historyIndex;
    private string? _selectedEndpoint;
    private string _stateText = "Disconnected";
    private string _identityText = string.Empty;
    private string _selectedRole = nameof(GatewayRole.Console);
    private string _selectedBaud = "38400";
    private string _selectedLogLevel = nameof(LogLevel.Off);
    private string _commandText = string.Empty;
    private string _hexText = string.Empty;
    private bool _addFcs = true;
    private int _tcpPort;
    private string _tcpText = string.Empty;
    private FrameRecordViewModel? _selectedFrame;
    private string _frameDetail = "Click a frame to see it decoded.";
    private string _frameFilter = string.Empty;
    private bool _busy;
    private bool _autoScroll = true;
    private bool _useTextConsole;

    /// <summary>
    /// Create a pane view model.
    /// </summary>
    /// <param name="set">The session set.</param>
    /// <param name="session">This pane's session.</param>
    /// <param name="transports">How to open endpoints.</param>
    /// <param name="ui">UI thread.</param>
    /// <param name="tcpPort">Remembered TCP listen port.</param>
    public GatewayPaneViewModel(GatewaySessionSet set, GatewaySession session, IGatewayTransportFactory transports,
                                IUiDispatcher ui, int tcpPort)
    {
        _set = set;
        Session = session;
        _transports = transports;
        _ui = ui;
        _tcpPort = tcpPort;

        ConnectCommand = new RelayCommand(Connect, () => !IsConnected && !_busy && SelectedEndpoint != null);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected && !_busy);
        RefreshPortsCommand = new RelayCommand(RefreshEndpoints);
        // Enabled whenever there is text: on a disconnected pane the session answers with the
        // notice "Not connected." instead of silence (Ronny typed STAT into a closed pane and saw
        // nothing, 2026-11-08).
        SendCommand = new RelayCommand(() => _ = SendCommandAsync(), () => CommandText.Length > 0);
        SendHexCommand = new RelayCommand(SendHex, () => IsConnected && HexText.Trim().Length > 0);
        SetBaudCommand = new RelayCommand(() => _ = RunAsync("BAUD " + SelectedBaud.Trim()), () => IsConnected);
        ClearConsoleCommand = new RelayCommand(() => ConsoleLines.Clear());
        ClearFramesCommand = new RelayCommand(() => { Frames.Clear(); Session.Frames.Clear(); });
        StopTcpCommand = new RelayCommand(() => Session.StopTcpListen(), () => Session.TcpPort != 0);

        session.Console.Added += line => _ui.Post(() => AddConsoleLine(line));
        session.Frames.Added += record => _ui.Post(() => AddFrame(record));
        session.StateChanged += _ => _ui.Post(RefreshState);
        RefreshEndpoints();
    }

    /// <summary>The session behind the pane.</summary>
    public GatewaySession Session { get; }

    /// <summary>"A" or "B".</summary>
    public string Label => Session.Label;

    /// <summary>Pane title.</summary>
    public string Title => "Port " + Label;

    /// <summary>COM ports (and later host:port) to pick from.</summary>
    public ObservableCollection<string> Endpoints { get; } = new ObservableCollection<string>();

    /// <summary>The chosen endpoint.</summary>
    public string? SelectedEndpoint
    {
        get => _selectedEndpoint;
        set
        {
            if (SetProperty(ref _selectedEndpoint, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Connected in any way.</summary>
    public bool IsConnected => Session.Link != null && Session.State != GatewaySessionState.Disconnected
                               && Session.State != GatewaySessionState.Faulted;

    /// <summary>State badge text.</summary>
    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    /// <summary>Firmware name and version, or what the probe found.</summary>
    public string IdentityText
    {
        get => _identityText;
        private set => SetProperty(ref _identityText, value);
    }

    /// <summary>True on the control-frame path: framed commands are available.</summary>
    public bool HasControlFrames => Session.State == GatewaySessionState.ControlFrames;

    /// <summary>
    /// True when the firmware can send log frames: control frames AND "log" among its HELLO
    /// capabilities (RC422Dev 0.2.0 has control frames but no log source yet).
    /// </summary>
    public bool HasFirmwareLog => HasControlFrames && Session.Identity != null && Session.Identity.Has("log");

    /// <summary>Role names for the picker.</summary>
    public string[] Roles { get; } = Enum.GetNames<GatewayRole>();

    /// <summary>The pane's role; setting it acts on the session set.</summary>
    public string SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (!SetProperty(ref _selectedRole, value) || !Enum.TryParse(value, out GatewayRole role))
            {
                return;
            }
            if (role == Session.Role)
            {
                return;
            }
            ApplyRole(role);
        }
    }

    /// <summary>Line rates for the picker; the box is editable.</summary>
    public string[] BaudRates { get; } = { "9600", "19200", "38400", "57600", "115200", "307200", "614400", "983000" };

    /// <summary>The rate to set with BAUD.</summary>
    public string SelectedBaud
    {
        get => _selectedBaud;
        set => SetProperty(ref _selectedBaud, value);
    }

    /// <summary>Log level names.</summary>
    public string[] LogLevels { get; } = Enum.GetNames<LogLevel>();

    /// <summary>Firmware log level; setting it sends LOG_LEVEL.</summary>
    public string SelectedLogLevel
    {
        get => _selectedLogLevel;
        set
        {
            if (SetProperty(ref _selectedLogLevel, value) && Enum.TryParse(value, out LogLevel level)
                && level != Session.LogLevel && IsConnected)
            {
                _ = Session.SetLogLevelAsync(level, FrameSource.Console);
            }
        }
    }

    /// <summary>The console lines shown.</summary>
    public ObservableCollection<ConsoleLineViewModel> ConsoleLines { get; } = new ObservableCollection<ConsoleLineViewModel>();

    /// <summary>The command being typed.</summary>
    public string CommandText
    {
        get => _commandText;
        set
        {
            if (SetProperty(ref _commandText, value))
            {
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Hex to send in the Free role.</summary>
    public string HexText
    {
        get => _hexText;
        set
        {
            if (SetProperty(ref _hexText, value))
            {
                SendHexCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Append the FCS to pasted hex.</summary>
    public bool AddFcs
    {
        get => _addFcs;
        set => SetProperty(ref _addFcs, value);
    }

    /// <summary>True in the Free role: the hex box is shown.</summary>
    public bool ShowHexBox => Session.Role == GatewayRole.Free;

    /// <summary>True in the TcpListen role: the port box is shown.</summary>
    public bool ShowTcpBox => Session.Role == GatewayRole.TcpListen;

    /// <summary>TCP listen port.</summary>
    public int TcpPort
    {
        get => _tcpPort;
        set => SetProperty(ref _tcpPort, value);
    }

    /// <summary>"listening on 1362, peer 172.24.50.23:4711" or empty.</summary>
    public string TcpText
    {
        get => _tcpText;
        private set => SetProperty(ref _tcpText, value);
    }

    /// <summary>Frame rows that pass <see cref="FrameFilter"/>.</summary>
    public ObservableCollection<FrameRecordViewModel> Frames { get; } = new ObservableCollection<FrameRecordViewModel>();

    /// <summary>
    /// Text a row must contain (case-insensitive) to be shown: "RX", "TX", "addr 01", "SABM",
    /// "FCS BAD", a source name. Empty shows everything. Changing it rebuilds the list from the
    /// session's frame log.
    /// </summary>
    public string FrameFilter
    {
        get => _frameFilter;
        set
        {
            if (SetProperty(ref _frameFilter, value ?? string.Empty))
            {
                RebuildFrames();
            }
        }
    }

    /// <summary>The clicked frame.</summary>
    public FrameRecordViewModel? SelectedFrame
    {
        get => _selectedFrame;
        set
        {
            if (SetProperty(ref _selectedFrame, value))
            {
                FrameDetail = value?.Detail ?? "Click a frame to see it decoded.";
            }
        }
    }

    /// <summary>The detail panel text.</summary>
    public string FrameDetail
    {
        get => _frameDetail;
        private set => SetProperty(ref _frameDetail, value);
    }

    /// <summary>
    /// Tick for a legacy gateway (RC422Dev): the probe then also tries the ESC ESC ESC text
    /// console. Off by default: HELLO only, command frames only, never an ESC byte.
    /// </summary>
    public bool UseTextConsole
    {
        get => _useTextConsole;
        set => SetProperty(ref _useTextConsole, value);
    }

    /// <summary>Follow new lines; false once the user scrolled up.</summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    /// <summary>Raised after a console line was added, for the view to scroll.</summary>
    public event Action? ConsoleLineAdded;

    /// <summary>Connect to the selected endpoint.</summary>
    public RelayCommand ConnectCommand { get; }

    /// <summary>Disconnect.</summary>
    public RelayCommand DisconnectCommand { get; }

    /// <summary>Re-read the port list.</summary>
    public RelayCommand RefreshPortsCommand { get; }

    /// <summary>Send the typed command.</summary>
    public RelayCommand SendCommand { get; }

    /// <summary>Send the pasted hex as a frame.</summary>
    public RelayCommand SendHexCommand { get; }

    /// <summary>Send BAUD with the selected rate.</summary>
    public RelayCommand SetBaudCommand { get; }

    /// <summary>Empty the console view.</summary>
    public RelayCommand ClearConsoleCommand { get; }

    /// <summary>Empty the frame list.</summary>
    public RelayCommand ClearFramesCommand { get; }

    /// <summary>Stop the TCP listener.</summary>
    public RelayCommand StopTcpCommand { get; }

    /// <summary>Previous command into the box (Up arrow).</summary>
    public void HistoryUp()
    {
        if (_history.Count == 0)
        {
            return;
        }
        _historyIndex = Math.Max(0, _historyIndex - 1);
        CommandText = _history[_historyIndex];
    }

    /// <summary>Next command into the box (Down arrow); past the newest clears it.</summary>
    public void HistoryDown()
    {
        if (_history.Count == 0)
        {
            return;
        }
        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        CommandText = _historyIndex == _history.Count ? string.Empty : _history[_historyIndex];
    }

    /// <summary>Re-read the endpoints, keeping the selection when it still exists.</summary>
    public void RefreshEndpoints()
    {
        string? keep = SelectedEndpoint;
        Endpoints.Clear();
        string[] names = _transports.ListEndpoints();
        for (int i = 0; i < names.Length; i++)
        {
            Endpoints.Add(names[i]);
        }
        if (keep != null && Endpoints.Contains(keep))
        {
            SelectedEndpoint = keep;
        }
        else if (SelectedEndpoint == null && Endpoints.Count > 0)
        {
            SelectedEndpoint = Endpoints[0];
        }
    }

    /// <summary>Run a command line on the gateway, as typed in the console.</summary>
    /// <param name="command">The line.</param>
    public Task RunAsync(string command)
    {
        return Session.SendCommandAsync(command, FrameSource.Console);
    }

    private void Connect()
    {
        string? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }
        SetBusy(true);
        Task.Run(() =>
        {
            try
            {
                Session.Connect(_transports.Create(endpoint), UseTextConsole);
            }
            catch (ArgumentException ex)
            {
                Session.Console.Add(ConsoleLineKind.Notice, ex.Message, FrameSource.Console);
            }
            finally
            {
                _ui.Post(() =>
                {
                    SetBusy(false);
                    RefreshState();
                });
            }
        });
    }

    private void Disconnect()
    {
        SetBusy(true);
        Task.Run(() =>
        {
            Session.Disconnect();
            _ui.Post(() =>
            {
                SetBusy(false);
                RefreshState();
            });
        });
    }

    private async Task SendCommandAsync()
    {
        string command = CommandText.Trim();
        if (command.Length == 0)
        {
            return;
        }
        if (_history.Count == 0 || _history[_history.Count - 1] != command)
        {
            _history.Add(command);
        }
        _historyIndex = _history.Count;
        CommandText = string.Empty;
        await RunAsync(command).ConfigureAwait(true);
    }

    private void SendHex()
    {
        if (!TryParseHex(HexText, out byte[] bytes, out string? error))
        {
            Session.Console.Add(ConsoleLineKind.Notice, error!, FrameSource.Console);
            return;
        }
        if (bytes.Length < (AddFcs ? 2 : 4))
        {
            Session.Console.Add(ConsoleLineKind.Notice, "A frame needs at least address and control"
                                + (AddFcs ? "." : " plus the two FCS bytes."), FrameSource.Console);
            return;
        }
        Session.SendRawFrame(bytes, AddFcs, FrameSource.Console);
    }

    private void ApplyRole(GatewayRole role)
    {
        switch (role)
        {
            case GatewayRole.Bridge:
                if (_set.A.Link == null || _set.B.Link == null)
                {
                    Session.Console.Add(ConsoleLineKind.Notice, "Bridge needs both panes connected.", FrameSource.Console);
                    _ui.Post(RefreshState);
                    return;
                }
                _set.SetBridge(true);
                break;

            case GatewayRole.TcpListen:
                if (Session.Role == GatewayRole.Bridge)
                {
                    _set.SetBridge(false);
                }
                if (!_set.TryStartTcpListen(Session, TcpPort, out _))
                {
                    _ui.Post(RefreshState);
                }
                break;

            default:
                if (Session.Role == GatewayRole.Bridge)
                {
                    _set.SetBridge(false);
                }
                if (Session.Role == GatewayRole.TcpListen)
                {
                    Session.StopTcpListen();
                }
                Session.Role = role;
                break;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
    }

    private void RefreshState()
    {
        StateText = Session.State switch
        {
            GatewaySessionState.ControlFrames => "CONTROL FRAMES",
            GatewaySessionState.TextConsole => "TEXT CONSOLE (legacy)",
            GatewaySessionState.Unknown => "NO GATEWAY ANSWERED",
            GatewaySessionState.FramesOnly => "FRAMES ONLY (TCP)",
            GatewaySessionState.Faulted => "PORT FAILED",
            GatewaySessionState.Probing => "probing...",
            GatewaySessionState.Connecting => "connecting...",
            _ => "Disconnected",
        };
        IdentityText = Session.Identity != null
            ? Session.Identity.Name + " " + Session.Identity.Version + " on " + Session.EndpointName
            : Session.EndpointName;
        _selectedRole = Session.Role.ToString();
        Raise(nameof(SelectedRole));
        _selectedLogLevel = Session.LogLevel.ToString();
        Raise(nameof(SelectedLogLevel));
        Raise(nameof(IsConnected));
        Raise(nameof(HasControlFrames));
        Raise(nameof(HasFirmwareLog));
        Raise(nameof(ShowHexBox));
        Raise(nameof(ShowTcpBox));
        TcpText = Session.TcpPort == 0 ? string.Empty
                : "listening on " + Session.TcpPort + (Session.TcpPeer != null ? ", peer " + Session.TcpPeer : ", no peer");
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        SendHexCommand.RaiseCanExecuteChanged();
        SetBaudCommand.RaiseCanExecuteChanged();
        StopTcpCommand.RaiseCanExecuteChanged();
    }

    private void AddConsoleLine(ConsoleLine line)
    {
        ConsoleLines.Add(new ConsoleLineViewModel(line));
        if (ConsoleLines.Count > MaxConsoleLines)
        {
            ConsoleLines.RemoveAt(0);
        }
        ConsoleLineAdded?.Invoke();
    }

    private void AddFrame(FrameRecord record)
    {
        FrameRecordViewModel row = new FrameRecordViewModel(record);
        if (!Passes(row))
        {
            return;
        }
        Frames.Add(row);
        if (Frames.Count > MaxFrameRows)
        {
            Frames.RemoveAt(0);
        }
    }

    private bool Passes(FrameRecordViewModel row)
    {
        return _frameFilter.Length == 0 || row.Text.Contains(_frameFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildFrames()
    {
        Frames.Clear();
        List<FrameRecord> kept = Session.Frames.Since(0);
        int start = Math.Max(0, kept.Count - MaxFrameRows);
        for (int i = start; i < kept.Count; i++)
        {
            FrameRecordViewModel row = new FrameRecordViewModel(kept[i]);
            if (Passes(row))
            {
                Frames.Add(row);
            }
        }
    }

    /// <summary>
    /// Parse pasted hex: pairs of digits, spaces, commas, colons and 0x prefixes allowed.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="error">Why it failed, or null.</param>
    /// <returns>True when parsed.</returns>
    public static bool TryParseHex(string text, out byte[] bytes, out string? error)
    {
        List<byte> list = new List<byte>();
        int nibbles = 0;
        int value = 0;
        string s = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
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
                error = "'" + c + "' is not a hex digit.";
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
            error = "Odd number of hex digits.";
            return false;
        }
        bytes = list.ToArray();
        error = null;
        return true;
    }
}
