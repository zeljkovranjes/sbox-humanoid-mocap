using System.Numerics;
using HumanoidMocap.Inference;

namespace HumanoidMocap.Worker;

/// <summary>MANO shape/pose blend shapes, joint regression, forward kinematics and
/// linear blend skinning from checkpoint buffers. All coordinates remain in the
/// backend's camera convention (metres, x-right/y-down/z-forward).</summary>
public sealed class ManoDecoder
{
    const int VertexCount=778,JointCount=16;
    readonly float[] template,shapeDirections,poseDirections,regressor,skinWeights,poseMean;
    readonly int[] parents,tips;
    public int[] Parents=>(int[])parents.Clone();
    public sealed record DecodedHand(Vector3[] Joints,Vector3[] Vertices,Vector3[] RestJoints,
        Quaternion[] LocalRotations,Vector3[] Landmarks);
    /// <summary>The caller verifies the pinned checkpoint hash before constructing this decoder.</summary>
    public ManoDecoder(TorchCheckpoint checkpoint,string prefix,bool mobileHand=false)
    {
        float[] Read(string name,int count)
        {
            var key=mobileHand?name switch{"v_template"=>"V_","shapedirs"=>"S_","posedirs"=>"P_","J_regressor"=>"J_","lbs_weights"=>"W_","parents"=>"K_",_=>name}:name;
            var data=checkpoint.ReadFloat(prefix+key);
            if(data.Length!=count||data.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Invalid MANO buffer: "+name);
            return data;
        }
        template=Read("v_template",VertexCount*3);shapeDirections=Read("shapedirs",VertexCount*3*10);
        poseDirections=Read("posedirs",135*VertexCount*3);regressor=Read("J_regressor",JointCount*VertexCount);
        skinWeights=Read("lbs_weights",VertexCount*JointCount);poseMean=mobileHand?new float[48]:Read("pose_mean",48);
        parents=Read("parents",JointCount).Select(v=>checked((int)v)).ToArray();
        tips=Read(mobileHand?"fingertip_vert":checkpoint.Tensors.ContainsKey(prefix+"extra_joints_idxs")?"extra_joints_idxs":"vertex_joint_selector.extra_joints_idxs",5).Select(v=>checked((int)v)).ToArray();
        if(parents[0]!=-1||parents.Skip(1).Where((p,i)=>p<0||p>i).Any()||tips.Any(i=>i<0||i>=VertexCount))
            throw new InvalidDataException("Invalid MANO hierarchy or fingertip indices.");
    }
    /// <param name="addPoseMean">WildHands adds MANO's axis-angle mean; WiLoR's
    /// MANOLayer accepts final rotation matrices directly and must not add it.</param>
    public DecodedHand Decode(float[] rotationMatrices,float[] shape,bool addPoseMean,CancellationToken cancellation=default)
    {
        if(rotationMatrices.Length!=JointCount*9||shape.Length!=10||rotationMatrices.Concat(shape).Any(v=>!float.IsFinite(v)))
            throw new ArgumentException("Invalid MANO prediction.");
        var vertices=new Vector3[VertexCount];
        for(var v=0;v<VertexCount;v++)
        {
            var x=template[v*3];var y=template[v*3+1];var z=template[v*3+2];
            for(var b=0;b<10;b++)
            {x+=shapeDirections[v*30+b]*shape[b];y+=shapeDirections[v*30+10+b]*shape[b];z+=shapeDirections[v*30+20+b]*shape[b];}
            vertices[v]=new(x,y,z);
        }
        var rest=new Vector3[JointCount];
        for(var j=0;j<JointCount;j++)for(var v=0;v<VertexCount;v++)rest[j]+=regressor[j*VertexCount+v]*vertices[v];
        var rotations=new Quaternion[JointCount];var feature=new float[135];
        for(var j=0;j<JointCount;j++)
        {
            cancellation.ThrowIfCancellationRequested();var i=j*9;
            // System.Numerics uses row-vector matrices; checkpoints use column vectors.
            var matrix=new Matrix4x4(rotationMatrices[i],rotationMatrices[i+3],rotationMatrices[i+6],0,
                rotationMatrices[i+1],rotationMatrices[i+4],rotationMatrices[i+7],0,
                rotationMatrices[i+2],rotationMatrices[i+5],rotationMatrices[i+8],0,0,0,0,1);
            var q=Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));
            if(addPoseMean)
            {
                if(q.W<0)q=new(-q.X,-q.Y,-q.Z,-q.W);
                var sine=new Vector3(q.X,q.Y,q.Z).Length();
                var aa=sine<1e-8f?new Vector3(q.X,q.Y,q.Z)*2:new Vector3(q.X,q.Y,q.Z)*(2*MathF.Atan2(sine,q.W)/sine);
                aa+=new Vector3(poseMean[j*3],poseMean[j*3+1],poseMean[j*3+2]);
                var angle=aa.Length();q=angle<1e-8f?Quaternion.Identity:Quaternion.CreateFromAxisAngle(aa/angle,angle);
            }
            rotations[j]=q;
            if(j==0)continue;
            matrix=Matrix4x4.CreateFromQuaternion(q);var at=(j-1)*9;
            feature[at]=matrix.M11-1;feature[at+1]=matrix.M21;feature[at+2]=matrix.M31;
            feature[at+3]=matrix.M12;feature[at+4]=matrix.M22-1;feature[at+5]=matrix.M32;
            feature[at+6]=matrix.M13;feature[at+7]=matrix.M23;feature[at+8]=matrix.M33-1;
        }
        for(var v=0;v<VertexCount;v++)
        {
            if((v&127)==0)cancellation.ThrowIfCancellationRequested();
            float dx=0,dy=0,dz=0;
            for(var p=0;p<135;p++)
            {var i=p*VertexCount*3+v*3;dx+=feature[p]*poseDirections[i];dy+=feature[p]*poseDirections[i+1];dz+=feature[p]*poseDirections[i+2];}
            vertices[v]+=new Vector3(dx,dy,dz);
        }
        var joints=new Vector3[JointCount];var world=new Quaternion[JointCount];
        for(var j=0;j<JointCount;j++)
        {
            var parent=parents[j];
            world[j]=parent<0?rotations[j]:Quaternion.Normalize(world[parent]*rotations[j]);
            joints[j]=parent<0?rest[j]:joints[parent]+Vector3.Transform(rest[j]-rest[parent],world[parent]);
        }
        var skinned=new Vector3[VertexCount];
        for(var v=0;v<VertexCount;v++)for(var j=0;j<JointCount;j++)
            skinned[v]+=skinWeights[v*JointCount+j]*(joints[j]+Vector3.Transform(vertices[v]-rest[j],world[j]));
        var all=new Vector3[21];Array.Copy(joints,all,JointCount);
        for(var i=0;i<tips.Length;i++)all[16+i]=skinned[tips[i]];
        int[] map={0,13,14,15,16,1,2,3,17,4,5,6,18,10,11,12,19,7,8,9,20};
        return new(joints,skinned,rest,rotations,map.Select(i=>all[i]).ToArray());
    }
}
