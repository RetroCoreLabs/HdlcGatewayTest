// SCREENSHOTS - draws the real window off screen against fake gateways and saves PNG pictures.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HdlcGatewayTest.App.Services;
using HdlcGatewayTest.App.ViewModels;
using HdlcGatewayTest.App.Views;
using HdlcGatewayTest.Core;
using HdlcGatewayTest.Core.Tests;
using HdlcGatewayTest.Gateway;
using NUnit.Framework;

namespace HdlcGatewayTest.App.Tests;

/// <summary>
/// Makes the pictures for the documentation: the real window, drawn off screen by
/// Avalonia.Headless with Skia, in front of fake gateways that answer HELLO like the bench
/// boards (HDLCGW on an ESP32-S2 as COM10, RC422Dev on a Pico as COM18) and loop every data
/// frame back. Nothing opens on the desktop and no real port is touched. The user's saved
/// settings are never read or written: the window gets a fresh settings object and nothing
/// that saves (MCP switch, Shutdown) is called.
/// <para>
/// Explicit: run it by name when the window changes, then commit the pictures.
/// <c>dotnet test tests/HdlcGatewayTest.App.Tests -nodeReuse:false --filter "TestCategory=Screenshots"</c>
/// from <c>src</c>. The PNG files go to <c>src/docs/screenshots</c>.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Writes the documentation pictures; run by name.")]
[Category("Screenshots")]
public sealed class Screenshots
{
    private const string EspHello =
        "name=HDLCGW\nversion=0.3.5\nbuild=bench\nchip=ESP32-S2\nboard=LOLIN S2 Mini\nmaxframe=4105\ncaps=cmd,log,stat\n"
        + "rev=0.0\ncores=1\nmhz=240\nflash=4194304\nflashimage=4194304\nflashmhz=80\nflashmode=dio\n"
        + "ram=154000\nramfree=12848\nramleast=11044\npsram=2097152\npsramfree=2003376\n";

    private const string PicoHello =
        "name=RC422Dev\nversion=0.2.0\nbuild=bench\nchip=RP2040\nboard=Raspberry Pi Pico\nmaxframe=4105\ncaps=cmd,stat\n"
        + "cores=2\nflashimage=2097152\nram=270336\npsram=0\n";

    private HeadlessUnitTestSession? _session;
    private string _outDir = string.Empty;

    /// <summary>The app as the headless session builds it: the real App, Skia drawing, Inter font.</summary>
    /// <returns>The app builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    [OneTimeSetUp]
    public void Start()
    {
        _session = HeadlessUnitTestSession.StartNew(typeof(Screenshots));
        _outDir = Path.Combine(FindSrc(), "docs", "screenshots");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void Stop()
    {
        _session?.Dispose();
    }

    /// <summary>Endpoints named like the bench ports, each with a fake gateway behind it.</summary>
    private sealed class BenchFactory : IGatewayTransportFactory
    {
        public readonly Dictionary<string, FakeGateway> Gateways = new Dictionary<string, FakeGateway>();

        public string[] ListEndpoints() => new[] { "COM10", "COM18" };

        public IGatewayTransport Create(string endpoint)
        {
            FakeTransport t = new FakeTransport { Name = endpoint };
            FakeGateway g = new FakeGateway(t, FakeGateway.Kind.ControlFrames)
            {
                HelloText = endpoint == "COM18" ? PicoHello : EspHello,
            };
            Gateways[endpoint] = g;
            return t;
        }
    }

    [TestCase("light")]
    [TestCase("dark")]
    public void Take(string theme)
    {
        _session!.Dispatch(() => TakeAll(theme), CancellationToken.None).GetAwaiter().GetResult();
    }

    private void TakeAll(string theme)
    {
        Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        BenchFactory factory = new BenchFactory();
        MainViewModel main = new MainViewModel(new AppSettings(), factory, new AvaloniaUiDispatcher(), StartupOptions.Parse(Array.Empty<string>()));
        main.Start();
        MainWindow window = new MainWindow { DataContext = main, Width = 1400, Height = 900 };
        window.Show();
        try
        {
            // 1. Just started: nothing connected.
            Save(window, "01-start", theme);

            // 2. Both boards connected: the ESP on pane A, the Pico on pane B.
            main.PaneA.SelectedEndpoint = "COM10";
            main.PaneA.ConnectCommand.Execute(null);
            main.PaneB.SelectedEndpoint = "COM18";
            main.PaneB.ConnectCommand.Execute(null);
            Assert.That(PumpUntil(() => main.PaneA.IsConnected && main.PaneB.IsConnected), Is.True, "connect");
            main.PaneA.CommandText = "STAT";
            main.PaneA.SendCommand.Execute(null);
            main.PaneB.CommandText = "BAUD 307200";
            main.PaneB.SendCommand.Execute(null);
            PumpFor(500);
            // The whole Version table, hardware rows included, in the picture: a taller window and
            // a taller tab row (300 pixels in MainWindow.axaml; a user drags the bar above it).
            Grid root = (Grid)window.Content!;
            GridLength tabRow = root.RowDefinitions[2].Height;
            window.Height = 1100;
            root.RowDefinitions[2].Height = new GridLength(460);
            SelectTab(window, "Version");
            Save(window, "02-connected-version", theme);
            root.RowDefinitions[2].Height = tabRow;
            window.Height = 900;

            // 3. Pane A in the Free role: frames typed in hex, decoded in the frame list.
            main.PaneA.SelectedRole = GatewayPaneViewModel.RoleName(GatewayRole.Free);
            string[] frames = { "01 3F", "03 73", "01 00 21 13 00 04 01 02", "01 01", "03 21 21 13 00 06" };
            for (int i = 0; i < frames.Length; i++)
            {
                main.PaneA.HexText = frames[i];
                main.PaneA.SendHexCommand.Execute(null);
            }
            Assert.That(PumpUntil(() => main.PaneA.Frames.Count >= 2 * frames.Length), Is.True, "frames");
            main.PaneA.SelectedFrame = main.PaneA.Frames[main.PaneA.Frames.Count - 1];
            PumpFor(200);
            Save(window, "03-free-role-frames", theme);

            // 4. The Setup window of pane A.
            PaneSetupWindow setup = new PaneSetupWindow { DataContext = PaneSetupViewModel.From(main.PaneA) };
            setup.Show();
            PumpFor(300);
            Save(setup, "04-pane-setup", theme);
            setup.Close();

            // 5. A short loopback traffic test on pane A, finished. Taller window so the result
            //    table fits. The fake loops frames back with no line in between, so the speed in
            //    this picture is not a line speed.
            window.Height = 1150;
            main.PaneA.SelectedRole = GatewayPaneViewModel.RoleName(GatewayRole.Console);
            main.Traffic.Rates = "307200";
            main.Traffic.Seconds = 3;
            main.Traffic.Size = 1024;
            SelectTab(window, "Traffic test");
            main.Traffic.StartCommand.Execute(null);
            Assert.That(PumpUntil(() => main.Traffic.Running, 3000), Is.True, "test start");
            Assert.That(PumpUntil(() => !main.Traffic.Running, 30000), Is.True, "test end");
            PumpFor(300);
            Save(window, "05-traffic-test", theme);

            // 6. The MCP tab (server off: the switch saves settings, so it is not touched).
            SelectTab(window, "MCP");
            Save(window, "06-mcp-tab", theme);
        }
        finally
        {
            window.Close();
            main.Sessions.Dispose();
        }
    }

    /// <summary>Height of the drawn title line, in pixels.</summary>
    private const int TitleHeight = 32;

    /// <summary>Width of the drawn border, in pixels.</summary>
    private const int BorderWidth = 1;

    /// <summary>
    /// Save the window's picture inside a drawn window frame: a border and a title line with the
    /// window's own title. A headless capture holds only the client area, and without the frame
    /// the picture ran straight into the README page around it (Ronny, 21 November 2026: "it flows
    /// into the text and it's confusing"). The colours follow Windows 11's own frame in the light
    /// and the dark theme.
    /// </summary>
    private void Save(Window window, string name, string theme)
    {
        PumpFor(100);
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.That(frame, Is.Not.Null, name);
        bool dark = theme == "dark";
        PixelSize inner = frame!.PixelSize;
        PixelSize outer = new PixelSize(inner.Width + (2 * BorderWidth), inner.Height + TitleHeight + (2 * BorderWidth));
        using RenderTargetBitmap framed = new RenderTargetBitmap(outer, new Vector(96, 96));
        using (DrawingContext dc = framed.CreateDrawingContext())
        {
            SolidColorBrush border = new SolidColorBrush(dark ? Color.FromRgb(0x5A, 0x5A, 0x5A) : Color.FromRgb(0x9A, 0x9A, 0x9A));
            SolidColorBrush bar = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
            IBrush text = dark ? Brushes.White : Brushes.Black;
            dc.FillRectangle(border, new Rect(0, 0, outer.Width, outer.Height));
            dc.FillRectangle(bar, new Rect(BorderWidth, BorderWidth, inner.Width, TitleHeight));
            FormattedText title = new FormattedText(window.Title ?? string.Empty, CultureInfo.InvariantCulture,
                                                    FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 13, text);
            dc.DrawText(title, new Point(BorderWidth + 12, BorderWidth + ((TitleHeight - title.Height) / 2)));
            dc.DrawImage(frame, new Rect(0, 0, inner.Width, inner.Height),
                         new Rect(BorderWidth, BorderWidth + TitleHeight, inner.Width, inner.Height));
        }
        framed.Save(Path.Combine(_outDir, name + "-" + theme + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void SelectTab(Window window, string header)
    {
        TabControl? tabs = window.FindDescendantOfType<TabControl>();
        Assert.That(tabs, Is.Not.Null);
        for (int i = 0; i < tabs!.ItemCount; i++)
        {
            if (tabs.Items[i] is TabItem item && Equals(item.Header, header))
            {
                tabs.SelectedIndex = i;
                PumpFor(100);
                return;
            }
        }
        Assert.Fail("no tab " + header);
    }

    /// <summary>Run the UI thread's queue until the condition holds; the fakes answer on the pump thread.</summary>
    private static bool PumpUntil(Func<bool> condition, int milliseconds = 5000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return true;
            }
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    /// <summary>Let posted updates and layout settle before a picture.</summary>
    private static void PumpFor(int milliseconds)
    {
        long end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }
    }

    private static string FindSrc()
    {
        DirectoryInfo? d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "HdlcGatewayTest.slnx")))
        {
            d = d.Parent;
        }
        Assert.That(d, Is.Not.Null, "src folder not found above the test folder");
        return d!.FullName;
    }
}
