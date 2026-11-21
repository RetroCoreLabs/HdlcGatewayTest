// PANE SETUP WINDOW - code-behind: OK validates and closes with true, Cancel closes with false.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using Avalonia.Controls;
using Avalonia.Interactivity;
using HdlcGatewayTest.App.ViewModels;

namespace HdlcGatewayTest.App.Views;

public partial class PaneSetupWindow : Window
{
    public PaneSetupWindow()
    {
        InitializeComponent();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PaneSetupViewModel setup && !setup.Validate())
        {
            return;    // the error line in the window says why
        }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
