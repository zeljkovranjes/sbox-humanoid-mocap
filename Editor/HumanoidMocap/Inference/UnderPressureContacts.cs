#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using HumanoidMocap.Mapping;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Inference;
using Vector3=System.Numerics.Vector3;
using Quaternion=System.Numerics.Quaternion;

/// <summary>Foot contacts from UnderPressure (Mourot et al. 2022, InterDigital), a small network that estimates
/// the vertical ground reaction force under each foot from the whole body's motion and reads contacts from
/// it. The body network's own stationary-foot predictions judge a foot by how little it moves, so a planted
/// foot that slides is exactly the one they miss; this judges it by where the body's weight goes. On a
/// step-dance clip it marked the alternating steps while the toes it called planted still slid about 1 m/s.
///
/// Port of models.DeepNetwork and data.Contacts.from_forces: joints on UnderPressure's 23-joint skeleton in
/// metres, Z up, at 100 frames a second; four 1-D convolutions and three fully connected layers give 16
/// pressure-insole cells per foot; front and back cell forces become toe and heel contacts.</summary>
public sealed class UnderPressureContacts
{
    public const string Source="UnderPressure foot contacts (vertical ground reaction forces estimated from the whole body's motion)";
    public const string CheckpointSha256="554180b0d33d53163a00bcf6e4522ed6a5800f81daa2fd503559309b62838e6c";
    public const long CheckpointBytes=4025022;
    public const string CheckpointUrl="https://raw.githubusercontent.com/InterDigitalInc/UnderPressure/e7ab73e7466444f262ec1c55f2f26afe51168796/pretrained.tar";
    const int Joints=23,Kernel=7,Cells=16;const double Rate=100;
    static readonly int[] Channels={3*Joints,128,128,256,256};
    static readonly int[] FrontCells={8,9,10,11,12,13,14,15},BackCells={0,1,2,3};
    readonly float[][] convWeight=new float[4][],convBias=new float[4][];
    readonly float[] fc1Weight,fc1Bias,fc2Weight,fc2Bias,outWeight;

    public UnderPressureContacts(string checkpoint)
    {
        using var file=new TorchCheckpoint(checkpoint,"model");
        var convLayers=new[]{3,6,9,12};
        for(var i=0;i<4;i++){convWeight[i]=file.ReadFloat($"{convLayers[i]}.weight");convBias[i]=file.ReadFloat($"{convLayers[i]}.bias");}
        fc1Weight=file.ReadFloat("16.weight");fc1Bias=file.ReadFloat("16.bias");
        fc2Weight=file.ReadFloat("19.weight");fc2Bias=file.ReadFloat("19.bias");outWeight=file.ReadFloat("22.weight");
        for(var i=0;i<4;i++)if(convWeight[i].Length!=Channels[i+1]*Channels[i]*Kernel||convBias[i].Length!=Channels[i+1])throw new InvalidOperationException("Unexpected UnderPressure checkpoint layout.");
        if(fc1Weight.Length!=256*256||fc2Weight.Length!=256*256||outWeight.Length!=2*Cells*256)throw new InvalidOperationException("Unexpected UnderPressure checkpoint layout.");
    }

    /// <summary>Contact per capture frame, 0 or 1, for the heel and toe of each foot, keyed by the document's
    /// FootL, ToeL, FootR and ToeR bone names; null when the skeleton lacks the joints.</summary>
    public Dictionary<string,float[]>? Estimate(MotionDocument document,CancellationToken cancellation=default)
    {
        var bones=document.Bones;int Role(BoneRole role)=>bones.FindIndex(b=>b.Role==role);
        var roles=new[]{BoneRole.Hips,BoneRole.Spine0,BoneRole.Spine1,BoneRole.Spine2,BoneRole.Neck,BoneRole.Head,
            BoneRole.ClavicleR,BoneRole.UpperArmR,BoneRole.LowerArmR,BoneRole.HandR,BoneRole.ClavicleL,BoneRole.UpperArmL,BoneRole.LowerArmL,BoneRole.HandL,
            BoneRole.UpperLegR,BoneRole.LowerLegR,BoneRole.FootR,BoneRole.ToeR,BoneRole.UpperLegL,BoneRole.LowerLegL,BoneRole.FootL,BoneRole.ToeL};
        var index=roles.Select(Role).ToArray();
        if(index.Any(i=>i<0)||document.Frames.Count<8)return null;
        var frames=document.Frames;var count=frames.Count;
        // UnderPressure order: pelvis, spine 1-4, neck, head, right arm (clavicle to wrist), left arm, right leg (hip to foot), left leg.
        // Its spine has one joint more than this skeleton; spine 3 is placed halfway between the two upper ones.
        var joints=new Vector3[count][];
        for(var f=0;f<count;f++)
        {
            var world=World(document,frames[f]);Vector3 At(int i)=>world[index[i]];
            var p=new Vector3[Joints];
            p[0]=At(0);p[1]=At(1);p[2]=At(2);p[3]=(At(2)+At(3))*.5f;p[4]=At(3);p[5]=At(4);p[6]=At(5);
            for(var j=0;j<16;j++)p[7+j]=At(6+j);
            // Y up to Z up.
            for(var j=0;j<Joints;j++)p[j]=new Vector3(p[j].X,-p[j].Z,p[j].Y);
            joints[f]=p;
        }
        // Floor: ankles about 5 cm up where the feet are lowest.
        var ankles=joints.Select(p=>Math.Min(p[17].Z,p[21].Z)).OrderBy(z=>z).ToArray();
        var lift=.05f-ankles[ankles.Length/20];
        var start=frames[0].Time;var duration=frames[^1].Time-start;var samples=Math.Max(2,(int)Math.Floor(duration*Rate)+1);
        var input=new float[Channels[0]*samples];var source=0;
        for(var s=0;s<samples;s++)
        {
            var t=start+s/Rate;while(source+1<count-1&&frames[source+1].Time<t)source++;
            var a=frames[source].Time;var b=frames[Math.Min(count-1,source+1)].Time;var w=(float)(b>a?Math.Clamp((t-a)/(b-a),0,1):0);
            var pa=joints[source];var pb=joints[Math.Min(count-1,source+1)];
            for(var j=0;j<Joints;j++)
            {
                var p=Vector3.Lerp(pa[j],pb[j],w);
                input[(j*3)*samples+s]=p.X;input[(j*3+1)*samples+s]=p.Y;input[(j*3+2)*samples+s]=p.Z+lift;
            }
        }
        var forces=Forces(input,samples,cancellation);
        var contacts=Contacts(forces,samples);
        // Back to the capture's frames: the nearest 100 Hz sample.
        var result=new Dictionary<string,float[]>
        {
            [bones[Role(BoneRole.ToeL)].Name]=new float[count],[bones[Role(BoneRole.FootL)].Name]=new float[count],
            [bones[Role(BoneRole.ToeR)].Name]=new float[count],[bones[Role(BoneRole.FootR)].Name]=new float[count],
        };
        for(var f=0;f<count;f++)
        {
            var s=Math.Clamp((int)Math.Round((frames[f].Time-start)*Rate),0,samples-1);
            result[bones[Role(BoneRole.ToeL)].Name][f]=contacts[s,0,0]?1:0;result[bones[Role(BoneRole.FootL)].Name][f]=contacts[s,1,0]?1:0;
            result[bones[Role(BoneRole.ToeR)].Name][f]=contacts[s,0,1]?1:0;result[bones[Role(BoneRole.FootR)].Name][f]=contacts[s,1,1]?1:0;
        }
        return result;
    }

    /// <summary>Network forward pass: channel-major input (69 x samples) to forces [sample, foot, cell].</summary>
    float[,,] Forces(float[] input,int samples,CancellationToken cancellation)
    {
        var x=input;
        for(var layer=0;layer<4;layer++)
        {
            cancellation.ThrowIfCancellationRequested();
            int cin=Channels[layer],cout=Channels[layer+1];var w=convWeight[layer];var bias=convBias[layer];var output=new float[cout*samples];var source=x;
            Parallel.For(0,cout,o=>
            {
                var row=output.AsSpan(o*samples,samples);row.Fill(bias[o]);
                for(var c=0;c<cin;c++)
                {
                    var channel=source.AsSpan(c*samples,samples);
                    for(var k=0;k<Kernel;k++)
                    {
                        var weight=w[(o*cin+c)*Kernel+k];if(weight==0)continue;var shift=k-Kernel/2;
                        // Replicate padding at both ends.
                        for(var t=0;t<samples;t++)row[t]+=weight*channel[Math.Clamp(t+shift,0,samples-1)];
                    }
                }
                for(var t=0;t<samples;t++)row[t]=Elu(row[t]);
            });
            x=output;
        }
        var forces=new float[samples,2,Cells];var features=x;
        Parallel.For(0,samples,t=>
        {
            Span<float> h=stackalloc float[256];Span<float> g=stackalloc float[256];
            for(var c=0;c<256;c++)h[c]=features[c*samples+t];
            Dense(fc1Weight,fc1Bias,h,g);for(var i=0;i<256;i++)g[i]=Elu(g[i]);
            Dense(fc2Weight,fc2Bias,g,h);for(var i=0;i<256;i++)h[i]=Elu(h[i]);
            for(var o=0;o<2*Cells;o++)
            {
                var sum=0f;for(var i=0;i<256;i++)sum+=outWeight[o*256+i]*h[i];
                forces[t,o/Cells,o%Cells]=sum>20?sum:MathF.Log(1+MathF.Exp(sum));
            }
        });
        return forces;
    }
    static void Dense(float[] weight,float[] bias,ReadOnlySpan<float> input,Span<float> output)
    {
        for(var o=0;o<output.Length;o++){var sum=bias[o];for(var i=0;i<input.Length;i++)sum+=weight[o*input.Length+i]*input[i];output[o]=sum;}
    }
    static float Elu(float v)=>v>0?v:MathF.Exp(v)-1;

    /// <summary>data.Contacts.from_forces: [sample, front/back, left/right].</summary>
    static bool[,,] Contacts(float[,,] raw,int samples)
    {
        // Gaussian moving average, 5 taps, sigma 1.667, replicate padding.
        var kernel=Enumerable.Range(-2,5).Select(x=>Math.Exp(-.5*(x/1.667)*(x/1.667))).ToArray();var norm=kernel.Sum();
        var forces=new double[samples,2,Cells];
        for(var t=0;t<samples;t++)for(var lr=0;lr<2;lr++)for(var c=0;c<Cells;c++)
        {double sum=0;for(var k=0;k<5;k++)sum+=kernel[k]*raw[Math.Clamp(t+k-2,0,samples-1),lr,c];forces[t,lr,c]=sum/norm;}
        var contacts=new bool[samples,2,2];
        for(var t=0;t<samples;t++)
        {
            var foot=new double[2];var local=new double[2,2];
            for(var lr=0;lr<2;lr++)
            {
                for(var c=0;c<Cells;c++)foot[lr]+=forces[t,lr,c];
                foreach(var c in FrontCells)local[0,lr]+=forces[t,lr,c];
                foreach(var c in BackCells)local[1,lr]+=forces[t,lr,c];
            }
            var total=foot[0]+foot[1];
            for(var lr=0;lr<2;lr++)
            {
                var both=local[0,lr]+local[1,lr];var scale=both>0?total/both:0;
                for(var fb=0;fb<2;fb++)contacts[t,fb,lr]=foot[lr]>=.10&&local[fb,lr]*scale>=.05;
            }
        }
        // Majority over 11 samples, repeated until nothing changes.
        for(var fb=0;fb<2;fb++)for(var lr=0;lr<2;lr++)
            for(var pass=0;pass<1000;pass++)
            {
                var next=new bool[samples];var changed=false;
                for(var t=0;t<samples;t++)
                {
                    var on=0;for(var k=-5;k<=5;k++)if(contacts[Math.Clamp(t+k,0,samples-1),fb,lr])on++;
                    next[t]=on>5.5;changed|=next[t]!=contacts[t,fb,lr];
                }
                for(var t=0;t<samples;t++)contacts[t,fb,lr]=next[t];
                if(!changed)break;
            }
        return contacts;
    }

    static Vector3[] World(MotionDocument document,MotionFrame frame)
    {
        var count=document.Bones.Count;var p=new Vector3[count];var q=new Quaternion[count];
        for(var i=0;i<count;i++)
        {
            var local=MotionDocument.V(frame.Positions[i]);var rotation=MotionDocument.Q(frame.Rotations[i]);var parent=document.Bones[i].Parent;
            if(parent<0){p[i]=local;q[i]=rotation;}
            else{p[i]=p[parent]+Vector3.Transform(local,q[parent]);q[i]=Quaternion.Normalize(q[parent]*rotation);}
        }
        return p;
    }
}
