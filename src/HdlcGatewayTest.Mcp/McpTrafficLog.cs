// MCP TRAFFIC LOG - every tool call and its result, so a person can see what the AI did.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Mcp;

/// <summary>One MCP call or result.</summary>
/// <param name="Time">When.</param>
/// <param name="Tool">Tool name.</param>
/// <param name="IsResult">False for the call, true for its result.</param>
/// <param name="IsError">For a result: whether it was an error.</param>
/// <param name="Text">Arguments, or the first text of the result, cut short.</param>
/// <param name="Elapsed">For a result: how long the call took.</param>
public sealed record McpTrafficEntry(DateTime Time, string Tool, bool IsResult, bool IsError, string Text, TimeSpan Elapsed);

/// <summary>
/// A bounded list of MCP calls and results. The GUI shows it live; the CLI prints it when
/// verbose. Thread safe.
/// </summary>
public sealed class McpTrafficLog
{
    private readonly List<McpTrafficEntry> _entries = new List<McpTrafficEntry>();
    private readonly object _lock = new object();
    private readonly int _capacity;

    /// <summary>
    /// Create a log.
    /// </summary>
    /// <param name="capacity">Entries kept.</param>
    public McpTrafficLog(int capacity = 2000)
    {
        _capacity = Math.Max(16, capacity);
    }

    /// <summary>Raised after an entry is added, on the adding thread.</summary>
    public event Action<McpTrafficEntry>? Added;

    /// <summary>Record a call.</summary>
    /// <param name="tool">Tool name.</param>
    /// <param name="arguments">Arguments, one line.</param>
    public void LogCall(string tool, string arguments)
    {
        Add(new McpTrafficEntry(DateTime.Now, tool, false, false, Shorten(arguments), TimeSpan.Zero));
    }

    /// <summary>Record a result.</summary>
    /// <param name="tool">Tool name.</param>
    /// <param name="isError">Whether the result is an error.</param>
    /// <param name="text">The first text block of the result.</param>
    /// <param name="elapsed">How long the call took.</param>
    public void LogResult(string tool, bool isError, string text, TimeSpan elapsed)
    {
        Add(new McpTrafficEntry(DateTime.Now, tool, true, isError, Shorten(text), elapsed));
    }

    /// <summary>A copy of the entries, oldest first.</summary>
    public List<McpTrafficEntry> Snapshot()
    {
        lock (_lock)
        {
            return new List<McpTrafficEntry>(_entries);
        }
    }

    private void Add(McpTrafficEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count == _capacity)
            {
                _entries.RemoveAt(0);
            }
            _entries.Add(entry);
        }
        Added?.Invoke(entry);
    }

    private static string Shorten(string text)
    {
        string oneLine = text.Replace("\r\n", " ").Replace('\n', ' ');
        return oneLine.Length <= 200 ? oneLine : oneLine.Substring(0, 200) + "...";
    }
}
