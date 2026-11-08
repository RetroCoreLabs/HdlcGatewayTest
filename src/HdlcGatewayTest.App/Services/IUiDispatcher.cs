// UI DISPATCHER - "run this on the UI thread", replaceable so view models are tested without a window.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using Avalonia.Threading;

namespace HdlcGatewayTest.App.Services;

/// <summary>Posts work to the UI thread.</summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Run an action on the UI thread, later. Never blocks the caller.
    /// </summary>
    /// <param name="action">The work.</param>
    void Post(Action action);
}

/// <summary>The real one: Avalonia's UI thread.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <inheritdoc/>
    public void Post(Action action)
    {
        Dispatcher.UIThread.Post(action);
    }
}

/// <summary>For tests: runs the action at once on the calling thread.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    /// <inheritdoc/>
    public void Post(Action action)
    {
        action();
    }
}
