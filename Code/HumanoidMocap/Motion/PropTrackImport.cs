using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Imported animation, explicitly aligned by the user to capture coordinates.
/// This does not estimate camera motion or track an object from video.</summary>
public static class PropTrackImport
{
    public static PropTrack Convert(SourceScene scene,int clipIndex,string id,MotionSpace space,
        double startTime,Vector3 offset,Quaternion rotation,float scale=1,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(string.IsNullOrWhiteSpace(id)||!double.IsFinite(startTime)||startTime<0||!float.IsFinite(scale)||scale<=0
            ||!float.IsFinite(offset.X)||!float.IsFinite(offset.Y)||!float.IsFinite(offset.Z)
            ||!float.IsFinite(rotation.LengthSquared())||rotation.LengthSquared()<1e-8f)
            throw new ArgumentException("Enter a prop name, nonnegative video time, finite placement and positive scale.");
        if(clipIndex<0||clipIndex>=scene.Clips.Count)throw new ArgumentException("Choose an animation take. A prop needs an explicit animated track.");
        var clip=scene.Clips[clipIndex];var rig=scene.Skeleton;
        if(clip.FrameCount is <1 or >108000||rig.Count is <1 or >1024)
            throw new ArgumentException("Prop animation exceeds the supported bone/frame budget. Import a shorter take.");
        if(rig.Count==0||rig.Bones.Count(b=>b.ParentIndex<0)!=1)
            throw new ArgumentException("Import one prop root per file. Detachable objects can be imported separately.");
        var axes=new[]{scene.CoordAxis,scene.UpAxis,scene.FrontAxis};var signs=new[]{scene.CoordAxisSign,scene.UpAxisSign,scene.FrontAxisSign};
        if(axes.Any(a=>a<0||a>2)||axes.Distinct().Count()!=3||signs.Any(s=>s is not (-1 or 1)))
            throw new ArgumentException("Invalid prop coordinate axes.");
        Vector3 Axis(Vector3 p)
        {
            float Component(int i)=>i==0?p.X:i==1?p.Y:p.Z;
            return new(Component(axes[0])*signs[0],Component(axes[1])*signs[1],Component(axes[2])*signs[2]);
        }
        var x=Axis(Vector3.UnitX);var y=Axis(Vector3.UnitY);var z=Axis(Vector3.UnitZ);
        var basis=new Matrix4x4(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,0,0,0,1);
        var inverse=Matrix4x4.Transpose(basis);rotation=Quaternion.Normalize(rotation);
        XForm Transform(XForm value,bool root)
        {
            var p=Axis(value.Pos)*(.01f*scale);
            // Basis conjugation also handles reflected source conventions without
            // representing a reflection as a quaternion.
            var r=Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(inverse*Matrix4x4.CreateFromQuaternion(value.Rot)*basis));
            return root?new(offset+Vector3.Transform(p,rotation),Quaternion.Normalize(rotation*r)):new(p,r);
        }
        var prop=new PropTrack{Id=id,Source=ObjectMotionSource.ImportedAnimation,Space=space};
        foreach(var bone in rig.Bones)
        {
            var rest=Transform(bone.RestLocal,bone.ParentIndex<0);
            prop.Bones.Add(new(){Name=bone.Name,Parent=bone.ParentIndex,Group="prop",RestPosition=MotionDocument.A(rest.Pos),RestRotation=MotionDocument.A(rest.Rot)});
        }
        if((long)clip.FrameCount*rig.Count>Formats.Fbx.FbxAnimationWriter.MaximumTransformSamples)
            throw new ArgumentException("Prop animation exceeds the transform budget. Import a shorter take.");
        for(var f=0;f<clip.FrameCount;f++)
        {
            cancellation.ThrowIfCancellationRequested();
            var locals=clip.Frames[f].Select((pose,b)=>Transform(pose,rig[b].ParentIndex<0)).ToArray();
            prop.Frames.Add(new(){Time=startTime+f/(double)clip.Fps,Positions=locals.Select(p=>MotionDocument.A(p.Pos)).ToArray(),
                Rotations=locals.Select(p=>MotionDocument.A(p.Rot)).ToArray(),Evidence=locals.Select(_=>JointEvidence.Authored).ToArray()});
        }
        new MotionDocument{SourceFps=clip.Fps,Space=space,Bones=prop.Bones,Frames=prop.Frames}.Validate();
        return prop;
    }

    public static MotionDocument Add(MotionDocument capture,PropTrack prop)
    {
        if(prop.Space!=capture.Space)throw new ArgumentException("Align the prop to the capture coordinate space before importing.");
        if(capture.Objects.Any(p=>p.Id==prop.Id))throw new ArgumentException("A prop with that name already exists. Choose a unique name.");
        if(prop.Frames.Count==0||prop.Frames[0].Time>capture.Frames[0].Time+1e-6||prop.Frames[^1].Time<capture.Frames[^1].Time-1e-6)
            throw new ArgumentException("The prop animation must cover the entire capture range. Adjust its start time or provide a longer take; missing motion is not extrapolated.");
        var result=capture.Copy();result.Objects.Add(prop);result.Validate();return result.Copy();
    }
}
