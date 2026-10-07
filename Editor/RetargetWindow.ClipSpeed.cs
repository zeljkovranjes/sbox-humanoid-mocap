using System;
using System.Globalization;
using System.Threading.Tasks;
using Editor;
using HumanoidMocap.Core.Motion;
using Sandbox;

namespace HumanoidMocap.EditorTools;

/// <summary>Clips that move too fast (see <see cref="ClipSpeed"/>): after a body capture the user is offered to slow the animation
/// down to a recommended speed and, for footage recorded faster than the body network's 30 frames per second, to capture it again
/// from every frame (<see cref="MotionInterleave"/>). The speed applies to the preview and to every export; Advanced → Animation
/// speed changes or undoes it.</summary>
public sealed partial class RetargetWindow
{
    /// <summary>Playback and export speed of the animation (1 = the video's own).</summary>
    float _animationSpeed=1;
    /// <summary>Capture every frame of high-frame-rate footage (two passes) instead of every other one.</summary>
    bool _everyFrame;
    /// <summary>The video the slow-down was last offered for, so it is offered once.</summary>
    string _slowDownOfferedFor;
    LineEdit _speedField;
    /// <summary>The capture stride of the last video captured (2 for 60 fps footage), and a capture started from here.</summary>
    int _lastCaptureStride=1;
    Task _processingTask;
    internal float GateTypicalSpeed { get; private set; }
    internal float? GateRecommendedSpeed { get; private set; }
    internal float GateAnimationSpeed=>_animationSpeed;

    /// <summary>A new video starts at its own speed, captured the usual way.</summary>
    void ResetClipSpeed()
    {
        _animationSpeed=1;_everyFrame=false;_slowDownOfferedFor=null;
        if(_speedField.IsValid())_speedField.Text="100";
    }

    /// <summary>After a body capture: if the picture moved too fast (the capture's own measure, see ClipSpeed), asks once whether to slow it down.</summary>
    async Task OfferSlowDownAsync(string video,int captureStride,float? pictureSpeed)
    {
        await EditorPipeline.SwitchToMainThread();if(!this.IsValid()||_slowDownOfferedFor==video)return;
        var typical=pictureSpeed??0;
        GateTypicalSpeed=typical;GateRecommendedSpeed=ClipSpeed.Recommended(typical);
        if(GateRecommendedSpeed is not float speed)return;
        _slowDownOfferedFor=video;
        var recapture=captureStride>1&&!_everyFrame;
        if(Environment.GetEnvironmentVariable("HM_GATE_RESULT") is not null)return;
        Dialog.AskConfirm(()=>ApplySlowDown(speed,recapture),
            FormattableString.Invariant($"This clip is very fast: it moves about {typical/ClipSpeed.Comfortable:0.0}× as quickly as a fast dance, so the animation will look rushed.\n\nSlow it down to {speed*100:0}% speed?")
            +(recapture?"\n\nIt will also be captured again using every frame of the video (it was recorded at a high frame rate), so fast moves are followed more closely. This takes about twice as long.":"")
            +"\n\nYou can change the speed later under Advanced → Animation speed.",
            "Clip is very fast","Slow down","Keep speed");
    }

    /// <summary>Plays and exports at <paramref name="speed"/>; with <paramref name="recapture"/>, captures the video again from every frame.</summary>
    void ApplySlowDown(float speed,bool recapture)
    {
        SetAnimationSpeed(speed);
        if(recapture){_everyFrame=true;_processingTask=ProcessImportedVideoAsync();}
        else _captureStatus.Text=FormattableString.Invariant($"Animation slowed to {speed*100:0}% speed. The export uses the same speed.");
    }
    internal Task GateSlowDownAsync()
    {
        if(GateRecommendedSpeed is not float speed)return Task.CompletedTask;
        ApplySlowDown(speed,_everyFrame==false&&_lastCaptureStride>1);
        return _processingTask??Task.CompletedTask;
    }

    void SetAnimationSpeed(float speed)
    {
        _animationSpeed=Math.Clamp(speed,.1f,2f);
        if(_speedField.IsValid())_speedField.Text=(_animationSpeed*100).ToString("0",CultureInfo.InvariantCulture);
    }

    /// <summary>Advanced → Animation speed, in percent.</summary>
    void AnimationSpeedEdited()
    {
        if(float.TryParse(_speedField.Text.TrimEnd('%',' '),NumberStyles.Float,CultureInfo.InvariantCulture,out var percent)&&percent>=10&&percent<=200)
        {
            SetAnimationSpeed(percent/100);
            _captureStatus.Text=Math.Abs(_animationSpeed-1)<1e-3f?"Animation plays at the video's speed.":FormattableString.Invariant($"Animation plays and exports at {_animationSpeed*100:0}% speed.");
        }
        else _speedField.Text=(_animationSpeed*100).ToString("0",CultureInfo.InvariantCulture);
    }
}
