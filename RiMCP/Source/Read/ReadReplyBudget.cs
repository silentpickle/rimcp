using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RiMCP.Bridge;
using RiMCP.Shared;
using RiMCP.Util;

namespace RiMCP.Read
{
    internal static class ReadReplyBudget
    {
        public static string Serialize(Dictionary<string, object> envelope, ReadRequest request, string pageField = null, string detailSection = null)
        {
            int maxBytes = MaxBytes(request);
            string body = Json.Serialize(envelope);
            return Fits(body, maxBytes) ? body : new CappedReply(envelope, maxBytes, pageField).Fit(request.Cursor, detailSection);
        }

        internal static int MaxBytes(ReadRequest request)
        {
            int maxBytes = request.OptionalInt(ReplyLimits.Argument) ?? ReplyLimits.DefaultBytes;
            if (maxBytes < ReplyLimits.MinBytes || maxBytes > ReplyLimits.MaxBytes)
            {
                throw new BridgeException(400, "maxBytes must be between " + ReplyLimits.MinBytes + " and " + ReplyLimits.MaxBytes + ".");
            }
            return maxBytes;
        }

        internal static bool Fits(string body, int maxBytes)
        {
            return Encoding.UTF8.GetByteCount(body) <= maxBytes;
        }

        private sealed class CappedReply
        {
            private const string DataField = "data";
            private readonly Dictionary<string, object> envelope;
            private readonly Dictionary<string, object> data;
            private readonly Dictionary<string, object> budget;
            private readonly List<string> omittedFields = new List<string>();
            private readonly int maxBytes;
            private readonly string pageField;
            private readonly RowPage page;

            public CappedReply(Dictionary<string, object> original, int maxBytes, string pageField)
            {
                this.maxBytes = maxBytes;
                this.pageField = pageField;
                envelope = new Dictionary<string, object>(original);
                envelope.TryGetValue(DataField, out object value);
                if (value is Dictionary<string, object> fields)
                {
                    data = new Dictionary<string, object>(fields);
                    envelope[DataField] = data;
                    page = RowPage.Find(envelope, data, pageField);
                }
                budget = Dto.Obj(Dto.Field("maxBytes", maxBytes), Dto.Field("omittedFields", omittedFields),
                    Dto.Field("hint", "Continue with nextCursor if present. To recover omitted fields or a row that did not fit, retry from the same cursor with a larger maxBytes, less detail, or a narrower query."));
                envelope["truncated"] = true;
                envelope["budget"] = budget;
            }

            public string Fit(int cursor, string detailSection)
            {
                string body = OmitDetail(detailSection);
                if (body != null && Fits(body, maxBytes))
                {
                    return body;
                }

                SetRows(cursor, 0);
                body = Json.Serialize(envelope);
                if (!Fits(body, maxBytes))
                {
                    body = FitFields();
                }
                return page == null || page.Rows.Length == 0 || !envelope.ContainsKey(DataField) ? body : FitPage(cursor, body);
            }

            private string OmitDetail(string detailSection)
            {
                if (detailSection == null || data == null)
                {
                    return null;
                }
                string[] path = detailSection.Split('.');
                if (path.Length == 1)
                {
                    if (!data.Remove(detailSection))
                    {
                        return null;
                    }
                    omittedFields.Add(detailSection);
                    return Json.Serialize(envelope);
                }
                if (!data.TryGetValue(path[0], out object value) || !(value is Dictionary<string, object> fields))
                {
                    return null;
                }
                var copy = new Dictionary<string, object>(fields);
                if (!copy.Remove(path.Last()))
                {
                    return null;
                }
                data[path[0]] = copy;
                omittedFields.Add(detailSection);
                return Json.Serialize(envelope);
            }

            private string FitFields()
            {
                if (data != null)
                {
                    foreach (string key in FieldsBySize())
                    {
                        data.Remove(key);
                        omittedFields.Add(key);
                        string body = Json.Serialize(envelope);
                        if (Fits(body, maxBytes))
                        {
                            return body;
                        }
                    }
                }
                // Even the page metadata may be too large for an unusually small budget.
                envelope.Remove(DataField);
                envelope.Remove("nextCursor");
                envelope["budget"] = Dto.Obj(Dto.Field("maxBytes", maxBytes), Dto.Field("omittedFields", new[] { DataField }),
                    Dto.Field("hint", "Retry with a larger maxBytes, less detail, or a narrower query."));
                return Json.Serialize(envelope);
            }

            private string[] FieldsBySize()
            {
                return data.Where(field => field.Value != null && (page == null || field.Key != pageField))
                    .OrderByDescending(field => Encoding.UTF8.GetByteCount(Json.Serialize(field.Value)))
                    .Select(field => field.Key)
                    .ToArray();
            }

            private string FitPage(int cursor, string best)
            {
                // The serialized page grows with each whole row. Never split a row or skip one.
                // At the full length, omission metadata disappears, so check that case separately.
                SetRows(cursor, page.Rows.Length);
                string full = Json.Serialize(envelope);
                if (Fits(full, maxBytes))
                {
                    return full;
                }

                int low = 1;
                int high = page.Rows.Length - 1;
                while (low <= high)
                {
                    int count = low + (high - low) / 2;
                    SetRows(cursor, count);
                    string candidate = Json.Serialize(envelope);
                    if (Fits(candidate, maxBytes))
                    {
                        best = candidate;
                        low = count + 1;
                        continue;
                    }
                    high = count - 1;
                }
                return best;
            }

            private void SetRows(int cursor, int count)
            {
                if (page == null || page.Rows.Length == 0)
                {
                    return;
                }

                page.SetCount(cursor, count);
                int omittedRows = page.Rows.Length - count;
                budget["omittedRows"] = omittedRows > 0 ? (object)omittedRows : null;
                budget["rowsField"] = omittedRows > 0 ? page.Path : null;
            }
        }

        private sealed class RowPage
        {
            public readonly Array Rows;
            public readonly string Path;
            private readonly string key;
            private readonly Dictionary<string, object> fields;
            private readonly Dictionary<string, object> paging;
            private readonly object nextCursor;
            private readonly object truncated;
            private readonly bool nested;

            private RowPage(Array rows, string name, Dictionary<string, object> fields, Dictionary<string, object> paging, bool nested)
            {
                Rows = rows;
                key = nested ? "items" : name;
                Path = nested ? name + "." + key : name;
                this.fields = fields;
                this.paging = paging;
                this.nested = nested;
                paging.TryGetValue("nextCursor", out nextCursor);
                paging.TryGetValue("truncated", out truncated);
            }

            public static RowPage Find(Dictionary<string, object> envelope, Dictionary<string, object> data, string name)
            {
                if (name == null || !data.TryGetValue(name, out object value))
                {
                    return null;
                }
                if (value is Dictionary<string, object> container && container.TryGetValue("items", out object items) && items is Array nestedRows)
                {
                    var copy = new Dictionary<string, object>(container);
                    data[name] = copy;
                    return new RowPage(nestedRows, name, copy, copy, true);
                }
                return value is Array rows ? new RowPage(rows, name, data, envelope, false) : null;
            }

            public void SetCount(int cursor, int count)
            {
                bool shortened = count < Rows.Length;
                fields[key] = shortened ? Rows.Cast<object>().Take(count).ToArray() : Rows;
                paging["nextCursor"] = shortened ? count == 0 ? null : (object)(cursor + count) : nextCursor;
                if (nested)
                {
                    paging["truncated"] = shortened ? true : truncated;
                }
            }
        }
    }
}
