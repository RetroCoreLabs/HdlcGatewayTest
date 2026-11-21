// PANE SETUP VIEW MODEL - everything that configures one pane, edited in its own window.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Collections.ObjectModel;
using HdlcGatewayTest.Core;
using HdlcGatewayTest.Gateway;

namespace HdlcGatewayTest.App.ViewModels;

/// <summary>
/// A copy of one pane's settings for the Setup window (Ronny, 2026-11-09: the pane itself must
/// not be a wall of controls; setting up A and B belongs in a window of its own). What the
/// window edits: how the pane reaches its gateway (a serial port, or TCP client to a gateway in
/// WiFi mode or to an emulator), the probe style, the line speed, the role, the TCP server, and
/// the firmware log level. Nothing is applied until <see cref="GatewayPaneViewModel.ApplySetup"/>.
/// </summary>
public sealed class PaneSetupViewModel : ObservableObject
{
    private string? _selectedSerialPort;
    private bool _useTcpClient;
    private string _tcpHost = string.Empty;
    private int _tcpClientPort = 1362;
    private bool _useTextConsole;
    private string _baud = "38400";
    private string _selectedRole = GatewayPaneViewModel.RoleNames[0];
    private bool _tcpServerEnabled;
    private int _tcpServerPort = 1362;
    private bool _tcpForwardEnabled;
    private string _tcpForwardHost = string.Empty;
    private int _tcpForwardPort = 1362;
    private string _logLevel = nameof(Core.LogLevel.Off);
    private string _error = string.Empty;

    /// <summary>Window title, "Setup gateway A".</summary>
    public string Title { get; init; } = "Setup gateway";

    /// <summary>Serial ports to pick from.</summary>
    public ObservableCollection<string> SerialPorts { get; } = new ObservableCollection<string>();

    /// <summary>The serial port, when not a TCP client.</summary>
    public string? SelectedSerialPort { get => _selectedSerialPort; set => SetProperty(ref _selectedSerialPort, value); }

    /// <summary>True: reach the gateway over TCP as a client (host and port below) instead of a serial port.</summary>
    public bool UseTcpClient
    {
        get => _useTcpClient;
        set
        {
            if (SetProperty(ref _useTcpClient, value))
            {
                Raise(nameof(UseSerialPort));
            }
        }
    }

    /// <summary>The opposite of <see cref="UseTcpClient"/>, for the other radio button.</summary>
    public bool UseSerialPort
    {
        get => !_useTcpClient;
        set => UseTcpClient = !value;
    }

    /// <summary>TCP client: host name or address of the gateway or emulator.</summary>
    public string TcpHost { get => _tcpHost; set => SetProperty(ref _tcpHost, value); }

    /// <summary>TCP client: port on that host.</summary>
    public int TcpClientPort { get => _tcpClientPort; set => SetProperty(ref _tcpClientPort, value); }

    /// <summary>Probe with ESC ESC ESC too, for a gateway without control frames.</summary>
    public bool UseTextConsole { get => _useTextConsole; set => SetProperty(ref _useTextConsole, value); }

    /// <summary>Line speed to set with BAUD.</summary>
    public string Baud { get => _baud; set => SetProperty(ref _baud, value); }

    /// <summary>Line speeds offered.</summary>
    public string[] BaudRates { get; init; } = Array.Empty<string>();

    /// <summary>Role names in plain words.</summary>
    public string[] Roles => GatewayPaneViewModel.RoleNames;

    /// <summary>The chosen role name.</summary>
    public string SelectedRole { get => _selectedRole; set => SetProperty(ref _selectedRole, value); }

    /// <summary>Listen on <see cref="TcpServerPort"/> for an emulator.</summary>
    public bool TcpServerEnabled { get => _tcpServerEnabled; set => SetProperty(ref _tcpServerEnabled, value); }

    /// <summary>The TCP server port.</summary>
    public int TcpServerPort { get => _tcpServerPort; set => SetProperty(ref _tcpServerPort, value); }

    /// <summary>Connect out to a TCP server (an emulator that listens) and forward frames both ways.</summary>
    public bool TcpForwardEnabled { get => _tcpForwardEnabled; set => SetProperty(ref _tcpForwardEnabled, value); }

    /// <summary>Host of the server to forward to.</summary>
    public string TcpForwardHost { get => _tcpForwardHost; set => SetProperty(ref _tcpForwardHost, value); }

    /// <summary>Port of the server to forward to.</summary>
    public int TcpForwardPort { get => _tcpForwardPort; set => SetProperty(ref _tcpForwardPort, value); }

    /// <summary>Log level names.</summary>
    public string[] LogLevels { get; } = Enum.GetNames<LogLevel>();

    /// <summary>Firmware log level to ask for.</summary>
    public string LogLevel { get => _logLevel; set => SetProperty(ref _logLevel, value); }

    /// <summary>Why the settings cannot be applied, or empty.</summary>
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>The endpoint the settings describe: "COM10" or "host:port".</summary>
    public string Endpoint => UseTcpClient ? TcpHost.Trim() + ":" + TcpClientPort : (SelectedSerialPort ?? string.Empty);

    /// <summary>
    /// Take the current settings of a pane.
    /// </summary>
    /// <param name="pane">The pane.</param>
    /// <returns>A copy to edit.</returns>
    public static PaneSetupViewModel From(GatewayPaneViewModel pane)
    {
        PaneSetupViewModel s = new PaneSetupViewModel
        {
            Title = "Setup gateway " + pane.Label,
            BaudRates = pane.BaudRates,
        };
        for (int i = 0; i < pane.Endpoints.Count; i++)
        {
            if (!pane.Endpoints[i].Contains(':'))
            {
                s.SerialPorts.Add(pane.Endpoints[i]);
            }
        }
        string endpoint = pane.SelectedEndpoint ?? string.Empty;
        if (TcpClientTransport.TryParseEndpoint(endpoint, out string host, out int port))
        {
            s.UseTcpClient = true;
            s.TcpHost = host;
            s.TcpClientPort = port;
            s.SelectedSerialPort = s.SerialPorts.Count > 0 ? s.SerialPorts[0] : null;
        }
        else
        {
            s.SelectedSerialPort = s.SerialPorts.Contains(endpoint) ? endpoint : (s.SerialPorts.Count > 0 ? s.SerialPorts[0] : null);
        }
        s.UseTextConsole = pane.UseTextConsole;
        s.Baud = pane.SelectedBaud;
        s.SelectedRole = pane.SelectedRole;
        s.TcpServerEnabled = pane.Session.TcpPort != 0 || pane.Session.Role == GatewayRole.TcpListen;
        s.TcpServerPort = pane.Session.TcpPort != 0 ? pane.Session.TcpPort : pane.TcpPort;
        s.TcpForwardEnabled = pane.Session.Role == GatewayRole.TcpClient;
        s.TcpForwardHost = pane.TcpForwardHost;
        s.TcpForwardPort = pane.TcpForwardPort;
        s.LogLevel = pane.SelectedLogLevel;
        return s;
    }

    /// <summary>
    /// Check the settings; sets <see cref="Error"/>.
    /// </summary>
    /// <returns>True when they can be applied.</returns>
    public bool Validate()
    {
        if (UseTcpClient)
        {
            if (TcpHost.Trim().Length == 0)
            {
                Error = "Give the host name or address to connect to.";
                return false;
            }
            if (TcpClientPort < 1 || TcpClientPort > 65535)
            {
                Error = "The TCP client port must be 1..65535.";
                return false;
            }
        }
        else if (string.IsNullOrEmpty(SelectedSerialPort))
        {
            Error = "Pick a serial port, or choose TCP client.";
            return false;
        }
        if (TcpForwardEnabled && TcpServerEnabled)
        {
            Error = "Choose either listening for an emulator or forwarding to one, not both.";
            return false;
        }
        if (TcpForwardEnabled && (TcpForwardHost.Trim().Length == 0 || TcpForwardPort < 1 || TcpForwardPort > 65535))
        {
            Error = "Give the host and a port (1..65535) of the emulator to forward to.";
            return false;
        }
        if (TcpServerEnabled && (TcpServerPort < 1 || TcpServerPort > 65535))
        {
            Error = "The TCP server port must be 1..65535.";
            return false;
        }
        if (!uint.TryParse(Baud.Trim(), out uint baud) || baud < 9600 || baud > 983000)
        {
            Error = "The line speed must be a number from 9600 to 983000.";
            return false;
        }
        Error = string.Empty;
        return true;
    }
}
