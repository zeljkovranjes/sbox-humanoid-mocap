#nullable enable annotations

using System;
using System.Linq;

namespace HumanoidMocap.Formats.Fbx;

using Vector3 = System.Numerics.Vector3;

/// <summary>Authored FBX colors, independent of material and texture names.</summary>
public static class FbxMaterialColor
{
    public static Vector3? Read(FbxNode material)
    {
        var properties = material.Child("Properties70")?.Children;
        var color = properties?.FirstOrDefault(p => p.Properties.FirstOrDefault() is "DiffuseColor")
            ?? properties?.FirstOrDefault(p => p.Properties.FirstOrDefault() is "Diffuse");
        if (color is null || color.Properties.Count < 7)
            return null;
        var value = new Vector3(color.Prop<float>(4), color.Prop<float>(5), color.Prop<float>(6));
        // The legacy Diffuse property already includes DiffuseFactor.
        var factor = properties?.FirstOrDefault(p => p.Properties.FirstOrDefault() is "DiffuseFactor");
        if (color.Properties[0] is "DiffuseColor" && factor?.Properties.Count >= 5)
            value *= factor.Prop<float>(4);
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? Vector3.Clamp(value, Vector3.Zero, Vector3.One) : null;
    }

    public static bool HasVertexColors(FbxNode geometry)
        => geometry.ChildrenNamed("LayerElementColor").Any(layer =>
            layer.Child("Colors")?.Properties.FirstOrDefault() is double[] colors
            && colors.Where((_, i) => i % 4 != 3).Any(c => Math.Abs(c - 1) > 1e-6));
}
