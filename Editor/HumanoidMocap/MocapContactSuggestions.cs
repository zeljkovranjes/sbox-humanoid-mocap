using System;
using System.Threading;
using System.Threading.Tasks;
using HumanoidMocap.Motion;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    internal async Task SuggestPropContactsAsync()
    {
        if(_editedMotion is null||_processing is not null)return;
        var expected=_editedMotion;var session=_editSession;var raw=_rawMotion;var cleanup=_appliedCleanup;
        _processing=new CancellationTokenSource();var token=_processing.Token;SetCaptureBusy(true);
        _captureStatus.Text="Checking observed hands against imported prop surfaces…";
        try
        {
            var result=await Task.Run(()=>{
                var source=raw.Copy();source.Contacts=expected.Copy().Contacts;
                var suggestions=PropContactSuggestions.Suggest(source,token);source.Contacts.AddRange(suggestions.Contacts);
                token.ThrowIfCancellationRequested();var edited=cleanup is null?source:MotionCleanup.Apply(source,cleanup);
                return (suggestions,edited);
            },token);
            await EditorPipeline.SwitchToMainThread();token.ThrowIfCancellationRequested();
            if(!this.IsValid()||session!=_editSession||expected!=_editedMotion)return;
            _editedMotion=result.edited;RefreshContacts();await RefreshMocapPreviewAsync();
            _captureStatus.Text=$"{result.suggestions.Contacts.Count} new contact suggestions. Review yellow intervals in Advanced before confirming. {result.suggestions.AmbiguousSamples} ambiguous samples left unconstrained.";
        }
        catch(OperationCanceledException){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text="Contact search cancelled. Existing contacts preserved.";}
        catch(Exception error){await EditorPipeline.SwitchToMainThread();if(this.IsValid())_captureStatus.Text=error.Message;}
        finally{await EditorPipeline.SwitchToMainThread();_processing.Dispose();_processing=null;if(this.IsValid()){SetCaptureBusy(false);StartNextQueuedVideo();}}
    }
}
