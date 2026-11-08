// BYTE FRAME DECODER - collects RFC 1662 byte-stuffed frames from a byte stream.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// Pulls frames out of the bytes a gateway sends over USB. Mirrors the decoder in the HDLCGW
/// firmware (byte_frame.cpp) so the two ends agree on every corner case: a flag both ends a
/// frame and opens the next; two flags in a row are idle and produce nothing; a frame longer
/// than the limit, or one that ends right after an escape byte, is dropped and counted.
/// Not thread safe; one decoder per port.
/// </summary>
public sealed class ByteFrameDecoder
{
    private readonly byte[] _buffer;
    private int _length;
    private bool _active;
    private bool _escaped;
    private bool _overflow;
    private bool _done;
    private int _dropped;

    /// <summary>
    /// Create a decoder.
    /// </summary>
    /// <param name="maxFrameLength">Longest frame accepted, FCS included.</param>
    public ByteFrameDecoder(int maxFrameLength = ByteFrame.MaxFrameLength)
    {
        if (maxFrameLength < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameLength));
        }
        _buffer = new byte[maxFrameLength];
    }

    /// <summary>The frame delivered by the last <see cref="Feed"/> that returned true.</summary>
    public ReadOnlySpan<byte> Frame => new ReadOnlySpan<byte>(_buffer, 0, _length);

    /// <summary>Bytes in <see cref="Frame"/>.</summary>
    public int Length => _length;

    /// <summary>Frames dropped because they were too long or ended right after an escape.</summary>
    public int DroppedFrames => _dropped;

    /// <summary>True between frames: no frame bytes collected since the last flag.</summary>
    public bool IsIdle => !_active || _done || _length == 0;

    /// <summary>Return to the idle state, waiting for a flag.</summary>
    public void Reset()
    {
        _length = 0;
        _active = false;
        _escaped = false;
        _overflow = false;
        _done = false;
    }

    /// <summary>
    /// Feed one byte from the port.
    /// </summary>
    /// <param name="data">The byte.</param>
    /// <returns>
    /// True when a complete frame is in <see cref="Frame"/>. Read it before the next call;
    /// the next data byte starts a new frame at position 0.
    /// </returns>
    public bool Feed(byte data)
    {
        if (data == ByteFrame.Flag)
        {
            bool ready = _active && !_done && !_escaped && !_overflow && _length > 0;
            bool dropped = _active && !_done && (_escaped || _overflow);
            _active = true;
            _escaped = false;
            _overflow = false;
            if (dropped)
            {
                _dropped++;
            }
            if (ready)
            {
                _done = true;
            }
            else
            {
                _length = 0;
            }
            return ready;
        }

        if (!_active)
        {
            return false;   // noise before the first flag, console text for example
        }

        if (_done)
        {
            _done = false;
            _length = 0;
        }

        if (data == ByteFrame.Escape)
        {
            _escaped = true;
            return false;
        }

        if (_escaped)
        {
            data ^= ByteFrame.EscapeMask;
            _escaped = false;
        }

        if (_length < _buffer.Length)
        {
            _buffer[_length++] = data;
        }
        else
        {
            _overflow = true;
        }
        return false;
    }
}
