#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HumanoidMocap.Core.Target;

/// <summary>
/// Base of the minimal KV3 value model used for vmdl files: <see cref="KvObject"/>,
/// <see cref="KvArray"/>, <see cref="KvString"/>, <see cref="KvLong"/>, <see cref="KvDouble"/>,
/// <see cref="KvBool"/>, <see cref="KvNull"/>. Integers and doubles are distinct kinds so the
/// writer can preserve the shipped <c>1</c>-vs-<c>1.0</c> style.
/// </summary>
public abstract class KvValue
{
    /// <summary>Structural (semantic) equality over whole trees: same kinds, same object
    /// key order, same array order, equal scalar values.</summary>
    public static bool DeepEquals(KvValue? a, KvValue? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;

        switch (a)
        {
            case KvObject oa when b is KvObject ob:
                if (oa.Count != ob.Count)
                    return false;
                for (var i = 0; i < oa.Count; i++)
                {
                    if (!string.Equals(oa.Keys[i], ob.Keys[i], StringComparison.Ordinal))
                        return false;
                    if (!DeepEquals(oa[oa.Keys[i]], ob[ob.Keys[i]]))
                        return false;
                }
                return true;
            case KvArray ra when b is KvArray rb:
                if (ra.Items.Count != rb.Items.Count)
                    return false;
                for (var i = 0; i < ra.Items.Count; i++)
                {
                    if (!DeepEquals(ra.Items[i], rb.Items[i]))
                        return false;
                }
                return true;
            case KvString sa when b is KvString sb:
                return string.Equals(sa.Value, sb.Value, StringComparison.Ordinal);
            case KvLong la when b is KvLong lb:
                return la.Value == lb.Value;
            case KvDouble da when b is KvDouble db:
                return da.Value.Equals(db.Value);
            case KvBool ba when b is KvBool bb:
                return ba.Value == bb.Value;
            case KvNull when b is KvNull:
                return true;
            default:
                return false;
        }
    }
}
