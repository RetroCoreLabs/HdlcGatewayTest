// CONTROL REQUEST - one request to the firmware and the reply chunks that answer it.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Text;

namespace HdlcGatewayTest.Core;

/// <summary>How a request ended.</summary>
public enum ControlRequestState
{
    /// <summary>Sent, no final chunk yet.</summary>
    Pending,

    /// <summary>The LAST chunk arrived and every chunk before it was seen.</summary>
    Completed,

    /// <summary>The LAST chunk arrived with the ERROR flag, or a NAK answered the request.</summary>
    Failed,

    /// <summary>The LAST chunk arrived but a chunk before it was missing (dropped on the USB path).</summary>
    Incomplete,

    /// <summary>No final chunk within the time the caller allowed.</summary>
    TimedOut,
}

/// <summary>
/// One request sent by <see cref="GatewayConsole"/>: collects the REPLY chunks with its id in
/// order, checks the chunk counter for gaps, and completes on the LAST flag. Wait with
/// <see cref="Wait"/> or <see cref="Completion"/>.
/// </summary>
public sealed class ControlRequest
{
    private readonly StringBuilder _text = new StringBuilder();
    private readonly TaskCompletionSource<ControlRequestState> _completion =
        new TaskCompletionSource<ControlRequestState>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new object();
    private int _expectedChunk;
    private bool _gap;
    private byte[]? _payload;

    internal ControlRequest(byte id, ControlFrameType type)
    {
        Id = id;
        Type = type;
    }

    /// <summary>The request id the firmware copies into its replies.</summary>
    public byte Id { get; }

    /// <summary>What was sent.</summary>
    public ControlFrameType Type { get; }

    /// <summary>Current state.</summary>
    public ControlRequestState State { get; private set; } = ControlRequestState.Pending;

    /// <summary>The reply text collected so far (all chunks, in order).</summary>
    public string Text
    {
        get
        {
            lock (_lock)
            {
                return _text.ToString();
            }
        }
    }

    /// <summary>The raw payload of the final frame (HELLO_REPLY, NAK), or null.</summary>
    public byte[]? Payload => _payload;

    /// <summary>NAK reason when the firmware refused the request, or null.</summary>
    public NakReason? Nak { get; private set; }

    /// <summary>Completes with the final state. Never faults.</summary>
    public Task<ControlRequestState> Completion => _completion.Task;

    /// <summary>
    /// Wait for the request to end.
    /// </summary>
    /// <param name="milliseconds">How long at most.</param>
    /// <returns>The final state; <see cref="ControlRequestState.TimedOut"/> when time ran out.</returns>
    public ControlRequestState Wait(int milliseconds)
    {
        if (_completion.Task.Wait(milliseconds))
        {
            return _completion.Task.Result;
        }
        MarkTimedOut();
        return State;
    }

    /// <summary>Mark the request as timed out; later chunks are ignored. Idempotent.</summary>
    public void MarkTimedOut()
    {
        lock (_lock)
        {
            if (State == ControlRequestState.Pending)
            {
                State = ControlRequestState.TimedOut;
                _completion.TrySetResult(State);
            }
        }
    }

    internal void OnFrame(in ControlFrameHeader header, ReadOnlySpan<byte> payload)
    {
        lock (_lock)
        {
            if (State != ControlRequestState.Pending)
            {
                return;
            }
            if (header.Type == ControlFrameType.Nak)
            {
                Nak = payload.Length > 0 ? (NakReason)payload[0] : NakReason.Refused;
                _payload = payload.ToArray();
                if (payload.Length > 1)
                {
                    _text.Append(Encoding.UTF8.GetString(payload.Slice(1)));
                }
                Finish(ControlRequestState.Failed);
                return;
            }
            if (header.Type == ControlFrameType.HelloReply)
            {
                _payload = payload.ToArray();
                Finish(ControlRequestState.Completed);
                return;
            }
            if (header.Type != ControlFrameType.Reply)
            {
                return;
            }
            if (header.Chunk != _expectedChunk)
            {
                _gap = true;
            }
            _expectedChunk = (header.Chunk + 1) & 0x0F;
            _text.Append(Encoding.UTF8.GetString(payload));
            if (header.IsLast)
            {
                _payload = payload.ToArray();
                if (header.IsError)
                {
                    Finish(ControlRequestState.Failed);
                }
                else
                {
                    Finish(_gap ? ControlRequestState.Incomplete : ControlRequestState.Completed);
                }
            }
        }
    }

    private void Finish(ControlRequestState state)
    {
        State = state;
        _completion.TrySetResult(state);
    }
}
