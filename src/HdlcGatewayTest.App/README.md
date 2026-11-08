# hdlc-gateway-tool

The window: two panes, one HDLC gateway each, built on the same libraries as `hdlc-loopback`.

## What a pane does

- **Connect** to a COM port, or type `host:port` (a gateway in WiFi mode, `hdlcgw.local:1362`)
  into the endpoint box. The tool sends HELLO; a firmware with control frames (HDLCGW 0.2.0
  and later) answers with its name and version and the badge says `CONTROL FRAMES`. If nothing
  answers within half a second the tool opens the legacy ESC ESC ESC text console, leaves it
  again, and tries HELLO once more (a board left in its text console by an earlier session
  types the first HELLO into its input line). Only when that is silent too is the firmware a
  text-console-only one (`TEXT CONSOLE (legacy)`: RC422Dev, older HDLCGW).
- **Console**: type a firmware command (`HELP`, `STAT`, `BAUD 38400`, `LOG 2`, `F`, `INSPECT`,
  ...), Enter sends, Up and Down walk the history. Replies, firmware log lines (`~`), events
  (`!`) and the tool's own notices (`*`) are colour coded. Commands an AI sent over MCP show as
  `> (mcp)`.
- **Line BAUD**: the gateway's transmit clock, sent as a `BAUD` command.
- **Firmware log**: the level of log frames the firmware sends while traffic flows (needs
  control frames). 2 shows bad frames and aborts, 4 one line per good frame.
- **Role**: what happens to a data frame the gateway receives.
  - *Console* (default) and *Free*: it is only listed. Free adds a hex box: paste bytes, tick
    "add FCS" or supply your own, Send frame.
  - *Echo*: it is sent straight back on the same port.
  - *Bridge*: it is sent on the other pane's port, and the other way round. Both panes must be
    connected; choosing Bridge on one pane sets both.
  - *TcpListen*: the pane also listens on a TCP port for the nd100x emulator, started with
    `--hdlc=N:<this-pc>:<port>`. Frames from the emulator go out on the line, frames from the
    line go to the emulator. Pane A and B need different ports (defaults 1362 and 1363).
- **Frames**: every frame in and out, newest last, with time, direction, source (Serial, Tcp,
  Echo, Bridge, Console, Mcp, Traffic), address, decoded control field and FCS verdict. Click
  one for the full decode and hex dump on the right. Control frames are purple, bad FCS red.
  The filter box keeps the rows whose text contains what you type: `RX`, `TX`, `addr 01`,
  `SABM`, `FCS BAD`, `Tcp`.

## Traffic test

The panel under the panes runs the same engine as `hdlc-loopback`: loopback on A, or A to B
and B to A, at a list of rates, with the frame size, window, seconds per rate and timeout the
command-line tool takes. The status line and the PASS/FAIL table are the same text.

## MCP

Tick **MCP server** in the status bar (remembered, default off) or start with `--mcp [port]` for
one run. The repository README has the client set-up for Claude Code, Cursor and VS Code and the
list of tools. The **MCP log** button shows every call and result.

## Settings

`%APPDATA%\HdlcGatewayTool\settings.json`: MCP on/off and port, the last endpoint and rate of
each pane, the TCP ports, the traffic test knobs. Delete it for defaults.
