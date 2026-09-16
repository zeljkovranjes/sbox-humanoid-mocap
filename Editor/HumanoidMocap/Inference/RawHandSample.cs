using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Inference;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Editor;

/// <summary>Shared data-only observation cache, compatible with existing editor jobs.</summary>
public sealed class RawHandSample
{
    public double Time { get; set; }
    public double DecoderTime { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<RawHand> Hands { get; set; } = new();
    public sealed class RawHand
    {
        public string Side { get; set; } = "";
        public float Presence { get; set; }
        public float Handedness { get; set; }
        public float[][] Image { get; set; } = System.Array.Empty<float[]>();
        public float[][] RelativeWorld { get; set; } = System.Array.Empty<float[]>();
        public bool Tracked { get; set; }
        public HandObservation ToObservation()=>new(Side,Presence,Handedness,Image.Select(MotionDocument.V).ToArray(),RelativeWorld.Select(MotionDocument.V).ToArray(),Tracked);
    }
}
