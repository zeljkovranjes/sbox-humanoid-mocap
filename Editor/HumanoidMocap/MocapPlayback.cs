using System;
using Editor;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    double _playhead;
    float _previewFps=30;
    bool _playing=true;
    Button _playButton;

    (double Start,double Last,double End) PlaybackRange
    {
        get
        {
            if(_editedMotion is { Frames.Count: > 0 } motion)
                return(motion.Frames[0].Time,motion.Frames[^1].Time,motion.Frames[^1].Time+1/motion.SourceFps);
            var duration=Math.Max(0,_video?.Duration??0);
            return(0,duration,duration);
        }
    }
    internal double PlaybackTime=>_playhead;
    internal double? SourcePlaybackTime=>_video?.FrameTime;
    internal bool SourcePlaybackPaused=>!_playing;
    internal byte[] SourceFramePng()=>_video?.FramePng();
    internal void SeekPlaybackFraction(float fraction)
    {
        var range=PlaybackRange;_playing=false;
        _playhead=range.Start+Math.Clamp(fraction,0,1)*(range.Last-range.Start);
        _video?.Request(_playhead);UpdateTransport();SynchronizePreview();
    }
    internal void TogglePlayback()
    {
        _playing=!_playing;
        if(_playing&&_playhead>=PlaybackRange.Last)_playhead=PlaybackRange.Start;
        UpdateTransport();
    }
    void ResetPlayback()
    {
        _playing=true;_playhead=PlaybackRange.Start;
        _video?.Request(_playhead);UpdateTransport();
    }
    void TickPlayback()
    {
        var range=PlaybackRange;
        if(_playing&&range.End>range.Start)
        {
            _playhead+=RealTime.Delta;
            if(_playhead>=range.End)_playhead=range.Start+(_playhead-range.Start)%(range.End-range.Start);
        }
        _video?.Request(Math.Clamp(_playhead,range.Start,range.Last));
        UpdateTransport();SynchronizePreview();
    }
    void UpdateTransport()
    {
        var range=PlaybackRange;
        _clock.Text=$"{_playhead:F2} s";
        _timeline.Value=range.Last>range.Start?(float)Math.Clamp((_playhead-range.Start)/(range.Last-range.Start),0,1):0;
        _contactTimeline?.SetPlayhead(_playhead);
        _playButton.Icon=_playing?"pause":"play_arrow";
    }
    void SynchronizePreview()
    {
        if(!_mocapPreview.IsValid()||_editedMotion is null)return;
        // Follow the frame actually on screen when decoding is slower than playback.
        var time=_video?.FrameTime??_playhead;
        _mocapPreview.Scrub((int)Math.Round((time-_editedMotion.Frames[0].Time)*_previewFps));
        _mocapPreview.ApplyCurrentFrame();
    }
}
