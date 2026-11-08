// SERIAL PORT NAMES - the COM ports on this machine, sorted.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using System.IO.Ports;

namespace HdlcGatewayTest.Serial;

/// <summary>
/// Lists serial ports for a port picker or <c>--list</c>.
/// </summary>
public static class SerialPortNames
{
    /// <summary>
    /// The port names, sorted case-insensitively (COM1, COM10, COM8 sort as text, like the IDE).
    /// </summary>
    /// <returns>The names.</returns>
    public static string[] List()
    {
        string[] names = SerialPort.GetPortNames();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }
}
