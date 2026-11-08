// GATEWAY CHANNEL - sorts what comes off a gateway's port into data frames, control frames and text.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>A handler for one frame; the span is valid only during the call.</summary>
/// <param name="frame">The frame bytes.</param>
public delegate void FrameHandler(ReadOnlySpan<byte> frame);

/// <summary>A handler for one control frame; the span is valid only during the call.</summary>
/// <param name="header">The decoded header.</param>
/// <param name="payload">The payload bytes.</param>
public delegate void ControlFrameHandler(in ControlFrameHeader header, ReadOnlySpan<byte> payload);

/// <summary>Writes bytes to the port; blocks until the driver took them.</summary>
/// <param name="data">The bytes.</param>
public delegate void ByteWriter(ReadOnlySpan<byte> data);

/// <summary>Sees every frame that passes a channel, in either direction, before anyone consumes it.</summary>
/// <param name="sent">True for a frame going out, false for one that came in.</param>
/// <param name="frame">The frame bytes; valid only during the call.</param>
public delegate void FrameObserver(bool sent, ReadOnlySpan<byte> frame);

/// <summary>
/// One gateway's byte channel. Incoming bytes are run through a <see cref="ByteFrameDecoder"/>
/// and every complete frame is handed to exactly one of three places: a frame with a good HDLC
/// FCS to <see cref="DataFrameReceived"/>, a control frame (address 0xFE, control check) to
/// <see cref="ControlFrameReceived"/>, anything else to <see cref="BadFrameReceived"/>. Bytes
/// that arrive before the first flag (the boot banner, text-console output) go to
/// <see cref="TextReceived"/> so a legacy console stays readable.
/// <para>
/// Outgoing frames go through <see cref="Send"/>, which byte-stuffs and writes under one lock,
/// so a command frame and a traffic frame from two threads can never interleave inside one
/// frame on the wire.
/// </para>
/// <see cref="Feed"/> is meant for one pump thread; <see cref="Send"/> may be called from any.
/// </summary>
public sealed class GatewayChannel
{
    private readonly ByteFrameDecoder _decoder;
    private readonly ByteWriter _writer;
    private readonly object _sendLock = new object();
    private readonly byte[] _sendBuffer;
    private readonly byte[] _textBuffer = new byte[256];
    private long _dataFrames;
    private long _controlFrames;
    private long _badFrames;

    /// <summary>
    /// Create a channel over a byte writer.
    /// </summary>
    /// <param name="writer">Writes to the port.</param>
    /// <param name="maxFrameLength">Longest frame accepted, FCS included.</param>
    public GatewayChannel(ByteWriter writer, int maxFrameLength = ByteFrame.MaxFrameLength)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _decoder = new ByteFrameDecoder(maxFrameLength);
        _sendBuffer = new byte[ByteFrame.MaxEncodedLength(maxFrameLength)];
    }

    /// <summary>A frame with a good HDLC FCS, for the line or from it.</summary>
    public FrameHandler? DataFrameReceived { get; set; }

    /// <summary>A control frame from the firmware.</summary>
    public ControlFrameHandler? ControlFrameReceived { get; set; }

    /// <summary>A frame that is neither: bad FCS, or too short. Counted in <see cref="BadFrames"/> too.</summary>
    public FrameHandler? BadFrameReceived { get; set; }

    /// <summary>Bytes outside any frame, before the first flag has ever been seen.</summary>
    public FrameHandler? TextReceived { get; set; }

    /// <summary>
    /// Every frame in and out, for a frame log, independent of which handler consumes it. Called
    /// for sent frames on the sender's thread inside <see cref="Send"/>, for received ones
    /// inside <see cref="Feed"/> before the typed handler.
    /// </summary>
    public FrameObserver? FrameObserved { get; set; }

    /// <summary>Good data frames received.</summary>
    public long DataFrames => _dataFrames;

    /// <summary>Control frames received.</summary>
    public long ControlFrames => _controlFrames;

    /// <summary>Frames that were neither data nor control.</summary>
    public long BadFrames => _badFrames;

    /// <summary>Frames the byte-frame decoder dropped (too long, or ended on an escape).</summary>
    public int DroppedFrames => _decoder.DroppedFrames;

    /// <summary>True between frames.</summary>
    public bool IsIdle => _decoder.IsIdle;

    /// <summary>
    /// Forget any partial frame and go back to waiting for a flag. Bytes then count as text
    /// again until the next flag.
    /// </summary>
    public void Reset()
    {
        _decoder.Reset();
    }

    /// <summary>
    /// Feed bytes from the port. Handlers run on the calling thread, inside this call.
    /// </summary>
    /// <param name="bytes">The bytes as read.</param>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        int textLength = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (!_decoder.IsActive && b != ByteFrame.Flag)
            {
                _textBuffer[textLength++] = b;
                if (textLength == _textBuffer.Length)
                {
                    FlushText(ref textLength);
                }
                continue;
            }
            if (textLength > 0)
            {
                FlushText(ref textLength);
            }
            if (_decoder.Feed(b))
            {
                Dispatch(_decoder.Frame);
            }
        }
        if (textLength > 0)
        {
            FlushText(ref textLength);
        }
    }

    /// <summary>
    /// Byte-stuff one frame and write it to the port as one unit.
    /// </summary>
    /// <param name="frame">The frame, check bytes included.</param>
    /// <exception cref="ArgumentException">The frame is longer than the channel allows.</exception>
    public void Send(ReadOnlySpan<byte> frame)
    {
        lock (_sendLock)
        {
            if (ByteFrame.MaxEncodedLength(frame.Length) > _sendBuffer.Length)
            {
                throw new ArgumentException("Frame too long for this channel.", nameof(frame));
            }
            int n = ByteFrame.Encode(frame, _sendBuffer);
            FrameObserved?.Invoke(true, frame);
            _writer(new ReadOnlySpan<byte>(_sendBuffer, 0, n));
        }
    }

    /// <summary>
    /// Build and send one control frame.
    /// </summary>
    /// <param name="type">Message type.</param>
    /// <param name="id">Request id, or 0.</param>
    /// <param name="payload">Payload, at most <see cref="ControlFrame.MaxPayload"/>.</param>
    public void SendControl(ControlFrameType type, byte id, ReadOnlySpan<byte> payload)
    {
        Span<byte> frame = stackalloc byte[ControlFrame.MaxPayload + ControlFrame.Overhead];
        int n = ControlFrame.Encode(type, id, 0, payload, frame);
        Send(frame.Slice(0, n));
    }

    /// <summary>
    /// Write raw bytes with no framing, under the same lock as <see cref="Send"/>. For the
    /// legacy text console (ESC ESC ESC, command lines).
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        lock (_sendLock)
        {
            _writer(bytes);
        }
    }

    private void FlushText(ref int textLength)
    {
        TextReceived?.Invoke(new ReadOnlySpan<byte>(_textBuffer, 0, textLength));
        textLength = 0;
    }

    private void Dispatch(ReadOnlySpan<byte> frame)
    {
        FrameObserved?.Invoke(false, frame);
        if (Fcs.IsGood(frame))
        {
            _dataFrames++;
            DataFrameReceived?.Invoke(frame);
            return;
        }
        if (ControlFrame.TryDecode(frame, out ControlFrameHeader header, out ReadOnlySpan<byte> payload))
        {
            _controlFrames++;
            ControlFrameReceived?.Invoke(in header, payload);
            return;
        }
        _badFrames++;
        BadFrameReceived?.Invoke(frame);
    }
}
