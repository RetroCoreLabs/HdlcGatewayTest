// CONTROL FRAME - messages between the host and the gateway firmware on the frame channel.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// Message types of a control frame. Values below 0x80 travel host to firmware, values with
/// bit 7 set travel firmware to host. Must match control_frame.h in the HDLCGW firmware.
/// </summary>
public enum ControlFrameType : byte
{
    /// <summary>Identify the firmware; also resets its log level and statistics push to off.</summary>
    Hello = 0x01,

    /// <summary>A console command line (no CR or LF); answered with <see cref="Reply"/> chunks.</summary>
    Command = 0x02,

    /// <summary>One byte: the <see cref="LogLevel"/> the firmware should send.</summary>
    LogLevel = 0x03,

    /// <summary>Two bytes little-endian: statistics push period in ms, 0 = off, minimum 100.</summary>
    StatPush = 0x04,

    /// <summary>Empty; answered with an empty <see cref="Reply"/>.</summary>
    Ping = 0x05,

    /// <summary>Protocol version byte, then key=value lines separated by LF.</summary>
    HelloReply = 0x81,

    /// <summary>Text produced by a command, in chunks; the last chunk carries <see cref="ControlFrameFlags.Last"/>.</summary>
    Reply = 0x82,

    /// <summary>Unsolicited (id 0): level byte, u32 milliseconds since boot, text.</summary>
    Log = 0x83,

    /// <summary>Unsolicited (id 0): a list of u32 counters, see <see cref="GatewayStatistics"/>.</summary>
    Stat = 0x84,

    /// <summary>Unsolicited (id 0): event code byte, then text.</summary>
    Event = 0x85,

    /// <summary>The request was refused: reason byte, then optional text.</summary>
    Nak = 0x86,
}

/// <summary>Flags byte of a control frame. Bits 4..7 hold a chunk counter.</summary>
[Flags]
public enum ControlFrameFlags : byte
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>Final chunk of a reply.</summary>
    Last = 0x01,

    /// <summary>The command failed or was refused.</summary>
    Error = 0x02,
}

/// <summary>Log levels, LOG_LEVEL payload and the first byte of a LOG frame.</summary>
public enum LogLevel : byte
{
    /// <summary>Nothing is sent.</summary>
    Off = 0,

    /// <summary>Errors only.</summary>
    Error = 1,

    /// <summary>Errors and warnings, such as a frame with a bad FCS.</summary>
    Warn = 2,

    /// <summary>Plus informational lines.</summary>
    Info = 3,

    /// <summary>Plus one line per good frame off the line.</summary>
    Debug = 4,

    /// <summary>Plus raw bytes when echo is on.</summary>
    Trace = 5,
}

/// <summary>Event codes, the first byte of an EVENT frame.</summary>
public enum GatewayEvent : byte
{
    /// <summary>The firmware started; the text is its banner.</summary>
    Boot = 1,

    /// <summary>The console mode changed; the text is FORWARDING, DEBUG or PACKET_GEN.</summary>
    ModeChange = 2,

    /// <summary>First byte seen on the receive clock after idle.</summary>
    LineRxActive = 3,

    /// <summary>No byte on the receive clock for two seconds.</summary>
    LineRxIdle = 4,

    /// <summary>The transmit clock was started.</summary>
    ClockOn = 5,

    /// <summary>The transmit clock was stopped.</summary>
    ClockOff = 6,

    /// <summary>A previous USB host went away (seen by the next host in its log).</summary>
    UsbHostLost = 7,

    /// <summary>WiFi station starts connecting; text "ssid=...".</summary>
    WifiConnecting = 16,

    /// <summary>WiFi got an address; text "ip=... rssi=...".</summary>
    WifiUp = 17,

    /// <summary>WiFi lost the network or was turned off; text is the state.</summary>
    WifiDown = 18,

    /// <summary>The TCP server socket is open; text "port=...".</summary>
    TcpListening = 19,

    /// <summary>A TCP peer connected; text "peer=ip:port" or "host=... port=...".</summary>
    TcpPeerConnected = 20,

    /// <summary>The TCP peer went away; line frames go to USB again.</summary>
    TcpPeerLost = 21,

    /// <summary>Client mode is dialling; text "host=... port=...".</summary>
    TcpConnecting = 22,

    /// <summary>The dial failed; the firmware retries after 5 s.</summary>
    TcpConnectFailed = 23,
}

/// <summary>Reasons in a NAK frame.</summary>
public enum NakReason : byte
{
    /// <summary>The firmware does not know the message type.</summary>
    UnknownType = 1,

    /// <summary>The payload length was wrong for the type.</summary>
    BadLength = 2,

    /// <summary>The request was understood but refused.</summary>
    Refused = 3,
}

/// <summary>The four header bytes of a control frame, decoded.</summary>
/// <param name="Type">Message type.</param>
/// <param name="Id">Request id, 0 for unsolicited frames.</param>
/// <param name="Flags">Flags including the chunk counter bits.</param>
public readonly record struct ControlFrameHeader(ControlFrameType Type, byte Id, byte Flags)
{
    /// <summary>True when the LAST flag is set.</summary>
    public bool IsLast => (Flags & (byte)ControlFrameFlags.Last) != 0;

    /// <summary>True when the ERROR flag is set.</summary>
    public bool IsError => (Flags & (byte)ControlFrameFlags.Error) != 0;

    /// <summary>The chunk counter, 0..15, from bits 4..7.</summary>
    public int Chunk => Flags >> ControlFrame.ChunkShift;
}

/// <summary>
/// Control frames: messages for the firmware itself, carried on the same byte-stuffed channel
/// as the HDLC frames for the line and told apart by two locks.
/// <list type="number">
/// <item><description>The HDLC address byte is <see cref="Address"/> (0xFE), an address no
/// station on an ND or X.25 link uses.</description></item>
/// <item><description>The two check bytes are the CRC-16/X.25 register stored WITHOUT the
/// usual inversion. A good HDLC frame always leaves <see cref="Fcs.Good"/> in a receiver's
/// register, a control frame always leaves <see cref="Fcs.ControlGood"/>; no byte string can
/// do both, so a data frame with a good FCS is never mistaken for a command.</description></item>
/// </list>
/// Layout: address, type, id, flags, payload, check (low byte first). Twin of control_frame.c
/// in the HDLCGW firmware; the two must agree byte for byte, and the tests pin a worked example.
/// </summary>
public static class ControlFrame
{
    /// <summary>HDLC address that marks a control frame.</summary>
    public const byte Address = 0xFE;

    /// <summary>Protocol version this code speaks; the firmware reports its own in HELLO_REPLY.</summary>
    public const byte ProtocolVersion = 1;

    /// <summary>Address, type, id, flags.</summary>
    public const int HeaderLength = 4;

    /// <summary>The un-inverted CRC.</summary>
    public const int CheckLength = 2;

    /// <summary>Header plus check.</summary>
    public const int Overhead = HeaderLength + CheckLength;

    /// <summary>Longest payload that fits in <see cref="ByteFrame.MaxFrameLength"/>.</summary>
    public const int MaxPayload = ByteFrame.MaxFrameLength - Overhead;

    /// <summary>Bit position of the chunk counter in the flags byte.</summary>
    public const int ChunkShift = 4;

    /// <summary>
    /// Build a control frame.
    /// </summary>
    /// <param name="type">Message type.</param>
    /// <param name="id">Request id, or 0.</param>
    /// <param name="flags">Flags byte, including a chunk counter if wanted.</param>
    /// <param name="payload">Payload bytes, at most <see cref="MaxPayload"/>.</param>
    /// <param name="destination">Receives the frame; at least payload length plus <see cref="Overhead"/>.</param>
    /// <returns>Number of bytes written.</returns>
    /// <exception cref="ArgumentException">The payload is too long or the destination too small.</exception>
    public static int Encode(ControlFrameType type, byte id, byte flags, ReadOnlySpan<byte> payload,
                             Span<byte> destination)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentException("Payload longer than a control frame can carry.", nameof(payload));
        }
        int length = payload.Length + Overhead;
        if (destination.Length < length)
        {
            throw new ArgumentException("Destination too small for the frame.", nameof(destination));
        }

        destination[0] = Address;
        destination[1] = (byte)type;
        destination[2] = id;
        destination[3] = flags;
        payload.CopyTo(destination.Slice(HeaderLength));
        Fcs.AppendControl(destination.Slice(0, length));
        return length;
    }

    /// <summary>
    /// Whether a frame off the channel is a control frame, and if so its header and payload.
    /// Both locks must hold: address <see cref="Address"/> and CRC residue
    /// <see cref="Fcs.ControlGood"/>. The type is not checked; an unknown type is still a control
    /// frame, never a frame for the line.
    /// </summary>
    /// <param name="frame">The whole frame as received, check included.</param>
    /// <param name="header">The decoded header when the frame is a control frame.</param>
    /// <param name="payload">The payload when the frame is a control frame.</param>
    /// <returns>True for a control frame.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out ControlFrameHeader header,
                                 out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;
        if (frame.Length < Overhead || frame[0] != Address || !Fcs.IsGoodControl(frame))
        {
            return false;
        }
        header = new ControlFrameHeader((ControlFrameType)frame[1], frame[2], frame[3]);
        payload = frame.Slice(HeaderLength, frame.Length - Overhead);
        return true;
    }
}
