// GATEWAY IDENTITY - what a HELLO_REPLY says about the firmware.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Text;

namespace HdlcGatewayTest.Core;

/// <summary>
/// The firmware's answer to HELLO: a protocol version byte followed by key=value lines
/// separated by LF. Known keys are name, version, build, maxframe and caps (comma separated);
/// unknown keys are kept in <see cref="Values"/> so a newer firmware loses nothing.
/// </summary>
public sealed class GatewayIdentity
{
    private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly string[] _capabilities;

    private GatewayIdentity(byte protocolVersion, string text)
    {
        ProtocolVersion = protocolVersion;
        Text = text;
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            _values[line.Substring(0, eq)] = line.Substring(eq + 1);
        }
        _capabilities = _values.TryGetValue("caps", out string? caps) && caps.Length > 0
            ? caps.Split(',')
            : Array.Empty<string>();
    }

    /// <summary>Control-frame protocol version the firmware speaks.</summary>
    public byte ProtocolVersion { get; }

    /// <summary>The raw key=value text.</summary>
    public string Text { get; }

    /// <summary>Firmware name, for example HDLCGW.</summary>
    public string Name => Get("name");

    /// <summary>Firmware version string.</summary>
    public string Version => Get("version");

    /// <summary>Build date and time.</summary>
    public string Build => Get("build");

    /// <summary>Longest frame the firmware accepts, or 0 when not reported.</summary>
    public int MaxFrame => int.TryParse(Get("maxframe"), out int n) ? n : 0;

    /// <summary>Capability words from the caps key.</summary>
    public IReadOnlyList<string> Capabilities => _capabilities;

    /// <summary>Every key and value as received.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>
    /// Whether a capability word is present.
    /// </summary>
    /// <param name="capability">The word, for example "log".</param>
    /// <returns>True when listed.</returns>
    public bool Has(string capability)
    {
        for (int i = 0; i < _capabilities.Length; i++)
        {
            if (string.Equals(_capabilities[i], capability, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Parse a HELLO_REPLY payload.
    /// </summary>
    /// <param name="payload">Version byte, then text.</param>
    /// <param name="identity">The result when the payload has at least the version byte.</param>
    /// <returns>True when parsed.</returns>
    public static bool TryParse(ReadOnlySpan<byte> payload, out GatewayIdentity? identity)
    {
        identity = null;
        if (payload.Length < 1)
        {
            return false;
        }
        identity = new GatewayIdentity(payload[0], Encoding.UTF8.GetString(payload.Slice(1)));
        return true;
    }

    private string Get(string key)
    {
        return _values.TryGetValue(key, out string? value) ? value : string.Empty;
    }
}
