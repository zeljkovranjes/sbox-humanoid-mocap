using System;
using HumanoidMocap.Editor.PhoneQr;

namespace HumanoidMocap.Editor;

/// <summary>Shared pixel-aligned QR geometry. Preserve a four-module quiet zone and keep
/// the center logo small relative to a high-error-correction symbol.</summary>
public static class PhoneQrPresentation
{
    public static QrCode Create(string url)=>QrCode.EncodeText(url,QrCode.Ecc.High);
    public static (int Unit,int Left,int Top,int LogoStart,int LogoModules) Measure(int modules,int width,int height)
    {
        var unit=Math.Max(1,(Math.Min(width,height)-24)/(modules+8));
        var left=(width-modules*unit)/2;var top=(height-modules*unit)/2;
        var logoModules=Math.Min(9,Math.Max(3,(modules/6)|1));
        return(unit,left,top,(modules-logoModules)/2,logoModules);
    }
}
