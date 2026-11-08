// APP SETTINGS - what the tool remembers between runs, in one JSON file under the user's profile.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.Text.Json;
using System.Text.Json.Serialization;

namespace HdlcGatewayTest.App.Services;

/// <summary>
/// Remembered settings: MCP on/off and port (off by default, Ronny 2026-11-08), the last
/// endpoint and line rate of each pane, the TCP listen ports, the traffic test knobs. Stored in
/// <c>%APPDATA%\HdlcGatewayTool\settings.json</c>; a missing or unreadable file means defaults.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Start the MCP server when the window opens.</summary>
    public bool McpEnabled { get; set; }

    /// <summary>MCP port on 127.0.0.1.</summary>
    public int McpPort { get; set; } = 2525;

    /// <summary>Last endpoint of pane A.</summary>
    public string EndpointA { get; set; } = string.Empty;

    /// <summary>Last endpoint of pane B.</summary>
    public string EndpointB { get; set; } = string.Empty;

    /// <summary>Last line rate chosen for pane A.</summary>
    public string BaudA { get; set; } = "38400";

    /// <summary>Last line rate chosen for pane B.</summary>
    public string BaudB { get; set; } = "38400";

    /// <summary>TCP listen port of pane A.</summary>
    public int TcpPortA { get; set; } = 1362;

    /// <summary>TCP listen port of pane B.</summary>
    public int TcpPortB { get; set; } = 1363;

    /// <summary>Traffic test rates, comma separated.</summary>
    public string TestRates { get; set; } = "9600,19200,38400,115200,307200,983000";

    /// <summary>Traffic test seconds per rate.</summary>
    public int TestSeconds { get; set; } = 120;

    /// <summary>Traffic test frame length.</summary>
    public int TestSize { get; set; } = 126;

    /// <summary>Traffic test window.</summary>
    public int TestWindow { get; set; } = 3000;

    /// <summary>Traffic test timeout.</summary>
    public int TestTimeout { get; set; } = 5;

    /// <summary>Where the file lives.</summary>
    [JsonIgnore]
    public static string Path =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               "HdlcGatewayTool", "settings.json");

    private static readonly JsonSerializerOptions s_json = new JsonSerializerOptions { WriteIndented = true };

    /// <summary>
    /// Read the file, or defaults when it is missing or broken.
    /// </summary>
    /// <returns>The settings.</returns>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                AppSettings? s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), s_json);
                if (s != null)
                {
                    return s;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            // Defaults; the next Save overwrites the broken file.
        }
        return new AppSettings();
    }

    /// <summary>Write the file. Failures are swallowed: settings are a convenience.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, s_json));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Nothing to do; the window keeps working without a saved file.
        }
    }
}
