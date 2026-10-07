namespace HumanoidMocap.Worker;

/// <summary>Finds and undoes stretches where the 2D pose network labelled the performer's left side as right. When the face
/// disappears (a head snapped back so only the throat faces the camera) the network can read the body as seen from behind and
/// swap every left and right joint; the body network then turned the whole performer around for those frames (tiki.mov: 180
/// degrees for a quarter of a second). A real turn narrows the shoulders through side-on; a swap reverses their full width
/// between two neighbouring frames. A stretch is taken as swapped when it opens and closes with such a reversal (the shoulders
/// at least <see cref="FullWidth"/> of their usual width on both sides of it, the hips agreeing wherever they are seen) and
/// lasts at most <see cref="MaximumSeconds"/>. Checked on 21 clips: only tiki's stretch matched; a single unpaired reversal
/// (gymnasts) is left alone.</summary>
public static class LeftRightSwaps
{
    public const float FullWidth=.6f,MaximumSeconds=2f,ShoulderScore=.5f,HipScore=.3f;
    /// <summary>COCO joints that come in left/right pairs: eyes, ears, shoulders, elbows, wrists, hips, knees, ankles.</summary>
    static readonly (int Left,int Right)[] Pairs={(1,2),(3,4),(5,6),(7,8),(9,10),(11,12),(13,14),(15,16)};

    /// <summary>The swapped stretches (first and last frame) in <paramref name="observations"/> (COCO x, y, score per joint).</summary>
    public static List<(int Start,int End)> Find(IReadOnlyList<float[]?> observations,IReadOnlyList<double> times)
    {
        var count=observations.Count;var result=new List<(int,int)>();if(count<3)return result;
        float? Width(float[]? o,int a,int b,float score)=>o is {Length:>=51}&&o[a*3+2]>=score&&o[b*3+2]>=score?o[a*3]-o[b*3]:null;
        var shoulders=observations.Select(o=>Width(o,5,6,ShoulderScore)).ToArray();var hips=observations.Select(o=>Width(o,11,12,HipScore)).ToArray();
        float Usual(float?[] widths){var seen=widths.Where(w=>w is not null).Select(w=>MathF.Abs(w!.Value)).OrderBy(w=>w).ToArray();return seen.Length==0?0:seen[seen.Length/2];}
        float shoulderUsual=Usual(shoulders),hipUsual=Usual(hips);if(shoulderUsual<=0)return result;
        bool Reversed(float? a,float? b,float usual)=>a is float x&&b is float y&&x*y<0&&MathF.Min(MathF.Abs(x),MathF.Abs(y))>=FullWidth*usual;
        var jumps=new List<int>();
        for(var t=1;t<count;t++)
        {
            if(!Reversed(shoulders[t-1],shoulders[t],shoulderUsual))continue;
            // The hips must agree wherever both frames see them.
            if(hips[t-1] is not null&&hips[t] is not null&&!Reversed(hips[t-1],hips[t],hipUsual))continue;
            jumps.Add(t);
        }
        for(var k=0;k+1<jumps.Count;k++)
        {
            int start=jumps[k],end=jumps[k+1]-1;
            if(times[end]-times[start]<=MaximumSeconds){result.Add((start,end));k++;}
        }
        return result;
    }

    /// <summary>Swaps every left/right joint pair of one frame's observations in place.</summary>
    public static void Swap(float[] o)
    {
        foreach(var (left,right) in Pairs)for(var c=0;c<3;c++)(o[left*3+c],o[right*3+c])=(o[right*3+c],o[left*3+c]);
    }
}
