// GATEWAY SESSION SET - the two panes together: pump thread, bridge pairing, TCP ports, traffic test.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using HdlcGatewayTest.Core;

namespace HdlcGatewayTest.Gateway;

/// <summary>
/// Owns pane A and pane B, the one pump thread that moves bytes for both (the same loop shape
/// the console tool's measured numbers came from), the Bridge pairing, the rule that two TCP
/// listeners need two different ports, and the traffic test, which borrows the links while it
/// runs.
/// </summary>
public sealed class GatewaySessionSet : IDisposable
{
    private Thread? _pump;
    private volatile bool _running;
    private CancellationTokenSource? _testCancel;

    /// <summary>Create the two sessions.</summary>
    public GatewaySessionSet()
    {
        A = new GatewaySession("A");
        B = new GatewaySession("B");
    }

    /// <summary>Pane A.</summary>
    public GatewaySession A { get; }

    /// <summary>Pane B.</summary>
    public GatewaySession B { get; }

    /// <summary>Both, in order.</summary>
    public GatewaySession[] All => new[] { A, B };

    /// <summary>True while a traffic test runs.</summary>
    public bool TestRunning => _testCancel != null;

    /// <summary>The engine of the running or last traffic test, or null.</summary>
    public TrafficTest? LastTest { get; private set; }

    /// <summary>
    /// A session by label, "A" or "B" (case-insensitive).
    /// </summary>
    /// <param name="label">The label.</param>
    /// <returns>The session, or null.</returns>
    public GatewaySession? ByLabel(string label)
    {
        if (string.Equals(label, "A", StringComparison.OrdinalIgnoreCase))
        {
            return A;
        }
        return string.Equals(label, "B", StringComparison.OrdinalIgnoreCase) ? B : null;
    }

    /// <summary>Start the pump thread. Idempotent.</summary>
    public void Start()
    {
        if (_running)
        {
            return;
        }
        _running = true;
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "gateway pump" };
        _pump.Start();
    }

    /// <summary>Stop the pump thread.</summary>
    public void Stop()
    {
        _running = false;
        _pump?.Join(1000);
        _pump = null;
    }

    /// <summary>
    /// Pair or unpair the panes as a bridge: a frame received on A is sent on B and the other way
    /// round. Both panes get the Bridge role, or go back to Console.
    /// </summary>
    /// <param name="on">True to bridge.</param>
    public void SetBridge(bool on)
    {
        if (on)
        {
            A.Peer = B;
            B.Peer = A;
            A.Role = GatewayRole.Bridge;
            B.Role = GatewayRole.Bridge;
        }
        else
        {
            A.Peer = null;
            B.Peer = null;
            if (A.Role == GatewayRole.Bridge)
            {
                A.Role = GatewayRole.Console;
            }
            if (B.Role == GatewayRole.Bridge)
            {
                B.Role = GatewayRole.Console;
            }
        }
    }

    /// <summary>
    /// Start a TCP listener on a pane after checking the other pane does not use the same port.
    /// </summary>
    /// <param name="session">A or B.</param>
    /// <param name="port">The port.</param>
    /// <param name="error">Why it was refused, or null.</param>
    /// <returns>True when listening.</returns>
    public bool TryStartTcpListen(GatewaySession session, int port, out string? error)
    {
        GatewaySession other = ReferenceEquals(session, A) ? B : A;
        if (other.TcpPort == port)
        {
            error = "Pane " + other.Label + " already listens on port " + port + "; A and B need different ports.";
            session.Console.Add(ConsoleLineKind.Notice, error, FrameSource.Console);
            return false;
        }
        return session.StartTcpListen(port, out error);
    }

    /// <summary>
    /// Run the traffic test on a worker thread over the path in <see cref="TrafficTestSettings.Path"/>:
    /// A->A, B->B, or A->B and B->A. The sessions' links are borrowed for the duration: their
    /// panes keep listing frames (the observer stays attached) but commands should not be sent
    /// meanwhile.
    /// </summary>
    /// <param name="settings">What to run, the path included.</param>
    /// <param name="observer">Who hears progress; called on the worker thread.</param>
    /// <returns>A task that completes with the pass verdict when the test ends.</returns>
    public Task<bool> RunTrafficTestAsync(TrafficTestSettings settings, ITrafficTestObserver observer)
    {
        if (_testCancel != null)
        {
            throw new InvalidOperationException("A traffic test is already running.");
        }
        TrafficTestPath path = settings.Path;
        bool useA = path != TrafficTestPath.LoopbackB;
        bool useB = path != TrafficTestPath.LoopbackA;
        GatewayLink? a = useA ? A.Link : null;
        GatewayLink? b = useB ? B.Link : null;
        if ((useA && a == null) || (useB && b == null))
        {
            throw new InvalidOperationException(useA && useB
                ? "Connect both panes first."
                : "Connect pane " + (useA ? "A" : "B") + " first.");
        }
        // The engine takes "a" as the sender for loopback, so pane B alone is handed over as "a".
        GatewayLink first = (a ?? b)!;
        GatewayLink? second = useA && useB ? b : null;    // two links: both ways or one way, per settings.Path
        string loopbackName = useA ? "A->A" : "B->B";
        CancellationTokenSource cts = new CancellationTokenSource();
        _testCancel = cts;
        TrafficTest test = new TrafficTest(settings, observer);
        LastTest = test;
        A.InTrafficTest = useA;
        B.InTrafficTest = useB;
        // The flags stop the pump thread's NEXT pass; wait for a pass already inside a link to end
        // before the engine pumps the same links from its own thread.
        A.WaitPumpIdle();
        B.WaitPumpIdle();
        return Task.Run(() =>
        {
            try
            {
                return test.Run(first, second, cts.Token, loopbackName);
            }
            finally
            {
                A.AttachFrameHandlers();
                B.AttachFrameHandlers();
                A.InTrafficTest = false;
                B.InTrafficTest = false;
                _testCancel = null;
                cts.Dispose();
            }
        });
    }

    /// <summary>Ask the running traffic test to stop after the current rate's drain.</summary>
    public void StopTrafficTest()
    {
        _testCancel?.Cancel();
    }

    private void PumpLoop()
    {
        while (_running)
        {
            int moved = 0;
            moved += PumpOne(A);
            moved += PumpOne(B);
            if (moved == 0)
            {
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>
    /// One pump of one session. An exception on this thread would end the whole process (an
    /// unhandled exception on a background thread is fatal in .NET), so it is caught here and
    /// written into that pane's console where a person sees it; the pump carries on.
    /// </summary>
    private static int PumpOne(GatewaySession session)
    {
        try
        {
            return session.Pump();
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            // A port vanished under us; the session's Faulted event reports it.
            return 0;
        }
        catch (Exception ex)
        {
            session.Console.Add(ConsoleLineKind.Notice, "Pump error: " + ex.GetType().Name + ": " + ex.Message,
                                FrameSource.Console);
            return 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        StopTrafficTest();
        Stop();
        A.Dispose();
        B.Dispose();
    }
}
