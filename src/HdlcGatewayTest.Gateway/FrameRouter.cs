// FRAME ROUTER - where a received data frame goes next, as a pure function of the pane's role.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

namespace HdlcGatewayTest.Gateway;

/// <summary>Destinations a frame can be sent to after it was received.</summary>
[Flags]
public enum FrameDestinations
{
    /// <summary>Nowhere; only listed.</summary>
    None = 0,

    /// <summary>Back out on this pane's own serial port.</summary>
    OwnSerial = 1,

    /// <summary>Out on the other pane's serial port.</summary>
    PeerSerial = 2,

    /// <summary>To this pane's TCP peer.</summary>
    OwnTcp = 4,
}

/// <summary>
/// The routing rule, kept free of I/O so it is tested as a table: a data frame that arrived
/// from the line or from the TCP peer, on a pane with a given role, goes to these destinations.
/// Control frames never reach this; the channel consumes them first.
/// </summary>
public static class FrameRouter
{
    /// <summary>
    /// Decide.
    /// </summary>
    /// <param name="role">The pane's role.</param>
    /// <param name="from">Serial (the line) or Tcp (the TCP peer).</param>
    /// <returns>Where to send the frame.</returns>
    public static FrameDestinations Route(GatewayRole role, FrameSource from)
    {
        switch (role)
        {
            case GatewayRole.Echo:
                return from == FrameSource.Serial ? FrameDestinations.OwnSerial : FrameDestinations.None;

            case GatewayRole.Bridge:
                return from == FrameSource.Serial ? FrameDestinations.PeerSerial : FrameDestinations.None;

            case GatewayRole.TcpListen:
                if (from == FrameSource.Serial)
                {
                    return FrameDestinations.OwnTcp;
                }
                return from == FrameSource.Tcp ? FrameDestinations.OwnSerial : FrameDestinations.None;

            case GatewayRole.Console:
            case GatewayRole.Free:
            default:
                return FrameDestinations.None;
        }
    }
}
