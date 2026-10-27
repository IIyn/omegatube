using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OmegaTube.Util
{
    // Minimal JSON reader/writer (no dependency on System.Text.Json, absent from TizenFX 8).
    // Objects are Dictionary<string, object>, arrays List<object>, numbers double, plus string/bool/null.
    public static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            object value = ReadValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length)
            {
                throw new FormatException("trailing characters at " + i);
            }
            return value;
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        // Typed accessors that tolerate missing keys / wrong types.
        public static Dictionary<string, object> Obj(object o, string key)
        {
            return (o as Dictionary<string, object>) != null && ((Dictionary<string, object>)o).TryGetValue(key, out object v)
                ? v as Dictionary<string, object> : null;
        }

        public static List<object> Arr(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            return d != null && d.TryGetValue(key, out object v) ? v as List<object> : null;
        }

        public static string Str(object o, string key, string fallback = "")
        {
            var d = o as Dictionary<string, object>;
            return d != null && d.TryGetValue(key, out object v) && v is string s ? s : fallback;
        }

        public static double Num(object o, string key, double fallback = 0)
        {
            var d = o as Dictionary<string, object>;
            return d != null && d.TryGetValue(key, out object v) && v is double n ? n : fallback;
        }

        public static bool Bool(object o, string key, bool fallback = false)
        {
            var d = o as Dictionary<string, object>;
            return d != null && d.TryGetValue(key, out object v) && v is bool b ? b : fallback;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }
        }

        private static object ReadValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length)
            {
                throw new FormatException("unexpected end");
            }
            char c = s[i];
            switch (c)
            {
                case '{':
                    return ReadObject(s, ref i);
                case '[':
                    return ReadArray(s, ref i);
                case '"':
                    return ReadString(s, ref i);
                case 't':
                    Expect(s, ref i, "true");
                    return true;
                case 'f':
                    Expect(s, ref i, "false");
                    return false;
                case 'n':
                    Expect(s, ref i, "null");
                    return null;
                default:
                    return ReadNumber(s, ref i);
            }
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
            {
                throw new FormatException("expected " + word + " at " + i);
            }
            i += word.Length;
        }

        private static Dictionary<string, object> ReadObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return d;
            }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ReadString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':')
                {
                    throw new FormatException("expected ':' at " + i);
                }
                i++;
                d[key] = ReadValue(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }
                if (i < s.Length && s[i] == '}')
                {
                    i++;
                    return d;
                }
                throw new FormatException("expected ',' or '}' at " + i);
            }
        }

        private static List<object> ReadArray(string s, ref int i)
        {
            var list = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return list;
            }
            while (true)
            {
                list.Add(ReadValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }
                if (i < s.Length && s[i] == ']')
                {
                    i++;
                    return list;
                }
                throw new FormatException("expected ',' or ']' at " + i);
            }
        }

        private static string ReadString(string s, ref int i)
        {
            if (s[i] != '"')
            {
                throw new FormatException("expected string at " + i);
            }
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        private static double ReadNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0)
            {
                i++;
            }
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        private static void WriteValue(StringBuilder sb, object v, int indent)
        {
            switch (v)
            {
                case null:
                    sb.Append("null");
                    break;
                case string str:
                    WriteString(sb, str);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case int n:
                    sb.Append(n.ToString(CultureInfo.InvariantCulture));
                    break;
                case Dictionary<string, object> obj:
                    sb.Append("{\n");
                    int k = 0;
                    foreach (var kv in obj)
                    {
                        sb.Append(' ', (indent + 1) * 2);
                        WriteString(sb, kv.Key);
                        sb.Append(": ");
                        WriteValue(sb, kv.Value, indent + 1);
                        sb.Append(++k < obj.Count ? ",\n" : "\n");
                    }
                    sb.Append(' ', indent * 2).Append('}');
                    break;
                case List<object> list:
                    sb.Append('[');
                    for (int j = 0; j < list.Count; j++)
                    {
                        if (j > 0)
                        {
                            sb.Append(", ");
                        }
                        WriteValue(sb, list[j], indent + 1);
                    }
                    sb.Append(']');
                    break;
                default:
                    WriteString(sb, v.ToString());
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
