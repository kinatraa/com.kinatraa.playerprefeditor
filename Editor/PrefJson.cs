using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace kinatraa.PlayerPrefEditor
{
    public enum PrefType { Int, Float, String, Unknown }

    public sealed class PrefEntry
    {
        public readonly string Key;
        public readonly PrefType Type;
        /// <summary>int, float or string; null when the type is Unknown.</summary>
        public readonly object Value;

        public PrefEntry(string key, PrefType type, object value)
        {
            Key = key;
            Type = type;
            Value = value;
        }
    }

    /// <summary>
    /// Converts PlayerPrefs values to and from formatted JSON.
    /// Document schema: { "key": { "type": "int" | "float" | "string", "value": ... }, ... }
    /// </summary>
    public static class PrefJson
    {
        public static string TypeName(PrefType type)
        {
            switch (type)
            {
                case PrefType.Int: return "int";
                case PrefType.Float: return "float";
                case PrefType.String: return "string";
                default: return "unknown";
            }
        }

        public static PrefType ParseType(string name)
        {
            switch (name)
            {
                case "int": return PrefType.Int;
                case "float": return PrefType.Float;
                case "string": return PrefType.String;
                default: return PrefType.Unknown;
            }
        }

        public static string FormatValue(PrefType type, object value) => ToToken(type, value).ToString(Formatting.Indented);

        public static string FormatDocument(IEnumerable<PrefEntry> entries)
        {
            var doc = new JObject();
            foreach (var e in entries)
                doc[e.Key] = new JObject { ["type"] = TypeName(e.Type), ["value"] = ToToken(e.Type, e.Value) };
            return doc.ToString(Formatting.Indented);
        }

        /// <summary>Parses a single value and checks it matches <paramref name="type"/>.</summary>
        public static bool TryParseValue(string json, PrefType type, out object value, out string error)
        {
            value = null;
            return TryParse(json, out var token, out error) && TryConvert(token, type, out value, out error);
        }

        /// <summary>Parses a whole document. Fails on the first invalid entry, so nothing partial is returned.</summary>
        public static bool TryParseDocument(string json, out List<PrefEntry> entries, out string error)
        {
            entries = new List<PrefEntry>();
            if (!TryParse(json, out var token, out error)) return false;
            var parsed = new List<PrefEntry>();
            if (!(token is JObject doc))
            {
                error = "The root must be a JSON object: { \"key\": { \"type\": ..., \"value\": ... } }.";
                return false;
            }

            foreach (var p in doc.Properties())
            {
                if (!(p.Value is JObject item) || item["type"]?.Type != JTokenType.String || !item.ContainsKey("value"))
                {
                    error = $"\"{p.Name}\": expected {{ \"type\": ..., \"value\": ... }}.";
                    return false;
                }

                var typeName = (string)item["type"];
                var type = ParseType(typeName);
                if (type == PrefType.Unknown)
                {
                    error = $"\"{p.Name}\": type must be int, float or string, not \"{typeName}\".";
                    return false;
                }

                if (!TryConvert(item["value"], type, out var value, out error))
                {
                    error = $"\"{p.Name}\": {error}";
                    return false;
                }

                parsed.Add(new PrefEntry(p.Name, type, value));
            }
            entries = parsed;
            return true;
        }

        static JToken ToToken(PrefType type, object value) =>
            type == PrefType.Unknown || value == null ? JValue.CreateNull() : new JValue(value);

        static bool TryParse(string json, out JToken token, out string error)
        {
            token = null;
            error = null;
            try
            {
                // DateParseHandling.None keeps date-like strings exactly as written.
                using (var reader = new JsonTextReader(new StringReader(json ?? "")) { DateParseHandling = DateParseHandling.None })
                {
                    token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    while (reader.Read())
                        if (reader.TokenType != JsonToken.Comment)
                            throw new JsonReaderException($"Unexpected content after the JSON value, line {reader.LineNumber}, position {reader.LinePosition}.");
                }
                return true;
            }
            catch (JsonException e)
            {
                error = "Invalid JSON: " + e.Message;
                return false;
            }
        }

        static bool TryConvert(JToken token, PrefType type, out object value, out string error)
        {
            value = null;
            error = null;
            switch (type)
            {
                case PrefType.Int:
                    if (token is JValue iv && iv.Value is long l && l >= int.MinValue && l <= int.MaxValue)
                    {
                        value = (int)l;
                        return true;
                    }
                    error = "int value must be a whole number from -2147483648 to 2147483647, e.g. 5.";
                    return false;

                case PrefType.Float:
                    if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                    {
                        var d = (double)token;
                        if (float.IsInfinity((float)d) && !double.IsInfinity(d))
                        {
                            error = "float value is out of range.";
                            return false;
                        }
                        value = (float)d;
                        return true;
                    }
                    error = "float value must be a number, e.g. 0.8.";
                    return false;

                case PrefType.String:
                    if (token.Type == JTokenType.String)
                    {
                        value = (string)token;
                        return true;
                    }
                    error = "string value must be a JSON string in double quotes, e.g. \"Hakien\".";
                    return false;

                default:
                    error = "Pick a type (int, float or string) before saving.";
                    return false;
            }
        }
    }
}
