using System;
using System.Linq;
using System.Numerics;

namespace HumanoidMocap.Core.Motion;
using Vector3 = System.Numerics.Vector3;

public sealed class CleanupSettings
{
    public float Root { get; set; } = .25f;
    public float Arms { get; set; } = .1f;
    public float Fingers { get; set; } = .025f;
    public double MaximumGapSeconds { get; set; } = .1;
    /// <summary>Longer losses between two observations are bridged with an eased glide over
    /// at most this many seconds, ending at the reacquired pose, instead of a frozen pose that
    /// snaps. Zero restores hold-and-snap. Bridged samples are labelled inferred, never observed.</summary>
    public double BridgeSeconds { get; set; } = 1.5;
    public float PreserveAngularSpeed { get; set; } = 6f;
    /// <summary>Zero-phase Butterworth smoothing of joint rotations on Rokoko's 0.5-10 strength scale
    /// (cutoff 10.5 - strength hertz; see <see cref="MocapSmooth"/>). Zero turns it off. The default, Rokoko's,
    /// brought WiLoR finger jitter on the HOT3D clip from eleven times the real hand's to 1.4 times and wrist
    /// jitter to the real hand's level, with wrist error unchanged (37.2 mm against 37.4 mm).</summary>
    public float Smoothing { get; set; } = 7;
    /// <summary>The same for root (wrist, for hand captures) positions. Zero turns it off.</summary>
    public float PositionSmoothing { get; set; } = 7;
}
