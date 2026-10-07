using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Core.Mapping;

namespace HumanoidMocap.Core.Motion;
using Vector3=System.Numerics.Vector3;

public sealed record MotionDiagnosticsReport(int Frames,double Start,double End,double MinimumFrameInterval,double MaximumFrameInterval,
    float MaximumObservedBoneLengthRangeMetres,float MaximumObservedRotationStepDegrees,double MaximumObservedRootSpeedMetresPerSecond,
    IReadOnlyList<JointTrackDiagnostics> Tracks)
{
    public string Interpretation=>"Derived motion checks, not backend confidence or ground-truth accuracy. Large motion changes can be intentional.";
    public string HandSummary=>string.Join(" ",Tracks.Where(t=>t.Role is BoneRole.HandL or BoneRole.HandR)
        .Select(t=>t.GeneratedIk>0
            ?$"{(t.Role==BoneRole.HandL?"Left":"Right")} hand: reconstructed {t.Reconstructed}/{Frames}, generated IK {t.GeneratedIk}/{Frames}; longest unresolved gap {t.LongestMissingSeconds:F2}s."
            :$"{(t.Role==BoneRole.HandL?"Left":"Right")} hand: observed {t.Reconstructed}/{Frames} frames; longest unresolved gap {t.LongestMissingSeconds:F2}s."));
}
