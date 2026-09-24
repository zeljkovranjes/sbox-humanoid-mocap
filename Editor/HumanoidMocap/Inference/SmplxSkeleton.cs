#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;

namespace HumanoidMocap.Inference;
using Vector3=System.Numerics.Vector3;

/// <summary>SMPL-X neutral, first 22 joints/10 shape coefficients, as used by GVHMR FK.
/// This does not reconstruct hands, faces, skin surfaces or confidence scores.</summary>
public sealed class SmplxSkeleton
{
    public const string NeutralSha256="376021446ddc86e99acacd795182bbef903e61d33b76b9d8b359c2b0865bd992";
    static readonly int[] parents={-1,0,0,0,1,2,3,4,5,6,7,8,9,9,9,12,13,14,16,17,18,19};
    public static ReadOnlySpan<int> Parents=>parents;
    readonly Vector3[] template=new Vector3[22];
    readonly Vector3[] shapeDirections=new Vector3[22*10];
    public sealed record Frame(Vector3[] Position,Quaternion[] Orientation,Vector3[] LocalPosition);
    public SmplxSkeleton(string path,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(!FileChecksum.Matches(path,NeutralSha256))
            throw new InvalidDataException("SMPL-X neutral model does not match the pinned version.");
        using var archive=ZipFile.OpenRead(path);
        var vertices=NumpyArray.Read(archive,"v_template.npy",cancellation);
        var regressor=NumpyArray.Read(archive,"J_regressor.npy",cancellation);
        var directions=NumpyArray.Read(archive,"shapedirs.npy",cancellation);
        var hierarchy=NumpyArray.Read(archive,"kintree_table.npy",cancellation);
        if(!vertices.Shape.SequenceEqual(new[]{10475,3})||!regressor.Shape.SequenceEqual(new[]{55,10475})||
           !directions.Shape.SequenceEqual(new[]{10475,3,400})||!hierarchy.Shape.SequenceEqual(new[]{2,55}))
            throw new InvalidDataException("Unexpected SMPL-X neutral array shapes.");
        for(var j=0;j<22;j++)
        {
            cancellation.ThrowIfCancellationRequested();
            if(j>0&&hierarchy.Values[j]!=parents[j])throw new InvalidDataException("Unexpected SMPL-X joint hierarchy.");
            for(var v=0;v<10475;v++)
            {
                var coefficient=regressor.Values[j*10475+v];if(coefficient==0)continue;
                template[j]+=new Vector3(vertices.Values[v*3],vertices.Values[v*3+1],vertices.Values[v*3+2])*coefficient;
                for(var beta=0;beta<10;beta++)shapeDirections[j*10+beta]+=new Vector3(
                    directions.Values[(v*3)*400+beta],directions.Values[(v*3+1)*400+beta],directions.Values[(v*3+2)*400+beta])*coefficient;
            }
        }
    }
    /// <summary>The rest joints made left-right symmetric about the pelvis: centre joints on its centre line,
    /// left and right joints mirrored with their heights and depths averaged. The shaped joints carry small
    /// asymmetries (a hip joint 1 cm higher than the other, the neck 1.6 cm to one side) that retargeting read
    /// as a lean, tipping every capture's torso about 5 degrees to the same side. Pose rotations are unchanged.</summary>
    public static Vector3[] Symmetric(Vector3[] rest)
    {
        if(rest.Length!=22)throw new ArgumentException("Expected 22 SMPL-X body joints.");
        var result=(Vector3[])rest.Clone();var centre=rest[0].X;
        foreach(var j in new[]{3,6,9,12,15})result[j]=rest[j] with{X=centre};
        foreach(var (left,right) in new[]{(1,2),(4,5),(7,8),(10,11),(13,14),(16,17),(18,19),(20,21)})
        {
            var half=((rest[left].X-centre)-(rest[right].X-centre))*.5f;
            var y=(rest[left].Y+rest[right].Y)*.5f;var z=(rest[left].Z+rest[right].Z)*.5f;
            result[left]=new Vector3(centre+half,y,z);result[right]=new Vector3(centre-half,y,z);
        }
        return result;
    }
    public Vector3[] RestPose(ReadOnlySpan<float> betas)
    {
        if(betas.Length!=10)throw new ArgumentException("SMPL-X skeleton requires ten shape coefficients.");
        var joints=(Vector3[])template.Clone();
        for(var b=0;b<10;b++)
        {if(!float.IsFinite(betas[b]))throw new ArgumentException("Non-finite body shape.");for(var j=0;j<22;j++)joints[j]+=shapeDirections[j*10+b]*betas[b];}
        return joints;
    }
    public Frame Forward(ReadOnlySpan<float> betas,ReadOnlySpan<Quaternion> bodyPose,Quaternion rootOrientation,Vector3 translation)
    {
        if(bodyPose.Length!=21)throw new ArgumentException("Expected 21 body rotations.");
        var rest=RestPose(betas);var positions=new Vector3[22];var rotations=new Quaternion[22];var local=new Vector3[22];
        for(var j=0;j<22;j++)
        {
            var parent=parents[j];local[j]=j==0?rest[j]+translation:rest[j]-rest[parent];
            var rotation=j==0?rootOrientation:bodyPose[j-1];
            if(!float.IsFinite(rotation.LengthSquared())||rotation.LengthSquared()<1e-12f)throw new ArgumentException("Invalid body rotation.");
            rotation=Quaternion.Normalize(rotation);
            rotations[j]=j==0?rotation:Quaternion.Normalize(rotations[parent]*rotation);
            positions[j]=j==0?local[j]:positions[parent]+Vector3.Transform(local[j],rotations[parent]);
        }
        return new(positions,rotations,local);
    }
}
