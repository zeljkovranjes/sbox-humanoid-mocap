#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HumanoidMocap.Cleanup;
using HumanoidMocap.Motion;

namespace HumanoidMocap.Editor;

/// <summary>Applied editor corrections beside a motion file, never in its raw channels.</summary>
public static class MocapAdjustmentStore
{
    public sealed class TargetEdit
    {
        public TargetCorrectionSettings Corrections { get; set; } = new();
        public RootMotionMode RootMotion { get; set; }
        public float Fov { get; set; } = 75;
        public float ViewPitch { get; set; }
        public float NearClip { get; set; } = 15;
    }
    public sealed class State
    {
        public int Schema { get; set; } = 1;
        public string InputHash { get; set; } = "";
        public string RawPath { get; set; } = "";
        public string RawHash { get; set; } = "";
        public CleanupSettings? Cleanup { get; set; }
        public List<ContactInterval> Contacts { get; set; } = new();
        public Dictionary<string,TargetEdit> Targets { get; set; } = new();
    }
    public sealed record Session(string Path,State State,MotionDocument Raw,MotionDocument Edited,string? Notice);
    static JsonSerializerOptions Options=>new(MotionDocument.JsonOptions){IncludeFields=true};
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));

    public static Session Load(string path,string? originalPath=null,CleanupSettings? initialCleanup=null)
    {
        path=System.IO.Path.GetFullPath(path);
        var inputBytes=File.ReadAllBytes(path);var input=MotionDocument.Parse(inputBytes);var inputHash=Hash(inputBytes);
        var storePath=path+".adjustments.json";State? state=null;string? notice=null;
        var rawBytes=originalPath is null?inputBytes:File.ReadAllBytes(originalPath);
        var rawHash=Hash(rawBytes);
        if(File.Exists(storePath))
        {
            if(new FileInfo(storePath).Length>1024*1024)throw new InvalidDataException("Saved adjustments exceed the 1 MiB limit.");
            state=JsonSerializer.Deserialize<State>(File.ReadAllText(storePath),Options)
                ??throw new InvalidDataException("Empty adjustment file.");
            Validate(state);
            if(state.InputHash!=inputHash&&(originalPath is null||state.RawHash!=rawHash))
            {state=null;notice="The motion changed; saved adjustments were not applied.";}
        }
        var restored=state is not null;
        if(state is not null)
        {
            // A missing/changed original must never silently become already-cleaned input.
            rawBytes=File.ReadAllBytes(state.RawPath);
            if(Hash(rawBytes)!=state.RawHash)throw new InvalidDataException("Original reconstruction changed. Restore it before reopening these adjustments.");
            state.InputHash=inputHash;
        }
        else state=new State{InputHash=inputHash,RawPath=System.IO.Path.GetFullPath(originalPath??path),RawHash=rawHash,
            Cleanup=initialCleanup,Contacts=input.Copy().Contacts};
        var raw=MotionDocument.Parse(rawBytes);
        var source=raw.Copy();source.Contacts=state.Contacts;
        var edited=restored?(state.Cleanup is null?source:MotionCleanup.Apply(source,state.Cleanup)):input;
        edited.Contacts=state.Contacts;edited.Validate();
        return new(storePath,state,raw,edited,notice);
    }

    public static string TargetKey(RetargetTargetSpec spec,bool firstPerson)
    {
        var data=JsonSerializer.Serialize(new{spec.UpAxis,spec.VmdlScale,firstPerson,bones=spec.Rig.Skeleton.Bones.Select(b=>new{
            b.Name,b.ParentIndex,role=spec.Rig.RoleOf(b.Index),position=MotionDocument.A(b.RestLocal.Pos),rotation=MotionDocument.A(b.RestLocal.Rot)})});
        return Hash(Encoding.UTF8.GetBytes(data));
    }

    public static void Save(Session session)
    {
        Validate(session.State);
        var json=JsonSerializer.Serialize(session.State,Options);
        if(Encoding.UTF8.GetByteCount(json)>1024*1024)throw new InvalidDataException("Saved adjustments exceed the 1 MiB limit.");
        var temporary=session.Path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temporary,json);File.Move(temporary,session.Path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }

    static void Validate(State state)
    {
        if(state.Schema!=1||state.Targets is null||state.Contacts is null||state.Targets.Count>64
            ||string.IsNullOrEmpty(state.RawPath)||state.Contacts.Any(c=>c is null||!Enum.IsDefined(typeof(ContactReview),c.Review)))
            throw new InvalidDataException("Unsupported saved adjustments.");
        foreach(var edit in state.Targets.Values)
        {
            if(edit?.Corrections is null||!Enum.IsDefined(typeof(RootMotionMode),edit.RootMotion)
                ||!float.IsFinite(edit.Fov)||!float.IsFinite(edit.ViewPitch)||!float.IsFinite(edit.NearClip))
                throw new InvalidDataException("Invalid saved target adjustments.");
            WristPositionOffsets.Validate(edit.Corrections.WristOffsets);
        }
        if(state.Cleanup is { } c&&(!float.IsFinite(c.Root)||!float.IsFinite(c.Arms)||!float.IsFinite(c.Fingers)
            ||!float.IsFinite(c.PreserveAngularSpeed)||c.PreserveAngularSpeed<0))
            throw new InvalidDataException("Invalid saved cleanup settings.");
    }
}
