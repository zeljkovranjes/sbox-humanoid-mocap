using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using HumanoidMocap.Motion;
using HumanoidMocap.Skeleton;

namespace HumanoidMocap.Formats.Fbx;
using Vector3 = System.Numerics.Vector3;

/// <summary>Extracts triangulated rigid skin regions. Mixed weights, additive skin,
/// nontriangular faces and independently animated mesh nodes are not guessed.</summary>
public static class FbxPropSurfaces
{
    public sealed record Result(List<PropSurface> Surfaces,int SkippedFaces);
    public static Result Read(byte[] data,SourceScene imported,float scale=1,CancellationToken cancellation=default)
    {
        var tree=FbxTokenizer.Parse(data);var scene=FbxScene.Build(tree);
        var links=tree.Child("Connections")?.ChildrenNamed("C").Where(n=>n.Prop<string>(0)=="OO")
            .Select(n=>(Child:n.Prop<long>(1),Parent:n.Prop<long>(2))).ToArray()??Array.Empty<(long Child,long Parent)>();
        var result=new List<PropSurface>();var skipped=0;var total=0;
        bool Uniform(Vector3 s)=>s.X>0&&Math.Abs(s.X-s.Y)<=s.X*1e-5f&&Math.Abs(s.X-s.Z)<=s.X*1e-5f;
        bool AnimatedScale(FbxObject? node)
        {
            var seen=new HashSet<long>();
            while(node is not null){if(!seen.Add(node.Id))throw new FormatException("Cyclic prop hierarchy.");
                if(scene.Stacks.Any(s=>s.Bindings.ContainsKey((node.Id,"Lcl Scaling")))||!Uniform(scene.GetVector3(node,"Lcl Scaling",Vector3.One)))return true;node=node.ModelParent;}
            return false;
        }
        Matrix4x4 World(FbxObject? node)
        {
            var matrix=Matrix4x4.Identity;var seen=new HashSet<long>();
            while(node is not null){if(!seen.Add(node.Id))throw new FormatException("Cyclic prop hierarchy.");matrix*=FbxTransform.FromModel(scene,node).LocalMatrixDefault();node=node.ModelParent;}
            return matrix;
        }
        IEnumerable<FbxObject> Children(long id)=>links.Where(c=>c.Parent==id&&scene.ObjectsById.ContainsKey(c.Child)).Select(c=>scene.ObjectsById[c.Child]);
        Vector3 ConvertPoint(Vector3 p)
        {
            float C(int i)=>i==0?p.X:i==1?p.Y:p.Z;
            return new Vector3(C(imported.CoordAxis)*imported.CoordAxisSign,C(imported.UpAxis)*imported.UpAxisSign,
                C(imported.FrontAxis)*imported.FrontAxisSign)*((float)scene.UnitScaleFactor*.01f*scale);
        }
        foreach(var geometry in scene.ObjectsById.Values.Where(o=>o.NodeType=="Geometry"&&o.SubClass=="Mesh"))
        {
            cancellation.ThrowIfCancellationRequested();
            var vertices=geometry.Node.Child("Vertices")?.AsDoubleArray(0);var polygon=geometry.Node.Child("PolygonVertexIndex")?.AsIntArray(0);
            if(vertices is null||polygon is null)continue;
            if(Children(geometry.Id).Any(o=>o.SubClass=="BlendShape")){skipped+=polygon.Count(i=>i<0);continue;}
            if(vertices.Length%3!=0||vertices.Length/3>100000||polygon.Length>600000)throw new FormatException("Prop contact mesh is too large or malformed. Use a triangulated low-poly mesh.");
            var count=vertices.Length/3;var assigned=new string?[count];var points=new Vector3[count];var weightSum=new double[count];var influences=new int[count];
            var meshModels=links.Where(c=>c.Child==geometry.Id&&scene.ObjectsById.ContainsKey(c.Parent)).Select(c=>scene.ObjectsById[c.Parent]).Where(o=>o.NodeType=="Model").ToArray();
            if(meshModels.Length!=1){skipped+=polygon.Count(i=>i<0);continue;}
            var mesh=meshModels[0];
            var geometric=Matrix4x4.CreateScale(scene.GetVector3(mesh,"GeometricScaling",Vector3.One))
                *Matrix4x4.CreateFromQuaternion(FbxTransform.EulerDegreesToQuaternion(scene.GetVector3(mesh,"GeometricRotation",Vector3.Zero),0))
                *Matrix4x4.CreateTranslation(scene.GetVector3(mesh,"GeometricTranslation",Vector3.Zero));
            var skins=Children(geometry.Id).Where(o=>o.NodeType=="Deformer"&&o.SubClass=="Skin").ToArray();
            foreach(var skin in skins)foreach(var cluster in Children(skin.Id).Where(o=>o.SubClass=="Cluster"))
            {
                cancellation.ThrowIfCancellationRequested();
                var bone=Children(cluster.Id).SingleOrDefault(o=>o.NodeType=="Model");
                var mode=cluster.Node.Child("Mode");
                var indices=cluster.Node.Child("Indexes")?.AsIntArray(0);var weights=cluster.Node.Child("Weights")?.AsDoubleArray(0);
                // Exporters may retain clusters for unused bones without either array.
                if(indices is null&&weights is null)continue;
                if(indices is null||weights is null||indices.Length!=weights.Length)throw new FormatException("Invalid prop skin weights.");
                var supported=bone is not null&&imported.Skeleton.IndexOf(bone.Name)>=0&&!AnimatedScale(bone)&&(mode is null||mode.Prop<string>(0) is "Normalize" or "TotalOne");
                var meshToBone=ReadMatrix(cluster.Node.Child("Transform"));var link=ReadMatrix(cluster.Node.Child("TransformLink"));
                if(!Matrix4x4.Invert(link,out _))throw new FormatException("Singular prop skin bind matrix.");
                // Serialized FBX Transform already maps mesh-node space into bind-bone
                // space. The SDK's GetTransformMatrix() instead returns mesh bind world.
                // Applying inverse TransformLink again double-transforms the surface.
                // Prepend geometry placement; absorb uniform scale omitted by rigid XForms.
                if(!Matrix4x4.Decompose(link,out var bindScale,out _,out _)||!Uniform(bindScale))supported=false;
                var local=geometric*meshToBone*Matrix4x4.CreateScale(bindScale);
                for(var i=0;i<indices.Length;i++)
                {
                    var v=indices[i];var w=weights[i];if(v<0||v>=count||!double.IsFinite(w)||w<0)throw new FormatException("Invalid prop vertex weight.");
                    if(w<=1e-6)continue;weightSum[v]+=w;influences[v]++;
                    if(supported){assigned[v]=bone!.Name;points[v]=ConvertPoint(Vector3.Transform(new((float)vertices[v*3],(float)vertices[v*3+1],(float)vertices[v*3+2]),local));}
                }
            }
            // An unskinned mesh may be rigidly parented to a known animated bone.
            if(skins.Length==0)
            {
                var transform=geometric;var node=mesh;var supported=true;var visited=new HashSet<long>();
                while(node is not null&&imported.Skeleton.IndexOf(node.Name)<0)
                {
                    if(!visited.Add(node.Id))throw new FormatException("Cyclic prop model hierarchy.");
                    if(scene.Stacks.Any(s=>s.Bindings.Keys.Any(k=>k.ModelId==node.Id))){supported=false;break;}
                    transform*=FbxTransform.FromModel(scene,node).LocalMatrixDefault();node=node.ModelParent;
                }
                if(supported&&node is not null&&!AnimatedScale(node)&&Matrix4x4.Decompose(World(node),out var parentScale,out _,out _)
                    &&Uniform(parentScale))
                {
                    transform*=Matrix4x4.CreateScale(parentScale);
                    for(var v=0;v<count;v++){assigned[v]=node.Name;weightSum[v]=1;influences[v]=1;points[v]=ConvertPoint(Vector3.Transform(new((float)vertices[v*3],(float)vertices[v*3+1],(float)vertices[v*3+2]),transform));}
                }
            }
            var byBone=new Dictionary<string,List<Vector3>>();var face=new List<int>();
            foreach(var index in polygon)
            {
                var v=index<0?-(long)index-1:index;if(v<0||v>=count)throw new FormatException("Invalid prop polygon index.");face.Add((int)v);
                if(index>=0)continue;
                var name=assigned[face[0]];
                if(face.Count==3&&name is not null&&face.All(i=>assigned[i]==name&&influences[i]==1&&Math.Abs(weightSum[i]-1)<1e-5)
                    &&Vector3.Cross(points[face[1]]-points[face[0]],points[face[2]]-points[face[0]]).LengthSquared()>1e-16f)
                {
                    if(total+3>100000)throw new FormatException("Prop contact geometry exceeds 100,000 vertices. Use a simpler mesh.");
                    if(!byBone.TryGetValue(name,out var list))byBone.Add(name,list=new());
                    foreach(var i in face)list.Add(points[i]);total+=3;
                }
                else skipped++;
                face.Clear();
            }
            if(face.Count>0)throw new FormatException("Unterminated prop polygon.");
            foreach(var pair in byBone)result.Add(new(){Bone=pair.Key,Source="FBX rigid triangle surface: "+geometry.Name,
                Vertices=pair.Value.Select(MotionDocument.A).ToArray(),Triangles=Enumerable.Range(0,pair.Value.Count).ToArray()});
        }
        return new(result,skipped);
    }
    static Matrix4x4 ReadMatrix(FbxNode? node)
    {
        var a=node?.AsDoubleArray(0);if(a is null||a.Length!=16||a.Any(x=>!double.IsFinite(x)))throw new FormatException("Missing or invalid prop skin bind matrix.");
        return new((float)a[0],(float)a[1],(float)a[2],(float)a[3],(float)a[4],(float)a[5],(float)a[6],(float)a[7],
            (float)a[8],(float)a[9],(float)a[10],(float)a[11],(float)a[12],(float)a[13],(float)a[14],(float)a[15]);
    }
}
