// TRAFFIC TEST VIEW MODEL - the bottom panel: settings, start/stop, live status and the report.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Collections.ObjectModel;
using System.Globalization;
using HdlcGatewayTest.App.Services;
using HdlcGatewayTest.Core;
using HdlcGatewayTest.Gateway;

namespace HdlcGatewayTest.App.ViewModels;

/// <summary>
/// Drives <see cref="GatewaySessionSet.RunTrafficTestAsync"/> with the same knobs as the console
/// tool and shows what the <see cref="TrafficTestRecorder"/> collects: the one-line status, the
/// messages, and the PASS/FAIL table.
/// </summary>
public sealed class TrafficTestViewModel : ObservableObject
{
    private readonly GatewaySessionSet _set;
    private readonly IUiDispatcher _ui;
    private readonly TrafficTestRecorder _recorder = new TrafficTestRecorder();
    private readonly AppSettings _settings;
    private string _rates;
    private int _seconds;
    private int _size;
    private int _window;
    private int _timeout;
    private bool _noBaud;
    private string _selectedPath = PathNames[0];
    private string _statusLine = string.Empty;
    private string _report = string.Empty;
    private string _verdict = string.Empty;
    private bool _running;

    /// <summary>
    /// Create the panel.
    /// </summary>
    /// <param name="set">The sessions.</param>
    /// <param name="settings">Remembered knobs.</param>
    /// <param name="ui">UI thread.</param>
    public TrafficTestViewModel(GatewaySessionSet set, AppSettings settings, IUiDispatcher ui)
    {
        _set = set;
        _settings = settings;
        _ui = ui;
        _rates = settings.TestRates;
        _seconds = settings.TestSeconds;
        _size = settings.TestSize;
        _window = settings.TestWindow;
        _timeout = settings.TestTimeout;
        StartCommand = new RelayCommand(Start, () => !_running);
        StopCommand = new RelayCommand(() => _set.StopTrafficTest(), () => _running);
        _recorder.Changed += () => _ui.Post(RefreshFromRecorder);
    }

    /// <summary>Comma-separated rates.</summary>
    public string Rates { get => _rates; set => SetProperty(ref _rates, value); }

    /// <summary>Seconds per rate.</summary>
    public int Seconds { get => _seconds; set => SetProperty(ref _seconds, value); }

    /// <summary>Frame length including FCS.</summary>
    public int Size { get => _size; set => SetProperty(ref _size, value); }

    /// <summary>Encoded bytes in flight.</summary>
    public int Window { get => _window; set => SetProperty(ref _window, value); }

    /// <summary>Seconds before a frame counts as lost.</summary>
    public int Timeout { get => _timeout; set => SetProperty(ref _timeout, value); }

    /// <summary>Do not set BAUD.</summary>
    public bool NoBaud { get => _noBaud; set => SetProperty(ref _noBaud, value); }

    /// <summary>The test paths in plain words, in <see cref="TrafficTestPath"/> order.</summary>
    public static readonly string[] PathNames =
    {
        "Gateway A alone, line looped back (its TD wired to its own RD)",
        "Gateway B alone, line looped back (its TD wired to its own RD)",
        "A and B wired to each other: A sends to B and B sends to A",
        "A and B wired to each other: only A sends, B only receives",
        "A and B wired to each other: only B sends, A only receives",
    };

    /// <summary>The path names for the picker.</summary>
    public string[] Paths => PathNames;

    /// <summary>The chosen path, one of <see cref="PathNames"/>.</summary>
    public string SelectedPath { get => _selectedPath; set => SetProperty(ref _selectedPath, value); }

    /// <summary>The chosen path as the engine's enum.</summary>
    public TrafficTestPath Path
    {
        get
        {
            int i = Array.IndexOf(PathNames, _selectedPath);
            return i < 0 ? TrafficTestPath.LoopbackA : (TrafficTestPath)i;
        }
        set => SelectedPath = PathNames[(int)value];
    }

    /// <summary>Live status line.</summary>
    public string StatusLine { get => _statusLine; private set => SetProperty(ref _statusLine, value); }

    /// <summary>Messages from the engine.</summary>
    public ObservableCollection<string> Messages { get; } = new ObservableCollection<string>();

    /// <summary>The report table.</summary>
    public string Report { get => _report; private set => SetProperty(ref _report, value); }

    /// <summary>RUNNING, PASS, FAIL or empty.</summary>
    public string Verdict { get => _verdict; private set => SetProperty(ref _verdict, value); }

    /// <summary>True while a test runs.</summary>
    public bool Running
    {
        get => _running;
        private set
        {
            if (SetProperty(ref _running, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Start.</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>Stop after the current rate drains.</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>Copy the knobs into the settings object for saving.</summary>
    public void SaveTo(AppSettings settings)
    {
        settings.TestRates = Rates;
        settings.TestSeconds = Seconds;
        settings.TestSize = Size;
        settings.TestWindow = Window;
        settings.TestTimeout = Timeout;
    }

    private void Start()
    {
        string[] parts = Rates.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        uint[] rates = new uint[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!uint.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out rates[i]) || rates[i] == 0)
            {
                Verdict = "'" + parts[i] + "' is not a bit rate.";
                return;
            }
        }
        if (rates.Length == 0)
        {
            Verdict = "Give at least one rate.";
            return;
        }
        if (Size < TestFrame.MinLength || Size > ByteFrame.MaxFrameLength)
        {
            Verdict = "Frame size must be " + TestFrame.MinLength + ".." + ByteFrame.MaxFrameLength + ".";
            return;
        }
        TrafficTestSettings settings = new TrafficTestSettings
        {
            Rates = rates,
            SecondsPerRate = Math.Max(1, Seconds),
            FrameLength = Size,
            WindowBytes = Math.Max(TestFrame.MinLength, Window),
            TimeoutSeconds = Math.Max(1, Timeout),
            NoBaud = NoBaud,
            VerboseFrames = 3,
            Path = Path,
        };
        SaveTo(_settings);
        _settings.Save();
        _recorder.Clear();
        Messages.Clear();
        Report = string.Empty;
        Task<bool> run;
        try
        {
            run = _set.RunTrafficTestAsync(settings, _recorder);
        }
        catch (InvalidOperationException ex)
        {
            Verdict = ex.Message;
            return;
        }
        Running = true;
        Verdict = "RUNNING";
        run.ContinueWith(t => _ui.Post(() =>
        {
            Running = false;
            Verdict = t.IsFaulted ? "FAILED: " + t.Exception?.GetBaseException().Message
                    : t.Result ? "PASS" : "FAIL";
            RefreshFromRecorder();
        }), TaskScheduler.Default);
    }

    private void RefreshFromRecorder()
    {
        StatusLine = _recorder.StatusLine;
        List<string> messages = _recorder.Messages;
        while (Messages.Count < messages.Count)
        {
            Messages.Add(messages[Messages.Count]);
        }
        List<RateResult> results = _recorder.Results;
        Report = results.Count == 0 ? string.Empty : TrafficTestRecorder.FormatReport(results);
    }
}
