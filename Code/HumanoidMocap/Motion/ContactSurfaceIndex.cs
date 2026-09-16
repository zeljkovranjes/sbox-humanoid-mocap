using System;
using System.Linq;
using System.Numerics;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Bounded nearest-triangle queries in a rigid object's local coordinates.</summary>
public sealed class ContactSurfaceIndex
{
    readonly Vector3[] _vertices;
    readonly int[] _indices,_order;
    readonly Node _root;
    sealed record Node(Vector3 Min,Vector3 Max,int Start,int Count,Node? Left=null,Node? Right=null);
    public ContactSurfaceIndex(PropSurface surface)
    {
        _vertices=surface.Vertices.Select(MotionDocument.V).ToArray();_indices=surface.Triangles;
        _order=Enumerable.Range(0,_indices.Length/3).ToArray();_root=Build(0,_order.Length);
    }
    Node Build(int start,int count)
    {
        var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
        for(var i=start;i<start+count;i++)for(var j=0;j<3;j++){var p=_vertices[_indices[_order[i]*3+j]];min=Vector3.Min(min,p);max=Vector3.Max(max,p);}
        if(count<=8)return new(min,max,start,count);
        var extent=max-min;var axis=extent.X>=extent.Y&&extent.X>=extent.Z?0:extent.Y>=extent.Z?1:2;
        float Center(int t){var p=(_vertices[_indices[t*3]]+_vertices[_indices[t*3+1]]+_vertices[_indices[t*3+2]])/3;return axis==0?p.X:axis==1?p.Y:p.Z;}
        Array.Sort(_order,start,count,System.Collections.Generic.Comparer<int>.Create((a,b)=>Center(a).CompareTo(Center(b))));
        var half=count/2;return new(min,max,start,count,Build(start,half),Build(start+half,count-half));
    }
    public bool TryClosest(Vector3 query,float maximumDistance,ref long remainingChecks,out Vector3 closest)
    {
        var best=maximumDistance*maximumDistance;var point=Vector3.Zero;bool found=false;long budget=remainingChecks;
        void Search(Node node)
        {
            if(--budget<0)throw new InvalidOperationException("Contact search exceeded its work budget. Use a shorter capture or simpler prop mesh.");
            if(Vector3.DistanceSquared(query,Vector3.Clamp(query,node.Min,node.Max))>best)return;
            if(node.Left is not null){Search(node.Left);Search(node.Right!);return;}
            for(var i=node.Start;i<node.Start+node.Count;i++)
            {
                var t=_order[i]*3;var p=Closest(query,_vertices[_indices[t]],_vertices[_indices[t+1]],_vertices[_indices[t+2]]);
                var d=Vector3.DistanceSquared(query,p);if(d<=best){best=d;point=p;found=true;}
            }
        }
        if(_order.Length>0)Search(_root);remainingChecks=budget;closest=point;return found;
    }
    static Vector3 Closest(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
    {
        var ab=b-a;var ac=c-a;var ap=p-a;var d1=Vector3.Dot(ab,ap);var d2=Vector3.Dot(ac,ap);
        if(d1<=0&&d2<=0)return a;
        var bp=p-b;var d3=Vector3.Dot(ab,bp);var d4=Vector3.Dot(ac,bp);if(d3>=0&&d4<=d3)return b;
        var vc=d1*d4-d3*d2;if(vc<=0&&d1>=0&&d3<=0)return a+ab*(d1/(d1-d3));
        var cp=p-c;var d5=Vector3.Dot(ab,cp);var d6=Vector3.Dot(ac,cp);if(d6>=0&&d5<=d6)return c;
        var vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0)return a+ac*(d2/(d2-d6));
        var va=d3*d6-d5*d4;if(va<=0&&d4-d3>=0&&d5-d6>=0)return b+(c-b)*((d4-d3)/((d4-d3)+(d5-d6)));
        var sum=va+vb+vc;if(Math.Abs(sum)<1e-20)return a;
        return a+ab*(vb/sum)+ac*(vc/sum);
    }
}
