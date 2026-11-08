// TRAFFIC TEST OBSERVER - how a run reports progress to whoever is watching.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// Callbacks from <see cref="TrafficTest"/>, all on the engine's thread. The console tool
/// prints them; the GUI marshals them to its window.
/// </summary>
public interface ITrafficTestObserver
{
    /// <summary>
    /// A line of plain information: what was probed, a timeout that was raised, an error.
    /// </summary>
    /// <param name="text">The line.</param>
    void Message(string text);

    /// <summary>
    /// Once a second while a rate runs, and once more with <paramref name="done"/> true when it ends.
    /// </summary>
    /// <param name="rate">Line rate in bit/s.</param>
    /// <param name="directions">The live directions; read their <see cref="TrafficDirection.Statistics"/>.</param>
    /// <param name="elapsedTicks">Stopwatch ticks since the rate started.</param>
    /// <param name="done">True for the final status of the rate.</param>
    void Status(uint rate, TrafficDirection[] directions, long elapsedTicks, bool done);

    /// <summary>
    /// A rate finished (or could not start); its numbers are frozen in the result.
    /// </summary>
    /// <param name="result">The result.</param>
    void RateFinished(RateResult result);
}
