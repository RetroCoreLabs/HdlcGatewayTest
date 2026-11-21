// GATEWAY PANE VIEW - code-behind: keyboard in the command box and auto-scroll of the console.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using Avalonia.Controls;
using Avalonia.Input;
using HdlcGatewayTest.App.ViewModels;

namespace HdlcGatewayTest.App.Views;

public partial class GatewayPaneView : UserControl
{
    private GatewayPaneViewModel? _vm;

    public GatewayPaneView()
    {
        InitializeComponent();
        CommandBox.KeyDown += OnCommandKeyDown;
        DataContextChanged += (_, _) =>
        {
            if (_vm != null)
            {
                _vm.ConsoleLineAdded -= OnConsoleLineAdded;
                _vm.SetupRequested -= OnSetupRequested;
            }
            _vm = DataContext as GatewayPaneViewModel;
            if (_vm != null)
            {
                _vm.ConsoleLineAdded += OnConsoleLineAdded;
                _vm.SetupRequested += OnSetupRequested;
            }
        };
    }

    /// <summary>Open the Setup window over the main window; apply when OK was pressed.</summary>
    private async void OnSetupRequested(GatewayPaneViewModel pane)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        pane.RefreshEndpoints();
        PaneSetupViewModel setup = PaneSetupViewModel.From(pane);
        PaneSetupWindow window = new PaneSetupWindow { DataContext = setup };
        bool ok = await window.ShowDialog<bool>(owner);
        if (ok)
        {
            pane.ApplySetup(setup);
        }
    }

    private void OnConsoleLineAdded()
    {
        if (_vm != null && _vm.AutoScroll && ConsoleList.ItemCount > 0)
        {
            ConsoleList.ScrollIntoView(ConsoleList.ItemCount - 1);
        }
    }

    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                if (_vm.SendCommand.CanExecute(null))
                {
                    _vm.SendCommand.Execute(null);
                }
                e.Handled = true;
                break;
            case Key.Up:
                _vm.HistoryUp();
                CommandBox.CaretIndex = CommandBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Down:
                _vm.HistoryDown();
                CommandBox.CaretIndex = CommandBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.CommandText = string.Empty;
                e.Handled = true;
                break;
        }
    }
}
