// TEE WRITER - everything written goes to the console and to a log file.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Text;

namespace HdlcGatewayTest.Loopback;

/// <summary>
/// A TextWriter that forwards to two others and flushes after every write, so a log file
/// written with <c>--log</c> shows the same text as the window at the same moment. Used so
/// that somebody who cannot see the window (a script, another session) can still read what
/// the run said.
/// </summary>
public sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _first;
    private readonly TextWriter _second;

    /// <summary>
    /// Combine two writers.
    /// </summary>
    /// <param name="first">Normally the console.</param>
    /// <param name="second">Normally the log file.</param>
    public TeeWriter(TextWriter first, TextWriter second)
    {
        _first = first;
        _second = second;
    }

    /// <inheritdoc/>
    public override Encoding Encoding => _first.Encoding;

    /// <inheritdoc/>
    public override void Write(char value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    /// <inheritdoc/>
    public override void Write(string? value)
    {
        _first.Write(value);
        _second.Write(value);
        _second.Flush();
    }

    /// <inheritdoc/>
    public override void WriteLine(string? value)
    {
        _first.WriteLine(value);
        _second.WriteLine(value);
        _second.Flush();
    }

    /// <inheritdoc/>
    public override void WriteLine()
    {
        _first.WriteLine();
        _second.WriteLine();
        _second.Flush();
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        _first.Flush();
        _second.Flush();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _second.Flush();
            _second.Dispose();
        }
        base.Dispose(disposing);
    }
}
