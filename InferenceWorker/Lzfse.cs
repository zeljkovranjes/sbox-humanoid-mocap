// Port of the LZFSE / LZVN decoder from https://github.com/lzfse/lzfse.
// Copyright (c) 2015-2016, Apple Inc. All rights reserved.
// Redistributed under the BSD 3-Clause license (below). C# port from ShortcutForge (MIT, github.com/kipergrof/ShortcutForge).
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
// following conditions are met: 1. Redistributions of source code must retain the above copyright notice, this list of
// conditions and the following disclaimer. 2. Redistributions in binary form must reproduce the above copyright notice,
// this list of conditions and the following disclaimer in the documentation and/or other materials provided with the
// distribution. 3. Neither the name of the copyright holder(s) nor the names of any contributors may be used to endorse
// or promote products derived from this software without specific prior written permission.
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DAMAGES ARISING IN ANY WAY OUT OF
// THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using System.Buffers.Binary;
using System.Numerics;

namespace HumanoidMocap.Worker;

/// <summary>
/// Decoder for Apple's LZFSE compression format (bvx2 / bvxn / bvx- blocks), a port of the
/// reference implementation (github.com/lzfse/lzfse, BSD licensed). Record3D compresses each depth and
/// confidence frame of its .r3d recordings with it.
/// </summary>
public static class Lzfse
{
    private const uint MagicEnd = 0x24787662;          // "bvx$"
    private const uint MagicUncompressed = 0x2d787662; // "bvx-"
    private const uint MagicV1 = 0x31787662;           // "bvx1"
    private const uint MagicV2 = 0x32787662;           // "bvx2"
    private const uint MagicLzvn = 0x6e787662;         // "bvxn"

    private const int LSymbols = 20, MSymbols = 20, DSymbols = 64, LiteralSymbols = 256;
    private const int LStates = 64, MStates = 64, DStates = 256, LiteralStates = 1024;

    private static readonly byte[] LExtraBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 5, 8];
    private static readonly int[] LBaseValue = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 20, 28, 60];
    private static readonly byte[] MExtraBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 5, 8, 11];
    private static readonly int[] MBaseValue = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 24, 56, 312];

    private static readonly byte[] DExtraBits =
    [
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7,
        8, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11, 12, 12, 12, 12, 13, 13, 13, 13, 14, 14, 14, 14, 15, 15, 15, 15,
    ];

    private static readonly int[] DBaseValue =
    [
        0, 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 28, 36, 44, 52, 60, 76, 92, 108, 124, 156, 188, 220,
        252, 316, 380, 444, 508, 636, 764, 892, 1020, 1276, 1532, 1788, 2044, 2556, 3068, 3580, 4092,
        5116, 6140, 7164, 8188, 10236, 12284, 14332, 16380, 20476, 24572, 28668, 32764, 40956, 49148,
        57340, 65532, 81916, 98300, 114684, 131068, 163836, 196604, 229372,
    ];

    /// <summary>Decodes a complete stream (up to the bvx$ end marker).</summary>
    public static byte[] Decode(ReadOnlySpan<byte> src) => Decode(src, out _);

    /// <summary>Decodes a stream and reports how many input bytes it occupied.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> src, out int consumed)
    {
        var output = new List<byte>(src.Length * 4);
        var pos = 0;
        while (true)
        {
            var magic = ReadU32(src, pos);
            switch (magic)
            {
                case MagicEnd:
                    consumed = pos + 4;
                    return output.ToArray();

                case MagicUncompressed:
                {
                    var n = (int)ReadU32(src, pos + 4);
                    Require(pos + 8 + n <= src.Length);
                    output.AddRange(src.Slice(pos + 8, n).ToArray());
                    pos += 8 + n;
                    break;
                }

                case MagicLzvn:
                {
                    var rawBytes = (int)ReadU32(src, pos + 4);
                    var payloadBytes = (int)ReadU32(src, pos + 8);
                    Require(pos + 12 + payloadBytes <= src.Length);
                    var start = output.Count;
                    Lzvn.Decode(src.Slice(pos + 12, payloadBytes), output, rawBytes);
                    Require(output.Count - start == rawBytes);
                    pos += 12 + payloadBytes;
                    break;
                }

                case MagicV2:
                    pos += DecodeV2Block(src[pos..], output);
                    break;

                case MagicV1:
                    throw new InvalidDataException("LZFSE v1 blocks are not supported.");

                default:
                    throw new InvalidDataException($"Invalid LZFSE block header at byte {pos}.");
            }
        }
    }

    private static uint ReadU32(ReadOnlySpan<byte> src, int pos)
    {
        Require(pos + 4 <= src.Length);
        return BinaryPrimitives.ReadUInt32LittleEndian(src[pos..]);
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidDataException("Corrupt LZFSE data.");
    }

    private static int GetField(ulong v, int offset, int nbits) => (int)((v >> offset) & ((1UL << nbits) - 1));

    private static int DecodeV2Block(ReadOnlySpan<byte> block, List<byte> output)
    {
        Require(block.Length >= 32);
        var nRawBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        var v0 = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
        var v1 = BinaryPrimitives.ReadUInt64LittleEndian(block[16..]);
        var v2 = BinaryPrimitives.ReadUInt64LittleEndian(block[24..]);

        var nLiterals = GetField(v0, 0, 20);
        var nLiteralPayloadBytes = GetField(v0, 20, 20);
        var nMatches = GetField(v0, 40, 20);
        var literalBits = GetField(v0, 60, 3) - 7;

        Span<int> literalState = [GetField(v1, 0, 10), GetField(v1, 10, 10), GetField(v1, 20, 10), GetField(v1, 30, 10)];
        var nLmdPayloadBytes = GetField(v1, 40, 20);
        var lmdBits = GetField(v1, 60, 3) - 7;

        var headerSize = GetField(v2, 0, 32);
        var lState = GetField(v2, 32, 10);
        var mState = GetField(v2, 42, 10);
        var dState = GetField(v2, 52, 10);

        Require(headerSize >= 32 && headerSize <= block.Length);
        Require(block.Length >= headerSize + nLiteralPayloadBytes + nLmdPayloadBytes);

        // Frequency tables, variable-length coded.
        var freq = new ushort[LSymbols + MSymbols + DSymbols + LiteralSymbols];
        {
            var src = 32;
            uint accum = 0;
            var accumBits = 0;
            for (var i = 0; i < freq.Length; i++)
            {
                while (src < headerSize && accumBits + 8 <= 32)
                {
                    accum |= (uint)block[src] << accumBits;
                    accumBits += 8;
                    src++;
                }
                var f = DecodeFreqValue(accum, out var nbits);
                Require(nbits <= accumBits);
                freq[i] = (ushort)f;
                accum >>= nbits;
                accumBits -= nbits;
            }
        }

        var lFreq = freq.AsSpan(0, LSymbols);
        var mFreq = freq.AsSpan(LSymbols, MSymbols);
        var dFreq = freq.AsSpan(LSymbols + MSymbols, DSymbols);
        var literalFreq = freq.AsSpan(LSymbols + MSymbols + DSymbols, LiteralSymbols);

        var literalDecoder = BuildDecoderTable(LiteralStates, literalFreq);
        var lDecoder = BuildValueDecoderTable(LStates, lFreq, LExtraBits, LBaseValue);
        var mDecoder = BuildValueDecoderTable(MStates, mFreq, MExtraBits, MBaseValue);
        var dDecoder = BuildValueDecoderTable(DStates, dFreq, DExtraBits, DBaseValue);

        foreach (var s in literalState) Require(s < LiteralStates);
        Require(lState < LStates && mState < MStates && dState < DStates);

        // Literals
        var payload = block.Slice(headerSize, nLiteralPayloadBytes + nLmdPayloadBytes);
        var literals = new byte[nLiterals + 4];
        {
            var input = new BitReader(payload[..nLiteralPayloadBytes], literalBits);
            for (var i = 0; i < nLiterals; i += 4)
            {
                input.Flush();
                for (var k = 0; k < 4; k++)
                {
                    var e = literalDecoder[literalState[k]];
                    literalState[k] = (e >> 16) + (int)input.Pull(e & 0xFF);
                    literals[i + k] = (byte)((e >> 8) & 0xFF);
                }
            }
        }

        // L, M, D triples
        var start = output.Count;
        {
            var input = new BitReader(payload.Slice(nLiteralPayloadBytes, nLmdPayloadBytes), lmdBits);
            var lit = 0;
            var d = -1;
            for (var i = 0; i < nMatches; i++)
            {
                input.Flush();
                var l = DecodeValue(ref lState, lDecoder, ref input);
                var m = DecodeValue(ref mState, mDecoder, ref input);
                var newD = DecodeValue(ref dState, dDecoder, ref input);
                if (newD != 0) d = newD;

                Require(lit + l <= nLiterals);
                for (var k = 0; k < l; k++) output.Add(literals[lit + k]);
                lit += l;

                if (m > 0)
                {
                    var from = output.Count - d;
                    Require(d > 0 && from >= 0); // matches may reach into previous blocks
                    for (var k = 0; k < m; k++) output.Add(output[from + k]);
                }
            }
        }

        Require(output.Count - start == nRawBytes);
        return headerSize + nLiteralPayloadBytes + nLmdPayloadBytes;
    }

    private static readonly sbyte[] FreqNbitsTable =
        [2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14, 2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14];

    private static readonly sbyte[] FreqValueTable =
        [0, 2, 1, 4, 0, 3, 1, -1, 0, 2, 1, 5, 0, 3, 1, -1, 0, 2, 1, 6, 0, 3, 1, -1, 0, 2, 1, 7, 0, 3, 1, -1];

    private static int DecodeFreqValue(uint bits, out int nbits)
    {
        var b = (int)(bits & 31);
        var n = FreqNbitsTable[b];
        nbits = n;
        return n switch
        {
            8 => 8 + (int)((bits >> 4) & 0xF),
            14 => 24 + (int)((bits >> 4) & 0x3FF),
            _ => FreqValueTable[b],
        };
    }

    private static int Clz(int v) => BitOperations.LeadingZeroCount((uint)v);

    /// <summary>Entry layout: bits 0-7 = k, 8-15 = symbol, 16-31 = delta.</summary>
    private static int[] BuildDecoderTable(int nStates, ReadOnlySpan<ushort> freq)
    {
        var table = new int[nStates];
        var nClz = Clz(nStates);
        var sum = 0;
        var t = 0;
        for (var i = 0; i < freq.Length; i++)
        {
            int f = freq[i];
            if (f == 0) continue;
            sum += f;
            Require(sum <= nStates);
            var k = Clz(f) - nClz;
            var j0 = ((2 * nStates) >> k) - f;
            for (var j = 0; j < f; j++)
            {
                int ek, delta;
                if (j < j0)
                {
                    ek = k;
                    delta = ((f + j) << k) - nStates;
                }
                else
                {
                    ek = k - 1;
                    delta = (j - j0) << (k - 1);
                }
                table[t++] = (ek & 0xFF) | (i << 8) | (delta << 16);
            }
        }
        return table;
    }

    private readonly record struct ValueEntry(int TotalBits, int ValueBits, int Delta, int VBase);

    private static ValueEntry[] BuildValueDecoderTable(int nStates, ReadOnlySpan<ushort> freq, byte[] vbits, int[] vbase)
    {
        var table = new ValueEntry[nStates];
        var nClz = Clz(nStates);
        var sum = 0;
        var t = 0;
        for (var i = 0; i < freq.Length; i++)
        {
            int f = freq[i];
            if (f == 0) continue;
            sum += f;
            Require(sum <= nStates);
            var k = Clz(f) - nClz;
            var j0 = ((2 * nStates) >> k) - f;
            for (var j = 0; j < f; j++)
            {
                table[t++] = j < j0
                    ? new ValueEntry(k + vbits[i], vbits[i], ((f + j) << k) - nStates, vbase[i])
                    : new ValueEntry(k - 1 + vbits[i], vbits[i], (j - j0) << (k - 1), vbase[i]);
            }
        }
        return table;
    }

    private static int DecodeValue(ref int state, ValueEntry[] table, ref BitReader input)
    {
        Require(state >= 0 && state < table.Length);
        var e = table[state];
        var bits = input.Pull(e.TotalBits);
        state = e.Delta + (int)(bits >> e.ValueBits);
        return e.VBase + (int)(bits & ((1UL << e.ValueBits) - 1));
    }

    /// <summary>Backwards bit stream (fse_in_stream64): bytes are consumed from the end.</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _buf;
        private int _pos; // bytes before _pos are still unread
        private ulong _accum;
        private int _accumBits;

        public BitReader(ReadOnlySpan<byte> buf, int n)
        {
            _buf = buf;
            if (n != 0)
            {
                Require(buf.Length >= 8);
                _pos = buf.Length - 8;
                _accum = BinaryPrimitives.ReadUInt64LittleEndian(buf[_pos..]);
                _accumBits = n + 64;
            }
            else
            {
                Require(buf.Length >= 7);
                _pos = buf.Length - 7;
                Span<byte> tmp = stackalloc byte[8];
                buf.Slice(_pos, 7).CopyTo(tmp);
                _accum = BinaryPrimitives.ReadUInt64LittleEndian(tmp);
                _accumBits = n + 56;
            }
            Require(_accumBits >= 56 && _accumBits < 64 && (_accum >> _accumBits) == 0);
        }

        public void Flush()
        {
            var nbits = (63 - _accumBits) & -8;
            var nbytes = nbits >> 3;
            // The reference decoder reads ahead past the start of the stream at the very end;
            // those bits are never consumed, so missing bytes are loaded as zeros.
            _pos -= nbytes;
            Require(_pos >= -8);
            ulong incoming = 0;
            for (var i = 0; i < nbytes; i++)
                if (_pos + i >= 0) incoming |= (ulong)_buf[_pos + i] << (8 * i);
            _accum = nbits == 0 ? _accum : (_accum << nbits) | incoming;
            _accumBits += nbits;
        }

        public ulong Pull(int n)
        {
            Require(n <= _accumBits);
            _accumBits -= n;
            var result = _accum >> _accumBits;
            _accum &= _accumBits == 0 ? 0 : (1UL << _accumBits) - 1;
            return result;
        }
    }
}

/// <summary>LZVN decoder (used by LZFSE for small inputs).</summary>
internal static class Lzvn
{
    public static void Decode(ReadOnlySpan<byte> src, List<byte> output, int expected)
    {
        var start = output.Count;
        var p = 0;
        var d = 0;
        while (p < src.Length)
        {
            int opc = src[p];
            int l, m, len;
            switch (opc)
            {
                case 0x06: // end of stream
                    return;
                case 0x0E or 0x16: // nop
                    p++;
                    continue;
                case >= 0xA0 and <= 0xBF: // medium distance
                    Need(src, p, 3);
                    l = (opc >> 3) & 3;
                    m = (((opc & 7) << 2) | (src[p + 1] & 3)) + 3;
                    d = (src[p + 2] << 6) | (src[p + 1] >> 2);
                    len = 3;
                    break;
                case 0xE0: // large literal
                    Need(src, p, 2);
                    l = src[p + 1] + 16; m = 0; len = 2;
                    break;
                case > 0xE0 and <= 0xEF: // small literal
                    l = opc & 0xF; m = 0; len = 1;
                    break;
                case 0xF0: // large match
                    Need(src, p, 2);
                    l = 0; m = src[p + 1] + 16; len = 2;
                    break;
                case > 0xF0: // small match
                    l = 0; m = opc & 0xF; len = 1;
                    break;
                case >= 0x70 and <= 0x7F or >= 0xD0 and <= 0xDF: // undefined
                    throw new InvalidDataException("Invalid LZVN opcode.");
                default:
                    l = (opc >> 6) & 3;
                    m = ((opc >> 3) & 7) + 3;
                    switch (opc & 7)
                    {
                        case 7: // large distance
                            Need(src, p, 3);
                            d = src[p + 1] | (src[p + 2] << 8);
                            len = 3;
                            break;
                        case 6: // previous distance
                            if (opc < 0x40) throw new InvalidDataException("Invalid LZVN opcode.");
                            len = 1;
                            break;
                        default: // small distance
                            Need(src, p, 2);
                            d = ((opc & 7) << 8) | src[p + 1];
                            len = 2;
                            break;
                    }
                    break;
            }

            p += len;
            Need(src, p, l);
            for (var k = 0; k < l; k++) output.Add(src[p + k]);
            p += l;

            if (m > 0)
            {
                var from = output.Count - d;
                if (d <= 0 || from < 0) throw new InvalidDataException("Invalid LZVN distance.");
                for (var k = 0; k < m; k++) output.Add(output[from + k]);
            }

            if (output.Count - start > expected) throw new InvalidDataException("The LZVN block is too long.");
        }
    }

    private static void Need(ReadOnlySpan<byte> src, int p, int n)
    {
        if (p + n > src.Length) throw new InvalidDataException("Truncated LZVN data.");
    }
}
