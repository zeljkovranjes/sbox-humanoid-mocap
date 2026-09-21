using System.Numerics;
using HumanoidMocap.Inference;

namespace HumanoidMocap.Worker;

/// <summary>Rules for letting WiLoR follow a hand the landmark detector has lost. WiLoR returns
/// a hand for any crop, so nothing here proves a hand is present; the rules only reject the
/// cases measured to be wrong. On a calibrated clip a hand gripping a keyboard edge-on was lost
/// for 62 frames in plain view, and following it gave 67 mm wrist and 50 mm fingertip
/// disagreement against 48 and 39 mm on detected frames. Hands that really left the image
/// reached its border or turned 50-150 degrees between frames within a few samples.</summary>
public static class HandFollowing
{
    public const float MinimumBorderDistance=.05f,MaximumWristShift=.35f,MaximumTurnDegrees=45,MinimumSizeRatio=.55f,MaximumSizeRatio=1.8f;
    public const double MaximumSeconds=3;
    /// <summary>Projected joints, in source pixels, of a WiLoR prediction made in the given crop.</summary>
    public static HandCapture.FollowedHand Describe(string side,WilorModel.Prediction hand,GvhmrDecoder.Box crop,int width,int height,double since)
    {
        var sign=side=="R"?1:-1;var focal=5000f/256*Math.Max(width,height);var scale=crop.Size*hand.WeakCamera[0]+1e-9f;
        var translation=new Vector3(sign*hand.WeakCamera[1]+2*(crop.CenterX-width*.5f)/scale,hand.WeakCamera[2]+2*(crop.CenterY-height*.5f)/scale,2*focal/scale);
        var image=hand.Hand.Landmarks.Select(l=>{var p=new Vector3(sign*l.X,l.Y,l.Z)+translation;return new[]{focal*p.X/p.Z+width*.5f,focal*p.Y/p.Z+height*.5f};}).ToArray();
        return new(side,image,hand.RotationMatrices.Take(9).ToArray(),crop.Size,since);
    }
    public static bool Plausible(HandCapture.FollowedHand last,HandCapture.FollowedHand next,int width,int height,double time)
    {
        if(time-last.Since>MaximumSeconds||next.Image.Any(p=>!float.IsFinite(p[0]+p[1])))return false;
        // A hand on its way out of the picture is not an occluded hand.
        var border=next.Image.Min(p=>Math.Min(Math.Min(p[0],p[1]),Math.Min(width-1-p[0],height-1-p[1])))/Math.Min(width,height);
        if(border<MinimumBorderDistance)return false;
        var shift=Vector2.Distance(new(next.Image[0][0],next.Image[0][1]),new(last.Image[0][0],last.Image[0][1]))/last.CropSize;
        var ratio=next.CropSize/last.CropSize;
        double trace=0;for(var k=0;k<9;k++)trace+=next.GlobalRotation[k]*last.GlobalRotation[k];
        var turn=Math.Acos(Math.Clamp((trace-1)/2,-1,1))*180/Math.PI;
        return shift<=MaximumWristShift&&turn<=MaximumTurnDegrees&&ratio>=MinimumSizeRatio&&ratio<=MaximumSizeRatio;
    }
}
