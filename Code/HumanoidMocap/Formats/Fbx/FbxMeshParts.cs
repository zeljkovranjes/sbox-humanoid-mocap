#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;

namespace HumanoidMocap.Formats.Fbx;

/// <summary>Names of mesh instances that ModelDoc can import independently.</summary>
public static class FbxMeshParts
{
    public static IReadOnlyList<string> ReadNames(byte[] data)
    {
        var root = FbxTokenizer.Parse(data);
        var objects = root.Child("Objects");
        var connections = root.Child("Connections");
        if (objects is null || connections is null)
            return Array.Empty<string>();

        var geometries = objects.Children
            .Where(n => n.Name == "Geometry" && n.Properties.Count > 0
                && n.Properties[0] is long or int)
            .Select(n => n.Prop<long>(0)).ToHashSet();
        var models = connections.ChildrenNamed("C")
            .Where(n => n.Properties.Count >= 3 && n.Properties[0] is "OO"
                && n.Properties[1] is long or int && n.Properties[2] is long or int
                && geometries.Contains(n.Prop<long>(1)))
            .Select(n => n.Prop<long>(2)).ToHashSet();
        return objects.Children
            .Where(n => n.Name == "Model" && n.Properties.Count >= 2
                && n.Properties[0] is long or int && n.Properties[1] is string
                && models.Contains(n.Prop<long>(0)))
            .Select(n => FbxNode.SplitName(n.Prop<string>(1)).Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
