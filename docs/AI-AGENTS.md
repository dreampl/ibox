# AI agents (MCP) and the IBOX command line

IBOX can be controlled by an AI assistant (Claude or any other MCP client) and by scripts on the same computer.

## Turn it on

IBOX → Settings → **AI agents (MCP) & command line**:

- **Allow agent control** — agents can read the status, servers, rules and DNS choice.
- **Allow changes** — agents can also connect, disconnect (or cancel a connection in progress), switch server and
  mode, add or remove routing rules and change the DNS choice.

Both are off by default. The channel is a local pipe (Windows named pipe, Linux Unix socket) that only programs
running as the same OS user can open; nothing listens on the network. Server credentials are never part of an answer.

## MCP client configuration

Windows (the path is shown, with a Copy button, in IBOX settings):

```json
{ "mcpServers": { "ibox": { "command": "C:\\Program Files\\IBOX\\ibox-ctl.exe", "args": ["mcp"] } } }
```

Linux:

```json
{ "mcpServers": { "ibox": { "command": "/opt/ibox/jackivpn", "args": ["mcp"] } } }
```

Tools: `ibox_status`, `ibox_list_servers`, `ibox_connect`, `ibox_disconnect`, `ibox_set_mode`, `ibox_list_rules`,
`ibox_add_rule`, `ibox_remove_rule`, `ibox_get_dns`, `ibox_set_dns`, `ibox_speed_test`.

## Command line

`ibox-ctl` (Windows) or `jackivpn ctl` (Linux), with the window running:

```
ibox-ctl status
ibox-ctl servers [filter]
ibox-ctl connect "Germany"        # or an id, or --smart
ibox-ctl disconnect
ibox-ctl mode tun|proxy
ibox-ctl rules
ibox-ctl rule add "Banks" direct --domain bmi.ir,bankmellat.ir
ibox-ctl rule remove <id>
ibox-ctl dns | dns smart | dns system | dns manual 178.22.122.100 [--doh dns.example]
ibox-ctl speedtest
```

Add `--json` for the raw answer. Exit codes: 0 ok, 1 refused (message on stderr), 2 usage, 3 IBOX not reachable.
