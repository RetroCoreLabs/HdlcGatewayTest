// CONSOLE LOG - what a pane's console shows: commands, replies, firmware log lines, notices.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Gateway;

/// <summary>What kind of line a console line is; the GUI colours by it.</summary>
public enum ConsoleLineKind
{
    /// <summary>A command the user (or an AI over MCP) sent.</summary>
    Command,

    /// <summary>A line of the firmware's reply to a command.</summary>
    Reply,

    /// <summary>An asynchronous firmware log line (LOG frame).</summary>
    Log,

    /// <summary>A firmware event (EVENT frame).</summary>
    Event,

    /// <summary>Raw text from the legacy text console or the boot banner.</summary>
    RawText,

    /// <summary>The tool's own message: connected, probe result, errors.</summary>
    Notice,
}

/// <summary>One console line.</summary>
/// <param name="Sequence">Position since the pane was created, counting from 1.</param>
/// <param name="Time">When it was added.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Text">The line, without line ending.</param>
/// <param name="Source">Who caused it: Console, Mcp, or Serial for firmware output.</param>
public sealed record ConsoleLine(long Sequence, DateTime Time, ConsoleLineKind Kind, string Text, FrameSource Source);

/// <summary>
/// A bounded list of console lines. Thread safe; readers take snapshots since a sequence number
/// (the MCP <c>hdlc_readnew</c> cursor), the GUI listens to <see cref="Added"/>.
/// </summary>
public sealed class ConsoleLog
{
    private readonly ConsoleLine?[] _ring;
    private readonly object _lock = new object();
    private long _next = 1;

    /// <summary>
    /// Create a log.
    /// </summary>
    /// <param name="capacity">Lines kept before the oldest is dropped.</param>
    public ConsoleLog(int capacity = 5000)
    {
        _ring = new ConsoleLine?[Math.Max(16, capacity)];
    }

    /// <summary>Raised after a line is added, on the adding thread.</summary>
    public event Action<ConsoleLine>? Added;

    /// <summary>Lines added since creation.</summary>
    public long Total => _next - 1;

    /// <summary>
    /// Add one line.
    /// </summary>
    /// <param name="kind">What it is.</param>
    /// <param name="text">The text; CR and LF at the end are removed.</param>
    /// <param name="source">Who caused it.</param>
    /// <returns>The line.</returns>
    public ConsoleLine Add(ConsoleLineKind kind, string text, FrameSource source = FrameSource.Serial)
    {
        ConsoleLine line;
        lock (_lock)
        {
            line = new ConsoleLine(_next, DateTime.Now, kind, text.TrimEnd('\r', '\n'), source);
            _ring[(int)(_next % _ring.Length)] = line;
            _next++;
        }
        Added?.Invoke(line);
        return line;
    }

    /// <summary>
    /// Add a block of text as one line per CR/LF-separated line. Empty trailing lines are dropped.
    /// </summary>
    /// <param name="kind">What the lines are.</param>
    /// <param name="text">The block.</param>
    /// <param name="source">Who caused it.</param>
    public void AddLines(ConsoleLineKind kind, string text, FrameSource source = FrameSource.Serial)
    {
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int last = lines.Length;
        while (last > 0 && lines[last - 1].Length == 0)
        {
            last--;
        }
        for (int i = 0; i < last; i++)
        {
            Add(kind, lines[i], source);
        }
    }

    /// <summary>
    /// The lines with a sequence number above <paramref name="afterSequence"/>, oldest first.
    /// </summary>
    /// <param name="afterSequence">Last sequence already seen; 0 for everything kept.</param>
    /// <param name="max">Cap on the result.</param>
    /// <returns>The lines.</returns>
    public List<ConsoleLine> Since(long afterSequence, int max = int.MaxValue)
    {
        List<ConsoleLine> result = new List<ConsoleLine>();
        lock (_lock)
        {
            long first = Math.Max(afterSequence + 1, Math.Max(1, _next - _ring.Length));
            for (long s = first; s < _next && result.Count < max; s++)
            {
                ConsoleLine? l = _ring[(int)(s % _ring.Length)];
                if (l != null && l.Sequence == s)
                {
                    result.Add(l);
                }
            }
        }
        return result;
    }

    /// <summary>Forget everything kept. Sequence numbers keep counting.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_ring);
        }
    }
}
