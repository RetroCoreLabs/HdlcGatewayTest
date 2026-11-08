// TEST FRAME - numbered frames whose content can be rebuilt from the number alone.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Buffers.Binary;

namespace HdlcGatewayTest.Core;

/// <summary>
/// The verdict on a received frame.
/// </summary>
public enum TestFrameVerdict
{
    /// <summary>FCS good, header consistent, every payload byte as generated.</summary>
    Ok,

    /// <summary>Shorter than a header plus FCS; cannot be one of ours.</summary>
    TooShort,

    /// <summary>The two FCS bytes do not match the rest; bits were damaged on the way.</summary>
    BadFcs,

    /// <summary>FCS good but the length field disagrees with the bytes received.</summary>
    BadLength,

    /// <summary>FCS good but a payload byte differs from what the sequence number generates.</summary>
    BadPayload,
}

/// <summary>
/// Builds and checks the frames the traffic test sends. Layout, all numbers big endian:
/// <code>
/// [0]     address   tells the direction apart when two ports run at once
/// [1]     control   0x03 = UI, Unnumbered Information, so a gateway console decodes it
/// [2..5]  sequence  32-bit, counts from 0 per direction
/// [6..7]  length    the whole frame including FCS, so a truncated frame is caught
/// [8..]   payload   xorshift32 stream seeded from sequence and address
/// [-2..]  FCS       low byte first
/// </code>
/// Because the payload is a function of the header, the receiver needs no copy of what was
/// sent: it reads the sequence number and regenerates the expected bytes.
/// </summary>
public static class TestFrame
{
    /// <summary>Bytes before the payload.</summary>
    public const int HeaderLength = 8;

    /// <summary>Shortest frame: header plus FCS, no payload.</summary>
    public const int MinLength = HeaderLength + 2;

    /// <summary>Control byte used: UI, no poll.</summary>
    public const byte Control = 0x03;

    /// <summary>
    /// Build frame number <paramref name="sequence"/>.
    /// </summary>
    /// <param name="sequence">Frame number.</param>
    /// <param name="length">Total length including FCS, <see cref="MinLength"/> or more.</param>
    /// <param name="address">Address byte, identifies the sending side.</param>
    /// <param name="destination">Receives the frame; at least <paramref name="length"/> bytes.</param>
    /// <returns>The frame as a slice of <paramref name="destination"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The length is below the minimum.</exception>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public static Span<byte> Build(uint sequence, int length, byte address, Span<byte> destination)
    {
        if (length < MinLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        if (destination.Length < length)
        {
            throw new ArgumentException("Destination too small for the frame.", nameof(destination));
        }

        Span<byte> frame = destination.Slice(0, length);
        frame[0] = address;
        frame[1] = Control;
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(2, 4), sequence);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(6, 2), (ushort)length);

        uint state = Seed(sequence, address);
        for (int i = HeaderLength; i < length - 2; i++)
        {
            state = Next(state);
            frame[i] = (byte)state;
        }

        Fcs.Append(frame);
        return frame;
    }

    /// <summary>
    /// Check a received frame against what its own header says it should contain.
    /// </summary>
    /// <param name="frame">The frame as received, FCS included.</param>
    /// <param name="sequence">The sequence number read from the header (0 when too short).</param>
    /// <param name="address">The address byte read from the header (0 when too short).</param>
    /// <returns>The verdict.</returns>
    public static TestFrameVerdict Verify(ReadOnlySpan<byte> frame, out uint sequence, out byte address)
    {
        sequence = 0;
        address = 0;
        if (frame.Length < MinLength)
        {
            return TestFrameVerdict.TooShort;
        }

        address = frame[0];
        sequence = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(2, 4));

        if (!Fcs.IsGood(frame))
        {
            return TestFrameVerdict.BadFcs;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(6, 2));
        if (length != frame.Length || frame[1] != Control)
        {
            return TestFrameVerdict.BadLength;
        }

        uint state = Seed(sequence, address);
        for (int i = HeaderLength; i < frame.Length - 2; i++)
        {
            state = Next(state);
            if (frame[i] != (byte)state)
            {
                return TestFrameVerdict.BadPayload;
            }
        }
        return TestFrameVerdict.Ok;
    }

    private static uint Seed(uint sequence, byte address)
    {
        // Knuth's multiplicative hash spreads neighbouring sequence numbers apart; the
        // address keeps the two directions of a two-port test from carrying the same bytes.
        // xorshift must never start at zero, it would stay there.
        uint seed = (sequence * 2654435761u) ^ ((uint)address << 24) ^ 0x9E3779B9u;
        return seed == 0 ? 1u : seed;
    }

    private static uint Next(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
