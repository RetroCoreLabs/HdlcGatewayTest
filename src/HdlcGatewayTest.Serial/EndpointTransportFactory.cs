// ENDPOINT TRANSPORT FACTORY - a COM port name or host:port in, the right transport out.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using HdlcGatewayTest.Core;
using HdlcGatewayTest.Gateway;

namespace HdlcGatewayTest.Serial;

/// <summary>
/// The hosts' <see cref="IGatewayTransportFactory"/>. An endpoint with a colon is
/// <c>host:port</c>: a gateway in WiFi mode (<c>hdlcgw.local:1362</c>) or nd100x as a server,
/// reached with <see cref="TcpClientTransport"/>. Anything else is a COM port name, opened as a
/// <see cref="SerialTransport"/> behind a <see cref="QueuedWriteTransport"/>.
/// </summary>
public sealed class EndpointTransportFactory : IGatewayTransportFactory
{
    /// <inheritdoc/>
    public string[] ListEndpoints()
    {
        return SerialPortNames.List();
    }

    /// <inheritdoc/>
    public IGatewayTransport Create(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("An endpoint is needed: a COM port name, or host:port.", nameof(endpoint));
        }
        string e = endpoint.Trim();
        if (e.Contains(':'))
        {
            if (!TcpClientTransport.TryParseEndpoint(e, out string host, out int port))
            {
                throw new ArgumentException("'" + e + "' is not host:port (port 1..65535).", nameof(endpoint));
            }
            return new TcpClientTransport(host, port);
        }
        // Serial writes block while the far end reads slowly; the queue keeps the one pump
        // thread reading both ports meanwhile (see QueuedWriteTransport for the bench case).
        return new QueuedWriteTransport(new SerialTransport(e));
    }
}
