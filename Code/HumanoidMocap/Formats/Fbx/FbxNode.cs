#nullable enable annotations

namespace HumanoidMocap.Formats.Fbx;

/// <summary>
/// A single node of an FBX document tree (binary or ASCII): a name, a flat list of
/// typed properties, and nested child nodes.
///
/// Property values are stored as the closest CLR type to what the file contained:
/// <list type="bullet">
///   <item><c>short</c> ('Y'), <c>bool</c> ('C'), <c>int</c> ('I'), <c>float</c> ('F'),
///         <c>double</c> ('D'), <c>long</c> ('L')</item>
///   <item><c>float[]</c> ('f'), <c>double[]</c> ('d'), <c>long[]</c> ('l'),
///         <c>int[]</c> ('i'), <c>bool[]</c>-as-<c>byte[]</c> ('b')</item>
///   <item><c>string</c> ('S' — kept raw, may contain the <c>\x00\x01</c> name/class
///         separator; see <see cref="SplitName"/>), <c>byte[]</c> ('R')</item>
/// </list>
/// ASCII files store numbers only as <c>long</c> / <c>double</c> (and arrays as
/// <c>long[]</c> / <c>double[]</c>), so the typed accessors below convert tolerantly.
/// </summary>
public sealed class FbxNode
{
    public string Name { get; }
    public List<object> Properties { get; } = new();
    public List<FbxNode> Children { get; } = new();

    public FbxNode(string name) => Name = name;

    /// <summary>First child with the given name, or null.</summary>
    public FbxNode? Child(string name)
    {
        foreach (var c in Children)
            if (c.Name == name)
                return c;
        return null;
    }

    /// <summary>All children with the given name, in document order.</summary>
    public IEnumerable<FbxNode> ChildrenNamed(string name)
    {
        foreach (var c in Children)
            if (c.Name == name)
                yield return c;
    }

    /// <summary>
    /// Property <paramref name="i"/> converted to <typeparamref name="T"/>.
    /// Numeric scalars convert tolerantly across widths (e.g. an 'I' i32 read as long);
    /// anything else must match the stored type exactly.
    /// </summary>
    public T Prop<T>(int i)
    {
        object v = RawProp(i);
        if (v is T t)
            return t;

        var target = typeof(T);
        // s&box whitelist: Type.IsPrimitive is banned; enumerate the convertible targets.
        if (v is IConvertible && (ConvertTargets.Contains(target) || target == typeof(string)))
        {
            try
            {
                return (T)Convert.ChangeType(v, target, System.Globalization.CultureInfo.InvariantCulture);
            }
            // ArithmeticException covers OverflowException, which is not s&box-whitelisted
            catch (Exception ex) when (ex is InvalidCastException or ArithmeticException or FormatException)
            {
                throw new FormatException(
                    $"FBX node '{Name}': property {i} is {v.GetType().Name}, not convertible to {target.Name}.", ex);
            }
        }

        throw new FormatException(
            $"FBX node '{Name}': property {i} is {v.GetType().Name}, expected {target.Name}.");
    }

    /// <summary>Property <paramref name="i"/> as a double array (converts f/l/i/b arrays).</summary>
    public double[] AsDoubleArray(int i) => RawProp(i) switch
    {
        double[] d => d,
        float[] f => Array.ConvertAll(f, x => (double)x),
        long[] l => Array.ConvertAll(l, x => (double)x),
        int[] n => Array.ConvertAll(n, x => (double)x),
        byte[] b => Array.ConvertAll(b, x => (double)x),
        bool[] o => Array.ConvertAll(o, x => x ? 1.0 : 0.0),
        var v => throw TypeError(i, v, "double[]"),
    };

    /// <summary>Property <paramref name="i"/> as a float array (converts d/l/i/b arrays).</summary>
    public float[] AsFloatArray(int i) => RawProp(i) switch
    {
        float[] f => f,
        double[] d => Array.ConvertAll(d, x => (float)x),
        long[] l => Array.ConvertAll(l, x => (float)x),
        int[] n => Array.ConvertAll(n, x => (float)x),
        byte[] b => Array.ConvertAll(b, x => (float)x),
        bool[] o => Array.ConvertAll(o, x => x ? 1f : 0f),
        var v => throw TypeError(i, v, "float[]"),
    };

    /// <summary>Property <paramref name="i"/> as a long array (converts i/b; d/f if integral).</summary>
    public long[] AsLongArray(int i) => RawProp(i) switch
    {
        long[] l => l,
        int[] n => Array.ConvertAll(n, x => (long)x),
        byte[] b => Array.ConvertAll(b, x => (long)x),
        bool[] o => Array.ConvertAll(o, x => x ? 1L : 0L),
        double[] d => Array.ConvertAll(d, x => checked((long)x)),
        float[] f => Array.ConvertAll(f, x => checked((long)x)),
        var v => throw TypeError(i, v, "long[]"),
    };

    /// <summary>Property <paramref name="i"/> as an int array (converts b; l/d/f narrowing-checked).</summary>
    public int[] AsIntArray(int i) => RawProp(i) switch
    {
        int[] n => n,
        long[] l => Array.ConvertAll(l, x => checked((int)x)),
        byte[] b => Array.ConvertAll(b, x => (int)x),
        bool[] o => Array.ConvertAll(o, x => x ? 1 : 0),
        double[] d => Array.ConvertAll(d, x => checked((int)x)),
        float[] f => Array.ConvertAll(f, x => checked((int)x)),
        var v => throw TypeError(i, v, "int[]"),
    };

    /// <summary>Property <paramref name="i"/> as raw bytes ('R' blobs or 'b' bool arrays).</summary>
    public byte[] AsByteArray(int i) => RawProp(i) switch
    {
        byte[] b => b,
        var v => throw TypeError(i, v, "byte[]"),
    };

    /// <summary>Property <paramref name="i"/> as a string (raw 'S' content, separators intact).</summary>
    public string AsString(int i) => RawProp(i) switch
    {
        string s => s,
        var v => throw TypeError(i, v, "string"),
    };

    /// <summary>
    /// Splits an FBX object name into (name, class).
    /// Binary files store <c>"Name\x00\x01Class"</c> (e.g. <c>"mixamorig:Hips\x00\x01Model"</c>);
    /// ASCII files store <c>"Class::Name"</c> (e.g. <c>"Model::pelvis"</c>).
    /// A plain string with neither separator yields (name, "").
    /// </summary>
    public static (string Name, string Class) SplitName(string raw)
    {
        int bin = raw.IndexOf("\0\x01", StringComparison.Ordinal);
        if (bin >= 0)
            return (raw[..bin], raw[(bin + 2)..]);

        int ascii = raw.IndexOf("::", StringComparison.Ordinal);
        if (ascii >= 0)
            return (raw[(ascii + 2)..], raw[..ascii]);

        return (raw, "");
    }

    /// <summary>Primitive scalar types <see cref="Prop{T}"/> converts to (whitelist-safe IsPrimitive substitute).</summary>
    private static readonly HashSet<Type> ConvertTargets = new()
    {
        typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
        typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(float), typeof(double), typeof(char),
    };

    private object RawProp(int i)
    {
        if (i < 0 || i >= Properties.Count)
            throw new FormatException(
                $"FBX node '{Name}': property index {i} out of range (has {Properties.Count}).");
        return Properties[i];
    }

    private FormatException TypeError(int i, object v, string wanted) =>
        new($"FBX node '{Name}': property {i} is {v.GetType().Name}, expected {wanted}.");
}
