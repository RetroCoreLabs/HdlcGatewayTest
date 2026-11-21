// GATEWAY STATISTICS - the counters a STAT control frame carries.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// The u32 counters of a STAT frame, in the firmware's order. New fields are only ever added at
/// the end, so a frame from an older firmware is shorter and the missing fields read as null.
/// A payload whose length is not a multiple of four is rejected. The first eight fields mean the
/// same on every gateway; the named properties after them follow HDLCGW's list, and RC422Dev's
/// own fields are named by <see cref="Describe"/> instead.
/// </summary>
public sealed class GatewayStatistics
{
    private readonly uint[] _values;

    private GatewayStatistics(uint[] values)
    {
        _values = values;
    }

    /// <summary>Number of fields the frame carried.</summary>
    public int FieldCount => _values.Length;

    /// <summary>Frames the host asked to send to the line.</summary>
    public uint? SentTotal => Field(0);

    /// <summary>Of those, queued for the line.</summary>
    public uint? SentOk => Field(1);

    /// <summary>Of those, too long or no room.</summary>
    public uint? SentBad => Field(2);

    /// <summary>Bytes in the frames sent, FCS included.</summary>
    public uint? SentBytes => Field(3);

    /// <summary>Frames seen on the line, good or bad.</summary>
    public uint? ReceivedTotal => Field(4);

    /// <summary>Line frames with a good FCS.</summary>
    public uint? ReceivedOk => Field(5);

    /// <summary>Line frames with a bad FCS, aborted or too long.</summary>
    public uint? ReceivedBad => Field(6);

    /// <summary>Bytes in the good line frames.</summary>
    public uint? ReceivedBytes => Field(7);

    /// <summary>Raw bytes taken from the line receiver.</summary>
    public uint? LineRxBytes => Field(8);

    /// <summary>Repeated idle bytes the receiver dropped.</summary>
    public uint? LineIdleDropped => Field(9);

    /// <summary>Receiver FIFO overruns.</summary>
    public uint? LineFifoOverruns => Field(10);

    /// <summary>Receiver DMA errors.</summary>
    public uint? LineDmaErrors => Field(11);

    /// <summary>Receive ring overflows.</summary>
    public uint? LineRingOverflow => Field(12);

    /// <summary>Words taken from the DMA before it closed them.</summary>
    public uint? LineWordsPeeked => Field(13);

    /// <summary>Frames to the host dropped because the USB ring was full.</summary>
    public uint? UsbFramesDropped => Field(14);

    /// <summary>Flag-to-flag runs too short to be a frame, ignored.</summary>
    public uint? ShortRunsIgnored => Field(15);

    /// <summary>Control frames the firmware could not queue.</summary>
    public uint? ControlFramesDropped => Field(16);

    /// <summary>WiFi mode: line frames sent to the TCP peer (firmware 0.3 and later).</summary>
    public uint? FramesToPeer => Field(17);

    /// <summary>WiFi mode: bytes sent to the TCP peer.</summary>
    public uint? BytesToPeer => Field(18);

    /// <summary>WiFi mode: frames received from the TCP peer and put on the line.</summary>
    public uint? FramesFromPeer => Field(19);

    /// <summary>WiFi mode: frames for the peer dropped because its ring was full.</summary>
    public uint? DroppedToPeer => Field(20);

    /// <summary>WiFi mode: TCP peer connections accepted or made.</summary>
    public uint? PeerConnects => Field(21);

    /// <summary>WiFi mode: times the station joined the network.</summary>
    public uint? WifiConnects => Field(22);

    // Field names per firmware, in wire order. Both lists are append-only; the first eight are
    // common. Agreed with the HDLCGW session (console_out.cpp order) and the RC422Dev session
    // (its message of 2026-11-08).
    private static readonly string[] s_hdlcgwNames =
    {
        "sent_total", "sent_ok", "sent_bad", "sent_bytes", "received_total", "received_ok", "received_bad",
        "received_bytes", "line_rx_bytes", "idle_dropped", "fifo_overruns", "dma_errors", "ring_overflow",
        "words_peeked", "usb_frames_dropped", "short_runs_ignored", "control_frames_dropped",
        "frames_to_peer", "bytes_to_peer", "frames_from_peer", "dropped_to_peer", "peer_connects", "wifi_connects",
        // Fields 24..26, ESP build 2026-11-09 00:05:32 (HDLCGW session): what the transmitter clocked and
        // what the host sent, to tell a starved line from a slow feed.
        "line_tx_data_bytes", "line_tx_idle_bytes", "usb_rx_bytes",
        // Fields 27, 28, ESP build 2026-11-09 ~01:00: USB send pieces whose untaken tail was written again
        // after the core's 250 ms write timeout (before, the tail was lost and the PC saw a bad FCS),
        // and output thrown away at a frame boundary because no host had the port open.
        "usb_short_writes_resumed", "usb_flushes_port_closed",
        // Fields 29..32, ESP build 2026-11-09 ~01:10 (rings moved to 4 KB internal RAM): high water
        // of the line RX, line TX and USB TX rings in bytes, and the lowest free internal heap seen.
        "line_rx_ring_high_water", "line_tx_ring_high_water", "usb_tx_ring_high_water", "internal_heap_low",
    };

    private static readonly string[] s_rc422devNames =
    {
        "sent_total", "sent_ok", "sent_bad", "sent_bytes", "received_total", "received_ok", "received_bad",
        "received_bytes", "recv_dropped", "recv_overflow", "recv_aborted", "control_dropped",
        // RC422Dev session, 2026-11-08 after the first two-board run: USB write timing, to see the
        // SDK's silent drops (500 ms buffer timeout, 1000 ms mutex timeout).
        "usb_writes", "usb_writes_over_450ms", "usb_writes_over_900ms", "usb_write_max_us",
        // Build 22:55:41: writes skipped because the Pico's SDK saw DTR low ("not connected").
        // Reads 1 after boot (the BOOT event before the port was open), so the baseline is 1.
        "usb_writes_not_connected",
        // Fields 17..21, build 22:57:57, order confirmed by the RC422Dev session (it differs from
        // its text line in one place: raises before the last-drop timestamp).
        "dtr_drops", "dtr_raises", "dtr_last_drop_ms", "usb_suspends", "usb_unmounts",
        // Fields 22, 23, build 23:04:42.
        "usb_bytes_requested", "usb_tx_completes",
        // Fields 24..34, build 23:28:36: the IN endpoint's own counters and the USB bus events,
        // for the hunt of frames accepted by the SDK's write that never became a transfer.
        "ep82_starts", "ep82_start_bytes", "ep82_completes", "ep82_complete_bytes", "ep82_failed",
        "usb_bus_suspends", "usb_bus_resumes", "usb_bus_resets", "usb_bus_unplugs", "usb_last_suspend_ms",
        "usb_writes_not_connected_after",
        // Fields 35..40 (announced, build pending): wraps on tud_cdc_n_write / _write_flush.
        "cdc_write_calls", "cdc_write_requested", "cdc_write_accepted", "cdc_flush_calls", "cdc_flush_bytes",
        "fifo_not_empty_after_write",
        // Fields 41, 42, build 23:37:17: bytes handed minus endpoint bytes started, max and jumps.
        "usb_lag_max", "usb_lag_events",
    };

    /// <summary>
    /// The fields as "name=value" lines for the firmware that sent them. Unknown firmware or a
    /// field past the known list gets "field_N=value".
    /// </summary>
    /// <param name="firmwareName">The name from HELLO_REPLY: HDLCGW, RC422Dev, or null.</param>
    /// <returns>One line per field.</returns>
    public string Describe(string? firmwareName)
    {
        string[] names = string.Equals(firmwareName, "RC422Dev", StringComparison.OrdinalIgnoreCase)
            ? s_rc422devNames
            : s_hdlcgwNames;
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < _values.Length; i++)
        {
            sb.Append(i < names.Length ? names[i] : "field_" + i).Append('=').Append(_values[i]).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse a STAT payload.
    /// </summary>
    /// <param name="payload">The payload bytes.</param>
    /// <param name="statistics">The result when the length is a multiple of four.</param>
    /// <returns>True when parsed.</returns>
    public static bool TryParse(ReadOnlySpan<byte> payload, out GatewayStatistics? statistics)
    {
        statistics = null;
        if ((payload.Length & 3) != 0)
        {
            return false;
        }
        uint[] values = new uint[payload.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            int o = i * 4;
            values[i] = (uint)(payload[o] | (payload[o + 1] << 8) | (payload[o + 2] << 16) | (payload[o + 3] << 24));
        }
        statistics = new GatewayStatistics(values);
        return true;
    }

    /// <summary>
    /// A field by index.
    /// </summary>
    /// <param name="index">0-based field index.</param>
    /// <returns>The value, or null when the frame did not carry that field.</returns>
    public uint? Field(int index)
    {
        if (index < 0 || index >= _values.Length)
        {
            return null;
        }
        return _values[index];
    }
}
