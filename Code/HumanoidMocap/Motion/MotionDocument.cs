using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using HumanoidMocap.Mapping;
using HumanoidMocap.Maths;
using HumanoidMocap.Skeleton;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

public enum MotionSpace { CameraRelative, WorldRelative }
public enum JointEvidence { Reconstructed, InferredGap, GeneratedIk, Unobserved, Authored }
public enum ObjectMotionSource { Unspecified = -1, Tracked, ImportedAnimation, CalibratedMarker, Manual }
public enum ContactReview { Suggested, Confirmed, Disabled }

/// <summary>Immutable source data by convention. Corrections produce a new document.</summary>
public sealed class MotionDocument
{
    public int Schema { get; set; } = 1;
    public string Name { get; set; } = "capture";
    public string Backend { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string SourceVideo { get; set; } = "";
    public MotionOrigin? OriginalReconstruction { get; set; }
    public double SourceFps { get; set; }
    public MotionSpace Space { get; set; }
    public string Axes { get; set; } = "right-handed-x-right-y-up";
    public string Units { get; set; } = "metres";
    public bool MetricScaleCalibrated { get; set; }
    public List<MotionBone> Bones { get; set; } = new();
    public List<MotionFrame> Frames { get; set; } = new();
    public List<CameraObservation> Cameras { get; set; } = new();
    public List<PropTrack> Objects { get; set; } = new();
    public List<ContactInterval> Contacts { get; set; } = new();
    /// <summary>Backend stationary-joint probabilities, not visibility or 3D accuracy.
    /// Values follow Frames and remain separate from reviewed object contacts.</summary>
    public List<StationaryJointTrack> StationaryJoints { get; set; } = new();
    public List<MotionCorrection> Corrections { get; set; } = new();
    public List<string> Diagnostics { get; set; } = new();

    public static JsonSerializerOptions JsonOptions => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static MotionDocument Parse(byte[] bytes)
    {
        var document = JsonSerializer.Deserialize<MotionDocument>(bytes, JsonOptions)
            ?? throw new FormatException("Empty motion document.");
        document.Validate();
        return document;
    }
    public MotionDocument Copy() => Parse(System.Text.Encoding.UTF8.GetBytes(ToJson()));

    public void Validate()
    {
        if (Schema != 1 || Units != "metres" || Axes != "right-handed-x-right-y-up")
            throw new FormatException("Unsupported motion schema, units or coordinate convention.");
        if(OriginalReconstruction is {} origin&&(string.IsNullOrWhiteSpace(origin.Path)||origin.Sha256 is not {Length:64}||origin.Sha256.Any(c=>!Uri.IsHexDigit(c))))
            throw new FormatException("Invalid original reconstruction reference.");
        if (!double.IsFinite(SourceFps) || SourceFps <= 0 || SourceFps > 1000)
            throw new FormatException("Invalid source frame rate.");
        if (Bones.Count is < 1 or > 1024 || Frames.Count is < 1 or > 108000)
            throw new FormatException("Motion exceeds the supported bone/frame budget or is empty.");
        var names = new HashSet<string>();
        for (var i = 0; i < Bones.Count; i++)
        {
            var b = Bones[i];
            if (string.IsNullOrWhiteSpace(b.Name) || !names.Add(b.Name) || b.Parent < -1 || b.Parent >= i)
                throw new FormatException("Bones must have unique names and parents before children.");
            CheckVector(b.RestPosition, 3); CheckRotation(b.RestRotation);
        }
        double previous = double.NegativeInfinity;
        foreach (var f in Frames)
        {
            if (!double.IsFinite(f.Time) || f.Time <= previous)
                throw new FormatException("Timestamps must be finite and strictly increasing.");
            previous = f.Time;
            if (f.Positions.Length != Bones.Count || f.Rotations.Length != Bones.Count || f.Evidence.Length != Bones.Count
                || (f.Confidence is not null && f.Confidence.Length != Bones.Count))
                throw new FormatException("Frame channels do not match skeleton.");
            for (var j = 0; j < Bones.Count; j++)
            {
                CheckVector(f.Positions[j], 3); CheckRotation(f.Rotations[j]);
                if(!Enum.IsDefined(typeof(JointEvidence),f.Evidence[j]))throw new FormatException("Unknown joint evidence label.");
                if (f.Confidence?[j] is float c && (!float.IsFinite(c) || c < 0 || c > 1))
                    throw new FormatException("Invalid backend confidence.");
            }
        }
        foreach (var c in Contacts)
        {
            if (c.Start < Frames[0].Time || c.End > Frames[^1].Time || c.End < c.Start || !double.IsFinite(c.Start+c.End))
                throw new FormatException("Contact interval is outside the clip.");
            CheckVector(c.LocalTarget,3);
            if (!Enum.IsDefined(typeof(ContactReview),c.Review)) throw new FormatException("Unknown contact review state.");
            if(c.TargetKeys is null)throw new FormatException("Contact target keys cannot be null.");
            double previousKey=double.NegativeInfinity;
            foreach(var key in c.TargetKeys)
            {
                if(!double.IsFinite(key.Time)||key.Time<=previousKey||key.Time<c.Start||key.Time>c.End)
                    throw new FormatException("Sliding contact keys must increase within the contact interval.");
                CheckVector(key.Position,3);previousKey=key.Time;
            }
        }
        PropContactMotion.ValidateObjects(this);
        if(StationaryJoints is null||StationaryJoints.Count>Bones.Count)throw new FormatException("Invalid stationary-joint tracks.");
        var stationaryNames=new HashSet<string>();
        foreach(var track in StationaryJoints)
            if(track is null||!stationaryNames.Add(track.Bone)||!names.Contains(track.Bone)||string.IsNullOrWhiteSpace(track.Source)
                ||track.Probability is null||track.Probability.Length!=Frames.Count||track.Probability.Any(p=>!float.IsFinite(p)||p<0||p>1))
                throw new FormatException("Stationary-joint probabilities must identify their source and match the motion frames.");
    }
    static void CheckVector(float[] v, int n)
    {
        if (v is null || v.Length != n || v.Any(x => !float.IsFinite(x)))
            throw new FormatException("Invalid transform channel.");
    }
    static void CheckRotation(float[] q)
    {
        CheckVector(q, 4);
        if (Math.Abs(q.Sum(x => x*x)-1) > .01f) throw new FormatException("Rotation must be a unit quaternion (xyzw).");
    }
    public static Vector3 V(float[] p) => new(p[0],p[1],p[2]);
    public static Quaternion Q(float[] q) => new(q[0],q[1],q[2],q[3]);
    public static float[] A(Vector3 p) => new[] { p.X,p.Y,p.Z };
    public static float[] A(Quaternion q) => new[] { q.X,q.Y,q.Z,q.W };

    /// <summary>Only the export adapter resamples. The source document retains original timestamps.</summary>
    public SourceScene ToSourceScene(float? sampleFps = null)
    {
        Validate();
        var skeleton = Skeleton.Skeleton.Create(Bones.Select(b => new BoneDefinition(b.Name,
            b.Parent < 0 ? null : Bones[b.Parent].Name, new XForm(V(b.RestPosition)*100, Q(b.RestRotation)))).ToArray());
        var mapping = new MappingResult("Motion document roles", MappingSource.Authored) { Confidence = 1 };
        for (var j=0;j<Bones.Count;j++)
            if (Bones[j].Role is { } role) mapping.RoleToBone[role] = skeleton.IndexOf(Bones[j].Name);
        float fps=sampleFps ?? (float)SourceFps;
        if (!float.IsFinite(fps) || fps <= 0 || fps>1000) throw new ArgumentOutOfRangeException(nameof(sampleFps));
        var duration=Frames[^1].Time-Frames[0].Time;
        var count=Math.Max(1,(int)Math.Round(duration*fps)+1);
        if (count>108000) throw new ArgumentException("Export exceeds frame budget.");
        var frames=new List<XForm[]>(count);var evidence=new List<JointEvidence[]>(count);var cursor=0;
        for (var i=0;i<count;i++)
        {
            var time=Math.Min(Frames[0].Time+i/fps,Frames[^1].Time);
            while(cursor+1<Frames.Count && Frames[cursor+1].Time<time)cursor++;
            var a=Frames[cursor];var b=Frames[Math.Min(cursor+1,Frames.Count-1)];
            var t=b.Time>a.Time?(float)((time-a.Time)/(b.Time-a.Time)):0;
            var frame=new XForm[Bones.Count];var frameEvidence=new JointEvidence[Bones.Count];
            for(var j=0;j<Bones.Count;j++)
            {
                var index=skeleton.IndexOf(Bones[j].Name);
                frame[index]=new XForm(
                Vector3.Lerp(V(a.Positions[j]),V(b.Positions[j]),t)*100,
                Quaternion.Slerp(Q(a.Rotations[j]),Q(b.Rotations[j]),t));
                frameEvidence[index]=t<=0?a.Evidence[j]:t>=1?b.Evidence[j]:
                    a.Evidence[j]==JointEvidence.Unobserved||b.Evidence[j]==JointEvidence.Unobserved?JointEvidence.Unobserved:
                    a.Evidence[j]==b.Evidence[j]?a.Evidence[j]:JointEvidence.InferredGap;
            }
            frames.Add(frame);evidence.Add(frameEvidence);
        }
        var notes=new List<string>(Diagnostics) { $"{Backend}: {Space}; metric scale calibrated={MetricScaleCalibrated}.",
            "Mapping confidence describes explicit bone-role assignments, not reconstruction accuracy." };
        if (Space==MotionSpace.CameraRelative) notes.Add("Camera-relative motion: no world root-motion claim. Body retargeting removes absolute camera placement and assumes the clip's lowest reconstructed joint reaches the ground; within-clip travel remains estimated.");
        return new SourceScene(skeleton,new[]{new Clip(Name,fps,false,frames,(float)SourceFps)},100,notes:notes)
        {
            AuthoredMapping=mapping, CaptureSpace=Space, CaptureEvidence=evidence,
            CaptureContacts=new PropContactMotion(this),
            CaptureStationaryJoints=StationaryJoints.ToDictionary(s=>s.Bone,s=>Enumerable.Range(0,count).Select(i=>{
                var time=Math.Min(Frames[0].Time+i/(double)fps,Frames[^1].Time);
                var lo=0;var hi=Frames.Count-1;
                while(lo<hi){var mid=(lo+hi)/2;if(Frames[mid].Time<time)lo=mid+1;else hi=mid;}
                // Conservative resampling: both bracketing observations must agree.
                return Frames[lo].Time==time||lo==0?s.Probability[lo]:Math.Min(s.Probability[lo-1],s.Probability[lo]);
            }).ToArray()),
            // A camera-space translation is not an authored offset from the model's
            // rest ground. Keeping it as one makes the target hover and start metres
            // away from the preview origin. The existing placement-free solve removes
            // one clip-wide offset, preserving motion and leaving raw capture untouched.
            RestPlacementAuthored=Space==MotionSpace.WorldRelative
        };
    }
}

public sealed class MotionOrigin
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class MotionBone
{
    public string Name { get; set; } = "";
    public int Parent { get; set; } = -1;
    public BoneRole? Role { get; set; }
    public string Group { get; set; } = "body";
    public float[] RestPosition { get; set; } = new float[3];
    public float[] RestRotation { get; set; } = new float[]{0,0,0,1};
}
public sealed class MotionFrame
{
    public double Time { get; set; }
    public float[][] Positions { get; set; } = Array.Empty<float[]>();
    public float[][] Rotations { get; set; } = Array.Empty<float[]>();
    public JointEvidence[] Evidence { get; set; } = Array.Empty<JointEvidence>();
    public float?[]? Confidence { get; set; }
}
public sealed class CameraObservation
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Calibrated { get; set; }
    public bool Synchronized { get; set; }
    public double TimeOffset { get; set; }
    public float[]? Intrinsics { get; set; }
    public float[]? Distortion { get; set; }
    public List<MotionFrame> Frames { get; set; } = new();
}
public sealed class PropTrack
{
    public string Id { get; set; } = "";
    public string ModelPath { get; set; } = "";
    /// <summary>Optional earlier object whose root parents this object's root. A detached
    /// object uses its own independent root track with no ParentObject.</summary>
    public string? ParentObject { get; set; }
    public ObjectMotionSource Source { get; set; } = ObjectMotionSource.Unspecified;
    public MotionSpace Space { get; set; }
    public List<MotionBone> Bones { get; set; } = new();
    public List<MotionFrame> Frames { get; set; } = new();
    /// <summary>Actual rigid contact geometry in object-bone-local metre coordinates.
    /// Not exported as character skin or reconstructed from hand motion.</summary>
    public List<PropSurface> Surfaces { get; set; } = new();
}
public sealed class PropSurface
{
    public string Bone { get; set; } = "";
    public string Source { get; set; } = "";
    public float[][] Vertices { get; set; } = Array.Empty<float[]>();
    public int[] Triangles { get; set; } = Array.Empty<int>();
}
public sealed class ContactInterval
{
    public string Bone { get; set; } = "";
    public string Object { get; set; } = "";
    public string ObjectBone { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public float[] LocalTarget { get; set; } = new float[3];
    public bool Sliding { get; set; }
    /// <summary>Object-bone-local metre positions at source-video timestamps. Required
    /// for a sliding constraint; not inferred from the hand model.</summary>
    public List<ContactTargetKey> TargetKeys { get; set; } = new();
    public ContactReview Review { get; set; }
    public string Reason { get; set; } = "";
}
public sealed class ContactTargetKey
{
    public double Time { get; set; }
    public float[] Position { get; set; } = new float[3];
}
public sealed class StationaryJointTrack
{
    public string Bone { get; set; } = "";
    public string Source { get; set; } = "";
    public float[] Probability { get; set; } = Array.Empty<float>();
}
public sealed class MotionCorrection
{
    public string Type { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public Dictionary<string,float> Settings { get; set; } = new();
}
