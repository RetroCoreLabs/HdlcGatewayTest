// FRAME LOG - the last N frames a pane saw, each way, with what they were.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using HdlcGatewayTest.Core;

namespace HdlcGatewayTest.Gateway;

/// <summary>One frame in the log, decoded enough for a list row and a detail panel.</summary>
public sealed class FrameRecord
{
    /// <summary>
    /// Make a record.
    /// </summary>
    /// <param name="sequence">Position in the log since the pane was created, counting from 1.</param>
    /// <param name="time">When it was seen.</param>
    /// <param name="sent">True for a frame going out of the PC, false for one that came in.</param>
    /// <param name="source">Where it came from or went.</param>
    /// <param name="bytes">The frame, check bytes included.</param>
    public FrameRecord(long sequence, DateTime time, bool sent, FrameSource source, byte[] bytes)
    {
        Sequence = sequence;
        Time = time;
        Sent = sent;
        Source = source;
        Bytes = bytes;
        FcsGood = Fcs.IsGood(bytes);
        ControlFrameHeader header = default;
        IsControl = !FcsGood && ControlFrame.TryDecode(bytes, out header, out _);
        ControlHeader = IsControl ? header : null;
    }

    /// <summary>Position in the log, counting from 1.</summary>
    public long Sequence { get; }

    /// <summary>When it was seen.</summary>
    public DateTime Time { get; }

    /// <summary>True when the PC sent it.</summary>
    public bool Sent { get; }

    /// <summary>Where it came from or went.</summary>
    public FrameSource Source { get; }

    /// <summary>The frame, check bytes included.</summary>
    public byte[] Bytes { get; }

    /// <summary>True when the HDLC FCS checks out (a data frame).</summary>
    public bool FcsGood { get; }

    /// <summary>True for a control frame (address 0xFE, control check).</summary>
    public bool IsControl { get; }

    /// <summary>The control header when <see cref="IsControl"/>.</summary>
    public ControlFrameHeader? ControlHeader { get; }

    /// <summary>The HDLC address byte, or null for a frame shorter than one byte.</summary>
    public byte? Address => Bytes.Length > 0 ? Bytes[0] : null;

    /// <summary>The control field decoded, or null for a frame shorter than two bytes or a control frame.</summary>
    public HdlcControl? Control => Bytes.Length > 1 && !IsControl ? HdlcControl.Decode(Bytes[1]) : null;

    /// <summary>
    /// One line for a list: direction, length, address, control summary, first bytes.
    /// </summary>
    /// <returns>The summary.</returns>
    public string Summary()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append(Sent ? "TX " : "RX ");
        sb.Append(Bytes.Length).Append(" B ");
        if (IsControl && ControlHeader.HasValue)
        {
            sb.Append("CTRL ").Append(ControlHeader.Value.Type).Append(" id ").Append(ControlHeader.Value.Id);
        }
        else if (Bytes.Length >= 2)
        {
            sb.Append("addr 0x").Append(Bytes[0].ToString("X2")).Append(' ').Append(HdlcControl.Decode(Bytes[1]).Summary());
            if (!FcsGood)
            {
                sb.Append(" FCS BAD");
            }
        }
        else
        {
            sb.Append("short");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The bytes as hex with ASCII, 16 per line, for the detail panel.
    /// </summary>
    /// <returns>The dump.</returns>
    public string HexDump()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int row = 0; row < Bytes.Length; row += 16)
        {
            sb.Append(row.ToString("X4")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (row + i < Bytes.Length)
                {
                    sb.Append(Bytes[row + i].ToString("X2")).Append(' ');
                }
                else
                {
                    sb.Append("   ");
                }
                if (i == 7)
                {
                    sb.Append(' ');
                }
            }
            sb.Append(' ');
            for (int i = 0; i < 16 && row + i < Bytes.Length; i++)
            {
                byte b = Bytes[row + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// A multi-line description for the detail panel: address, control field, info length and the
    /// FCS verdict with the value the FCS should have had.
    /// </summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append(Sent ? "Sent" : "Received").Append(" via ").Append(Source).Append(" at ")
          .Append(Time.ToString("HH:mm:ss.fff")).Append(", ").Append(Bytes.Length).Append(" bytes\r\n");
        if (IsControl && ControlHeader.HasValue)
        {
            ControlFrameHeader h = ControlHeader.Value;
            sb.Append("Control frame: ").Append(h.Type).Append(", id ").Append(h.Id)
              .Append(", chunk ").Append(h.Chunk).Append(h.IsLast ? ", LAST" : "").Append(h.IsError ? ", ERROR" : "")
              .Append("\r\n");
            return sb.ToString();
        }
        if (Bytes.Length < 2)
        {
            sb.Append("Too short for an HDLC frame.\r\n");
            return sb.ToString();
        }
        HdlcControl c = HdlcControl.Decode(Bytes[1]);
        sb.Append("Address         : 0x").Append(Bytes[0].ToString("X2")).Append("\r\n");
        sb.Append("Frame type      : ").Append(c.ClassLetter).Append("\r\n");
        if (c.Name.Length > 0)
        {
            sb.Append("Msg name        : ").Append(c.Name).Append("\r\n");
            sb.Append("Msg description : ").Append(c.Description).Append("\r\n");
        }
        sb.Append("Poll/Final      : ").Append(c.PollFinal ? 1 : 0).Append("\r\n");
        if (c.Class == HdlcFrameClass.Information)
        {
            sb.Append("N(S)            : ").Append(c.SendSequence).Append("\r\n");
        }
        if (c.Class != HdlcFrameClass.Unnumbered)
        {
            sb.Append("N(R)            : ").Append(c.ReceiveSequence).Append("\r\n");
        }
        else
        {
            sb.Append("U:M1            : ").Append(c.M1).Append("\r\n");
            sb.Append("U:M2            : ").Append(c.M2).Append("\r\n");
        }
        int infoLength = Math.Max(0, Bytes.Length - 4);
        sb.Append("Info length     : ").Append(infoLength).Append("\r\n");
        ushort received = (ushort)(Bytes[Bytes.Length - 2] | (Bytes[Bytes.Length - 1] << 8));
        ushort correct = (ushort)~Fcs.Compute(new ReadOnlySpan<byte>(Bytes, 0, Bytes.Length - 2));
        sb.Append("FCS             : 0x").Append(received.ToString("X4"))
          .Append(FcsGood ? " [OK]" : " [NOT OK, correct 0x" + correct.ToString("X4") + "]").Append("\r\n");
        return sb.ToString();
    }
}

/// <summary>
/// A bounded ring of <see cref="FrameRecord"/>s. Thread safe. Readers take snapshots since a
/// sequence number, which is how both the GUI list and the MCP <c>hdlc_frames</c> tool read it.
/// </summary>
public sealed class FrameLog
{
    private readonly FrameRecord?[] _ring;
    private readonly object _lock = new object();
    private long _next = 1;

    /// <summary>
    /// Create a log.
    /// </summary>
    /// <param name="capacity">Frames kept before the oldest is dropped.</param>
    public FrameLog(int capacity = 10_000)
    {
        _ring = new FrameRecord?[Math.Max(16, capacity)];
    }

    /// <summary>Raised after a record is added, on the adding thread.</summary>
    public event Action<FrameRecord>? Added;

    /// <summary>Records added since creation, including ones that have since been dropped.</summary>
    public long Total => _next - 1;

    /// <summary>
    /// Add a frame.
    /// </summary>
    /// <param name="sent">Direction.</param>
    /// <param name="source">Origin or destination.</param>
    /// <param name="frame">The bytes; copied.</param>
    /// <returns>The record.</returns>
    public FrameRecord Add(bool sent, FrameSource source, ReadOnlySpan<byte> frame)
    {
        FrameRecord record;
        lock (_lock)
        {
            record = new FrameRecord(_next, DateTime.Now, sent, source, frame.ToArray());
            _ring[(int)(_next % _ring.Length)] = record;
            _next++;
        }
        Added?.Invoke(record);
        return record;
    }

    /// <summary>
    /// The records with a sequence number above <paramref name="afterSequence"/>, oldest first,
    /// at most <paramref name="max"/>.
    /// </summary>
    /// <param name="afterSequence">Last sequence already seen; 0 for everything kept.</param>
    /// <param name="max">Cap on the result.</param>
    /// <returns>The records.</returns>
    public List<FrameRecord> Since(long afterSequence, int max = int.MaxValue)
    {
        List<FrameRecord> result = new List<FrameRecord>();
        lock (_lock)
        {
            long first = Math.Max(afterSequence + 1, Math.Max(1, _next - _ring.Length));
            for (long s = first; s < _next && result.Count < max; s++)
            {
                FrameRecord? r = _ring[(int)(s % _ring.Length)];
                if (r != null && r.Sequence == s)
                {
                    result.Add(r);
                }
            }
        }
        return result;
    }

    /// <summary>Forget everything kept. Sequence numbers keep counting.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_ring);
        }
    }
}
