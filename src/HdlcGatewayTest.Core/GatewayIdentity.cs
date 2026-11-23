// GATEWAY IDENTITY - what a HELLO_REPLY says about the firmware.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Text;

namespace HdlcGatewayTest.Core;

/// <summary>
/// The firmware's answer to HELLO: a protocol version byte followed by key=value lines
/// separated by LF. Known keys are name, version, build, chip, board, maxframe, caps (comma
/// separated) and the hardware keys rev, serial, cores, mhz, flash, flashimage, flashmhz,
/// flashmode, ram, ramfree, ramleast, ramblock, psram, psramfree and psramblock (2026-11-23);
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

    /// <summary>The chip the firmware runs on, for example ESP32-S2 or RP2040 (key "chip"; empty on older firmware).</summary>
    public string Chip => Get("chip");

    /// <summary>The board, for example "LOLIN S2 Mini" or "Raspberry Pi Pico" (key "board"; empty on older firmware).</summary>
    public string Board => Get("board");

    /// <summary>Longest frame the firmware accepts, or 0 when not reported.</summary>
    public int MaxFrame => int.TryParse(Get("maxframe"), out int n) ? n : 0;

    /// <summary>Chip revision, for example "0.0" (key "rev"; empty on older firmware).</summary>
    public string Revision => Get("rev");

    /// <summary>
    /// The chip's serial number (key "serial"): the factory MAC from eFuse on an ESP32, the flash
    /// chip's unique ID on a Pico. Empty on older firmware.
    /// </summary>
    public string Serial => Get("serial");

    /// <summary>CPU cores (key "cores"), or null when not reported.</summary>
    public long? Cores => GetNumber("cores");

    /// <summary>CPU clock in MHz (key "mhz"), or null when not reported.</summary>
    public long? CpuMhz => GetNumber("mhz");

    /// <summary>Flash size read from the flash chip, in bytes (key "flash"), or null when not reported.</summary>
    public long? FlashBytes => GetNumber("flash");

    /// <summary>Flash size the firmware image was built for, in bytes (key "flashimage"), or null.</summary>
    public long? FlashImageBytes => GetNumber("flashimage");

    /// <summary>Flash clock in MHz (key "flashmhz"), or null when not reported.</summary>
    public long? FlashMhz => GetNumber("flashmhz");

    /// <summary>Flash access mode, for example "qio" or "dio" (key "flashmode"; empty when not reported).</summary>
    public string FlashMode => Get("flashmode");

    /// <summary>Internal RAM (heap) size in bytes (key "ram"), or null when not reported.</summary>
    public long? RamBytes => GetNumber("ram");

    /// <summary>Internal RAM free when HELLO was answered, in bytes (key "ramfree"), or null.</summary>
    public long? RamFree => GetNumber("ramfree");

    /// <summary>Least internal RAM free since the board started, in bytes (key "ramleast"), or null.</summary>
    public long? RamLeast => GetNumber("ramleast");

    /// <summary>Largest free block of internal RAM, in bytes (key "ramblock"), or null.</summary>
    public long? RamBlock => GetNumber("ramblock");

    /// <summary>PSRAM size in bytes (key "psram"; 0 when the board has none), or null when not reported.</summary>
    public long? PsramBytes => GetNumber("psram");

    /// <summary>PSRAM free in bytes (key "psramfree"), or null when not reported.</summary>
    public long? PsramFree => GetNumber("psramfree");

    /// <summary>Largest free block of PSRAM, in bytes (key "psramblock"), or null when not reported.</summary>
    public long? PsramBlock => GetNumber("psramblock");

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

    /// <summary>A whole number from a key, or null when the key is missing or not a number.</summary>
    private long? GetNumber(string key)
    {
        return _values.TryGetValue(key, out string? value)
               && long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n)
            ? n
            : null;
    }
}
