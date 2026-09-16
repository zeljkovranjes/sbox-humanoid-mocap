#nullable enable
using System;
using System.Numerics;
using System.Threading;

namespace HumanoidMocap.Inference;
using Vector3=System.Numerics.Vector3;

/// <summary>GVHMR's two-pass source-limb CCD refinement, adapted from process_ik and
/// CCD_IK at ee960bb6. Targets must be in the same space as the supplied root.
/// This solves model contact targets, not observed hand/object grips. See Gvhmr.LICENSE.</summary>
public static class GvhmrLimbIk
{
    static readonly int[][] chains={new[]{0,1,4,7,10},new[]{0,2,5,8,11},new[]{9,13,16,18,20},new[]{9,14,17,19,21}};
    public static Quaternion[] Solve(SmplxSkeleton skeleton,GvhmrDecoder.Pose pose,GvhmrDecoder.Root root,
        Vector3[] targets,CancellationToken cancellation=default)
    {
        var n=pose.Frames;
        if(n<1||n>GvhmrTemporalNetwork.MaximumFrames||targets.Length!=n*6||root.Orientation.Length!=n||root.Translation.Length!=n)
            throw new ArgumentException("Contact IK tracks do not match the body clip.");
        foreach(var target in targets)if(!GvhmrDecoder.Finite(target))throw new ArgumentException("Non-finite contact target.");
        var output=new Quaternion[n*21];var parents=SmplxSkeleton.Parents;
        for(var t=0;t<n;t++)
        {
            cancellation.ThrowIfCancellationRequested();
            var original=skeleton.Forward(pose.Betas.AsSpan(t*10,10),pose.BodyRotations.AsSpan(t*21,21),root.Orientation[t],root.Translation[t]);
            var local=new Matrix4x4[22];
            for(var j=0;j<22;j++)
            {
                local[j]=Matrix4x4.CreateFromQuaternion(j==0?root.Orientation[t]:pose.BodyRotations[t*21+j-1]);
                local[j].Translation=original.LocalPosition[j];
            }
            for(var c=0;c<chains.Length;c++)
            {
                var chain=chains[c];var all=Forward(local,parents);var chainLocal=new Matrix4x4[5];
                for(var j=0;j<5;j++)chainLocal[j]=j==0?all[chain[0]]:local[chain[j]];
                var targetIndices=c<2?new[]{3,4}:new[]{4};
                var targetBase=c<2?c*2:c+2;
                var goals=new Vector3[targetIndices.Length];
                for(var j=0;j<goals.Length;j++)goals[j]=targets[t*6+targetBase+j];
                SolveChain(chainLocal,targetIndices,goals);
                for(var j=1;j<5;j++)local[chain[j]]=WithRotation(local[chain[j]],chainLocal[j]);
            }
            for(var j=1;j<22;j++)
            {
                var rotation=Rotation(local[j]);
                output[t*21+j-1]=GvhmrDecoder.Continuous(rotation,t==0?pose.BodyRotations[j-1]:output[(t-1)*21+j-1]);
            }
        }
        return output;
    }
    // Matrices are the transpose of upstream column-vector matrices. Preserve the
    // reference's column-normalization (rows here) during CCD, including multi-target
    // averaging; only the final exported local attitudes become unit quaternions.
    internal static void SolveChain(Matrix4x4[] local,int[] targets,Vector3[] goals)
    {
        var parents=new[]{-1,0,1,2,3};
        for(var iteration=0;iteration<2;iteration++)
        {
            var world=Forward(local,parents);
            for(var j=1;j<local.Length-1;j++)
            {
                var position=world[j].Translation;var rotation=Rotation(world[j]);
                var x=Vector3.Zero;var y=Vector3.Zero;var count=0;
                for(var k=0;k<targets.Length;k++)
                {
                    if(j>=targets[k])continue;
                    var delta=Between(world[targets[k]].Translation-position,goals[k]-position);
                    var solved=Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity,delta,(j+1f)/local.Length)*rotation);
                    x+=Vector3.Transform(Vector3.UnitX,solved);y+=Vector3.Transform(Vector3.UnitY,solved);count++;
                }
                if(count==0)continue;
                x=Normalize(x/count);y=Normalize(y/count);var z=Vector3.Cross(x,y);
                var solvedWorld=new Matrix4x4(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,0,0,0,1);
                var parent=world[j-1];parent.Translation=Vector3.Zero;
                if(!Matrix4x4.Invert(parent,out var inverse))throw new InvalidOperationException("Degenerate contact IK parent frame.");
                local[j]=WithRotation(local[j],solvedWorld*NormalizeRows(inverse));
                world=Forward(local,parents);
            }
        }
    }
    static Matrix4x4[] Forward(Matrix4x4[] local,ReadOnlySpan<int> parents)
    {
        var world=new Matrix4x4[local.Length];
        for(var j=0;j<local.Length;j++)world[j]=parents[j]<0?local[j]:NormalizeRows(local[j]*world[parents[j]]);
        return world;
    }
    static Vector3 Normalize(Vector3 value)=>value/Math.Max(value.Length(),1e-9f);
    static Quaternion Between(Vector3 from,Vector3 to)
    {
        if(from.LengthSquared()<1e-16f||to.LengthSquared()<1e-16f)return Quaternion.Identity;
        var cross=Vector3.Cross(from,to);
        var w=MathF.Sqrt(from.LengthSquared()*to.LengthSquared())+Vector3.Dot(from,to);
        if(cross.LengthSquared()==0&&Math.Abs(w)<=1e-4f)cross=Vector3.UnitY;
        return Quaternion.Normalize(new Quaternion(cross,w));
    }
    static Matrix4x4 NormalizeRows(Matrix4x4 value)
    {
        // matrix.normalized_matrix uses norm + epsilon, rather than a clamp.
        var x=new Vector3(value.M11,value.M12,value.M13);x/=x.Length()+1e-9f;
        var y=new Vector3(value.M21,value.M22,value.M23);y/=y.Length()+1e-9f;
        var z=new Vector3(value.M31,value.M32,value.M33);z/=z.Length()+1e-9f;
        value.M11=x.X;value.M12=x.Y;value.M13=x.Z;value.M21=y.X;value.M22=y.Y;value.M23=y.Z;
        value.M31=z.X;value.M32=z.Y;value.M33=z.Z;return value;
    }
    static Matrix4x4 WithRotation(Matrix4x4 original,Matrix4x4 rotation)
    {rotation.Translation=original.Translation;return rotation;}
    internal static Quaternion Rotation(Matrix4x4 m)
    {
        // PyTorch3D matrix_to_quaternion chooses the best-conditioned component.
        var a=new[]{Math.Max(0,1+m.M11+m.M22+m.M33),Math.Max(0,1+m.M11-m.M22-m.M33),
            Math.Max(0,1-m.M11+m.M22-m.M33),Math.Max(0,1-m.M11-m.M22+m.M33)};
        var i=0;for(var j=1;j<4;j++)if(a[j]>a[i])i=j;
        var q=i switch
        {
            0=>new Quaternion(m.M23-m.M32,m.M31-m.M13,m.M12-m.M21,a[0]),
            1=>new Quaternion(a[1],m.M12+m.M21,m.M13+m.M31,m.M23-m.M32),
            2=>new Quaternion(m.M12+m.M21,a[2],m.M23+m.M32,m.M31-m.M13),
            _=>new Quaternion(m.M13+m.M31,m.M23+m.M32,a[3],m.M12-m.M21)
        };
        if(!float.IsFinite(q.LengthSquared())||q.LengthSquared()<1e-12f)throw new InvalidOperationException("Invalid contact IK rotation.");
        return Quaternion.Normalize(q);
    }
}
