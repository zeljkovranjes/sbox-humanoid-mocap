using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HumanoidMocap.Motion;
using Vector3 = System.Numerics.Vector3;

/// <summary>Zero-phase smoothing of motion tracks, ported from MocapSmooth's reference
/// (github.com/Dylanyz/MocapSmooth, Tools/smooth_core.py, Apache-2.0), which reproduces Rokoko Studio's
/// filter: an order-2 Butterworth low-pass run forward and backward (filtfilt, odd reflection padding, settled
/// initial state), so there is no lag and no phase shift. A Gaussian alternative never overshoots.
/// Quaternion tracks are made sign-continuous, filtered per component and renormalised.</summary>
public static class MocapSmooth
{
    public enum Shape{Butterworth,Gaussian}
    /// <summary>Rokoko's strength slider (0.5 to 10, higher is smoother) to the cutoff in hertz.</summary>
    public static double CutoffFromStrength(double strength)
    {
        if(!(strength>=.5&&strength<=10))throw new ArgumentOutOfRangeException(nameof(strength),"Strength runs from 0.5 to 10.");
        return 10.5-strength;
    }

    /// <summary>Digital Butterworth low-pass by bilinear transform of the analog prototype; a[0] is one.</summary>
    public static (double[] B,double[] A) ButterLowpass(int order,double cutoff,double sampleRate)
    {
        if(order<1)throw new ArgumentOutOfRangeException(nameof(order));
        var nyquist=sampleRate/2;if(!(cutoff>0&&cutoff<nyquist))throw new ArgumentOutOfRangeException(nameof(cutoff),$"Cutoff must lie in (0, {nyquist}).");
        var warped=2*sampleRate*Math.Tan(Math.PI*cutoff/sampleRate);var fs2=2*sampleRate;
        var polesZ=new Complex[order];Complex denominator=Complex.One;
        for(var k=0;k<order;k++)
        {
            var pole=warped*Complex.Exp(Complex.ImaginaryOne*Math.PI*(2*k+order+1)/(2*order));
            polesZ[k]=(fs2+pole)/(fs2-pole);denominator*=fs2-pole;
        }
        var gain=Math.Pow(warped,order)*(Complex.One/denominator).Real;
        var b=Poly(Enumerable.Repeat(new Complex(-1,0),order).ToArray()).Select(c=>gain*c.Real).ToArray();
        var a=Poly(polesZ).Select(c=>c.Real).ToArray();
        return (b.Select(v=>v/a[0]).ToArray(),a.Select(v=>v/a[0]).ToArray());
    }
    static Complex[] Poly(Complex[] roots)
    {
        var c=new Complex[roots.Length+1];c[0]=Complex.One;
        for(var i=0;i<roots.Length;i++)for(var j=i+1;j>=1;j--)c[j]-=roots[i]*c[j-1];
        return c;
    }
    /// <summary>Steady-state filter state for a unit step, so the filter starts settled.</summary>
    static double[] InitialState(double[] b,double[] a)
    {
        var n=a.Length-1;var m=new double[n,n];var rhs=new double[n];
        for(var i=0;i<n;i++){for(var j=0;j<n;j++)m[i,j]=(i==j?1:0)-(j==0?-a[i+1]:(j==i+1?1:0));rhs[i]=b[i+1]-a[i+1]*b[0];}
        return Solve(m,rhs);
    }
    static double[] Solve(double[,] m,double[] rhs)
    {
        var n=rhs.Length;var x=rhs.ToArray();var a=new double[n,n];for(var i=0;i<n;i++)for(var j=0;j<n;j++)a[i,j]=m[i,j];
        for(var c=0;c<n;c++)
        {
            var pivot=c;for(var r=c+1;r<n;r++)if(Math.Abs(a[r,c])>Math.Abs(a[pivot,c]))pivot=r;
            if(pivot!=c){for(var k=0;k<n;k++)(a[c,k],a[pivot,k])=(a[pivot,k],a[c,k]);(x[c],x[pivot])=(x[pivot],x[c]);}
            for(var r=0;r<n;r++){if(r==c)continue;var f=a[r,c]/a[c,c];for(var k=c;k<n;k++)a[r,k]-=f*a[c,k];x[r]-=f*x[c];}
        }
        for(var i=0;i<n;i++)x[i]/=a[i,i];return x;
    }
    static double[] Filter(double[] b,double[] a,double[] x,double[] zi,double scale)
    {
        var n=b.Length;var d=zi.Select(v=>v*scale).ToArray();var y=new double[x.Length];
        for(var i=0;i<x.Length;i++)
        {
            var yi=b[0]*x[i]+d[0];
            for(var j=0;j<n-2;j++)d[j]=b[j+1]*x[i]+d[j+1]-a[j+1]*yi;
            d[n-2]=b[n-1]*x[i]-a[n-1]*yi;y[i]=yi;
        }
        return y;
    }
    /// <summary>Forward-backward filtering with odd reflection padding; zero phase, squared magnitude.</summary>
    public static double[] FiltFilt(double[] b,double[] a,double[] x)
    {
        if(x.Length<4)return x.ToArray();
        var edge=Math.Min(3*Math.Max(a.Length,b.Length),x.Length-1);
        var ext=new double[x.Length+2*edge];
        for(var i=0;i<edge;i++)ext[i]=2*x[0]-x[edge-i];
        Array.Copy(x,0,ext,edge,x.Length);
        for(var i=0;i<edge;i++)ext[edge+x.Length+i]=2*x[^1]-x[x.Length-2-i];
        var zi=InitialState(b,a);
        var forward=Filter(b,a,ext,zi,ext[0]);Array.Reverse(forward);
        var backward=Filter(b,a,forward,zi,forward[0]);Array.Reverse(backward);
        return backward.Skip(edge).Take(x.Length).ToArray();
    }
    /// <summary>Gaussian with the -3 dB point at the cutoff, edges clamped. Never overshoots.</summary>
    public static double[] Gaussian(double[] x,double cutoff,double sampleRate)
    {
        var sigma=.1325*sampleRate/cutoff;var radius=Math.Max(1,(int)Math.Ceiling(4*sigma));
        var kernel=Enumerable.Range(-radius,2*radius+1).Select(i=>Math.Exp(-i*i/(2*sigma*sigma))).ToArray();var total=kernel.Sum();
        var y=new double[x.Length];
        for(var i=0;i<x.Length;i++){double s=0;for(var k=-radius;k<=radius;k++)s+=kernel[k+radius]*x[Math.Clamp(i+k,0,x.Length-1)];y[i]=s/total;}
        return y;
    }
    static double[] Smooth(double[] x,double cutoff,double sampleRate,Shape shape,int order)
    {
        if(shape==Shape.Gaussian)return Gaussian(x,cutoff,sampleRate);
        var (b,a)=ButterLowpass(order,cutoff,sampleRate);return FiltFilt(b,a,x);
    }
    /// <summary>Replaces single-frame spikes: a sample far from both neighbours while the neighbours agree with each
    /// other. A low-pass filter would spread such a flip over several frames instead of removing it. Fast real motion
    /// moves the neighbours apart too, so it is never taken for a spike. Returns how many samples were replaced.</summary>
    public static int RemoveSpikes(Quaternion[] q,float minimumDegrees=15,float ratio=3)
    {
        static float Angle(Quaternion a,Quaternion b)=>2*MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(a,b)),0,1))*180/MathF.PI;
        var replaced=0;
        for(var i=1;i+1<q.Length;i++)
        {
            var before=Angle(q[i-1],q[i]);var after=Angle(q[i],q[i+1]);var across=Angle(q[i-1],q[i+1]);
            if(Math.Min(before,after)>Math.Max(minimumDegrees,ratio*across)){q[i]=Quaternion.Slerp(q[i-1],q[i+1],.5f);replaced++;}
        }
        // The first and last samples have one neighbour: a jump far larger than the next step is a spike too,
        // and the filter's edge padding would otherwise keep it exactly.
        if(q.Length>=3&&Angle(q[0],q[1])>Math.Max(minimumDegrees,ratio*Angle(q[1],q[2]))){q[0]=q[1];replaced++;}
        var n=q.Length;if(n>=3&&Angle(q[n-1],q[n-2])>Math.Max(minimumDegrees,ratio*Angle(q[n-2],q[n-3]))){q[n-1]=q[n-2];replaced++;}
        return replaced;
    }
    /// <summary>The same for positions; <paramref name="minimumDistance"/> in the track's units.</summary>
    public static int RemoveSpikes(Vector3[] p,float minimumDistance,float ratio=3)
    {
        var replaced=0;
        for(var i=1;i+1<p.Length;i++)
        {
            var before=Vector3.Distance(p[i-1],p[i]);var after=Vector3.Distance(p[i],p[i+1]);var across=Vector3.Distance(p[i-1],p[i+1]);
            if(Math.Min(before,after)>Math.Max(minimumDistance,ratio*across)){p[i]=(p[i-1]+p[i+1])*.5f;replaced++;}
        }
        if(p.Length>=3&&Vector3.Distance(p[0],p[1])>Math.Max(minimumDistance,ratio*Vector3.Distance(p[1],p[2]))){p[0]=p[1];replaced++;}
        var n=p.Length;if(n>=3&&Vector3.Distance(p[n-1],p[n-2])>Math.Max(minimumDistance,ratio*Vector3.Distance(p[n-2],p[n-3]))){p[n-1]=p[n-2];replaced++;}
        return replaced;
    }
    /// <summary>Smooths a quaternion track (xyzw). Signs are made continuous first, since q and -q are the same rotation.</summary>
    public static Quaternion[] Quaternions(IReadOnlyList<Quaternion> track,double cutoff,double sampleRate,Shape shape=Shape.Butterworth,int order=2)
    {
        var q=track.ToArray();for(var i=1;i<q.Length;i++)if(Quaternion.Dot(q[i],q[i-1])<0)q[i]=-q[i];
        var channels=new double[4][];for(var c=0;c<4;c++)channels[c]=Smooth(q.Select(v=>(double)(c switch{0=>v.X,1=>v.Y,2=>v.Z,_=>v.W})).ToArray(),cutoff,sampleRate,shape,order);
        var result=new Quaternion[q.Length];
        for(var i=0;i<q.Length;i++){var v=new Quaternion((float)channels[0][i],(float)channels[1][i],(float)channels[2][i],(float)channels[3][i]);result[i]=v.LengthSquared()>0?Quaternion.Normalize(v):q[i];}
        return result;
    }
    /// <summary>Smooths a position track. Smoothing global motion can add foot sliding; keep root cutoffs conservative.</summary>
    public static Vector3[] Positions(IReadOnlyList<Vector3> track,double cutoff,double sampleRate,Shape shape=Shape.Butterworth,int order=2)
    {
        var channels=new double[3][];for(var c=0;c<3;c++)channels[c]=Smooth(track.Select(v=>(double)(c==0?v.X:c==1?v.Y:v.Z)).ToArray(),cutoff,sampleRate,shape,order);
        return Enumerable.Range(0,track.Count).Select(i=>new Vector3((float)channels[0][i],(float)channels[1][i],(float)channels[2][i])).ToArray();
    }
}
