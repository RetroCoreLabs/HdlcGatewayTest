// GATEWAY LINK - one gateway: its transport, its frame channel, and the way to give it commands.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Diagnostics;

namespace HdlcGatewayTest.Core;

/// <summary>How a link talks to the firmware for commands.</summary>
public enum GatewayCommandPath
{
    /// <summary>Not probed yet.</summary>
    Unknown,

    /// <summary>HELLO was answered: commands go as control frames, traffic keeps flowing.</summary>
    ControlFrames,

    /// <summary>No HELLO reply: commands go through the ESC ESC ESC text console (RC422Dev, old HDLCGW).</summary>
    TextConsole,

    /// <summary>Neither answered; the gateway may be absent or in a mode we cannot leave.</summary>
    None,
}

/// <summary>
/// One gateway as the test engine, the console tool and the GUI see it: a transport, the
/// <see cref="GatewayChannel"/> on top of it, the framed <see cref="GatewayConsole"/>, and a
/// <see cref="TextConsole"/> for firmware that has no control frames. <see cref="Probe"/>
/// decides which command path to use; <see cref="RunCommand"/> then hides the difference.
/// <see cref="Pump"/> moves received bytes into the channel and must be called regularly by
/// one thread.
/// </summary>
public sealed class GatewayLink : IDisposable
{
    private readonly byte[] _rx = new byte[8192];

    /// <summary>
    /// Wrap a transport. Nothing is opened here.
    /// </summary>
    /// <param name="transport">The transport; disposed with the link.</param>
    public GatewayLink(IGatewayTransport transport)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Channel = new GatewayChannel(transport.Write);
        Console = new GatewayConsole(Channel);
        Text = new TextConsole(transport);
    }

    /// <summary>The raw byte link.</summary>
    public IGatewayTransport Transport { get; }

    /// <summary>Frames in and out.</summary>
    public GatewayChannel Channel { get; }

    /// <summary>Commands over control frames.</summary>
    public GatewayConsole Console { get; }

    /// <summary>The legacy text console.</summary>
    public TextConsole Text { get; }

    /// <summary>The transport's name.</summary>
    public string Name => Transport.Name;

    /// <summary>Which way commands go, after <see cref="Probe"/>.</summary>
    public GatewayCommandPath CommandPath { get; private set; } = GatewayCommandPath.Unknown;

    /// <summary>What the firmware said to HELLO, or null.</summary>
    public GatewayIdentity? Identity { get; private set; }

    /// <summary>Open the transport.</summary>
    public void Open()
    {
        Transport.Open();
    }

    /// <summary>
    /// Read what the transport has and feed it to the channel. Handlers run on this thread.
    /// Bounded to a few buffers per call so a caller that holds a lock around it lets others
    /// in: an Echo pane wired in loopback feeds itself forever, and the first version of this
    /// loop never returned then (Gateway tests hung, 2026-11-08).
    /// </summary>
    /// <returns>Bytes moved; 0 when nothing had arrived.</returns>
    public int Pump()
    {
        int total = 0;
        for (int rounds = 0; rounds < 8; rounds++)
        {
            int n = Transport.Read(_rx);
            if (n <= 0)
            {
                break;
            }
            Channel.Feed(new ReadOnlySpan<byte>(_rx, 0, n));
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Find out how to give this gateway commands. The text console is tried FIRST (plain ESC
    /// ESC ESC, then CR, then flag plus ESC, the order hdlc-loopback always used) and left again
    /// with EXIT; then HELLO is sent. A firmware with control frames answers HELLO and ends up on
    /// that path; a text-console-only firmware (RC422Dev, old HDLCGW) stays on the console path.
    /// Why this order and not HELLO first: RC422Dev locks its console after the FIRST forwarded
    /// frame, and a HELLO it does not understand IS a forwarded frame, so HELLO-first left the
    /// Pico unreachable (bench 2026-11-08, a wasted two-board run). The console-first order also
    /// recovers a board left in DEBUG by an earlier session, where a HELLO would be typed into
    /// the input line. When neither the console nor HELLO answers, the path is None. Sets
    /// <see cref="CommandPath"/> and <see cref="Identity"/>. A legacy firmware clocks the one
    /// HELLO frame onto the line as one junk frame (bad FCS, dropped by any receiver).
    /// </summary>
    /// <param name="helloMilliseconds">How long to wait for a HELLO reply; HDLCGW answers within a few ms.</param>
    /// <param name="transcript">What the text console printed, when it was tried.</param>
    /// <param name="tryConsole">
    /// False sends HELLO only and never an ESC byte: for a gateway known to speak control frames
    /// (HDLCGW 0.2.0 and later), when the console must not be touched (Ronny, 2026-11-08: talk
    /// to the firmware with command frames, not ESC ESC ESC). RC422Dev then ends as None.
    /// </param>
    /// <returns>The command path found.</returns>
    public GatewayCommandPath Probe(int helloMilliseconds, out string transcript, bool tryConsole = false)
    {
        Identity = null;
        transcript = string.Empty;
        if (!tryConsole)
        {
            if (TryHello(helloMilliseconds))
            {
                return CommandPath;
            }
            Channel.Reset();
            CommandPath = GatewayCommandPath.None;
            return CommandPath;
        }

        if (!Text.Enter(out transcript))
        {
            // No console. A HELLO is safe now: a gateway that has no console and forwards the
            // frame is unreachable either way.
            if (TryHello(helloMilliseconds))
            {
                return CommandPath;
            }
            Channel.Reset();
            CommandPath = GatewayCommandPath.None;
            return CommandPath;
        }

        // The console answered. Before sending ANY frame, ask the console whether this firmware
        // has control frames: LOG is an HDLCGW command that RC422Dev does not know, and RC422Dev
        // locks its console after the first forwarded frame, so a HELLO it does not understand
        // would leave it unreachable (two wasted two-board runs, 2026-11-08).
        bool hasFrames = Text.Command("LOG", out string logReply)
                         && logReply.Contains("Log level", StringComparison.OrdinalIgnoreCase);
        Text.Exit();
        if (hasFrames && TryHello(helloMilliseconds))
        {
            return CommandPath;
        }
        Channel.Reset();
        CommandPath = GatewayCommandPath.TextConsole;
        return CommandPath;
    }

    /// <summary>One HELLO exchange; on success sets Identity and the ControlFrames path.</summary>
    private bool TryHello(int milliseconds)
    {
        Channel.Reset();
        ControlRequest hello = Console.Hello("HdlcGatewayTest");
        PumpUntil(hello, milliseconds);
        if (hello.State == ControlRequestState.Completed && hello.Payload != null
            && GatewayIdentity.TryParse(hello.Payload, out GatewayIdentity? identity) && identity != null)
        {
            Identity = identity;
            CommandPath = GatewayCommandPath.ControlFrames;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Run one console command on whichever path <see cref="Probe"/> found. On the text path
    /// the console is entered, the command run and the console left again, so forwarding
    /// resumes afterwards; the prompt is stripped from the reply.
    /// </summary>
    /// <param name="command">The command line without CR.</param>
    /// <param name="reply">What the gateway answered.</param>
    /// <param name="milliseconds">How long to wait for the end of the reply.</param>
    /// <returns>True when the reply completed (framed: LAST without ERROR; text: a prompt followed).</returns>
    public bool RunCommand(string command, out string reply, int milliseconds = 2000)
    {
        switch (CommandPath)
        {
            case GatewayCommandPath.ControlFrames:
            {
                Channel.Reset();
                ControlRequest request = Console.SendCommand(command);
                PumpUntil(request, milliseconds);
                reply = request.Text;
                return request.State == ControlRequestState.Completed;
            }

            case GatewayCommandPath.TextConsole:
            {
                if (!Text.Enter(out string transcript))
                {
                    reply = transcript;
                    Channel.Reset();
                    return false;
                }
                bool ok = Text.Command(command, out transcript, milliseconds);
                Text.Exit();
                Channel.Reset();
                reply = StripEcho(transcript, command);
                return ok;
            }

            default:
                reply = "the gateway on " + Name + " answered neither HELLO nor the text console";
                return false;
        }
    }

    /// <summary>Feed port bytes to the channel until the request ends or the time is up.</summary>
    private void PumpUntil(ControlRequest request, int milliseconds)
    {
        long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * milliseconds / 1000);
        while (request.State == ControlRequestState.Pending && Stopwatch.GetTimestamp() < deadline)
        {
            if (Pump() == 0)
            {
                Thread.Sleep(2);
            }
        }
        if (request.State == ControlRequestState.Pending)
        {
            request.MarkTimedOut();
        }
    }

    /// <summary>
    /// The text console echoes the command and ends with a prompt; keep only what is between.
    /// </summary>
    private static string StripEcho(string transcript, string command)
    {
        string s = transcript;
        int start = s.IndexOf(command, StringComparison.Ordinal);
        if (start >= 0)
        {
            s = s.Substring(start + command.Length);
        }
        s = s.TrimStart('\r', '\n');
        int lastLine = s.LastIndexOf('\n');
        if (lastLine >= 0 && TextConsole.IsPrompt(s))
        {
            s = s.Substring(0, lastLine + 1);
        }
        else if (TextConsole.IsPrompt(s))
        {
            s = string.Empty;
        }
        return s;
    }

    /// <summary>Cancel pending requests and dispose the transport.</summary>
    public void Dispose()
    {
        Console.CancelAll();
        Transport.Dispose();
    }
}
