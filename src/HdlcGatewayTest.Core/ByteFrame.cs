// BYTE FRAME - RFC 1662 byte stuffing between the host and a gateway over USB serial.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// The framing both gateways speak on their USB serial port while forwarding:
/// <list type="bullet">
/// <item><description>0x7E delimits a frame (and is sent before every frame as well);</description></item>
/// <item><description>0x7D escapes the next byte, which is sent XOR 0x20;</description></item>
/// <item><description>0x7E, 0x7D and every byte below 0x20 are escaped on the way out.</description></item>
/// </list>
/// The bytes inside are the HDLC frame exactly as it goes on the synchronous line: address,
/// control, information and the two FCS bytes. The gateway neither adds nor strips the FCS.
/// </summary>
public static class ByteFrame
{
    /// <summary>Frame delimiter.</summary>
    public const byte Flag = 0x7E;

    /// <summary>Escape byte; the byte after it was XORed with <see cref="EscapeMask"/>.</summary>
    public const byte Escape = 0x7D;

    /// <summary>Applied to an escaped byte.</summary>
    public const byte EscapeMask = 0x20;

    /// <summary>
    /// Longest frame HDLCGW accepts or forwards, FCS included. RC422Dev's limit is 128; a
    /// longer frame shows as an overflow on its console, not as a CRC error.
    /// </summary>
    public const int MaxFrameLength = 1024;

    /// <summary>
    /// Worst-case encoded size of a frame: every byte escaped, plus two flags.
    /// </summary>
    /// <param name="frameLength">Bytes in the frame.</param>
    /// <returns>Bytes the encoded form can take at most.</returns>
    public static int MaxEncodedLength(int frameLength)
    {
        return 2 + (2 * frameLength);
    }

    /// <summary>
    /// Byte-stuff one frame: flag, escaped bytes, flag.
    /// </summary>
    /// <param name="frame">The frame, FCS included.</param>
    /// <param name="destination">Receives the encoded bytes; at least <see cref="MaxEncodedLength"/>.</param>
    /// <returns>Number of bytes written to <paramref name="destination"/>.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public static int Encode(ReadOnlySpan<byte> frame, Span<byte> destination)
    {
        if (destination.Length < MaxEncodedLength(frame.Length))
        {
            throw new ArgumentException("Destination too small for the worst-case encoding.",
                                        nameof(destination));
        }

        int n = 0;
        destination[n++] = Flag;
        for (int i = 0; i < frame.Length; i++)
        {
            byte b = frame[i];
            if (b == Flag || b == Escape || b < 0x20)
            {
                destination[n++] = Escape;
                destination[n++] = (byte)(b ^ EscapeMask);
            }
            else
            {
                destination[n++] = b;
            }
        }
        destination[n++] = Flag;
        return n;
    }
}
