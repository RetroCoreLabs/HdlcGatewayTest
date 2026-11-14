# hdlc-loopback

The command-line tool, for scripts, CI logs and a terminal: the traffic test, single firmware
commands, and the MCP server without a window.

```powershell
hdlc-loopback --list                                    # serial ports
hdlc-loopback --port COM10 --command STAT --command INFO  # firmware commands, answers printed
hdlc-loopback --port COM10 --rates 38400 --seconds 10   # loopback traffic test, one rate
hdlc-loopback --port COM10 --port-b COM18               # two gateways, A->B and B->A
hdlc-loopback --mcp                                     # MCP server on 127.0.0.1:2525 until Ctrl-C
hdlc-loopback --help                                    # every option
```

## How it talks to the firmware

At the start it probes the gateway: the ESC ESC ESC text console first (RC422Dev locks its
console after the first forwarded frame, so no frame may go out before that), then HELLO as a
control frame. HDLCGW 0.2.0 and later answer HELLO, and `BAUD` and `--command` then travel as
control frames without leaving forwarding mode; a firmware that does not answer (RC422Dev,
older HDLCGW) gets the text console. The tool says which path it uses in its first lines.
`--no-console` skips the console entirely and never sends an ESC byte: use it when the gateway
is known to speak control frames and its console must not be touched.

## The traffic test

For every rate: set `BAUD` on each gateway, send numbered frames with the window full, check
every frame that comes back (sequence, payload, FCS), drain, and freeze the counters. The final
table has one row per rate and direction: sent, OK, bad FCS, corrupt, lost, late, dropped,
kilobit per second, round trip. `PASS` in every row gives exit code 0.

`--size` is the frame length with FCS (126 by default; HDLCGW and RC422Dev both take up to 1024), `--window`
the encoded bytes in flight, `--timeout` the seconds before a frame counts as lost (raised
automatically when the window takes longer than that to cross a slow line), `--no-baud` keeps the
gateways' clocks, `--verbose N` explains the first N bad frames, `--log FILE` copies everything
printed.

## MCP

`--mcp [PORT]` hosts the same MCP server the window has, prints every call and result, and runs
until Ctrl-C. The AI opens the ports itself with `hdlc_open`. The repository README has the
client set-up and the list of tools.
