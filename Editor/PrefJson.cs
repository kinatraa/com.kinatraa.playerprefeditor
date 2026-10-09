using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>Same type and value (the key is not compared).</summary>
        public bool SameValue(PrefEntry other) =>
            other != null && Type == other.Type && Type != PrefType.Unknown && Equals(Value, other.Value);
    }

    /// <summary>
    /// Converts PlayerPrefs values to and from formatted JSON.
    /// Document schema: { "key": { "type": "int" | "float" | "string", "value": ... }, ... }
    /// </summary>
    public static class PrefJson
    {
        // JSON has no literal for these, so they are written as strings.
        const string NaN = "NaN", PositiveInfinity = "Infinity", NegativeInfinity = "-Infinity";

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
            if (!(token is JObject doc))
            {
                error = "The root must be a JSON object: { \"key\": { \"type\": ..., \"value\": ... } }.";
                return false;
            }

            var parsed = new List<PrefEntry>();
            foreach (var p in doc.Properties())
            {
                if (p.Name.Length == 0)
                {
                    error = "Keys must not be empty.";
                    return false;
                }
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

        /// <summary>
        /// Rewrites a value's JSON for another type when the value survives the change:
        /// 5 → 5.0 → "5", "0.8" → 0.8, 2.0 → 2. Returns false when it would not (e.g. "abc" → int).
        /// </summary>
        public static bool TryConvertText(string json, PrefType from, PrefType to, out string converted)
        {
            converted = null;
            if (to == PrefType.Unknown || !TryParseValue(json, from, out var value, out _)) return false;
            if (from == to)
            {
                converted = FormatValue(to, value);
                return true;
            }

            object result = null;
            switch (to)
            {
                case PrefType.Int:
                    if (value is float f && !float.IsNaN(f) && !float.IsInfinity(f) && f == System.Math.Floor(f) && f >= -2147483648f && f < 2147483648f)
                        result = (int)f;
                    else if (value is string s && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                        result = i;
                    break;
                case PrefType.Float:
                    if (value is int n && (int)(float)n == n)
                        result = (float)n;
                    else if (value is string s && float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        result = parsed;
                    break;
                case PrefType.String:
                    result = value is float fv ? FloatToString(fv) : System.Convert.ToString(value, CultureInfo.InvariantCulture);
                    break;
            }
            if (result == null) return false;
            converted = FormatValue(to, result);
            return true;
        }

        /// <summary>True when a string value holds a JSON object or array; <paramref name="pretty"/> is it formatted.</summary>
        public static bool TryExpandEmbedded(string value, out string pretty)
        {
            pretty = null;
            if (string.IsNullOrWhiteSpace(value) || !TryParse(value, out var token, out _)) return false;
            if (!(token is JObject) && !(token is JArray)) return false;
            pretty = token.ToString(Formatting.Indented);
            return true;
        }

        /// <summary>Turns edited JSON back into the string to store: indented if the original was multi-line, compact otherwise.</summary>
        public static bool TryCollapseEmbedded(string json, bool indented, out string value, out string error)
        {
            value = null;
            if (!TryParse(json, out var token, out error)) return false;
            value = token.ToString(indented ? Formatting.Indented : Formatting.None);
            return true;
        }

        static string FloatToString(float f) =>
            float.IsNaN(f) ? NaN : float.IsPositiveInfinity(f) ? PositiveInfinity : float.IsNegativeInfinity(f) ? NegativeInfinity
            : f.ToString("R", CultureInfo.InvariantCulture);

        static JToken ToToken(PrefType type, object value)
        {
            if (type == PrefType.Unknown || value == null) return JValue.CreateNull();
            if (value is float f && (float.IsNaN(f) || float.IsInfinity(f))) return new JValue(FloatToString(f));
            return new JValue(value);
        }

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
                            error = "float value is out of range (about ±3.4e38).";
                            return false;
                        }
                        value = (float)d;
                        return true;
                    }
                    if (token.Type == JTokenType.String)
                    {
                        switch ((string)token)
                        {
                            case NaN: value = float.NaN; return true;
                            case PositiveInfinity: value = float.PositiveInfinity; return true;
                            case NegativeInfinity: value = float.NegativeInfinity; return true;
                        }
                    }
                    error = "float value must be a number, e.g. 0.8 (or \"NaN\", \"Infinity\", \"-Infinity\").";
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
