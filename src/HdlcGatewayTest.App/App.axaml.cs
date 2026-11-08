// APP - Avalonia application: loads the XAML and opens the main window.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HdlcGatewayTest.App.Services;
using HdlcGatewayTest.App.ViewModels;
using HdlcGatewayTest.App.Views;
using HdlcGatewayTest.Serial;

namespace HdlcGatewayTest.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppSettings settings = AppSettings.Load();
            MainViewModel main = new MainViewModel(settings, new EndpointTransportFactory(), new AvaloniaUiDispatcher(),
                                                   Program.Options);
            desktop.MainWindow = new MainWindow { DataContext = main };
            desktop.Exit += (_, _) => main.Shutdown();
            main.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
