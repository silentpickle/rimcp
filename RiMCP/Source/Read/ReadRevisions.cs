using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RiMCP.Shared;
using RiMCP.Util;

namespace RiMCP.Read
{
    // Main-thread snapshots retain only content hashes and row order. The selector and load are
    // part of the token so a revision can be shared by clients asking for the same view.
    internal static class ReadRevisions
    {
        private const int Retained = 48;
        private static readonly Dictionary<string, Snapshot> snapshots = new Dictionary<string, Snapshot>();
        private static readonly Queue<string> retentionOrder = new Queue<string>();
        private static string load;

        public static string TrySerialize(ReadRequest request, Dictionary<string, object> envelope, string tool,
            Dictionary<string, object> data, string pageField, IEnumerable<string> rowIds)
        {
            string since = request.Get("since");
            if (request.OptionalBool("trackRevision") != true && since == null)
            {
                return null;
            }

            string loadId = envelope.TryGetValue("loadId", out object currentLoad) ? (string)currentLoad : null;
            if (load != loadId)
            {
                snapshots.Clear();
                retentionOrder.Clear();
                load = loadId;
            }
            envelope.TryGetValue("mapId", out object map);
            string selector = Json.Serialize(request.Query
                .Where(pair => !string.Equals(pair.Key, "since", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(pair.Key, "trackRevision", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(pair.Key, ReplyLimits.Argument, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => pair.Value));

            string[] ids = rowIds?.ToArray() ?? Array.Empty<string>();
            object[] rows = pageField == null ? Array.Empty<object>() : ((IEnumerable)data[pageField]).Cast<object>().ToArray();
            if (ids.Length != rows.Length || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            {
                if (since != null)
                {
                    envelope["reset"] = true;
                }
                return null;
            }
            var sections = data.Where(field => field.Key != pageField).ToDictionary(field => field.Key, field => field.Value);

            using (SHA256 sha = SHA256.Create())
            {
                string scope = Hash(sha, Json.Serialize(new[] { tool, loadId, map, selector }));
                var current = new Snapshot(scope, ids,
                    sections.ToDictionary(field => field.Key, field => Hash(sha, Json.Serialize(field.Value))),
                    ids.Zip(rows, (id, row) => new { id, digest = Hash(sha, Json.Serialize(row)) })
                        .ToDictionary(item => item.id, item => item.digest));
                envelope.TryGetValue("nextCursor", out object next);
                string revision = Hash(sha, Json.Serialize(new object[]
                {
                    scope, current.Sections, current.Order, current.Rows, envelope["truncated"], next
                }));
                var revised = new Dictionary<string, object>(envelope) { ["revision"] = revision };
                int maximum = ReadReplyBudget.MaxBytes(request);
                if (since != null && snapshots.TryGetValue(since, out Snapshot previous) && previous.Scope == scope)
                {
                    var delta = new Dictionary<string, object>(revised) { ["since"] = since, ["delta"] = true };
                    delta["data"] = sections.Where(field => Changed(previous.Sections, field.Key, current.Sections[field.Key]))
                        .ToDictionary(field => field.Key, field => field.Value);
                    string[] removedFields = previous.Sections.Keys.Except(current.Sections.Keys).ToArray();
                    if (removedFields.Length > 0)
                    {
                        delta["removedFields"] = removedFields;
                    }
                    if (pageField != null)
                    {
                        object[] upsert = ids.Zip(rows, (id, value) => new { id, value })
                            .Where(row => Changed(previous.Rows, row.id, current.Rows[row.id]))
                            .Select(row => (object)Dto.Obj(Dto.Field("id", row.id), Dto.Field("value", row.value))).ToArray();
                        string[] removed = previous.Order.Where(id => !current.Rows.ContainsKey(id)).ToArray();
                        bool reordered = !previous.Order.SequenceEqual(ids);
                        if (upsert.Length > 0 || removed.Length > 0 || reordered)
                        {
                            delta["listDelta"] = Dto.Obj(Dto.Field("field", pageField), Dto.Field("upsert", upsert),
                                Dto.Field("removed", removed), Dto.Field("order", reordered ? ids : null));
                        }
                    }

                    string change = Json.Serialize(delta);
                    if (ReadReplyBudget.Fits(change, maximum))
                    {
                        Remember(revision, current);
                        return change;
                    }
                }

                if (since != null)
                {
                    revised["reset"] = true;
                    envelope["reset"] = true;
                }
                if (pageField != null)
                {
                    revised["rowIds"] = ids;
                }
                string full = Json.Serialize(revised);
                if (ReadReplyBudget.Fits(full, maximum))
                {
                    Remember(revision, current);
                    return full;
                }
                // A byte-capped reply may drop parts or rows. Do not make it a delta baseline.
                return null;
            }
        }

        private static bool Changed(Dictionary<string, string> previous, string key, string hash)
        {
            return !previous.TryGetValue(key, out string old) || old != hash;
        }

        private static void Remember(string revision, Snapshot snapshot)
        {
            if (snapshots.ContainsKey(revision))
            {
                return;
            }
            if (retentionOrder.Count == Retained)
            {
                snapshots.Remove(retentionOrder.Dequeue());
            }
            snapshots.Add(revision, snapshot);
            retentionOrder.Enqueue(revision);
        }

        private static string Hash(SHA256 sha, string content)
        {
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
            return BitConverter.ToString(digest, 0, 16).Replace("-", "").ToLowerInvariant();
        }

        private sealed class Snapshot
        {
            public readonly string Scope;
            public readonly string[] Order;
            public readonly Dictionary<string, string> Sections;
            public readonly Dictionary<string, string> Rows;

            public Snapshot(string scope, string[] order, Dictionary<string, string> sections, Dictionary<string, string> rows)
            {
                Scope = scope;
                Order = order;
                Sections = sections;
                Rows = rows;
            }
        }
    }
}
