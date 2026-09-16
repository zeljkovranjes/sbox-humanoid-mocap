using System;
using HumanoidMocap.Mapping;
using Sandbox;

namespace HumanoidMocap.Editor;

public sealed partial class PreviewWidget
{
    /// <summary>Blender-style octahedral bones and round joints on the solved target.
    /// Gizmo IgnoreDepth keeps them visible through the skinned mesh.</summary>
    public bool ShowTargetBones { get; set; } = true;
    public int TargetBoneOverlayCount { get; private set; }
    static readonly Color TargetBoneOutline = new(1f,.58f,.16f,1f);

    void DrawTargetBones()
    {
        TargetBoneOverlayCount=0;
        if(!ShowTargetBones||_worldScratch is null||_clip is null)return;
        UpdateGizmoInputs(false);
        using(Gizmo.Scope("HumanoidMocap.TargetBones"))
        {
            // Endpoints below are already world-space. Never inherit another gizmo's transform.
            Gizmo.Transform=Transform.Zero;
            Gizmo.Draw.IgnoreDepth=true;
            Gizmo.Draw.LineThickness=1.2f;
            foreach(var bone in _rig.Skeleton.Bones)
            {
                if(_rig.RoleOf(bone.Index) is not { } role)continue;
                if(FirstPerson&&!IsArmOrFinger(role))continue;
                // The clavicle's parenting offset starts at the chest bone, well below
                // the collarbone. It is a hierarchy link, not a solid anatomical bone.
                // The actual clavicle is drawn by the clavicle -> upper-arm edge below.
                if(role is BoneRole.ClavicleL or BoneRole.ClavicleR)continue;
                var parent=bone.ParentIndex;
                while(parent>=0&&_rig.RoleOf(parent) is null)parent=_rig.Skeleton[parent].ParentIndex;
                if(parent<0||FirstPerson&&_rig.RoleOf(parent) is { } parentRole&&!IsArmOrFinger(parentRole))continue;
                if(!TryTargetBonePosition(parent,out var head)||!TryTargetBonePosition(bone.Index,out var tail))continue;
                var delta=tail-head;var length=delta.Length;
                if(length<.02f)continue;
                var direction=delta/length;
                var u=Vector3.Cross(direction,MathF.Abs(direction.z)<.8f?Vector3.Up:Vector3.Forward).Normal;
                var v=Vector3.Cross(direction,u).Normal;
                var width=Math.Clamp(length*.1f,.035f,.7f);
                var waist=head+delta*.22f;
                var ring=new[]{waist+u*width,waist+v*width,waist-u*width,waist-v*width};
                // Low-opacity faces retain the silhouette without hiding finger articulation.
                Gizmo.Draw.Color=TargetBoneOutline.WithAlpha(.16f);
                for(var i=0;i<4;i++)
                {
                    var next=ring[(i+1)%4];
                    Gizmo.Draw.SolidTriangle(head,ring[i],next);
                    Gizmo.Draw.SolidTriangle(tail,next,ring[i]);
                }
                Gizmo.Draw.Color=TargetBoneOutline;
                for(var i=0;i<4;i++)
                {
                    Gizmo.Draw.Line(head,ring[i]);Gizmo.Draw.Line(tail,ring[i]);
                    Gizmo.Draw.Line(ring[i],ring[(i+1)%4]);
                }
                var radius=Math.Clamp(length*.045f,.045f,.2f);
                Gizmo.Draw.SolidSphere(head,radius,6,6);Gizmo.Draw.SolidSphere(tail,radius,6,6);
                TargetBoneOverlayCount++;
            }
        }
    }

    bool TryTargetBonePosition(int index,out Vector3 position)
    {
        if(_sceneModel.IsValid()&&!SkeletonOnly)
        {
            // Model constraints can change the rendered pose after our overrides (the
            // Human's ring-to-pinky constraints are one example). Draw the skin's actual
            // skeleton, not the unconstrained reconstruction used to drive it.
            var modelBone=_rigToModelBone[index];
            if(modelBone<0){position=default;return false;}
            position=_sceneModel.GetBoneWorldTransform(modelBone).Position;
            return true;
        }
        position=RigWorldToEngine(_worldScratch[index]).Position;
        return true;
    }

    static bool IsArmOrFinger(BoneRole role)=>role is BoneRole.UpperArmL or BoneRole.UpperArmR or
        BoneRole.LowerArmL or BoneRole.LowerArmR or BoneRole.HandL or BoneRole.HandR||
        role.ToString().StartsWith("Thumb",StringComparison.Ordinal)||role.ToString().StartsWith("Index",StringComparison.Ordinal)||
        role.ToString().StartsWith("Middle",StringComparison.Ordinal)||role.ToString().StartsWith("Ring",StringComparison.Ordinal)||
        role.ToString().StartsWith("Pinky",StringComparison.Ordinal);
}
