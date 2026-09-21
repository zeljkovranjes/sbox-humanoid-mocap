using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Editor;

public sealed partial class RetargetWindow
{
    MocapAdjustmentStore.Session _editSession;
    CleanupSettings _appliedCleanup;
    string _adjustmentSaveError;

    void ResetAdjustmentFields()
    {
        var cleanup=_appliedCleanup??new CleanupSettings();
        _rootSmooth.Text=cleanup.Root.ToString(CultureInfo.InvariantCulture);
        _armSmooth.Text=cleanup.Arms.ToString(CultureInfo.InvariantCulture);
        _fingerSmooth.Text=cleanup.Fingers.ToString(CultureInfo.InvariantCulture);
        FitMocapPlacementToTarget();
    }

    async Task ReviewContactAsync(ContactInterval contact,ContactReview review)
    {
        if(_processing is not null)return;
        if(review==ContactReview.Confirmed&&new PropContactMotion(_editedMotion).UnsupportedReason(contact) is {} reason)
        {_captureStatus.Text=reason;return;}
        contact.Review=review;
        // Rebuild from observations so contact protection is applied before filtering.
        var source=_rawMotion.Copy();source.Contacts=_editedMotion.Copy().Contacts;
        _editedMotion=_appliedCleanup is null?source:MotionCleanup.Apply(source,_appliedCleanup);
        RefreshContacts();
        await RefreshMocapPreviewAsync();
    }

    void RestoreTargetAdjustments()
    {
        if(_editSession is null||_target is null)return;
        if(!_editSession.State.Targets.TryGetValue(MocapAdjustmentStore.TargetKey(_target.Spec,_firstPerson),out var edit))return;
        string V(System.Numerics.Vector3 v)=>FormattableString.Invariant($"{v.X:R},{v.Y:R},{v.Z:R}");
        string F(float v)=>v.ToString("R",CultureInfo.InvariantCulture);
        var c=edit.Corrections;
        _shoulderL.Text=V(c.LeftShoulder);_shoulderR.Text=V(c.RightShoulder);
        _elbowL.Text=V(c.LeftElbow);_elbowR.Text=V(c.RightElbow);
        _capturePosition.Text=V(c.CaptureCameraPosition);_captureYaw.Text=F(c.CaptureCameraYawDegrees);_capturePitch.Text=F(c.CaptureCameraPitchDegrees);
        _captureFacesSubject=c.CaptureFacesSubject;
        _reach.Text=F(c.Reach);_ground.Text=F(c.GroundOffset);_facing.Text=F(c.FacingDegrees);
        _stabilizeFeetControl.Value=c.StabilizeFeet;
        _wristOffsets.Clear();_wristOffsets.AddRange(c.WristOffsets.Select(e=>e.Copy()));++_wristEditRevision;
        _rootMotion=edit.RootMotion;_inPlaceControl.Value=_rootMotion==RootMotionMode.InPlace;
        _fov.Text=F(edit.Fov);_viewPitch.Text=F(edit.ViewPitch);_viewNear.Text=F(edit.NearClip);
    }

    void SaveAppliedAdjustments(MocapAdjustmentStore.Session session,string key,MocapAdjustmentStore.TargetEdit edit,
        CleanupSettings cleanup,MotionDocument motion)
    {
        if(session is null||session!=_editSession)return;
        session.State.Cleanup=cleanup;session.State.Contacts=motion.Contacts;session.State.Targets[key]=edit;
        try{MocapAdjustmentStore.Save(session);_adjustmentSaveError=null;}
        catch(Exception error){_captureStatus.Text=_adjustmentSaveError="Preview ready, but adjustments could not be saved: "+error.Message;}
    }
}
