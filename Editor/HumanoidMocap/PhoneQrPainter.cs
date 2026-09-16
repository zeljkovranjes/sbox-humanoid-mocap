using Editor;
using Sandbox;
using HumanoidMocap.Editor.PhoneQr;

namespace HumanoidMocap.Editor;

internal static class PhoneQrPainter
{
    // Official s&box mark supplied with the editor, also used by its own window chrome.
    static readonly Pixmap Logo=Pixmap.FromFile("logo_rounded.png");
    public static void Draw(QrCode code,int width,int height)
    {
        Paint.ClearPen();Paint.SetBrush(Theme.WindowBackground);Paint.DrawRect(new Rect(0,0,width,height));
        if(code is null)return;
        var (unit,left,top,logoStart,logoModules)=PhoneQrPresentation.Measure(code.Size,width,height);
        Paint.SetBrush(Color.White);Paint.DrawRect(new Rect(left-4*unit,top-4*unit,(code.Size+8)*unit,(code.Size+8)*unit),Theme.ControlRadius);
        Paint.SetBrush(Color.Black);
        for(var y=0;y<code.Size;y++)for(var x=0;x<code.Size;x++)if(code.GetModule(x,y))Paint.DrawRect(new Rect(left+x*unit,top+y*unit,unit,unit));
        if(Logo is null)return;
        var xLogo=left+logoStart*unit;var yLogo=top+logoStart*unit;var size=logoModules*unit;
        Paint.SetBrush(Color.White);Paint.DrawRect(new Rect(xLogo-unit,yLogo-unit,size+2*unit,size+2*unit));
        Paint.Draw(new Rect(xLogo,yLogo,size,size),Logo);
    }
}
