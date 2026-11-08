// WIFI STATUS TESTS - the agreed WIFI STATUS lines parse, in every TCP mode, and extra fields survive.
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen

using NUnit.Framework;

namespace HdlcGatewayTest.Core.Tests;

[TestFixture]
public sealed class WifiStatusTests
{
    [Test]
    public void ServerMode_Connected()
    {
        string text = "WiFi: ON ssid=Lab state=CONNECTED ip=192.168.1.77 rssi=-61 hostname=hdlcgw\r\n"
                      + "TCP: SERVER port=1362 listening=yes peer=192.168.1.180:51234\r\n"
                      + "MIRROR: OFF\r\n";
        Assert.That(WifiStatus.TryParse(text, out WifiStatus? s), Is.True);
        Assert.That(s!.WifiOnOff, Is.EqualTo("ON"));
        Assert.That(s.Ssid, Is.EqualTo("Lab"));
        Assert.That(s.State, Is.EqualTo("CONNECTED"));
        Assert.That(s.Ip, Is.EqualTo("192.168.1.77"));
        Assert.That(s.Rssi, Is.EqualTo(-61));
        Assert.That(s.Hostname, Is.EqualTo("hdlcgw"));
        Assert.That(s.IsUp, Is.True);
        Assert.That(s.TcpMode, Is.EqualTo("SERVER"));
        Assert.That(s.TcpPort, Is.EqualTo(1362));
        Assert.That(s.TcpListening, Is.True);
        Assert.That(s.TcpPeer, Is.EqualTo("192.168.1.180:51234"));
        Assert.That(s.Mirror, Is.EqualTo("OFF"));
    }

    [Test]
    public void ClientMode_AndOff_AndInsideInfoOutput()
    {
        string client = "Build: Nov  9 2026\r\nWiFi: ON ssid=Lab state=CONNECTED ip=10.0.0.5 rssi=-70 hostname=hdlcgw\r\n"
                        + "TCP: CLIENT host=192.168.1.180 port=1362 state=CONNECTING\r\nMIRROR: ON\r\nFree Heap: 1\r\n";
        Assert.That(WifiStatus.TryParse(client, out WifiStatus? s), Is.True);
        Assert.That(s!.TcpMode, Is.EqualTo("CLIENT"));
        Assert.That(s.TcpHost, Is.EqualTo("192.168.1.180"));
        Assert.That(s.TcpPort, Is.EqualTo(1362));
        Assert.That(s.TcpState, Is.EqualTo("CONNECTING"));
        Assert.That(s.State, Is.EqualTo("CONNECTED"), "the WiFi state is not overwritten by the TCP state");
        Assert.That(s.Mirror, Is.EqualTo("ON"));

        Assert.That(WifiStatus.TryParse("WiFi: OFF ssid=(none) state=OFF ip=none rssi=0 hostname=hdlcgw\r\nTCP: OFF\r\n", out s), Is.True);
        Assert.That(s!.WifiOnOff, Is.EqualTo("OFF"));
        Assert.That(s.IsUp, Is.False);
        Assert.That(s.TcpMode, Is.EqualTo("OFF"));
        Assert.That(s.TcpPort, Is.EqualTo(0));
    }

    [Test]
    public void UnknownKeysKept_AndNoLinesMeansFalse()
    {
        Assert.That(WifiStatus.TryParse("WiFi: ON ssid=X state=IDLE ip=none rssi=0 hostname=hdlcgw channel=6\r\n", out WifiStatus? s), Is.True);
        Assert.That(s!.Values["channel"], Is.EqualTo("6"));
        Assert.That(WifiStatus.TryParse("HDLC frames   Total#   OK\r\n", out _), Is.False);
    }

    [Test]
    public void Statistics_WifiFieldsAreAppendedAtTheEnd()
    {
        byte[] payload = new byte[23 * 4];
        payload[17 * 4] = 5;    // frames_to_peer
        payload[22 * 4] = 2;    // wifi_connects
        Assert.That(GatewayStatistics.TryParse(payload, out GatewayStatistics? st), Is.True);
        Assert.That(st!.FramesToPeer, Is.EqualTo(5u));
        Assert.That(st.WifiConnects, Is.EqualTo(2u));
        Assert.That(GatewayStatistics.TryParse(new byte[17 * 4], out st), Is.True);
        Assert.That(st!.FramesToPeer, Is.Null, "a firmware without WiFi reports 17 fields");
    }

    [Test]
    public void Statistics_Describe_NamesFieldsByFirmware()
    {
        byte[] rc = new byte[12 * 4];
        rc[8 * 4] = 3;      // recv_dropped
        rc[11 * 4] = 1;     // control_dropped
        Assert.That(GatewayStatistics.TryParse(rc, out GatewayStatistics? s), Is.True);
        string pico = s!.Describe("RC422Dev");
        Assert.That(pico, Does.Contain("recv_dropped=3"));
        Assert.That(pico, Does.Contain("control_dropped=1"));
        Assert.That(pico, Does.Not.Contain("line_rx_bytes"));
        byte[] rc16 = new byte[16 * 4];
        rc16[15 * 4] = 7;
        Assert.That(GatewayStatistics.TryParse(rc16, out GatewayStatistics? s16), Is.True);
        Assert.That(s16!.Describe("RC422Dev"), Does.Contain("usb_write_max_us=7"));
        string esp = s.Describe("HDLCGW");
        Assert.That(esp, Does.Contain("line_rx_bytes=3"), "the same wire position means something else on HDLCGW");
        Assert.That(GatewayStatistics.TryParse(new byte[30 * 4], out s), Is.True);
        Assert.That(s!.Describe(null), Does.Contain("field_29=0"));
    }
}
