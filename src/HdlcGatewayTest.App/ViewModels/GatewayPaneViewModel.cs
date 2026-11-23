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
    private string _selectedRole = RoleNames[0];
    private string _selectedBaud = "38400";
    private string _selectedLogLevel = nameof(LogLevel.Off);
    private string _commandText = string.Empty;
    private string _hexText = string.Empty;
    private bool _addFcs = true;
    private int _tcpPort;
    private string _tcpForwardHost = string.Empty;
    private int _tcpForwardPort = 1362;
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
        StartTcpCommand = new RelayCommand(() => ApplyRole(GatewayRole.TcpListen), () => IsConnected);
        SetupCommand = new RelayCommand(() => SetupRequested?.Invoke(this), () => !_busy);

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
                Raise(nameof(ConfigSummary));
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

    /// <summary>
    /// The roles in plain words, in <see cref="GatewayRole"/> order (Ronny, 2026-11-09: the bare
    /// enum names did not say what a role does or how it relates to the traffic test).
    /// </summary>
    public static readonly string[] RoleNames =
    {
        "Console",
        "Free (paste hex)",
        "Echo (send back)",
        "Bridge A <-> B",
        "TCP server",
        "TCP client (to emulator)",
    };

    /// <summary>Role names for the picker.</summary>
    public string[] Roles => RoleNames;

    /// <summary>The plain-words name of a role.</summary>
    /// <param name="role">The role.</param>
    /// <returns>Its entry in <see cref="RoleNames"/>.</returns>
    public static string RoleName(GatewayRole role)
    {
        return RoleNames[(int)role];
    }

    /// <summary>
    /// The pane's role as its plain-words name; setting it acts on the session set. The enum
    /// name is accepted too.
    /// </summary>
    public string SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (!SetProperty(ref _selectedRole, value))
            {
                return;
            }
            int index = Array.IndexOf(RoleNames, value);
            GatewayRole role;
            if (index >= 0)
            {
                role = (GatewayRole)index;
            }
            else if (!Enum.TryParse(value, out role))
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
        set
        {
            if (SetProperty(ref _selectedBaud, value))
            {
                Raise(nameof(ConfigSummary));
            }
        }
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

    /// <summary>TCP server port: the port the pane listens on when told to.</summary>
    public int TcpPort
    {
        get => _tcpPort;
        set => SetProperty(ref _tcpPort, value);
    }

    /// <summary>Firmware name from HELLO, or "-" when nothing answered.</summary>
    public string FirmwareName => Identity(i => i.Name);

    /// <summary>Firmware version from HELLO.</summary>
    public string FirmwareVersion => Identity(i => i.Version);

    /// <summary>Firmware build date and time from HELLO.</summary>
    public string FirmwareBuild => Identity(i => i.Build);

    /// <summary>Chip from HELLO (ESP32-S2, RP2040), "-" on firmware that does not say.</summary>
    public string FirmwareChip => Identity(i => i.Chip);

    /// <summary>Board from HELLO (LOLIN S2 Mini, Raspberry Pi Pico).</summary>
    public string FirmwareBoard => Identity(i => i.Board);

    /// <summary>Chip revision from HELLO, for example "0.0".</summary>
    public string FirmwareRevision => Identity(i => i.Revision);

    /// <summary>Chip serial number from HELLO (factory MAC on an ESP32, flash unique ID on a Pico).</summary>
    public string FirmwareSerial => Identity(i => i.Serial);

    /// <summary>"2 cores, 240 MHz" from HELLO, or empty on firmware that does not say.</summary>
    public string FirmwareCpu => Identity(i => Join(
        i.Cores.HasValue ? i.Cores.Value + (i.Cores.Value == 1 ? " core" : " cores") : string.Empty,
        i.CpuMhz.HasValue ? i.CpuMhz.Value + " MHz" : string.Empty));

    /// <summary>"4 MB on the chip, image built for 4 MB, 80 MHz, qio" from HELLO.</summary>
    public string FirmwareFlash => Identity(i => Join(
        i.FlashBytes.HasValue ? Bytes(i.FlashBytes.Value) + " on the chip" : string.Empty,
        i.FlashImageBytes.HasValue ? "image built for " + Bytes(i.FlashImageBytes.Value) : string.Empty,
        i.FlashMhz.HasValue ? i.FlashMhz.Value + " MHz" : string.Empty,
        i.FlashMode));

    /// <summary>Internal RAM from HELLO: size, free, least free since start, largest block.</summary>
    public string FirmwareRam => Identity(i => Join(
        i.RamBytes.HasValue ? Bytes(i.RamBytes.Value) : string.Empty,
        i.RamFree.HasValue ? Bytes(i.RamFree.Value) + " free" : string.Empty,
        i.RamLeast.HasValue ? "least " + Bytes(i.RamLeast.Value) : string.Empty,
        i.RamBlock.HasValue ? "largest block " + Bytes(i.RamBlock.Value) : string.Empty));

    /// <summary>PSRAM from HELLO: "none" when the board has none, else size, free and largest block.</summary>
    public string FirmwarePsram => Identity(i => i.PsramBytes == 0 ? "none" : Join(
        i.PsramBytes.HasValue ? Bytes(i.PsramBytes.Value) : string.Empty,
        i.PsramFree.HasValue ? Bytes(i.PsramFree.Value) + " free" : string.Empty,
        i.PsramBlock.HasValue ? "largest block " + Bytes(i.PsramBlock.Value) : string.Empty));

    /// <summary>The chip's 128-bit unique ID from HELLO.</summary>
    public string FirmwareUniqueId => Identity(i => i.UniqueId);

    /// <summary>"package 0, eFuse block 0.2, built-in flash 2, built-in PSRAM 1" from HELLO.</summary>
    public string FirmwareChipIds => Identity(i => Join(
        i.Package.Length > 0 ? "package " + i.Package : string.Empty,
        i.EfuseBlockVersion.Length > 0 ? "eFuse block " + i.EfuseBlockVersion : string.Empty,
        i.EmbeddedFlash.Length > 0 ? "built-in flash " + i.EmbeddedFlash : string.Empty,
        i.EmbeddedPsram.Length > 0 ? "built-in PSRAM " + i.EmbeddedPsram : string.Empty));

    /// <summary>"204016 (maker 20)" from HELLO: the flash chip's JEDEC ID and its maker byte.</summary>
    public string FirmwareFlashId => Identity(i => i.FlashId.Length >= 2
        ? i.FlashId + " (maker " + i.FlashId.Substring(0, 2) + ")"
        : i.FlashId);

    /// <summary>The USB and flash-supply eFuses from HELLO, each digit named.</summary>
    public string FirmwareFuses => Identity(i => Join(
        i.UsbFuses.Length == 4
            ? "USB off " + i.UsbFuses[0] + ", download off " + i.UsbFuses[1] + ", pins swapped " + i.UsbFuses[2]
              + ", external PHY " + i.UsbFuses[3]
            : i.UsbFuses,
        i.VddSpiFuses.Length == 3
            ? "VDD_SPI force " + i.VddSpiFuses[0] + ", tieh " + i.VddSpiFuses[1] + ", xpd " + i.VddSpiFuses[2]
            : i.VddSpiFuses));

    /// <summary>A byte count as "4 MB" when it is a whole number of MB, else "12,848 bytes".</summary>
    public static string Bytes(long n)
    {
        const long Mb = 1024 * 1024;
        if (n >= Mb && n % Mb == 0)
        {
            return (n / Mb).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }
        return n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " bytes";
    }

    /// <summary>The non-empty parts joined with ", ".</summary>
    private static string Join(params string[] parts)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
            {
                continue;
            }
            if (sb.Length > 0)
            {
                sb.Append(", ");
            }
            sb.Append(parts[i]);
        }
        return sb.ToString();
    }

    /// <summary>Longest frame the firmware takes, from HELLO.</summary>
    public string FirmwareMaxFrame => Identity(i => i.MaxFrame > 0 ? i.MaxFrame + " bytes" : string.Empty);

    /// <summary>Capability words from HELLO.</summary>
    public string FirmwareCapabilities => Identity(i => string.Join(", ", i.Capabilities));

    /// <summary>Control-frame protocol version from HELLO.</summary>
    public string FirmwareProtocol => Identity(i => i.ProtocolVersion.ToString());

    /// <summary>The port or host:port this pane is on, or "-".</summary>
    public string FirmwarePort => Session.EndpointName.Length > 0 ? Session.EndpointName : "-";

    /// <summary>
    /// One line for the bottom bar: "Port A: HDLCGW 0.2.0 on ESP32-S2 (LOLIN S2 Mini), built Nov 9 2026 01:21:29".
    /// </summary>
    public string VersionSummary
    {
        get
        {
            GatewayIdentity? id = Session.Identity;
            if (id == null)
            {
                return Title + ": no gateway identified";
            }
            string where = id.Chip.Length > 0 ? " on " + id.Chip + (id.Board.Length > 0 ? " (" + id.Board + ")" : string.Empty) : string.Empty;
            return Title + ": " + id.Name + " " + id.Version + where + ", built " + id.Build;
        }
    }

    private string Identity(Func<GatewayIdentity, string> pick)
    {
        GatewayIdentity? id = Session.Identity;
        if (id == null)
        {
            return "-";
        }
        string value = pick(id);
        return value.Length > 0 ? value : "-";
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

    /// <summary>Host of the TCP server (an emulator) this pane forwards to in the TCP client role.</summary>
    public string TcpForwardHost { get => _tcpForwardHost; set => SetProperty(ref _tcpForwardHost, value); }

    /// <summary>Port of that TCP server.</summary>
    public int TcpForwardPort { get => _tcpForwardPort; set => SetProperty(ref _tcpForwardPort, value); }

    /// <summary>Start listening on <see cref="TcpPort"/>: the TCP server role.</summary>
    public RelayCommand StartTcpCommand { get; }

    /// <summary>Open the Setup window; the view listens on <see cref="SetupRequested"/>.</summary>
    public RelayCommand SetupCommand { get; }

    /// <summary>Raised by <see cref="SetupCommand"/>; the view opens the window and calls <see cref="ApplySetup"/>.</summary>
    public event Action<GatewayPaneViewModel>? SetupRequested;

    /// <summary>
    /// One line under the header saying how the pane is set up: endpoint, role, line speed, TCP server.
    /// </summary>
    public string ConfigSummary
    {
        get
        {
            string endpoint = SelectedEndpoint ?? "no port chosen";
            string how = endpoint.Contains(':') ? "TCP client to " + endpoint : endpoint;
            string tcp = Session.TcpPort != 0
                ? "TCP server on " + Session.TcpPort + (Session.TcpPeer != null ? " (peer " + Session.TcpPeer + ")" : " (no peer yet)")
                : Session.Role == GatewayRole.TcpClient ? "forwarding to " + Session.TcpPeer : "no TCP server";
            return how + "  |  " + RoleName(Session.Role) + "  |  " + SelectedBaud + " bit/s  |  " + tcp
                   + (UseTextConsole ? "  |  legacy console" : string.Empty);
        }
    }

    /// <summary>
    /// Apply what the Setup window collected: the endpoint (reconnecting when it changed and
    /// the pane was connected), the probe style, the line speed (BAUD sent when connected),
    /// the role and the TCP server, and the log level.
    /// </summary>
    /// <param name="setup">The edited settings; must have passed <see cref="PaneSetupViewModel.Validate"/>.</param>
    public void ApplySetup(PaneSetupViewModel setup)
    {
        string endpoint = setup.Endpoint;
        if (endpoint.Length > 0 && !Endpoints.Contains(endpoint))
        {
            Endpoints.Add(endpoint);    // a host:port the port list cannot know
        }
        bool endpointChanged = !string.Equals(SelectedEndpoint, endpoint, StringComparison.Ordinal);
        SelectedEndpoint = endpoint;
        UseTextConsole = setup.UseTextConsole;
        bool baudChanged = !string.Equals(SelectedBaud, setup.Baud.Trim(), StringComparison.Ordinal);
        SelectedBaud = setup.Baud.Trim();
        TcpPort = setup.TcpServerPort;
        TcpForwardHost = setup.TcpForwardHost.Trim();
        TcpForwardPort = setup.TcpForwardPort;
        GatewayRole role = setup.TcpForwardEnabled ? GatewayRole.TcpClient
            : setup.TcpServerEnabled ? GatewayRole.TcpListen
            : Array.IndexOf(RoleNames, setup.SelectedRole) is int i && i >= 0 ? (GatewayRole)i : GatewayRole.Console;
        if (role == GatewayRole.TcpListen && Session.Role == GatewayRole.TcpListen && Session.TcpPort != setup.TcpServerPort)
        {
            Session.StopTcpListen();    // same role, other port: restart the listener below
        }

        if (IsConnected && endpointChanged)
        {
            Reconnect(endpoint, role, baudChanged);
            return;
        }
        if (role == GatewayRole.TcpClient && Session.Role == GatewayRole.TcpClient && Session.TcpPeer != TcpForwardHost + ":" + TcpForwardPort)
        {
            Session.StopTcpListen();    // same role, other server: reconnect below
        }
        if (role != Session.Role || (role == GatewayRole.TcpListen && Session.TcpPort == 0))
        {
            ApplyRole(role);
        }
        if (IsConnected && baudChanged)
        {
            _ = RunAsync("BAUD " + SelectedBaud);
        }
        SelectedLogLevel = setup.LogLevel;
        Raise(nameof(ConfigSummary));
    }

    private void Reconnect(string endpoint, GatewayRole role, bool sendBaud)
    {
        SetBusy(true);
        Task.Run(() =>
        {
            Session.Disconnect();
            try
            {
                Session.Connect(_transports.Create(endpoint), UseTextConsole);
            }
            catch (ArgumentException ex)
            {
                Session.Console.Add(ConsoleLineKind.Notice, ex.Message, FrameSource.Console);
            }
            _ui.Post(() =>
            {
                SetBusy(false);
                RefreshState();
                if (role != Session.Role)
                {
                    ApplyRole(role);
                }
                if (sendBaud && IsConnected)
                {
                    _ = RunAsync("BAUD " + SelectedBaud);
                }
                Raise(nameof(ConfigSummary));
            });
        });
    }

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

            case GatewayRole.TcpClient:
                if (Session.Role == GatewayRole.Bridge)
                {
                    _set.SetBridge(false);
                }
                if (TcpForwardHost.Length == 0)
                {
                    Session.Console.Add(ConsoleLineKind.Notice, "Give the TCP server's host in Setup first.", FrameSource.Console);
                    _ui.Post(RefreshState);
                    return;
                }
                Task.Run(() =>
                {
                    Session.StartTcpClient(TcpForwardHost, TcpForwardPort, out _);
                    _ui.Post(RefreshState);
                });
                break;

            default:
                if (Session.Role == GatewayRole.Bridge)
                {
                    _set.SetBridge(false);
                }
                if (Session.Role == GatewayRole.TcpListen || Session.Role == GatewayRole.TcpClient)
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
        _selectedRole = RoleName(Session.Role);
        Raise(nameof(SelectedRole));
        _selectedLogLevel = Session.LogLevel.ToString();
        Raise(nameof(SelectedLogLevel));
        Raise(nameof(IsConnected));
        Raise(nameof(HasControlFrames));
        Raise(nameof(HasFirmwareLog));
        Raise(nameof(ShowHexBox));
        Raise(nameof(FirmwareName));
        Raise(nameof(FirmwareVersion));
        Raise(nameof(FirmwareBuild));
        Raise(nameof(FirmwareChip));
        Raise(nameof(FirmwareBoard));
        Raise(nameof(FirmwareRevision));
        Raise(nameof(FirmwareSerial));
        Raise(nameof(FirmwareCpu));
        Raise(nameof(FirmwareFlash));
        Raise(nameof(FirmwareRam));
        Raise(nameof(FirmwarePsram));
        Raise(nameof(FirmwareUniqueId));
        Raise(nameof(FirmwareChipIds));
        Raise(nameof(FirmwareFlashId));
        Raise(nameof(FirmwareFuses));
        Raise(nameof(FirmwareMaxFrame));
        Raise(nameof(FirmwareCapabilities));
        Raise(nameof(FirmwareProtocol));
        Raise(nameof(FirmwarePort));
        Raise(nameof(VersionSummary));
        Raise(nameof(ConfigSummary));
        TcpText = Session.TcpPort == 0 ? "not listening"
                : "listening on " + Session.TcpPort + (Session.TcpPeer != null ? ", peer " + Session.TcpPeer : ", no peer yet");
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        SendHexCommand.RaiseCanExecuteChanged();
        SetBaudCommand.RaiseCanExecuteChanged();
        StopTcpCommand.RaiseCanExecuteChanged();
        StartTcpCommand.RaiseCanExecuteChanged();
        SetupCommand.RaiseCanExecuteChanged();
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
