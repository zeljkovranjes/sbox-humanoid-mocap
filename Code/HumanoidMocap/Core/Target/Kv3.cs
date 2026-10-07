#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HumanoidMocap.Core.Target;

/// <summary>
/// Minimal KV3 text reader/writer sufficient for vmdl files: header comment, objects, arrays,
/// quoted strings with escapes, triple-quoted multi-line strings, numbers (int/double kept
/// distinct), bools, null, bare identifiers as strings, trailing commas, and
/// <c>//</c>/<c>/* */</c> comments. Unknown constructs fail loudly with a
/// <see cref="FormatException"/> rather than being silently corrupted. The writer re-serializes
/// in the shipped vmdl style (tabs, <c>key = </c> before block values, trailing commas after
/// object array entries, inline scalar arrays, CRLF).
/// </summary>
public static class Kv3
{
    /// <summary>Parses KV3 text into a document.</summary>
    /// <exception cref="FormatException">Thrown on any construct this reader does not
    /// understand, with line/column context.</exception>
    public static Kv3Document Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text);
        return parser.ParseDocument();
    }

    /// <summary>Serializes a document to KV3 text in shipped-vmdl style.</summary>
    public static string Serialize(Kv3Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var sb = new StringBuilder();
        sb.Append(document.Header).Append("\r\n");
        if (document.Root is not KvObject rootObject)
            throw new FormatException("KV3 root must be an object to serialize a vmdl document.");
        WriteObjectBlock(sb, rootObject, 0);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- writer

    private static void WriteObjectBlock(StringBuilder sb, KvObject obj, int indent)
    {
        Indent(sb, indent).Append("{\r\n");
        foreach (var key in obj.Keys)
            WritePair(sb, key, obj[key], indent + 1);
        Indent(sb, indent).Append("}\r\n");
    }

    private static void WritePair(StringBuilder sb, string key, KvValue value, int indent)
    {
        var keyText = IsIdentifier(key) ? key : QuoteString(key);
        switch (value)
        {
            case KvObject o:
                Indent(sb, indent).Append(keyText).Append(" = \r\n");
                WriteObjectBlockAsValue(sb, o, indent, trailingComma: false);
                break;
            case KvArray a when IsBlockArray(a):
                Indent(sb, indent).Append(keyText).Append(" = \r\n");
                WriteArrayBlock(sb, a, indent, trailingComma: false);
                break;
            default:
                Indent(sb, indent).Append(keyText).Append(" = ").Append(ScalarText(value)).Append("\r\n");
                break;
        }
    }

    private static void WriteObjectBlockAsValue(StringBuilder sb, KvObject obj, int indent, bool trailingComma)
    {
        Indent(sb, indent).Append("{\r\n");
        foreach (var key in obj.Keys)
            WritePair(sb, key, obj[key], indent + 1);
        Indent(sb, indent).Append(trailingComma ? "},\r\n" : "}\r\n");
    }

    private static void WriteArrayBlock(StringBuilder sb, KvArray array, int indent, bool trailingComma)
    {
        Indent(sb, indent).Append("[\r\n");
        foreach (var item in array.Items)
        {
            switch (item)
            {
                case KvObject o:
                    WriteObjectBlockAsValue(sb, o, indent + 1, trailingComma: true);
                    break;
                case KvArray a when IsBlockArray(a):
                    WriteArrayBlock(sb, a, indent + 1, trailingComma: true);
                    break;
                case KvArray a:
                    Indent(sb, indent + 1).Append(InlineArrayText(a)).Append(",\r\n");
                    break;
                default:
                    Indent(sb, indent + 1).Append(ScalarText(item)).Append(",\r\n");
                    break;
            }
        }
        Indent(sb, indent).Append(trailingComma ? "],\r\n" : "]\r\n");
    }

    /// <summary>Arrays containing objects or block arrays are written multi-line; arrays of
    /// scalars (incl. nested inline arrays) stay on one line, as in shipped vmdl files.</summary>
    private static bool IsBlockArray(KvArray array)
    {
        foreach (var item in array.Items)
        {
            if (item is KvObject || (item is KvArray nested && IsBlockArray(nested)))
                return true;
        }
        return false;
    }

    private static string InlineArrayText(KvArray array)
    {
        if (array.Items.Count == 0)
            return "[ ]";
        var sb = new StringBuilder("[ ");
        for (var i = 0; i < array.Items.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(array.Items[i] is KvArray nested ? InlineArrayText(nested) : ScalarText(array.Items[i]));
        }
        return sb.Append(" ]").ToString();
    }

    private static string ScalarText(KvValue value)
        => value switch
        {
            KvString s => QuoteString(s.Value),
            KvLong l => l.Value.ToString(CultureInfo.InvariantCulture),
            KvDouble d => DoubleText(d.Value),
            KvBool b => b.Value ? "true" : "false",
            KvNull => "null",
            KvArray a => InlineArrayText(a),
            _ => throw new FormatException($"Cannot serialize {value.GetType().Name} as a scalar."),
        };

    private static string DoubleText(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new FormatException($"Cannot serialize non-finite double {value} to KV3.");
        var s = value.ToString("R", CultureInfo.InvariantCulture);
        return s.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0 ? s : s + ".0";
    }

    private static string QuoteString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\'': sb.Append("\\'"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || (!char.IsLetter(s[0]) && s[0] != '_'))
            return false;
        foreach (var c in s)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }
        return true;
    }

    private static StringBuilder Indent(StringBuilder sb, int indent) => sb.Append('\t', indent);

    // ---------------------------------------------------------------- parser

    private sealed class Parser
    {
        private readonly string _text;
        private int _pos;

        public Parser(string text)
        {
            _text = text;
            _pos = 0;
            if (_text.Length > 0 && _text[0] == '\uFEFF')
                _pos = 1;
        }

        public Kv3Document ParseDocument()
        {
            SkipWhitespace(allowComments: false);
            if (!Match("<!--"))
                throw Error("Expected KV3 header comment '<!-- ... -->'");
            var headerStart = _pos - 4;
            var end = _text.IndexOf("-->", _pos, StringComparison.Ordinal);
            if (end < 0)
                throw Error("Unterminated KV3 header comment");
            _pos = end + 3;
            var header = _text[headerStart.._pos];

            SkipWhitespace();
            var root = ParseValue();
            SkipWhitespace();
            if (_pos != _text.Length)
                throw Error("Unexpected trailing content after root value");
            return new Kv3Document(header, root);
        }

        private KvValue ParseValue()
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
                throw Error("Unexpected end of input, expected a value");

            var c = _text[_pos];
            if (c == '{')
                return ParseObject();
            if (c == '[')
                return ParseArray();
            if (c == '"')
                return new KvString(ParseString());
            if (c == '-' || c == '+' || char.IsDigit(c) || (c == '.' && _pos + 1 < _text.Length && char.IsDigit(_text[_pos + 1])))
                return ParseNumber();
            if (char.IsLetter(c) || c == '_')
            {
                var word = ParseIdentifier();
                return word switch
                {
                    "true" => new KvBool(true),
                    "false" => new KvBool(false),
                    "null" => KvNull.Instance,
                    _ => new KvString(word),
                };
            }

            throw Error($"Unexpected character '{c}'");
        }

        private KvObject ParseObject()
        {
            Expect('{');
            var obj = new KvObject();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _text.Length)
                    throw Error("Unterminated object");
                if (_text[_pos] == '}')
                {
                    _pos++;
                    return obj;
                }

                string key;
                if (_text[_pos] == '"')
                    key = ParseString();
                else if (char.IsLetter(_text[_pos]) || _text[_pos] == '_')
                    key = ParseIdentifier();
                else
                    throw Error($"Expected object key, found '{_text[_pos]}'");

                SkipWhitespace();
                Expect('=');
                var value = ParseValue();
                if (obj.GetOrNull(key) is not null)
                    throw Error($"Duplicate key '{key}' in object");
                obj[key] = value;

                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == ',')
                    _pos++; // tolerate optional commas between pairs
            }
        }

        private KvArray ParseArray()
        {
            Expect('[');
            var array = new KvArray();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _text.Length)
                    throw Error("Unterminated array");
                if (_text[_pos] == ']')
                {
                    _pos++;
                    return array;
                }

                array.Items.Add(ParseValue());
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == ',')
                {
                    _pos++;
                    continue;
                }
                if (_pos < _text.Length && _text[_pos] == ']')
                    continue; // last item without trailing comma
                throw Error("Expected ',' or ']' in array");
            }
        }

        private string ParseString()
        {
            if (Match("\"\"\""))
            {
                var end = _text.IndexOf("\"\"\"", _pos, StringComparison.Ordinal);
                if (end < 0)
                    throw Error("Unterminated multi-line string");
                var content = _text[_pos..end];
                _pos = end + 3;
                return content;
            }

            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length)
                    throw Error("Unterminated string");
                var c = _text[_pos++];
                if (c == '"')
                    return sb.ToString();
                if (c == '\n' || c == '\r')
                    throw Error("Unescaped newline in single-line string");
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (_pos >= _text.Length)
                    throw Error("Unterminated escape sequence");
                var e = _text[_pos++];
                sb.Append(e switch
                {
                    '"' => '"',
                    '\'' => '\'',
                    '\\' => '\\',
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    _ => throw Error($"Unsupported escape sequence '\\{e}'"),
                });
            }
        }

        private string ParseIdentifier()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
                _pos++;
            return _text[start.._pos];
        }

        private KvValue ParseNumber()
        {
            var start = _pos;
            if (_pos < _text.Length && (_text[_pos] == '-' || _text[_pos] == '+'))
                _pos++;
            var isDouble = false;
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (char.IsDigit(c))
                {
                    _pos++;
                }
                else if (c == '.')
                {
                    isDouble = true;
                    _pos++;
                }
                else if (c == 'e' || c == 'E')
                {
                    isDouble = true;
                    _pos++;
                    if (_pos < _text.Length && (_text[_pos] == '-' || _text[_pos] == '+'))
                        _pos++;
                }
                else
                {
                    break;
                }
            }

            var token = _text[start.._pos];
            if (isDouble)
            {
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw Error($"Invalid number '{token}'");
                return new KvDouble(d);
            }

            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                throw Error($"Invalid integer '{token}'");
            return new KvLong(l);
        }

        private void SkipWhitespace(bool allowComments = true)
        {
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (char.IsWhiteSpace(c))
                {
                    _pos++;
                    continue;
                }
                if (allowComments && c == '/' && _pos + 1 < _text.Length)
                {
                    if (_text[_pos + 1] == '/')
                    {
                        var nl = _text.IndexOf('\n', _pos);
                        _pos = nl < 0 ? _text.Length : nl + 1;
                        continue;
                    }
                    if (_text[_pos + 1] == '*')
                    {
                        var close = _text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                        if (close < 0)
                            throw Error("Unterminated block comment");
                        _pos = close + 2;
                        continue;
                    }
                }
                break;
            }
        }

        private bool Match(string token)
        {
            if (_pos + token.Length > _text.Length
                || string.CompareOrdinal(_text, _pos, token, 0, token.Length) != 0)
            {
                return false;
            }
            _pos += token.Length;
            return true;
        }

        private void Expect(char c)
        {
            if (_pos >= _text.Length || _text[_pos] != c)
                throw Error($"Expected '{c}'");
            _pos++;
        }

        private FormatException Error(string message)
        {
            var line = 1;
            var col = 1;
            for (var i = 0; i < _pos && i < _text.Length; i++)
            {
                if (_text[i] == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
            }
            return new FormatException($"KV3 parse error at line {line}, column {col}: {message}");
        }
    }
}
