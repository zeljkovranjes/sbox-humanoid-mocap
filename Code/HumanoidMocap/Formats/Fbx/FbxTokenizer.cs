#nullable enable annotations

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace HumanoidMocap.Formats.Fbx;

/// <summary>
/// Low-level FBX tokenizer. <see cref="Parse"/> accepts either format:
///
/// <para><b>Binary FBX</b> — detected by the 23-byte magic
/// <c>"Kaydara FBX Binary  \x00\x1a\x00"</c> followed by a little-endian u32 version
/// (e.g. 7400, 7700). Then a flat sequence of top-level node records.
/// Node record layout:</para>
/// <code>
///   u32|u64 endOffset      absolute byte offset just past this node (u64 for version >= 7500)
///   u32|u64 numProperties
///   u32|u64 propertyListLen  bytes occupied by the property list
///   u8      nameLen
///   byte[nameLen] name (ASCII)
///   ...properties (propertyListLen bytes)...
///   ...nested child records until endOffset, terminated by a NULL record...
/// </code>
/// The NULL sentinel record is an all-zero header: 13 bytes (3*u32 + u8) for
/// version &lt; 7500, 25 bytes (3*u64 + u8) for &gt;= 7500.
///
/// <para>Property records start with a one-byte type code:</para>
/// <code>
///   'Y' i16   'C' u8 bool (bit 0)   'I' i32   'F' f32   'D' f64   'L' i64
///   'f','d','l','i','b'  arrays: u32 arrayLen, u32 encoding, u32 compressedLength
///       encoding 0 = raw contiguous little-endian elements
///       encoding 1 = zlib stream (RFC 1950, 2-byte header) of compressedLength bytes
///   'S'  u32 len + raw bytes (strings may embed "\x00\x01" name/class separators)
///   'R'  u32 len + raw bytes (opaque blob)
/// </code>
///
/// <para><b>ASCII FBX</b> — fallback when the magic is absent. Grammar:
/// <c>Name: prop, prop, prop { children }</c>, where props are numbers
/// (int64 or double), quoted strings, or bare words; arrays use
/// <c>*N { a: v,v,v }</c>; comments run from ';' to end of line.</para>
/// </summary>
public static class FbxTokenizer
{
    /// <summary>
    /// Parses FBX bytes into a virtual root node whose <see cref="FbxNode.Children"/>
    /// are the document's top-level nodes. Throws <see cref="FormatException"/>
    /// (with byte-offset context for binary files) on malformed input.
    /// </summary>
    public static FbxNode Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (HasBinaryMagic(data))
            return ParseBinary(data);
        return ParseAscii(data);
    }

    // =====================================================================
    // Binary
    // =====================================================================

    // "Kaydara FBX Binary  " + 0x00 0x1A 0x00 (23 bytes total).
    private static readonly byte[] Magic =
        "Kaydara FBX Binary  \0\x1a\0"u8.ToArray();

    private static bool HasBinaryMagic(byte[] data)
        => data.Length >= Magic.Length && data.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    private static FbxNode ParseBinary(byte[] data)
    {
        int pos = Magic.Length;
        uint version = ReadU32(data, ref pos);
        // FBX 6.x stores transforms in Properties60 blocks with a different property layout;
        // parsing it with 7.x semantics silently yields identity transforms. Reject it.
        if (version < 7000)
            throw new FormatException(
                $"FBX 6.x (Properties60) is not supported (file declares version {version}); re-export as FBX 7.x (2011 or newer).");
        // Version >= 7500 widened the three node-header fields from u32 to u64.
        bool wide = version >= 7500;

        var root = new FbxNode("(root)");
        while (pos < data.Length)
        {
            var child = ReadNode(data, ref pos, wide);
            if (child is null) // NULL sentinel terminates the top-level list
                break;
            root.Children.Add(child);
        }

        if (root.Children.Count == 0)
            throw new FormatException($"FBX binary: no nodes found after header (offset {pos}).");
        return root;
    }

    /// <summary>Reads one node record; returns null for the all-zero NULL sentinel.</summary>
    private static FbxNode? ReadNode(byte[] data, ref int pos, bool wide)
    {
        int headerStart = pos;
        int sentinelSize = wide ? 25 : 13; // 3 offset fields + nameLen byte, all zero

        ulong endOffset = ReadOffsetField(data, ref pos, wide);
        ulong numProps = ReadOffsetField(data, ref pos, wide);
        ulong propListLen = ReadOffsetField(data, ref pos, wide);
        byte nameLen = ReadU8(data, ref pos);

        if (endOffset == 0 && numProps == 0 && propListLen == 0 && nameLen == 0)
            return null; // NULL record (13 or 25 zero bytes)

        if (endOffset <= (ulong)headerStart || endOffset > (ulong)data.Length)
            throw new FormatException(
                $"FBX binary: node at offset {headerStart} has invalid endOffset {endOffset} (file is {data.Length} bytes).");
        if (numProps > int.MaxValue || propListLen > (ulong)data.Length)
            throw new FormatException(
                $"FBX binary: node at offset {headerStart} has implausible header (numProps={numProps}, propListLen={propListLen}).");

        string name = Encoding.ASCII.GetString(ReadBytes(data, ref pos, nameLen));
        var node = new FbxNode(name);

        int propsEnd = checked(pos + (int)propListLen);
        if (propsEnd > (int)endOffset)
            throw new FormatException(
                $"FBX binary: node '{name}' at offset {headerStart}: property list overruns node end ({propsEnd} > {endOffset}).");

        for (ulong i = 0; i < numProps; i++)
            node.Properties.Add(ReadProperty(data, ref pos, name));

        if (pos != propsEnd)
            throw new FormatException(
                $"FBX binary: node '{name}' at offset {headerStart}: property list length mismatch (ended at {pos}, declared {propsEnd}).");

        // Remaining bytes up to endOffset are nested children plus a trailing NULL record.
        int end = (int)endOffset;
        while (pos < end)
        {
            if (end - pos < sentinelSize)
                throw new FormatException(
                    $"FBX binary: node '{name}': {end - pos} stray bytes before node end at offset {pos}.");
            var child = ReadNode(data, ref pos, wide);
            if (child is null)
            {
                if (pos != end)
                    throw new FormatException(
                        $"FBX binary: node '{name}': NULL sentinel at offset {pos - sentinelSize} but node ends at {end}.");
                break;
            }
            node.Children.Add(child);
        }

        if (pos != end)
            throw new FormatException(
                $"FBX binary: node '{name}': cursor {pos} does not match declared endOffset {end}.");
        return node;
    }

    private static object ReadProperty(byte[] data, ref int pos, string owner)
    {
        int at = pos;
        char code = (char)ReadU8(data, ref pos);
        switch (code)
        {
            case 'Y': return BinaryPrimitives.ReadInt16LittleEndian(ReadBytes(data, ref pos, 2));
            case 'C': return (ReadU8(data, ref pos) & 1) == 1;
            case 'I': return BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(data, ref pos, 4));
            case 'F': return BinaryPrimitives.ReadSingleLittleEndian(ReadBytes(data, ref pos, 4));
            case 'D': return BinaryPrimitives.ReadDoubleLittleEndian(ReadBytes(data, ref pos, 8));
            case 'L': return BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(data, ref pos, 8));

            case 'f': return ReadArray(data, ref pos, owner, 4, ToFloatArray);
            case 'd': return ReadArray(data, ref pos, owner, 8, ToDoubleArray);
            case 'l': return ReadArray(data, ref pos, owner, 8, ToLongArray);
            case 'i': return ReadArray(data, ref pos, owner, 4, ToIntArray);
            // bool arrays become bool[] (NOT byte[]) so a re-serializer can tell them
            // apart from 'R' blobs and write the correct type code back.
            case 'b': return ReadArray(data, ref pos, owner, 1,
                b => Array.ConvertAll(b, x => (x & 1) == 1));

            case 'S':
            {
                uint len = ReadU32(data, ref pos);
                // Kept raw: object names embed "\x00\x01" between name and class.
                return Encoding.UTF8.GetString(ReadBytes(data, ref pos, checked((int)len)));
            }
            case 'R':
            {
                uint len = ReadU32(data, ref pos);
                return ReadBytes(data, ref pos, checked((int)len)).ToArray();
            }

            default:
                throw new FormatException(
                    $"FBX binary: node '{owner}': unknown property type code '{code}' (0x{(byte)code:X2}) at offset {at}.");
        }
    }

    /// <summary>
    /// Array property body: u32 arrayLen (element count), u32 encoding, u32 compressedLength.
    /// Encoding 0 = raw little-endian elements; encoding 1 = zlib (RFC 1950) stream.
    /// </summary>
    private static object ReadArray(byte[] data, ref int pos, string owner, int elemSize, Func<byte[], object> convert)
    {
        int at = pos;
        uint arrayLen = ReadU32(data, ref pos);
        uint encoding = ReadU32(data, ref pos);
        uint byteLen = ReadU32(data, ref pos); // compressedLength (also raw byte length when encoding 0)

        long expected = (long)arrayLen * elemSize;
        if (expected > int.MaxValue)
            throw new FormatException($"FBX binary: node '{owner}': array at offset {at} too large ({arrayLen} elements).");

        byte[] raw;
        switch (encoding)
        {
            case 0:
                if (byteLen != expected)
                    throw new FormatException(
                        $"FBX binary: node '{owner}': raw array at offset {at} declares {byteLen} bytes for {arrayLen} x{elemSize}-byte elements.");
                raw = ReadBytes(data, ref pos, (int)byteLen).ToArray();
                break;

            case 1:
            {
                var compressed = ReadBytes(data, ref pos, checked((int)byteLen)).ToArray();
                raw = new byte[expected];
                try
                {
                    // s&box whitelist: ZLibStream is banned, DeflateStream is allowed.
                    // zlib framing = 2-byte header + deflate payload (+ adler32 we never read).
                    var ms = new MemoryStream(compressed);
                    ms.Seek(2, SeekOrigin.Begin);
                    using var zs = new DeflateStream(ms, CompressionMode.Decompress);
                    zs.ReadExactly(raw);
                }
                // broad filter: InvalidDataException (corrupt deflate data) is not
                // s&box-whitelisted, so it cannot be named here
                catch (Exception ex) when (ex is not FormatException)
                {
                    throw new FormatException(
                        $"FBX binary: node '{owner}': zlib array at offset {at} failed to decompress to {expected} bytes.", ex);
                }
                break;
            }

            default:
                throw new FormatException(
                    $"FBX binary: node '{owner}': unknown array encoding {encoding} at offset {at}.");
        }

        return convert(raw);
    }

    private static object ToFloatArray(byte[] raw)
    {
        var a = new float[raw.Length / 4];
        for (int i = 0; i < a.Length; i++)
            a[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4));
        return a;
    }

    private static object ToDoubleArray(byte[] raw)
    {
        var a = new double[raw.Length / 8];
        for (int i = 0; i < a.Length; i++)
            a[i] = BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(i * 8));
        return a;
    }

    private static object ToLongArray(byte[] raw)
    {
        var a = new long[raw.Length / 8];
        for (int i = 0; i < a.Length; i++)
            a[i] = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(i * 8));
        return a;
    }

    private static object ToIntArray(byte[] raw)
    {
        var a = new int[raw.Length / 4];
        for (int i = 0; i < a.Length; i++)
            a[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4));
        return a;
    }

    private static ulong ReadOffsetField(byte[] data, ref int pos, bool wide)
        => wide
            ? BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(data, ref pos, 8))
            : BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(data, ref pos, 4));

    private static byte ReadU8(byte[] data, ref int pos)
    {
        if (pos >= data.Length)
            throw new FormatException($"FBX binary: unexpected end of file reading byte at offset {pos}.");
        return data[pos++];
    }

    private static uint ReadU32(byte[] data, ref int pos)
        => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(data, ref pos, 4));

    private static ReadOnlySpan<byte> ReadBytes(byte[] data, ref int pos, int count)
    {
        // Bounds math in long: a huge declared count would overflow `pos + count` in int,
        // slip past the check and surface as the wrong exception type from AsSpan.
        if (count < 0 || (long)pos + count > data.Length)
            throw new FormatException(
                $"FBX binary: unexpected end of file reading {count} bytes at offset {pos} (file is {data.Length} bytes).");
        var span = data.AsSpan(pos, count);
        pos += count;
        return span;
    }

    // =====================================================================
    // ASCII
    // =====================================================================

    private enum TokKind { Word, Colon, Comma, Open, Close, Star, Number, String, Eof }

    private readonly record struct Tok(TokKind Kind, string Text, int Line);

    private static FbxNode ParseAscii(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);
        var toks = LexAscii(text);
        int i = 0;
        var root = new FbxNode("(root)");
        while (toks[i].Kind != TokKind.Eof)
        {
            if (toks[i].Kind != TokKind.Word || toks[i + 1].Kind != TokKind.Colon)
                throw new FormatException(
                    $"FBX ascii: line {toks[i].Line}: expected 'Name:' but found '{toks[i].Text}' — not a valid FBX file (no binary magic either).");
            root.Children.Add(ParseAsciiNode(toks, ref i));
        }
        if (root.Children.Count == 0)
            throw new FormatException("FBX ascii: file contains no nodes (and no binary magic).");
        return root;
    }

    /// <summary>Parses <c>Name: prop, prop { children }</c> with <c>*N { a: ... }</c> array support.</summary>
    private static FbxNode ParseAsciiNode(List<Tok> toks, ref int i)
    {
        string name = toks[i].Text;
        i += 2; // Word + Colon
        var node = new FbxNode(name);

        // Array form: Name: *N { a: v,v,v }
        if (toks[i].Kind == TokKind.Star)
        {
            long declared = ParseAsciiLong(toks[i]);
            i++;
            Expect(toks, ref i, TokKind.Open, name);
            // The payload is wrapped in an "a:" pseudo-node.
            if (toks[i].Kind == TokKind.Word && toks[i].Text == "a" && toks[i + 1].Kind == TokKind.Colon)
                i += 2;
            var values = new List<string>();
            while (toks[i].Kind == TokKind.Number)
            {
                values.Add(toks[i].Text);
                i++;
                if (toks[i].Kind == TokKind.Comma)
                    i++;
            }
            Expect(toks, ref i, TokKind.Close, name);
            if (values.Count != declared)
                throw new FormatException(
                    $"FBX ascii: node '{name}': array declared {declared} elements but contains {values.Count}.");
            node.Properties.Add(ToAsciiArray(values, name));
            return node;
        }

        // Scalar props: numbers, strings, bare words — comma separated.
        while (true)
        {
            var t = toks[i];
            if (t.Kind == TokKind.Number)
            {
                node.Properties.Add(ParseAsciiNumber(t));
                i++;
            }
            else if (t.Kind == TokKind.String)
            {
                node.Properties.Add(t.Text);
                i++;
            }
            else if (t.Kind == TokKind.Word && toks[i + 1].Kind != TokKind.Colon)
            {
                // Bare word value (e.g. `Shading: Y`); a Word followed by ':' starts the next node.
                node.Properties.Add(t.Text);
                i++;
            }
            else
            {
                break;
            }

            if (toks[i].Kind == TokKind.Comma)
                i++;
            else
                break;
        }

        // Children block.
        if (toks[i].Kind == TokKind.Open)
        {
            i++;
            while (toks[i].Kind != TokKind.Close)
            {
                if (toks[i].Kind == TokKind.Eof)
                    throw new FormatException($"FBX ascii: node '{name}': unterminated '{{' block.");
                if (toks[i].Kind != TokKind.Word || toks[i + 1].Kind != TokKind.Colon)
                    throw new FormatException(
                        $"FBX ascii: line {toks[i].Line}: expected child 'Name:' inside '{name}', found '{toks[i].Text}'.");
                node.Children.Add(ParseAsciiNode(toks, ref i));
            }
            i++; // consume '}'
        }

        return node;
    }

    /// <summary>Numbers with '.', 'e', or 'E' become double[]; otherwise long[].</summary>
    private static object ToAsciiArray(List<string> values, string owner)
    {
        bool isDouble = values.Any(v => v.Contains('.') || v.Contains('e') || v.Contains('E'));
        if (isDouble)
        {
            var a = new double[values.Count];
            for (int i = 0; i < a.Length; i++)
                a[i] = ParseAsciiDouble(values[i], owner);
            return a;
        }
        else
        {
            var a = new long[values.Count];
            for (int i = 0; i < a.Length; i++)
            {
                if (!long.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out a[i]))
                    throw new FormatException($"FBX ascii: node '{owner}': bad integer '{values[i]}'.");
            }
            return a;
        }
    }

    private static object ParseAsciiNumber(Tok t)
    {
        string s = t.Text;
        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E')
            && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            return l;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return d;
        throw new FormatException($"FBX ascii: line {t.Line}: malformed number '{s}'.");
    }

    private static long ParseAsciiLong(Tok t)
        => long.TryParse(t.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)
            ? l
            : throw new FormatException($"FBX ascii: line {t.Line}: malformed array length '*{t.Text}'.");

    private static double ParseAsciiDouble(string s, string owner)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d
            : throw new FormatException($"FBX ascii: node '{owner}': bad number '{s}'.");

    private static void Expect(List<Tok> toks, ref int i, TokKind kind, string owner)
    {
        if (toks[i].Kind != kind)
            throw new FormatException(
                $"FBX ascii: node '{owner}': line {toks[i].Line}: expected {kind}, found '{toks[i].Text}'.");
        i++;
    }

    private static List<Tok> LexAscii(string text)
    {
        var toks = new List<Tok>();
        int line = 1;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == ';') // comment to end of line
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }

            switch (c)
            {
                case ':': toks.Add(new Tok(TokKind.Colon, ":", line)); i++; continue;
                case ',': toks.Add(new Tok(TokKind.Comma, ",", line)); i++; continue;
                case '{': toks.Add(new Tok(TokKind.Open, "{", line)); i++; continue;
                case '}': toks.Add(new Tok(TokKind.Close, "}", line)); i++; continue;
            }

            if (c == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0)
                    throw new FormatException($"FBX ascii: line {line}: unterminated string literal.");
                toks.Add(new Tok(TokKind.String, text[(i + 1)..end], line));
                i = end + 1;
                continue;
            }

            if (c == '*') // array length marker: *N
            {
                int start = ++i;
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                    i++;
                if (i == start)
                    throw new FormatException($"FBX ascii: line {line}: '*' not followed by array length.");
                toks.Add(new Tok(TokKind.Star, text[start..i], line));
                continue;
            }

            if (char.IsAsciiDigit(c) || c == '-' || c == '+' || c == '.')
            {
                int start = i;
                i++;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    // '+'/'-' only valid directly after an exponent marker
                    if (text[i] is '+' or '-' && text[i - 1] is not ('e' or 'E'))
                        break;
                    i++;
                }
                toks.Add(new Tok(TokKind.Number, text[start..i], line));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '|' or '.'))
                    i++;
                toks.Add(new Tok(TokKind.Word, text[start..i], line));
                continue;
            }

            throw new FormatException($"FBX ascii: line {line}: unexpected character '{c}' (0x{(int)c:X2}).");
        }

        toks.Add(new Tok(TokKind.Eof, "<eof>", line));
        return toks;
    }
}
