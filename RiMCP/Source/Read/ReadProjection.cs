using System;
using System.Collections.Generic;
using System.Linq;
using RiMCP.Bridge;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Read
{
    internal sealed class ReadProjection
    {
        private readonly string[] fields;

        private ReadProjection(string[] fields)
        {
            this.fields = fields;
        }

        public static ReadProjection Parse(ReadRequest request, string[] supported)
        {
            if (request.Get("fields") == null)
            {
                return null;
            }
            if (request.IdsOnly)
            {
                throw new BridgeException(400, "Use fields or idsOnly.");
            }
            string[] fields = request.List("fields").Distinct(StringComparer.Ordinal).ToArray();
            if (fields.Length == 0)
            {
                throw new BridgeException(400, "Give at least one field.");
            }
            string unknown = fields.FirstOrDefault(field => !supported.Contains(field, StringComparer.Ordinal));
            if (unknown != null)
            {
                throw new BridgeException(400, "Unknown field '" + unknown + "'. Choose from " + string.Join(", ", supported) + ".");
            }
            return new ReadProjection(fields);
        }

        public bool Includes(string field)
        {
            return fields.Contains(field, StringComparer.Ordinal);
        }

        public static object Serialize(ReadProjection projection, Thing thing, Func<object> describe)
        {
            return projection == null ? describe() : projection.Project(thing, describe);
        }

        private object Project(Thing thing, Func<object> describe)
        {
            Dictionary<string, object> native = null;
            var row = new Dictionary<string, object>(fields.Length);
            foreach (string field in fields)
            {
                row[field] = Value(field, thing, describe, ref native);
            }
            return row;
        }

        private static object Value(string field, Thing thing, Func<object> describe, ref Dictionary<string, object> native)
        {
            switch (field)
            {
                case "id": return ReadUtil.ThingId(thing);
                case "def": return thing.def.defName;
                case "cell": return Dto.Obj(Dto.Field("x", thing.Position.x), Dto.Field("z", thing.Position.z));
                default:
                    native = native ?? (Dictionary<string, object>)describe();
                    return native.TryGetValue(field, out object value) ? value : null;
            }
        }
    }

    internal static class ReadBatch
    {
        public static bool IsBatch(ReadRequest request, string pathId)
        {
            bool hasIds = request.Get("ids") != null;
            bool batchPath = pathId == ReadShape.BatchPathId;
            if (hasIds != batchPath)
            {
                throw new BridgeException(400, "Give a single id or ids.");
            }
            return batchPath;
        }

        public static BridgeResponse Reply(ReadContext context, string field, Func<string, object> describe)
        {
            string[] ids = context.Request.List("ids");
            if (ids.Length == 0 || ids.Length > ReadShape.MaxIds)
            {
                throw new BridgeException(400, "Give 1 to " + ReadShape.MaxIds + " ids.");
            }
            Page<string> page = new Page<string>(ids, context.Request);
            object[] rows = page.Items.Select(id => ReadOne(id, describe)).ToArray();
            return ReadEnvelope.Ok(context, Dto.Obj(Dto.Field(field, rows)), page.Truncated, page.NextCursor, field);
        }

        private static object ReadOne(string id, Func<string, object> describe)
        {
            object data = describe(id);
            return Dto.Obj(Dto.Field("id", id), Dto.Field("data", data),
                Dto.Field("error", data == null ? "ID not found on the selected map." : null));
        }
    }
}
