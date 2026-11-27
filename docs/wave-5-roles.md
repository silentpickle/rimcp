# Wave 5 role connections

Run one RimMCP stdio proxy per role against the same game instance. The main role controls time, decisions, and saves. Give a maintenance role the reads and mutations needed for its assignment. Start a new client session after changing a proxy's settings.

For the role labels in the table below, the proxy returns role-specific startup guidance. Every role can read `rimcp://guide/getting-started` or open the [getting-started guide](getting-started.md). The host still assigns work and passes results between roles.

## Proxy settings

| Variable | Behavior |
| --- | --- |
| `RIMCP_ROLE` | Optional label carried alongside the MCP client name and proxy session. Use 1–32 ASCII letters, digits, underscores, or hyphens. It is an activity label. |
| `RIMCP_TOOL_GROUPS` | Optional comma-separated group IDs. Valid IDs are `observe`, `diagnostics`, `time`, `administration`, `orders`, `pawnControl`, `decisions`, `gameSettings`, and `gameSetup`. |
| `RIMCP_TOOLS` | Optional comma-separated exact MCP tool names. Use this to give a role just a few tools, including diagnostic reads such as `get_game_log`. |

When both scope variables are present, their tools are combined. Omit an unused variable. Empty and unknown entries stop startup. Unscoped clients keep the original behavior. The proxy lists the intersection of its scope and the mod's enabled tools. If the game is offline, it advertises only its local scope. A direct call outside that scope is refused before any game request. The mod still enforces global access for every forwarded call.

Keep the bridge token and broad proxy configuration in the trusted runner. The local scope is enforced on requests through that proxy. A client with its own bridge credential or access to a broader proxy can use the access available there. Give each role only its assigned MCP connection.

| Role | `RIMCP_TOOL_GROUPS` | `RIMCP_TOOLS` |
| --- | --- | --- |
| Main | `observe,time,decisions,orders,pawnControl,gameSetup` | Omit when groups cover the assignment |
| Steward | `observe` | `set_work_priorities` for the initial trial. Add individual administration tools as standing duties expand. |
| Scribe | Omit | `get_game_context,get_turn_digest,get_events,get_colony_status,get_advance,get_operation,list_saves,get_game_log` |
| Sentinel (`sentinel`) | Omit | `get_game_context,get_turn_digest,get_advance,get_events,list_threats` |
| Land advisor (`land-advisor`) | Omit | `get_game_context,get_map_survey,list_resources,check_placement` |
| Builder (`builder`) | Omit | `get_game_context,get_map_survey,check_placement,list_buildings,list_resources`. Add `place_blueprint` only for an assignment that includes order submission. |
| Researcher (`researcher`) | Omit | `get_game_context,get_research,search_defs,get_def`. Configure RimSage or Rimdex Reference separately if used. |

For scribe sessions with no diagnostics assignment, omit `get_game_log` from the comma-separated list. An exact list avoids enabling other diagnostic commands, including `export_reference`.

Optional specialists can be assigned when a specific question or task comes up. Give Sentinel watcher stops and digests from main. Give Builder a scoped assignment and avoid overlapping writes with Steward. Builder can use the read-only placement check first. The `place_blueprint` tool supports both preview and submission, so granting it allows both.

Assignments and reports should carry load ID, map ID, and evidence ticks. A role identifies the tick of each consequential observation or action receipt and, when available, its latest context tick and pause state before reporting. Main compares that evidence with current context and intervening actions before using it. Same-tick mutations can occur while paused; a timestamp alone does not establish freshness. Send urgent warnings immediately, and recheck action-specific preconditions. The full [report timing convention](../../rimworld/plan/wave-5-agents.md#report-timing-and-freshness) covers changed loads, multi-tick reports, and scribe checkpoints.

## Connect a client

Build and use the updated mod DLL and native proxy. Use **Connection → Copy MCP config** in the running mod for the command, arguments, URL, and token. Keep the token in private settings or pass it from a trusted local process. The following examples use an absolute path to the native executable, with `RIMCP_URL` and `RIMCP_TOKEN` already in the launching process's environment.

**Codex.** Put the relevant entry in the role session's private MCP configuration. Confirm its MCP tool list before handing it work. Use the active client configuration to disable any other RimMCP connection for that session. [Codex MCP configuration](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)

```toml
[mcp_servers.rimcp]
command = "/absolute/path/to/RiMCP.McpProxy"
env_vars = ["RIMCP_URL", "RIMCP_TOKEN"]

[mcp_servers.rimcp.env]
RIMCP_ROLE = "scribe"
RIMCP_TOOLS = "get_game_context,get_turn_digest,get_events,get_colony_status,get_advance,get_operation,list_saves,get_game_log"
```

**Claude Code.** Save the selected entry in a private MCP JSON file and start the role session with `claude --strict-mcp-config --mcp-config /path/to/role-mcp.json`. Check the connection in `/mcp`. [Claude Code MCP configuration](https://code.claude.com/docs/en/mcp)

```json
{
  "mcpServers": {
    "rimcp": {
      "type": "stdio",
      "command": "/absolute/path/to/RiMCP.McpProxy",
      "env": {
        "RIMCP_URL": "${RIMCP_URL}",
        "RIMCP_TOKEN": "${RIMCP_TOKEN}",
        "RIMCP_ROLE": "scribe",
        "RIMCP_TOOLS": "get_game_context,get_turn_digest,get_events,get_colony_status,get_advance,get_operation,list_saves,get_game_log"
      }
    }
  }
}
```

## Verify a role

1. On a disposable copy, read `get_game_context` first and verify the load ID, map, DLL hash, and proxy version. Inspect the advertised tools for your role. For the scribe, `get_turn_digest` should be listed and `advance_time` and `save_game` should be absent.
2. In a test client, call a known tool outside the proxy scope by exact name. A tool-scope error confirms the proxy refused it. Also test with an allowed group globally disabled in the mod. The tool disappears from discovery and the mod rejects a forwarded call to it.
3. Compare allowed main and steward mutations in RimMCP's in-game **Agent log**. Attribution shows the configured role, MCP client name, and proxy session. Keep a mutation's original `requestId` when retrying it.
4. For each connected role, read `get_game_context` and inspect `data.playMetrics.proxyRole`. It reports the role, successful call count, and original bridge reply bytes for the active load in that proxy process. The sample counts replies that carry a load ID and tick. Use before/after samples from the same process to measure a checkpoint. A restarted proxy starts a new sample.

## November 27 verification

- The full build and regression run passed 24 proxy tests and 97 compatibility checks. The harness accepted the three expected Unity-native checks under standalone Mono. A mock bridge confirmed local scoping during discovery when the game is reachable and when it is offline, and verified that a disallowed direct call sends no game request.
- Three separate stdio proxies connected to a single disposable RimWorld 1.6.4871 rev597 instance. The scribe list contained exactly eight reads. Direct calls to time, save, work priorities, and reference export were refused. The steward changed one Mining priority and retried it without a second activity entry. The main advanced time and verified a save. The Agent log displayed distinct main and steward labels alongside the MCP client and proxy session.
- With the disposable mod set to **Read only**, a proxy scoped to `observe,time` listed `get_game_context` and excluded `set_game_speed`. A direct call to `set_game_speed` reached the mod and was refused because the global Time group was off.
- The scribe read the turn digest and status, followed one checkpoint letter using `get_events`, sampled retained warnings, and confirmed the save via `get_operation`. The same checkpoint evidence produced the same event identity. The consumed cursor returned no duplicate, and the game reported a history reset after reload.
- The DLL advertised SHA-256 `6721be098ebd15f7c651d4bbb44eabe0fd83616c27657d3ea3eaa0bc42b22d1d`. The proxy reported version 1.2.0. The trial used scripted clients for MCP transport and game behavior. Running the role prompts in actual Codex and Claude Code play sessions remains open.

### Light writing trial

A second disposable run used the main connection's digest and verified save receipt to produce a diary and resumable handoff. The scoped scribe connection recorded 9 successful pre-reload calls and 28,856 bridge reply bytes across the harness, including replay and metric sampling. The history cursor suppressed a duplicate test letter and reset on reload. Codex wrote the checkpoint from captured responses. Independent model roles in Codex and Claude Code, live incomplete history, and a normal checkpoint's incremental cost remain open. [Colony trial and diary](../../rimworld/colonies/benevolina-of-november/handoffs/2026-11-27-wave-5.md#light-scribe-writing-trial)
