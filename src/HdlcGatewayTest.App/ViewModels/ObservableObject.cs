// OBSERVABLE OBJECT - the small base every view model needs, written by hand instead of a library.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace HdlcGatewayTest.App.ViewModels;

/// <summary>
/// Raises <see cref="PropertyChanged"/> so the window follows the view model. A few lines beat a
/// source-generator dependency for a handful of view models (plan decision 2026-11-08).
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Store a value and raise the event when it changed.
    /// </summary>
    /// <typeparam name="T">Property type.</typeparam>
    /// <param name="field">Backing field.</param>
    /// <param name="value">New value.</param>
    /// <param name="name">Property name, filled in by the compiler.</param>
    /// <returns>True when the value changed.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>
    /// Raise the event for a property by name.
    /// </summary>
    /// <param name="name">Property name.</param>
    protected void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A button's action with an enable rule.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>
    /// Create a command.
    /// </summary>
    /// <param name="execute">What it does.</param>
    /// <param name="canExecute">When it is enabled; null for always.</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc/>
    public bool CanExecute(object? parameter)
    {
        return _canExecute == null || _canExecute();
    }

    /// <inheritdoc/>
    public void Execute(object? parameter)
    {
        _execute();
    }

    /// <summary>Tell the buttons to ask <see cref="CanExecute"/> again.</summary>
    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
