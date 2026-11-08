// MAIN VIEW MODEL - the window: two panes, the traffic panel, the MCP server and the settings.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Collections.ObjectModel;
using HdlcGatewayTest.App.Services;
using HdlcGatewayTest.Gateway;
using HdlcGatewayTest.Mcp;

namespace HdlcGatewayTest.App.ViewModels;

/// <summary>
/// Owns the <see cref="GatewaySessionSet"/>, the two pane view models, the traffic test panel,
/// the MCP server (off unless remembered on or <c>--mcp</c> was given) and the settings file.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IGatewayTransportFactory _transports;
    private readonly IUiDispatcher _ui;
    private readonly StartupOptions _startup;
    private readonly McpTrafficLog _mcpLog = new McpTrafficLog();
    private McpServerHost? _mcp;
    private bool _mcpEnabled;
    private int _mcpPort;
    private string _mcpStatus = "MCP off";
    private bool _showMcpLog;

    /// <summary>
    /// Create the window's model.
    /// </summary>
    /// <param name="settings">Loaded settings.</param>
    /// <param name="transports">How to open endpoints.</param>
    /// <param name="ui">UI thread.</param>
    /// <param name="startup">Command line.</param>
    public MainViewModel(AppSettings settings, IGatewayTransportFactory transports, IUiDispatcher ui, StartupOptions startup)
    {
        _settings = settings;
        _transports = transports;
        _ui = ui;
        _startup = startup;
        Sessions = new GatewaySessionSet();
        PaneA = new GatewayPaneViewModel(Sessions, Sessions.A, transports, ui, settings.TcpPortA)
        {
            SelectedBaud = settings.BaudA,
        };
        PaneB = new GatewayPaneViewModel(Sessions, Sessions.B, transports, ui, settings.TcpPortB)
        {
            SelectedBaud = settings.BaudB,
        };
        if (settings.EndpointA.Length > 0 && PaneA.Endpoints.Contains(settings.EndpointA))
        {
            PaneA.SelectedEndpoint = settings.EndpointA;
        }
        if (settings.EndpointB.Length > 0 && PaneB.Endpoints.Contains(settings.EndpointB))
        {
            PaneB.SelectedEndpoint = settings.EndpointB;
        }
        Traffic = new TrafficTestViewModel(Sessions, settings, ui);
        _mcpEnabled = settings.McpEnabled;
        _mcpPort = startup.McpPort != 0 ? startup.McpPort : settings.McpPort;
        _mcpLog.Added += entry => _ui.Post(() =>
        {
            McpLog.Add(entry.Time.ToString("HH:mm:ss.fff") + " " + (entry.IsResult ? (entry.IsError ? "ERR " : "ok  ") : "call")
                       + " " + entry.Tool + " " + entry.Text);
            if (McpLog.Count > 500)
            {
                McpLog.RemoveAt(0);
            }
        });
        ToggleMcpLogCommand = new RelayCommand(() => ShowMcpLog = !ShowMcpLog);
    }

    /// <summary>The sessions.</summary>
    public GatewaySessionSet Sessions { get; }

    /// <summary>Pane A.</summary>
    public GatewayPaneViewModel PaneA { get; }

    /// <summary>Pane B.</summary>
    public GatewayPaneViewModel PaneB { get; }

    /// <summary>The traffic test panel.</summary>
    public TrafficTestViewModel Traffic { get; }

    /// <summary>Window title.</summary>
    public string Title => "HDLC Gateway Tool " + Version;

    /// <summary>Version of this build.</summary>
    public static string Version => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>MCP server on or off (remembered).</summary>
    public bool McpEnabled
    {
        get => _mcpEnabled;
        set
        {
            if (SetProperty(ref _mcpEnabled, value))
            {
                _settings.McpEnabled = value;
                _settings.Save();
                if (value)
                {
                    _ = StartMcpAsync();
                }
                else
                {
                    _ = StopMcpAsync();
                }
            }
        }
    }

    /// <summary>MCP port (remembered).</summary>
    public int McpPort
    {
        get => _mcpPort;
        set
        {
            if (SetProperty(ref _mcpPort, value) && value > 0 && value < 65536)
            {
                _settings.McpPort = value;
                _settings.Save();
            }
        }
    }

    /// <summary>"MCP at http://127.0.0.1:2525/mcp" or why it is off.</summary>
    public string McpStatus
    {
        get => _mcpStatus;
        private set => SetProperty(ref _mcpStatus, value);
    }

    /// <summary>Show the MCP call log strip.</summary>
    public bool ShowMcpLog
    {
        get => _showMcpLog;
        set => SetProperty(ref _showMcpLog, value);
    }

    /// <summary>MCP calls and results, newest last.</summary>
    public ObservableCollection<string> McpLog { get; } = new ObservableCollection<string>();

    /// <summary>Show or hide the MCP log.</summary>
    public RelayCommand ToggleMcpLogCommand { get; }

    /// <summary>Start the pump thread and, when asked, the MCP server.</summary>
    public void Start()
    {
        Sessions.Start();
        if (_mcpEnabled || _startup.McpForThisRun)
        {
            _ = StartMcpAsync();
        }
    }

    /// <summary>Save settings, stop MCP, close ports.</summary>
    public void Shutdown()
    {
        _settings.EndpointA = PaneA.SelectedEndpoint ?? string.Empty;
        _settings.EndpointB = PaneB.SelectedEndpoint ?? string.Empty;
        _settings.BaudA = PaneA.SelectedBaud;
        _settings.BaudB = PaneB.SelectedBaud;
        _settings.TcpPortA = PaneA.TcpPort;
        _settings.TcpPortB = PaneB.TcpPort;
        Traffic.SaveTo(_settings);
        _settings.Save();
        try
        {
            StopMcpAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
        {
            // Closing anyway.
        }
        Sessions.Dispose();
    }

    private async Task StartMcpAsync()
    {
        if (_mcp != null)
        {
            return;
        }
        HdlcToolProvider tools = new HdlcToolProvider(Sessions, _transports, _mcpLog, Version);
        try
        {
            _mcp = await McpServerHost.StartAsync(tools, _mcpPort).ConfigureAwait(false);
            _ui.Post(() => McpStatus = "MCP at " + _mcp!.Url);
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is System.Net.Sockets.SocketException)
        {
            _ui.Post(() => McpStatus = "MCP failed on port " + _mcpPort + ": " + ex.Message);
        }
    }

    private async Task StopMcpAsync()
    {
        McpServerHost? mcp = _mcp;
        _mcp = null;
        if (mcp != null)
        {
            await mcp.DisposeAsync().ConfigureAwait(false);
        }
        _ui.Post(() => McpStatus = "MCP off");
    }
}
