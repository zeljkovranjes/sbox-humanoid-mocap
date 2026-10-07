using System;
using System.Collections.Generic;
using System.Numerics;
using HumanoidMocap.Core.Maths;

namespace HumanoidMocap.Core.Motion;
using Vector3 = System.Numerics.Vector3;

public readonly record struct ContactSample(double Time,Vector3 Hand, XForm Object,
    Vector3 ClosestSurfacePoint, float FingerClosure, bool Observed,Vector3? Wrist=null);
