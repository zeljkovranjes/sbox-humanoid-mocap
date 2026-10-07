#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HumanoidMocap.Core.Dl;

/// <summary>
/// One named float32 tensor of a <see cref="SameWeights"/> blob.
/// </summary>
/// <param name="Shape">Dimensions (row-major / C order).</param>
/// <param name="Data">Flat float32 data, length = product of <paramref name="Shape"/>.</param>
public readonly record struct WeightTensor(int[] Shape, float[] Data);
