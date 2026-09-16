using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HumanoidMocap.Mapping;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Reversible target edit in capture-camera metres. Never a reconstructed observation.</summary>
public sealed class WristPositionOffset
{
    public BoneRole Hand { get; set; } = BoneRole.HandR;
    public double Start { get; set; }
    public double End { get; set; }
    public float[] Offset { get; set; } = new float[3];
    public double FadeSeconds { get; set; } = .1;
    public bool Enabled { get; set; } = true;
    public WristPositionOffset Copy()=>new(){Hand=Hand,Start=Start,End=End,Offset=Offset.ToArray(),FadeSeconds=FadeSeconds,Enabled=Enabled};
}

public static class WristPositionOffsets
{
    public static void Validate(IReadOnlyList<WristPositionOffset> edits)
    {
        if(edits is null||edits.Count>64)throw new ArgumentException("At most 64 wrist corrections are supported.");
        foreach(var edit in edits)
            if(edit is null||edit.Hand is not (BoneRole.HandL or BoneRole.HandR)||!double.IsFinite(edit.Start)||!double.IsFinite(edit.End)||
                edit.End<=edit.Start||!double.IsFinite(edit.FadeSeconds)||edit.FadeSeconds<=0||edit.FadeSeconds>5||
                edit.Offset is not {Length:3}||edit.Offset.Any(v=>!float.IsFinite(v))||MotionDocument.V(edit.Offset).LengthSquared()>.25f)
                throw new ArgumentException("Wrist corrections need a hand, a nonempty time range, a fade in (0,5] seconds and a finite offset no longer than 50 cm.");
        for(var i=0;i<edits.Count;i++)for(var j=i+1;j<edits.Count;j++)
            if(edits[i].Enabled&&edits[j].Enabled&&edits[i].Hand==edits[j].Hand&&edits[i].Start<edits[j].End&&edits[j].Start<edits[i].End)
                throw new ArgumentException("Enabled corrections for the same wrist cannot overlap. Edit or disable the existing interval first.");
    }

    public static float Weight(WristPositionOffset edit,double time,double clipStart,double clipEnd)
    {
        if(!edit.Enabled||time<edit.Start||time>edit.End)return 0;
        var fade=Math.Min(edit.FadeSeconds,(edit.End-edit.Start)/2);
        double Smooth(double t){t=Math.Clamp(t,0,1);return t*t*(3-2*t);}
        // Clip boundaries have no neighbouring samples to blend to. Interior
        // boundaries return smoothly to untouched motion without a position snap.
        var enter=edit.Start<=clipStart?1:Smooth((time-edit.Start)/fade);
        var leave=edit.End>=clipEnd?1:Smooth((edit.End-time)/fade);
        return (float)Math.Min(enter,leave);
    }

    public static Vector3 Sample(IReadOnlyList<WristPositionOffset> edits,BoneRole hand,double time,double clipStart,double clipEnd)
    {
        var offset=Vector3.Zero;
        foreach(var edit in edits)if(edit.Hand==hand)offset+=MotionDocument.V(edit.Offset)*Weight(edit,time,clipStart,clipEnd);
        return offset;
    }
}
