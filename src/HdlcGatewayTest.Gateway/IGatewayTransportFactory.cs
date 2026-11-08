// GATEWAY TRANSPORT FACTORY - turns an endpoint name into a transport; the hosts supply it.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using HdlcGatewayTest.Core;

namespace HdlcGatewayTest.Gateway;

/// <summary>
/// How the session layer and the MCP tools get a transport for an endpoint the user named,
/// without depending on System.IO.Ports themselves. The GUI and the CLI implement it with the
/// serial transport (and a TCP client later); tests implement it with fakes.
/// </summary>
public interface IGatewayTransportFactory
{
    /// <summary>
    /// The endpoints a user can pick from, for example the COM port names.
    /// </summary>
    /// <returns>Names, sorted.</returns>
    string[] ListEndpoints();

    /// <summary>
    /// Make a transport for an endpoint. Nothing is opened here.
    /// </summary>
    /// <param name="endpoint">COM10, or host:port.</param>
    /// <returns>The transport.</returns>
    /// <exception cref="ArgumentException">The endpoint is not understood.</exception>
    IGatewayTransport Create(string endpoint);
}
