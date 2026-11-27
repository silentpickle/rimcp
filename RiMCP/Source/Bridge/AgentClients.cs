using System;
using System.Collections.Generic;
using System.Linq;

namespace RiMCP.Bridge
{
    // A short label for the client behind a request, from the names MCP clients give themselves.
    internal static class AgentClients
    {
        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "codex-mcp-client", "Codex" },
            { "codex", "Codex" },
            { "claude-code", "Claude Code" },
            { "claude-ai", "Claude" },
            { "opencode", "OpenCode" }
        };

        public static string Describe(string name, string session, string role = null)
        {
            string client = Clean(name, 40);
            string id = Clean(session, 8);
            string roleLabel = ValidRole(role) ? role : null;
            if (client == null && id == null && roleLabel == null)
            {
                return null;
            }
            string known;
            string label = client == null ? "Agent" : Known.TryGetValue(client, out known) ? known : client;
            if (roleLabel != null)
            {
                label = roleLabel + " (" + label + ")";
            }
            return id == null ? label : label + " " + id;
        }

        private static bool ValidRole(string role)
        {
            return !string.IsNullOrEmpty(role) && role.Length <= 32 &&
                role.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_'));
        }

        private static string Clean(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            string kept = new string(text.Trim().Where(c => char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_' || c == '.' || c == ' ').ToArray());
            kept = kept.Length > max ? kept.Substring(0, max) : kept;
            return kept.Length == 0 ? null : kept;
        }
    }
}
