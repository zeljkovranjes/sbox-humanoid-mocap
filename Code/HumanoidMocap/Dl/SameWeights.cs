#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HumanoidMocap.Dl;

/// <summary>
/// One named float32 tensor of a <see cref="SameWeights"/> blob.
/// </summary>
/// <param name="Shape">Dimensions (row-major / C order).</param>
/// <param name="Data">Flat float32 data, length = product of <paramref name="Shape"/>.</param>
public readonly record struct WeightTensor(int[] Shape, float[] Data);

/// <summary>
/// Parses the committed SAME model weight blob
/// (<c>Assets/humanoid_mocap/dl/same_v1.weights</c>, produced by
/// <c>dev/m10/scripts/export_weights.py</c>) into named float32 tensors: the GAT
/// encoder/decoder parameters under their original PyTorch state-dict keys plus the
/// <c>ms.*</c> feature normalization statistics. No file IO — callers pass bytes
/// (the Editor reads the asset file; tests read the repo file).
/// </summary>
/// <remarks>
/// Blob format (little endian): 8-byte magic <c>HRSAMEW1</c>, int32 tensor count, then per
/// tensor: int32 UTF-8 name length, name bytes, int32 rank, int32 dims, float32 data.
/// </remarks>
public sealed class SameWeights
{
    private const string Magic = "HRSAMEW1";

    private readonly Dictionary<string, WeightTensor> _tensors;

    private SameWeights(Dictionary<string, WeightTensor> tensors)
    {
        _tensors = tensors;
    }

    /// <summary>All tensors by name.</summary>
    public IReadOnlyDictionary<string, WeightTensor> Tensors => _tensors;

    /// <summary>Returns the named tensor.</summary>
    /// <exception cref="ArgumentException">Thrown when the blob has no such tensor.</exception>
    public WeightTensor Get(string name)
        => _tensors.TryGetValue(name, out var tensor)
            ? tensor
            : throw new ArgumentException($"SAME weights blob has no tensor '{name}'.");

    /// <summary>Returns the flat data of the named <c>ms.*</c> normalization stat
    /// (e.g. <c>Stat("q_m")</c> for the rotation-feature mean).</summary>
    public float[] Stat(string key) => Get("ms." + key).Data;

    /// <summary>Parses a weight blob.</summary>
    /// <exception cref="FormatException">Thrown on a wrong magic or truncated/invalid data.</exception>
    public static SameWeights Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data, writable: false));

            var magic = Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length));
            if (magic != Magic)
                throw new FormatException(
                    $"Not a SAME weight blob (magic '{magic}', expected '{Magic}').");

            var count = reader.ReadInt32();
            if (count is < 0 or > 4096)
                throw new FormatException($"Implausible tensor count {count}.");

            var tensors = new Dictionary<string, WeightTensor>(count, StringComparer.Ordinal);
            for (var t = 0; t < count; t++)
            {
                var nameLength = reader.ReadInt32();
                if (nameLength is <= 0 or > 1024)
                    throw new FormatException($"Implausible tensor name length {nameLength}.");
                var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));

                var rank = reader.ReadInt32();
                if (rank is < 0 or > 8)
                    throw new FormatException($"Implausible tensor rank {rank} for '{name}'.");
                var shape = new int[rank];
                long total = 1;
                for (var d = 0; d < rank; d++)
                {
                    shape[d] = reader.ReadInt32();
                    if (shape[d] <= 0)
                        throw new FormatException($"Non-positive dimension in tensor '{name}'.");
                    total *= shape[d];
                }
                if (total > int.MaxValue / sizeof(float))
                    throw new FormatException($"Tensor '{name}' too large.");

                var bytes = reader.ReadBytes((int)total * sizeof(float));
                if (bytes.Length != total * sizeof(float))
                    throw new FormatException($"Truncated data for tensor '{name}'.");
                var values = new float[total];
                Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
                if (!tensors.TryAdd(name, new WeightTensor(shape, values)))
                    throw new FormatException($"Duplicate tensor '{name}'.");
            }

            return new SameWeights(tensors);
        }
        catch (EndOfStreamException)
        {
            throw new FormatException("Truncated SAME weight blob.");
        }
    }
}
