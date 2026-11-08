// FCS - the 16-bit frame check sequence HDLC frames carry.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// CRC-16/X.25 as used by HDLC, SDLC and RFC 1662 PPP: polynomial 0x1021 reflected (0x8408),
/// initial value 0xFFFF, result complemented and sent low byte first. The gateways (HDLCGW on
/// the ESP32-S2 and RC422Dev on the Pico) use exactly this, and they forward the two FCS bytes
/// to the host untouched, so the host can check a frame end to end.
/// </summary>
public static class Fcs
{
    /// <summary>Starting value of the CRC register.</summary>
    public const ushort Initial = 0xFFFF;

    /// <summary>
    /// What the CRC register holds after a good frame including its own two FCS bytes. Running
    /// the CRC over the whole frame and comparing with this is the standard receive check.
    /// </summary>
    public const ushort Good = 0xF0B8;

    private static readonly ushort[] s_table = BuildTable();

    /// <summary>
    /// One CRC step over one byte.
    /// </summary>
    /// <param name="crc">The CRC register so far.</param>
    /// <param name="data">The next frame byte.</param>
    /// <returns>The updated CRC register.</returns>
    public static ushort Update(ushort crc, byte data)
    {
        return (ushort)((crc >> 8) ^ s_table[(crc ^ data) & 0xFF]);
    }

    /// <summary>
    /// Run the CRC over a span of bytes starting from <see cref="Initial"/>.
    /// </summary>
    /// <param name="data">The bytes, normally address, control and information.</param>
    /// <returns>The CRC register, NOT yet complemented.</returns>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = Initial;
        for (int i = 0; i < data.Length; i++)
        {
            crc = Update(crc, data[i]);
        }
        return crc;
    }

    /// <summary>
    /// Write the FCS for the first <c>frame.Length - 2</c> bytes into the last two bytes of the
    /// span, low byte first, the way it goes on the wire.
    /// </summary>
    /// <param name="frame">The frame with two spare bytes at the end for the FCS.</param>
    /// <exception cref="ArgumentException">The span is shorter than two bytes.</exception>
    public static void Append(Span<byte> frame)
    {
        if (frame.Length < 2)
        {
            throw new ArgumentException("A frame needs room for the two FCS bytes.", nameof(frame));
        }
        ushort fcs = (ushort)~Compute(frame.Slice(0, frame.Length - 2));
        frame[frame.Length - 2] = (byte)(fcs & 0xFF);
        frame[frame.Length - 1] = (byte)(fcs >> 8);
    }

    /// <summary>
    /// Check a received frame: the CRC over every byte including the FCS must leave
    /// <see cref="Good"/> in the register.
    /// </summary>
    /// <param name="frameWithFcs">The whole frame as received, FCS included.</param>
    /// <returns>True when the FCS matches; false for a damaged frame or one shorter than 2 bytes.</returns>
    public static bool IsGood(ReadOnlySpan<byte> frameWithFcs)
    {
        if (frameWithFcs.Length < 2)
        {
            return false;
        }
        return Compute(frameWithFcs) == Good;
    }

    private static ushort[] BuildTable()
    {
        ushort[] table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort value = (ushort)i;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((value & 1) != 0)
                {
                    value = (ushort)((value >> 1) ^ 0x8408);
                }
                else
                {
                    value = (ushort)(value >> 1);
                }
            }
            table[i] = value;
        }
        return table;
    }
}
