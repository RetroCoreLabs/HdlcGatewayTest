// GATEWAY TRANSPORT - the thing that moves raw bytes to and from one gateway.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// Raw bytes to and from one gateway: a COM port today, a TCP socket to a gateway in WiFi mode
/// or to the nd100x emulator later. Everything above this (framing, control frames, the test
/// engine) only sees this interface, so it is unit tested against an in-memory fake.
/// <para>
/// Contract: <see cref="Read"/> never blocks and returns what has arrived so far;
/// <see cref="Write"/> blocks until the driver or socket has taken the bytes. An
/// implementation reads on its own thread into a queue; <see cref="ReceiveOverflow"/> counts
/// bytes that queue threw away.
/// </para>
/// </summary>
public interface IGatewayTransport : IDisposable
{
    /// <summary>Name for messages: COM10, or host:port.</summary>
    string Name { get; }

    /// <summary>True between <see cref="Open"/> and <see cref="IDisposable.Dispose"/> while the link is up.</summary>
    bool IsOpen { get; }

    /// <summary>Bytes that arrived while the receive queue was full and were thrown away.</summary>
    long ReceiveOverflow { get; }

    /// <summary>
    /// Receive errors the driver below the transport reported: for a serial port the UART or
    /// USB-serial driver's overrun, buffer-overflow, framing and parity events; 0 for a socket.
    /// Lets a lost frame be placed on the PC side or excluded from it (bench 2026-11-08: 8 whole
    /// frames vanished between the Pico's USB write and the PC with nothing counting them).
    /// </summary>
    long DriverErrors { get; }

    /// <summary>
    /// Raw bytes received since <see cref="Open"/>, before any framing. Compared with what the
    /// far end says it wrote, this tells whether bytes were lost on the way (bench 2026-11-08:
    /// whole frames vanished between the Pico's USB write and the PC with every counter at zero).
    /// </summary>
    long BytesReceived { get; }

    /// <summary>
    /// Bytes written and how long the writes blocked. A write returns when the driver or socket
    /// has taken the bytes, so a far end that reads slowly shows up as long writes here.
    /// </summary>
    WriteMeter Writes { get; }

    /// <summary>
    /// Bytes accepted by <see cref="Write"/> and not yet handed to the driver or socket. 0 for a
    /// transport that writes at once; the write-queue wrapper reports its backlog, so the
    /// traffic engine can tell frames still waiting in the PC from frames lost on the way.
    /// </summary>
    long QueuedWriteBytes => 0;

    /// <summary>
    /// How the receive side reads: reads that returned data, the most bytes one read returned,
    /// and the longest gap between two such reads in microseconds. A serial transport measures
    /// them on its reader thread; others report zeros. One line for a status display.
    /// </summary>
    string DescribeReads() => string.Empty;

    /// <summary>
    /// Start the read statistics over (biggest read, longest gap), so a traffic run's figures
    /// are its own and not the idle time before it. Nothing to do for a transport without them.
    /// </summary>
    void ResetReadStatistics()
    {
    }

    /// <summary>
    /// True when the far end carries HDLC frames only and never parses control frames: the TCP
    /// stream of a gateway in WiFi mode (its commands go over USB) and nd100x. A probe with HELLO
    /// would go onto the line as a junk frame, so the session skips it.
    /// </summary>
    bool FramesOnly { get; }

    /// <summary>
    /// Open the link and start receiving.
    /// </summary>
    /// <exception cref="IOException">The port or socket could not be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The port is in use.</exception>
    void Open();

    /// <summary>
    /// Take up to <paramref name="destination"/>.Length received bytes. Never blocks.
    /// </summary>
    /// <param name="destination">Where the bytes go.</param>
    /// <returns>How many were copied, 0 when nothing is waiting.</returns>
    int Read(Span<byte> destination);

    /// <summary>
    /// Write bytes. Blocks until the driver or socket has taken them.
    /// </summary>
    /// <param name="data">The bytes.</param>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Raised on the reader thread when the link failed (port unplugged, socket reset).
    /// After this, <see cref="Read"/> returns nothing more.
    /// </summary>
    event Action<Exception>? Faulted;
}
