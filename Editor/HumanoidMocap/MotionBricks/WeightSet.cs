#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace HumanoidMocap.MotionBricks;

/// <summary>
/// The tensors of one MotionBricks component (<c>pose</c>, <c>root</c>, <c>vq-decoder</c> or
/// <c>support</c>), loaded from its GGUF file and checked against the bundle identity the port
/// was written for. Lookups by name fail with a clear message instead of a null.
/// </summary>
public sealed class WeightSet
{
    public const string Architecture = "motionbricks";
    public const string Skeleton = "g1skel34";
    public const string UpstreamRevision = "a0732b642c0333077e127a2f56ab0014c196bca4";

    private readonly Dictionary<string, Tensor> _floats = new( StringComparer.Ordinal );
    private readonly Dictionary<string, int[]> _ints = new( StringComparer.Ordinal );

    public string Component { get; }
    public IReadOnlyDictionary<string, object> Metadata { get; }
    public long ParameterCount { get; }

    private WeightSet( string component, IReadOnlyDictionary<string, object> metadata, long parameters )
    {
        Component = component;
        Metadata = metadata;
        ParameterCount = parameters;
    }

    /// <summary>
    /// Loads and validates a component. <paramref name="expectedTensors"/> and
    /// <paramref name="expectedParameters"/> come from the pinned bundle manifest.
    /// </summary>
    public static WeightSet Load( string path, string component, int expectedTensors, long expectedParameters, CancellationToken cancel = default )
    {
        var file = GgufFile.Open( path );
        Expect( file.GetString( "general.architecture" ) == Architecture, path, "is not a MotionBricks model" );
        Expect( file.GetUInt( "motionbricks.format_version" ) == 1, path, "uses an unsupported MotionBricks format version" );
        Expect( file.GetString( "motionbricks.component" ) == component, path, $"is not the '{component}' component" );
        Expect( file.GetString( "motionbricks.skeleton" ) == Skeleton, path, "was made for a different skeleton" );
        Expect( file.GetString( "motionbricks.upstream_revision" ) == UpstreamRevision, path, "is from a different MotionBricks release" );
        Expect( file.Tensors.Count == expectedTensors, path, $"has {file.Tensors.Count} tensors (expected {expectedTensors})" );
        var parameters = (long)(file.GetUInt( "motionbricks.parameter_count" ) ?? 0);
        Expect( parameters == expectedParameters, path, "has an unexpected parameter count" );

        var set = new WeightSet( component, file.Metadata, parameters );
        using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20 );
        foreach ( var info in file.Tensors.Values.OrderBy( t => t.DataOffset ) )
        {
            cancel.ThrowIfCancellationRequested();
            var shape = info.Shape.Select( d => (int)d ).ToArray();
            if ( info.Type == GgufTensorType.I32 )
                set._ints[info.Name] = file.ReadInts( stream, info );
            else
                set._floats[info.Name] = new Tensor( info.Name, file.ReadFloats( stream, info ), shape );
        }
        foreach ( var value in set._floats.Values )
            if ( value.Data.Any( v => !float.IsFinite( v ) ) )
                throw new InvalidDataException( $"{System.IO.Path.GetFileName( path )} contains invalid numbers in '{value.Name}'." );
        return set;
    }

    private static void Expect( bool condition, string path, string problem )
    {
        if ( !condition )
            throw new InvalidDataException( $"{System.IO.Path.GetFileName( path )} {problem}." );
    }

    /// <summary>The named float tensor (following the converter's 64-byte name compaction).</summary>
    public Tensor this[string name]
    {
        get
        {
            if ( _floats.TryGetValue( name, out var tensor ) )
                return tensor;
            if ( name.Length >= 64 && _floats.TryGetValue( name.Replace( "self_attn.", "attn.", StringComparison.Ordinal ), out tensor ) )
                return tensor;
            throw new InvalidDataException( $"The {Component} weights have no tensor '{name}'." );
        }
    }

    public int[] Ints( string name ) => _ints.TryGetValue( name, out var values )
        ? values
        : throw new InvalidDataException( $"The {Component} weights have no tensor '{name}'." );

    /// <summary>Checks a tensor's GGML shape (a guard against mismatched weights).</summary>
    public Tensor Shaped( string name, params int[] shape )
    {
        var tensor = this[name];
        var actual = tensor.Shape.Concat( Enumerable.Repeat( 1, Math.Max( 0, shape.Length - tensor.Shape.Length ) ) ).Take( shape.Length );
        if ( !actual.SequenceEqual( shape ) || tensor.Shape.Skip( shape.Length ).Any( d => d != 1 ) )
            throw new InvalidDataException( $"Tensor '{name}' has shape [{string.Join( ",", tensor.Shape )}], expected [{string.Join( ",", shape )}]." );
        return tensor;
    }
}
