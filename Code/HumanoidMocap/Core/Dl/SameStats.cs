#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Skeleton;
using HumanoidMocap.Core.Solve;
using SkeletonModel = HumanoidMocap.Core.Skeleton.Skeleton;

namespace HumanoidMocap.Core.Dl;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>The z-normalization statistics shipped with the SAME checkpoint
/// (<c>ms_dict</c>): per-feature mean/std applied to every input except contact.</summary>
public sealed class SameStats
{
    internal float[] LoM, LoS, GoM, GoS, QM, QS, PM, PS, RM, RS, PvM, PvS, QvM, QvS, PprevM, PprevS;

    /// <summary>Reads the 16 <c>ms.*</c> arrays from a parsed weight blob.</summary>
    public SameStats(SameWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        LoM = weights.Stat("lo_m"); LoS = weights.Stat("lo_s");
        GoM = weights.Stat("go_m"); GoS = weights.Stat("go_s");
        QM = weights.Stat("q_m"); QS = weights.Stat("q_s");
        PM = weights.Stat("p_m"); PS = weights.Stat("p_s");
        RM = weights.Stat("r_m"); RS = weights.Stat("r_s");
        PvM = weights.Stat("pv_m"); PvS = weights.Stat("pv_s");
        QvM = weights.Stat("qv_m"); QvS = weights.Stat("qv_s");
        PprevM = weights.Stat("pprev_m"); PprevS = weights.Stat("pprev_s");
    }
}
