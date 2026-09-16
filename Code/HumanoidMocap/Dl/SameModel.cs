#nullable enable annotations

using System;

namespace HumanoidMocap.Dl;

/// <summary>
/// Pure-managed forward pass of the SAME GAT autoencoder (Lee et al., SIGGRAPH Asia 2023,
/// ckpt0) — the exact graph the spike's explicit-math ONNX export implements
/// (<c>dev/m10/scripts/export_onnx.py::gat_conv_explicit</c>, verified bit-equal to the
/// original PyG layers), re-expressed as plain float32 loops. No ONNX Runtime, no native
/// code; deterministic (fixed accumulation order).
/// </summary>
/// <remarks>
/// <para>Architecture (ckpt0 config): encoder = 4 × GATConv (32 → 16ch×16heads ×3 → 32,
/// ReLU between, last layer 1 head), then per-frame global <b>max</b>-pool over nodes →
/// z[32] per frame. Decoder = 4 × GATConv (38 = z32 ⊕ skel6 input, the 6 skeleton features
/// re-concatenated at every layer, → 11 outputs), ReLU between.</para>
/// <para>Edge convention (both graphs): bidirectional parent↔child pairs plus one self-loop
/// per node — equivalent to the original layer's internal remove/add_self_loops on the
/// unmasked inference path. One graph per frame; frames are batched as disjoint unions with
/// node indices offset per frame.</para>
/// <para>GAT layer math per <c>gat_conv_explicit</c>:
/// <c>h = W·x</c> per node, attention logit per edge
/// <c>e = leaky_relu(att_src·h[src] + att_dst·h[dst], 0.2)</c>, softmax over the incoming
/// edges of each destination node (per head), output = attention-weighted sum of source
/// <c>h</c> over incoming edges, heads concatenated, plus bias.</para>
/// </remarks>
public sealed class SameModel
{
    /// <summary>Encoder input width (6 skeleton + 26 pose features).</summary>
    public const int InputDim = 32;

    /// <summary>Per-frame embedding width.</summary>
    public const int ZDim = 32;

    /// <summary>Target skeleton feature width (lo3 ⊕ go3).</summary>
    public const int SkelDim = 6;

    /// <summary>Decoder output width (r4 ⊕ q6 ⊕ c1, normalized).</summary>
    public const int OutDim = 11;

    private const float NegativeSlope = 0.2f;

    private readonly GatLayer[] _encoder;
    private readonly GatLayer[] _decoder;

    /// <summary>Builds the model from a parsed weight blob.</summary>
    /// <exception cref="ArgumentException">Thrown when the blob lacks expected tensors or
    /// their shapes do not match the ckpt0 architecture.</exception>
    public SameModel(SameWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        _encoder = LoadStack(weights, "encoder.0.convs", expectedLayers: 4, inputDim: InputDim);
        _decoder = LoadStack(weights, "decoder.0.deconvs", expectedLayers: 4, inputDim: ZDim + SkelDim);
        if (_encoder[^1].OutDim != ZDim)
            throw new ArgumentException($"Encoder output width {_encoder[^1].OutDim}, expected {ZDim}.");
        if (_decoder[^1].OutDim != OutDim)
            throw new ArgumentException($"Decoder output width {_decoder[^1].OutDim}, expected {OutDim}.");
    }

    /// <summary>
    /// Encoder: per-node features → per-frame embeddings.
    /// </summary>
    /// <param name="x">Normalized node features, flat [nodeCount × <see cref="InputDim"/>].</param>
    /// <param name="edgeSrc">Edge source node per edge (bidirectional + self-loops).</param>
    /// <param name="edgeDst">Edge destination node per edge.</param>
    /// <param name="batch">Frame id per node (0-based, contiguous).</param>
    /// <param name="frameCount">Number of frames.</param>
    /// <returns>z, flat [frameCount × <see cref="ZDim"/>].</returns>
    public float[] Encode(float[] x, int[] edgeSrc, int[] edgeDst, int[] batch, int frameCount)
    {
        var nodeZ = EncodeNodes(x, edgeSrc, edgeDst);
        return MaxPool(nodeZ, ZDim, batch, frameCount);
    }

    /// <summary>Encoder without the final per-frame pool: per-node embeddings,
    /// flat [nodeCount × <see cref="ZDim"/>] (exposed for parity tests).</summary>
    public float[] EncodeNodes(float[] x, int[] edgeSrc, int[] edgeDst)
    {
        ArgumentNullException.ThrowIfNull(x);
        var n = CheckNodes(x, InputDim, edgeSrc, edgeDst);
        var h = x;
        for (var i = 0; i < _encoder.Length; i++)
        {
            h = _encoder[i].Forward(h, n, edgeSrc, edgeDst);
            if (i + 1 != _encoder.Length)
                Relu(h);
        }
        return h;
    }

    /// <summary>
    /// Decoder: per-frame embeddings conditioned on the target skeleton graph → per-node
    /// outputs (normalized r4 ⊕ q6 ⊕ c1).
    /// </summary>
    /// <param name="z">Per-frame embeddings, flat [frameCount × <see cref="ZDim"/>].</param>
    /// <param name="targetX">Normalized target skeleton features tiled per frame,
    /// flat [nodeCount × <see cref="SkelDim"/>].</param>
    /// <param name="edgeSrc">Edge source node per edge (bidirectional + self-loops, all frames).</param>
    /// <param name="edgeDst">Edge destination node per edge.</param>
    /// <param name="batch">Frame id per node.</param>
    /// <returns>hatD, flat [nodeCount × <see cref="OutDim"/>].</returns>
    public float[] Decode(float[] z, float[] targetX, int[] edgeSrc, int[] edgeDst, int[] batch)
    {
        ArgumentNullException.ThrowIfNull(z);
        ArgumentNullException.ThrowIfNull(targetX);
        ArgumentNullException.ThrowIfNull(batch);
        var n = CheckNodes(targetX, SkelDim, edgeSrc, edgeDst);
        if (batch.Length != n)
            throw new ArgumentException($"batch length {batch.Length} != node count {n}.");

        // dec_x starts as z gathered per node; skel features are concatenated before EVERY
        // layer (tgt_all_lyr = true in ckpt0).
        var h = new float[n * ZDim];
        for (var i = 0; i < n; i++)
            Array.Copy(z, batch[i] * ZDim, h, i * ZDim, ZDim);

        var width = ZDim;
        for (var i = 0; i < _decoder.Length; i++)
        {
            var concat = ConcatColumns(h, width, targetX, SkelDim, n);
            h = _decoder[i].Forward(concat, n, edgeSrc, edgeDst);
            width = _decoder[i].OutDim;
            if (i + 1 != _decoder.Length)
                Relu(h);
        }
        return h;
    }

    /// <summary>Per-frame max-pool over nodes (the encoder's host-side pool).</summary>
    public static float[] MaxPool(float[] nodeValues, int width, int[] batch, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(nodeValues);
        ArgumentNullException.ThrowIfNull(batch);
        var pooled = new float[frameCount * width];
        Array.Fill(pooled, float.NegativeInfinity);
        for (var i = 0; i < batch.Length; i++)
        {
            var f = batch[i];
            for (var c = 0; c < width; c++)
            {
                var v = nodeValues[i * width + c];
                if (v > pooled[f * width + c])
                    pooled[f * width + c] = v;
            }
        }
        return pooled;
    }

    private static int CheckNodes(float[] x, int width, int[] edgeSrc, int[] edgeDst)
    {
        ArgumentNullException.ThrowIfNull(edgeSrc);
        ArgumentNullException.ThrowIfNull(edgeDst);
        if (x.Length % width != 0)
            throw new ArgumentException($"Feature array length {x.Length} is not a multiple of {width}.");
        if (edgeSrc.Length != edgeDst.Length)
            throw new ArgumentException("edgeSrc/edgeDst lengths differ.");
        return x.Length / width;
    }

    private static void Relu(float[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] < 0f)
                values[i] = 0f;
        }
    }

    private static float[] ConcatColumns(float[] a, int aWidth, float[] b, int bWidth, int n)
    {
        var outWidth = aWidth + bWidth;
        var result = new float[n * outWidth];
        for (var i = 0; i < n; i++)
        {
            Array.Copy(a, i * aWidth, result, i * outWidth, aWidth);
            Array.Copy(b, i * bWidth, result, i * outWidth + aWidth, bWidth);
        }
        return result;
    }

    private static GatLayer[] LoadStack(SameWeights weights, string prefix, int expectedLayers, int inputDim)
    {
        var layers = new GatLayer[expectedLayers];
        var expectedIn = inputDim;
        for (var i = 0; i < expectedLayers; i++)
        {
            var layer = new GatLayer(
                weights.Get($"{prefix}.{i}.lin_src.weight"),
                weights.Get($"{prefix}.{i}.att_src"),
                weights.Get($"{prefix}.{i}.att_dst"),
                weights.Get($"{prefix}.{i}.bias"),
                $"{prefix}.{i}");
            if (layer.InDim != expectedIn && i == 0)
                throw new ArgumentException(
                    $"{prefix}.0 expects input width {layer.InDim}, model contract says {expectedIn}.");
            layers[i] = layer;
            // Decoder layers past the first see the previous output ⊕ skel features.
            expectedIn = layer.OutDim + (prefix.Contains("deconvs", StringComparison.Ordinal) ? SkelDim : 0);
        }
        return layers;
    }

    /// <summary>One GATConv re-expressed as explicit tensor math (no masking path).</summary>
    private sealed class GatLayer
    {
        public int InDim { get; }
        public int Heads { get; }
        public int Channels { get; }
        public int OutDim => Heads * Channels;

        private readonly float[] _w;       // [Heads*Channels, InDim] row-major
        private readonly float[] _attSrc;  // [Heads, Channels]
        private readonly float[] _attDst;  // [Heads, Channels]
        private readonly float[] _bias;    // [Heads*Channels]

        public GatLayer(WeightTensor w, WeightTensor attSrc, WeightTensor attDst, WeightTensor bias, string name)
        {
            if (w.Shape.Length != 2 || attSrc.Shape.Length != 3 || attDst.Shape.Length != 3 || bias.Shape.Length != 1)
                throw new ArgumentException($"Unexpected tensor ranks in GAT layer '{name}'.");
            Heads = attSrc.Shape[1];
            Channels = attSrc.Shape[2];
            InDim = w.Shape[1];
            if (w.Shape[0] != OutDim || bias.Shape[0] != OutDim
                || attDst.Shape[1] != Heads || attDst.Shape[2] != Channels)
                throw new ArgumentException($"Inconsistent tensor shapes in GAT layer '{name}'.");
            _w = w.Data;
            _attSrc = attSrc.Data;
            _attDst = attDst.Data;
            _bias = bias.Data;
        }

        public float[] Forward(float[] x, int n, int[] edgeSrc, int[] edgeDst)
        {
            if (x.Length != n * InDim)
                throw new ArgumentException($"GAT layer expects {InDim} features, got {x.Length / n}.");

            var hc = OutDim;
            // h = x · Wᵀ  →  [n, Heads*Channels]
            var h = new float[n * hc];
            for (var i = 0; i < n; i++)
            {
                var xRow = i * InDim;
                var hRow = i * hc;
                for (var o = 0; o < hc; o++)
                {
                    var wRow = o * InDim;
                    var sum = 0f;
                    for (var k = 0; k < InDim; k++)
                        sum += x[xRow + k] * _w[wRow + k];
                    h[hRow + o] = sum;
                }
            }

            // Per-node attention halves: aSrc/aDst[i, head] = Σ_c h[i, head, c] · att[head, c].
            var aSrc = new float[n * Heads];
            var aDst = new float[n * Heads];
            for (var i = 0; i < n; i++)
            {
                for (var head = 0; head < Heads; head++)
                {
                    var hOff = i * hc + head * Channels;
                    var attOff = head * Channels;
                    var src = 0f;
                    var dst = 0f;
                    for (var c = 0; c < Channels; c++)
                    {
                        src += h[hOff + c] * _attSrc[attOff + c];
                        dst += h[hOff + c] * _attDst[attOff + c];
                    }
                    aSrc[i * Heads + head] = src;
                    aDst[i * Heads + head] = dst;
                }
            }

            // Edge logits + segment softmax over the incoming edges of each destination.
            var e = edgeSrc.Length;
            var alpha = new float[e * Heads];
            var segMax = new float[n * Heads];
            Array.Fill(segMax, float.NegativeInfinity);
            for (var ei = 0; ei < e; ei++)
            {
                var s = edgeSrc[ei];
                var d = edgeDst[ei];
                for (var head = 0; head < Heads; head++)
                {
                    var logit = aSrc[s * Heads + head] + aDst[d * Heads + head];
                    if (logit < 0f)
                        logit *= NegativeSlope;
                    alpha[ei * Heads + head] = logit;
                    if (logit > segMax[d * Heads + head])
                        segMax[d * Heads + head] = logit;
                }
            }
            var segSum = new float[n * Heads];
            for (var ei = 0; ei < e; ei++)
            {
                var d = edgeDst[ei];
                for (var head = 0; head < Heads; head++)
                {
                    var ex = MathF.Exp(alpha[ei * Heads + head] - segMax[d * Heads + head]);
                    alpha[ei * Heads + head] = ex;
                    segSum[d * Heads + head] += ex;
                }
            }

            // out[dst] = Σ_incoming attention · h[src], heads concatenated, + bias.
            var result = new float[n * hc];
            for (var ei = 0; ei < e; ei++)
            {
                var s = edgeSrc[ei];
                var d = edgeDst[ei];
                for (var head = 0; head < Heads; head++)
                {
                    var att = alpha[ei * Heads + head] / segSum[d * Heads + head];
                    var srcOff = s * hc + head * Channels;
                    var dstOff = d * hc + head * Channels;
                    for (var c = 0; c < Channels; c++)
                        result[dstOff + c] += att * h[srcOff + c];
                }
            }
            for (var i = 0; i < n; i++)
            {
                var row = i * hc;
                for (var o = 0; o < hc; o++)
                    result[row + o] += _bias[o];
            }
            return result;
        }
    }
}
