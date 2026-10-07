using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Core.Mapping;

namespace HumanoidMocap.Core.Motion;
using Vector3=System.Numerics.Vector3;

public sealed record JointTrackDiagnostics(string Bone,BoneRole? Role,int Reconstructed,int Inferred,int GeneratedIk,int Unobserved,
    double LongestMissingSeconds,IReadOnlyList<MissingObservationInterval> MissingIntervals,int Authored=0);
