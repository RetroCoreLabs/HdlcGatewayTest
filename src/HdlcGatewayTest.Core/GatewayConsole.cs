// GATEWAY CONSOLE - commands to the firmware and their replies, over control frames.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Text;

namespace HdlcGatewayTest.Core;

/// <summary>A log line from the firmware.</summary>
/// <param name="level">Its level.</param>
/// <param name="milliseconds">The firmware's millisecond clock when it was written.</param>
/// <param name="text">The line.</param>
public delegate void LogLineHandler(LogLevel level, uint milliseconds, string text);

/// <summary>An event from the firmware.</summary>
/// <param name="code">The event code.</param>
/// <param name="text">Its text, possibly empty.</param>
public delegate void GatewayEventHandler(GatewayEvent code, string text);

/// <summary>Statistics pushed by the firmware.</summary>
/// <param name="statistics">The counters.</param>
public delegate void StatisticsHandler(GatewayStatistics statistics);

/// <summary>
/// The framed console: sends HELLO, commands, log level and statistics requests as control
/// frames, matches every reply to its request by id, and hands unsolicited frames (log lines,
/// events, statistics) to callbacks. Sits on a <see cref="GatewayChannel"/> and takes over its
/// <see cref="GatewayChannel.ControlFrameReceived"/>.
/// </summary>
public sealed class GatewayConsole
{
    private readonly GatewayChannel _channel;
    private readonly ControlRequest?[] _pending = new ControlRequest?[256];
    private readonly object _lock = new object();
    private int _nextId = 1;

    /// <summary>
    /// Attach to a channel.
    /// </summary>
    /// <param name="channel">The channel; its control-frame handler is replaced.</param>
    public GatewayConsole(GatewayChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _channel.ControlFrameReceived = OnControlFrame;
    }

    /// <summary>A LOG frame arrived.</summary>
    public LogLineHandler? LogReceived { get; set; }

    /// <summary>An EVENT frame arrived.</summary>
    public GatewayEventHandler? EventReceived { get; set; }

    /// <summary>A STAT frame arrived.</summary>
    public StatisticsHandler? StatisticsReceived { get; set; }

    /// <summary>A reply arrived for an id nobody is waiting on (late, after a timeout).</summary>
    public long StrayReplies { get; private set; }

    /// <summary>
    /// Send HELLO. On completion the request's <see cref="ControlRequest.Payload"/> parses with
    /// <see cref="GatewayIdentity.TryParse"/>.
    /// </summary>
    /// <param name="hostName">Optional text naming this program.</param>
    /// <returns>The pending request.</returns>
    public ControlRequest Hello(string hostName = "HdlcGatewayTest")
    {
        return Send(ControlFrameType.Hello, Encoding.UTF8.GetBytes(hostName));
    }

    /// <summary>
    /// Send a console command line.
    /// </summary>
    /// <param name="command">The line without CR or LF.</param>
    /// <returns>The pending request; its <see cref="ControlRequest.Text"/> is the reply.</returns>
    public ControlRequest SendCommand(string command)
    {
        return Send(ControlFrameType.Command, Encoding.UTF8.GetBytes(command));
    }

    /// <summary>
    /// Set the firmware's log level.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <returns>The pending request.</returns>
    public ControlRequest SetLogLevel(LogLevel level)
    {
        return Send(ControlFrameType.LogLevel, new byte[] { (byte)level });
    }

    /// <summary>
    /// Ask for periodic STAT frames.
    /// </summary>
    /// <param name="periodMilliseconds">Period, 0 to stop; the firmware enforces a 100 ms minimum.</param>
    /// <returns>The pending request.</returns>
    public ControlRequest SetStatisticsPush(ushort periodMilliseconds)
    {
        return Send(ControlFrameType.StatPush,
                    new byte[] { (byte)(periodMilliseconds & 0xFF), (byte)(periodMilliseconds >> 8) });
    }

    /// <summary>
    /// Send PING.
    /// </summary>
    /// <returns>The pending request.</returns>
    public ControlRequest Ping()
    {
        return Send(ControlFrameType.Ping, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Drop every pending request as timed out (port closed, gateway gone).
    /// </summary>
    public void CancelAll()
    {
        lock (_lock)
        {
            for (int i = 0; i < _pending.Length; i++)
            {
                _pending[i]?.MarkTimedOut();
                _pending[i] = null;
            }
        }
    }

    private ControlRequest Send(ControlFrameType type, ReadOnlySpan<byte> payload)
    {
        ControlRequest request;
        lock (_lock)
        {
            byte id = AllocateId();
            request = new ControlRequest(id, type);
            _pending[id] = request;
        }
        _channel.SendControl(type, request.Id, payload);
        return request;
    }

    private byte AllocateId()
    {
        for (int tries = 0; tries < 255; tries++)
        {
            int id = _nextId;
            _nextId = _nextId == 255 ? 1 : _nextId + 1;
            ControlRequest? old = _pending[id];
            if (old == null || old.State != ControlRequestState.Pending)
            {
                return (byte)id;
            }
        }
        throw new InvalidOperationException("255 requests pending; the gateway is not answering.");
    }

    private void OnControlFrame(in ControlFrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.Id == 0)
        {
            OnUnsolicited(in header, payload);
            return;
        }
        ControlRequest? request;
        lock (_lock)
        {
            request = _pending[header.Id];
        }
        if (request == null || request.State != ControlRequestState.Pending)
        {
            StrayReplies++;
            return;
        }
        request.OnFrame(in header, payload);
        if (request.State != ControlRequestState.Pending)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_pending[header.Id], request))
                {
                    _pending[header.Id] = null;
                }
            }
        }
    }

    private void OnUnsolicited(in ControlFrameHeader header, ReadOnlySpan<byte> payload)
    {
        switch (header.Type)
        {
            case ControlFrameType.Log:
                if (payload.Length >= 5)
                {
                    uint ms = (uint)(payload[1] | (payload[2] << 8) | (payload[3] << 16) | (payload[4] << 24));
                    LogReceived?.Invoke((LogLevel)payload[0], ms, Encoding.UTF8.GetString(payload.Slice(5)));
                }
                break;

            case ControlFrameType.Event:
                if (payload.Length >= 1)
                {
                    EventReceived?.Invoke((GatewayEvent)payload[0], Encoding.UTF8.GetString(payload.Slice(1)));
                }
                break;

            case ControlFrameType.Stat:
                if (GatewayStatistics.TryParse(payload, out GatewayStatistics? statistics) && statistics != null)
                {
                    StatisticsReceived?.Invoke(statistics);
                }
                break;

            default:
                StrayReplies++;
                break;
        }
    }
}
