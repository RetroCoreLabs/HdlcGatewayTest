// TEXT CONSOLE - the legacy ESC ESC ESC console of both gateways, driven over any transport.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Diagnostics;
using System.Text;

namespace HdlcGatewayTest.Core;

/// <summary>
/// Knows the three-ESC text console well enough to open it, run a command, wait for the prompt
/// and leave again. This is the only way to talk to RC422Dev and to HDLCGW builds before
/// control frames; <see cref="GatewayLink"/> falls back to it when HELLO goes unanswered.
/// Reads the transport directly (not through a <see cref="GatewayChannel"/>), because console
/// text is not framed; the caller resets its channel afterwards.
/// </summary>
public sealed class TextConsole
{
    private readonly IGatewayTransport _transport;

    /// <summary>
    /// Attach to a transport.
    /// </summary>
    /// <param name="transport">The open transport.</param>
    public TextConsole(IGatewayTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Write a text line: the text followed by CR, which both gateways take as the end of a
    /// command.
    /// </summary>
    /// <param name="text">The command without line ending.</param>
    public void WriteLine(string text)
    {
        _transport.Write(Encoding.Latin1.GetBytes(text + "\r"));
    }

    /// <summary>
    /// Read whatever arrives for a while and return it as text. Used to drain console output
    /// before binary traffic starts.
    /// </summary>
    /// <param name="milliseconds">How long to keep reading.</param>
    /// <returns>Everything read, bytes mapped one to one to characters.</returns>
    public string ReadTextFor(int milliseconds)
    {
        StringBuilder sb = new StringBuilder();
        byte[] buf = new byte[4096];
        long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * milliseconds / 1000);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            int n = _transport.Read(buf);
            if (n > 0)
            {
                sb.Append(Encoding.Latin1.GetString(buf, 0, n));
            }
            else
            {
                Thread.Sleep(5);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Read until the text contains <paramref name="expected"/> or the time is up.
    /// </summary>
    /// <param name="expected">Text to wait for, compared case-insensitively.</param>
    /// <param name="milliseconds">How long to wait at most.</param>
    /// <param name="transcript">Everything read while waiting.</param>
    /// <returns>True when the text appeared.</returns>
    public bool WaitForText(string expected, int milliseconds, out string transcript)
    {
        return WaitUntil(milliseconds, s => s.Contains(expected, StringComparison.OrdinalIgnoreCase), out transcript);
    }

    /// <summary>
    /// Wait for a console prompt. HDLCGW prints "# " or "@ " (HDLC decode on), RC422Dev the
    /// same with a '>' before the space when echo is on; either may be preceded by "[rx:tx]".
    /// </summary>
    /// <param name="milliseconds">How long to wait at most.</param>
    /// <param name="transcript">Everything read while waiting.</param>
    /// <returns>True when a prompt appeared.</returns>
    public bool WaitForPrompt(int milliseconds, out string transcript)
    {
        return WaitUntil(milliseconds, IsPrompt, out transcript);
    }

    /// <summary>
    /// Whether text ends in one of the gateway prompts.
    /// </summary>
    /// <param name="text">Console text.</param>
    /// <returns>True for "# ", "@ ", "#&gt; " or "@&gt; " at the end.</returns>
    public static bool IsPrompt(string text)
    {
        return text.EndsWith("# ", StringComparison.Ordinal) || text.EndsWith("@ ", StringComparison.Ordinal)
               || text.EndsWith("#> ", StringComparison.Ordinal) || text.EndsWith("@> ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Open the debug console: three ESC bytes while the gateway is idle in forwarding mode.
    /// Both gateways answer with a banner line and a prompt ending in '#' or '@'.
    /// </summary>
    /// <param name="transcript">What the gateway printed.</param>
    /// <returns>True when a prompt appeared.</returns>
    public bool Enter(out string transcript)
    {
        // Anything the gateway printed earlier (a version banner, a stale prompt) is not ours.
        _ = ReadTextFor(200);

        // The gateway may be in any of its three modes from an earlier session, and both
        // firmwares only look for ESC ESC ESC while their byte-frame decoder is between
        // frames. The order of the probes matters, and the two gateways disagree on one byte:
        // a flag puts HDLCGW's decoder back to "between frames", but OPENS a frame on
        // RC422Dev, whose decoder has not seen a flag yet after boot; three ESCs sent after a
        // flag there become frame data, the junk frame is forwarded, and RC422Dev's console
        // is locked until a reset (that happened on the bench 2026-11-08). So:
        //
        //   1. plain ESC ESC ESC    FORWARDING on either gateway right after boot -> prompt.
        //                           PACKET_GEN -> back to FORWARDING, no prompt yet.
        //   2. CR                   DEBUG: the ESCs sit in the input line, CR runs that junk
        //                           line ("Unknown command") and prints a prompt.
        //   3. flag, ESC ESC ESC    HDLCGW left mid-frame by an earlier session (the CR of
        //                           step 2 has polluted its decoder too): the flag clears it.
        //                           RC422Dev never gets here when it was in FORWARDING.
        _transport.Write(new byte[] { 0x1B, 0x1B, 0x1B });
        if (WaitForPrompt(1000, out transcript))
        {
            return true;
        }
        _transport.Write(new byte[] { 0x0D });
        if (WaitForPrompt(600, out transcript))
        {
            return true;
        }
        _transport.Write(new byte[] { ByteFrame.Flag, 0x1B, 0x1B, 0x1B });
        return WaitForPrompt(2000, out transcript);
    }

    /// <summary>
    /// Run one console command and wait for the next prompt.
    /// </summary>
    /// <param name="command">The command line without CR.</param>
    /// <param name="transcript">What the gateway printed, prompt included.</param>
    /// <param name="milliseconds">How long to wait for the prompt.</param>
    /// <returns>True when a prompt followed.</returns>
    public bool Command(string command, out string transcript, int milliseconds = 2000)
    {
        WriteLine(command);
        return WaitForPrompt(milliseconds, out transcript);
    }

    /// <summary>
    /// Leave the console with EXIT and throw away the text the gateway prints on the way out,
    /// so the first byte a frame decoder sees afterwards is a flag.
    /// </summary>
    public void Exit()
    {
        WriteLine("EXIT");
        _ = ReadTextFor(300);
    }

    private bool WaitUntil(int milliseconds, Func<string, bool> done, out string transcript)
    {
        StringBuilder sb = new StringBuilder();
        byte[] buf = new byte[4096];
        long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * milliseconds / 1000);
        bool found = false;
        while (!found && Stopwatch.GetTimestamp() < deadline)
        {
            int n = _transport.Read(buf);
            if (n > 0)
            {
                sb.Append(Encoding.Latin1.GetString(buf, 0, n));
                found = done(sb.ToString());
            }
            else
            {
                Thread.Sleep(5);
            }
        }
        transcript = sb.ToString();
        return found;
    }
}
