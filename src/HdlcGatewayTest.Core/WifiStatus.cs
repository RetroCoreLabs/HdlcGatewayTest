// WIFI STATUS - the three lines WIFI STATUS (and INFO) print on a gateway in WiFi mode, parsed.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Core;

/// <summary>
/// The firmware's WiFi report (HDLCGW WiFi mode, format agreed with the firmware 2026-11-08):
/// <code>
/// WiFi: ON|OFF ssid=&lt;name or (none)&gt; state=&lt;word&gt; ip=&lt;a.b.c.d or none&gt; rssi=&lt;dBm or 0&gt; hostname=hdlcgw
/// TCP: SERVER port=&lt;n&gt; listening=yes|no peer=&lt;ip or none&gt;  |  TCP: CLIENT host=&lt;h&gt; port=&lt;n&gt; state=&lt;word&gt;  |  TCP: OFF
/// MIRROR: ON|OFF
/// </code>
/// Each line is a first word followed by space-separated key=value pairs; unknown keys are kept
/// in <see cref="Values"/> so a newer firmware loses nothing. Lines that are not one of the three
/// are ignored, so the whole INFO output can be handed in.
/// </summary>
public sealed class WifiStatus
{
    private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

    private WifiStatus()
    {
    }

    /// <summary>"ON" or "OFF" after "WiFi:", or empty when the line was missing.</summary>
    public string WifiOnOff => Get("WiFi");

    /// <summary>The network name, or "(none)".</summary>
    public string Ssid => Get("ssid");

    /// <summary>OFF, IDLE, CONNECTING, CONNECTED, NO_SSID, FAILED, LOST or DISCONNECTED.</summary>
    public string State => Get("state");

    /// <summary>The address, or "none".</summary>
    public string Ip => Get("ip");

    /// <summary>Signal strength in dBm, 0 when not connected.</summary>
    public int Rssi => int.TryParse(Get("rssi"), out int n) ? n : 0;

    /// <summary>The mDNS host name, normally hdlcgw.</summary>
    public string Hostname => Get("hostname");

    /// <summary>SERVER, CLIENT or OFF after "TCP:", or empty when the line was missing.</summary>
    public string TcpMode => Get("TCP");

    /// <summary>The TCP port, 0 when none.</summary>
    public int TcpPort => int.TryParse(Get("port"), out int n) ? n : 0;

    /// <summary>Server mode: whether the socket listens.</summary>
    public bool TcpListening => string.Equals(Get("listening"), "yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>Server mode: the peer's address, or "none"; client mode: empty.</summary>
    public string TcpPeer => Get("peer");

    /// <summary>Client mode: the host dialled.</summary>
    public string TcpHost => Get("host");

    /// <summary>Client mode: CONNECTING or CONNECTED. (The WiFi state is <see cref="State"/>.)</summary>
    public string TcpState => Get("tcpstate");

    /// <summary>"ON" or "OFF" after "MIRROR:".</summary>
    public string Mirror => Get("MIRROR");

    /// <summary>True when the firmware reports WiFi connected with an address.</summary>
    public bool IsUp => string.Equals(State, "CONNECTED", StringComparison.OrdinalIgnoreCase) && Ip.Length > 0
                        && !string.Equals(Ip, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every key and value as parsed.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>
    /// Parse the report out of a block of console text.
    /// </summary>
    /// <param name="text">The reply to WIFI STATUS, WIFI, TCP or INFO.</param>
    /// <param name="status">The result when at least the WiFi or the TCP line was found.</param>
    /// <returns>True when found.</returns>
    public static bool TryParse(string text, out WifiStatus? status)
    {
        WifiStatus s = new WifiStatus();
        bool any = false;
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("WiFi:", StringComparison.Ordinal))
            {
                any = true;
                s.ParseLine("WiFi", line.Substring(5), tcpLine: false);
            }
            else if (line.StartsWith("TCP:", StringComparison.Ordinal))
            {
                any = true;
                s.ParseLine("TCP", line.Substring(4), tcpLine: true);
            }
            else if (line.StartsWith("MIRROR:", StringComparison.Ordinal))
            {
                s._values["MIRROR"] = line.Substring(7).Trim();
            }
        }
        status = any ? s : null;
        return any;
    }

    private void ParseLine(string head, string rest, bool tcpLine)
    {
        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq < 0)
            {
                if (!_values.ContainsKey(head))
                {
                    _values[head] = parts[i];
                }
                continue;
            }
            string key = parts[i].Substring(0, eq);
            string value = parts[i].Substring(eq + 1);
            // The TCP line's state= is the socket's, the WiFi line's is the station's.
            if (tcpLine && string.Equals(key, "state", StringComparison.Ordinal))
            {
                key = "tcpstate";
            }
            _values[key] = value;
        }
    }

    private string Get(string key)
    {
        return _values.TryGetValue(key, out string? v) ? v : string.Empty;
    }
}
