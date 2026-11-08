// RATE RESULT - the frozen numbers of one rate of a traffic test.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// One row group of the final report: the rate, each direction's name and a copy of its
/// counters (the direction objects are reused per rate), and an error when the rate could not
/// run at all.
/// </summary>
public sealed class RateResult
{
    /// <summary>
    /// Freeze the numbers of a rate.
    /// </summary>
    /// <param name="rate">Line rate in bit/s.</param>
    /// <param name="directions">The directions as they stand now.</param>
    /// <param name="error">Why the rate could not run, or null.</param>
    public RateResult(uint rate, TrafficDirection[] directions, string? error)
    {
        Rate = rate;
        DirectionNames = new string[directions.Length];
        Statistics = new TrafficStatistics[directions.Length];
        for (int i = 0; i < directions.Length; i++)
        {
            DirectionNames[i] = directions[i].Name;
            Statistics[i] = directions[i].Statistics.Clone();
        }
        Error = error;
    }

    /// <summary>Line rate in bit/s.</summary>
    public uint Rate { get; }

    /// <summary>Direction labels, same order as <see cref="Statistics"/>.</summary>
    public string[] DirectionNames { get; }

    /// <summary>Frozen counters per direction.</summary>
    public TrafficStatistics[] Statistics { get; }

    /// <summary>Why the rate did not run, or null.</summary>
    public string? Error { get; }

    /// <summary>True when there was no error and every direction passed.</summary>
    public bool Passed
    {
        get
        {
            if (Error != null)
            {
                return false;
            }
            bool ok = true;
            for (int i = 0; i < Statistics.Length; i++)
            {
                ok &= Statistics[i].Passed;
            }
            return ok;
        }
    }
}
