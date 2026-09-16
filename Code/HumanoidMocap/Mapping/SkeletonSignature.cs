#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using SkeletonModel = HumanoidMocap.Skeleton.Skeleton;

namespace HumanoidMocap.Mapping;

/// <summary>
/// Stable identity hash of a skeleton's naming + hierarchy, used to key user preset
/// profiles (<c>Assets/humanoid_mocap/profiles/user/&lt;signature&gt;.json</c>): the
/// same rig is recognized instantly on re-import, any bone rename or reparent produces a
/// different signature.
/// </summary>
/// <remarks>
/// The signature is order-sensitive but deterministic: SHA-256 (lower-case hex) of the
/// pre-order traversal of <c>"name|parentName;"</c> pairs (roots first, children in stored
/// order, empty parent name for roots). Rest transforms intentionally do not contribute —
/// a re-export with the same armature but slightly different bind values should still hit
/// the saved preset.
/// </remarks>
public static class SkeletonSignature
{
    /// <summary>Computes the signature (64 hex chars) of a skeleton.</summary>
    public static string Compute(SkeletonModel skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);

        var childrenByParent = new List<int>[skeleton.Count];
        var roots = new List<int>();
        for (var i = 0; i < skeleton.Count; i++)
        {
            childrenByParent[i] = new List<int>();
            var parent = skeleton[i].ParentIndex;
            if (parent < 0)
                roots.Add(i);
            else
                childrenByParent[parent].Add(i);
        }

        var builder = new StringBuilder(skeleton.Count * 24);
        var stack = new Stack<int>();
        for (var r = roots.Count - 1; r >= 0; r--)
            stack.Push(roots[r]);
        while (stack.Count > 0)
        {
            var index = stack.Pop();
            var bone = skeleton[index];
            builder.Append(bone.Name)
                .Append('|')
                .Append(bone.ParentIndex < 0 ? string.Empty : skeleton[bone.ParentIndex].Name)
                .Append(';');
            var children = childrenByParent[index];
            for (var c = children.Count - 1; c >= 0; c--)
                stack.Push(children[c]);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
