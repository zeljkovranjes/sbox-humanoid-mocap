#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Numerics;

namespace HumanoidMocap.EditorTools.MotionBricks;

using Vector3 = System.Numerics.Vector3;

/// <summary>Where a transition run spent its time (milliseconds), for diagnostics.</summary>
public sealed record TransitionTiming( double Root, double Pose, double Decoder, double Representation );
