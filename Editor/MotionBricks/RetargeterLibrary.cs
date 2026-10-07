#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;

using System.Collections;
using System.Numerics;
using System.Reflection;
using HumanoidMocap.Core.Mapping;

namespace HumanoidMocap.EditorTools.MotionBricks;

using Vector3 = System.Numerics.Vector3;

/// <summary>
/// The installed Humanoid Retargeter library, reached by reflection so Humanoid Mocap compiles and
/// explains itself when the library is missing or too old (the same pattern the Weapon Importer
/// uses). Only a small, stable surface is used: <c>Skeleton.Create</c>,
/// <c>Retargeter.ResolveMapping</c>, <c>TargetRig.FromSkeleton</c> and
/// <c>RuntimePoseRetargeter</c>. Assemblies are scanned newest first so hot-reloaded copies win.
/// </summary>
public sealed class RetargeterLibrary
{
    public const string InstallUrl = "https://github.com/zeljkovranjes/humanoid-retargeter";

    private readonly Type _xform;
    private readonly Type _boneDefinition;
    private readonly Type _skeleton;
    private readonly Type _retargeter;
    private readonly Type _mappingResult;
    private readonly Type _mappingSource;
    private readonly Type _boneRole;
    private readonly Type _targetRig;
    private readonly Type _runtime;
    private readonly FieldInfo _pos;
    private readonly FieldInfo _rot;

    public Assembly Assembly { get; }
    public string Version => Assembly.GetName().Version?.ToString() ?? "unknown";

    private RetargeterLibrary( Assembly assembly, Type xform, Type boneDefinition, Type skeleton, Type retargeter, Type mappingResult,
        Type mappingSource, Type boneRole, Type targetRig, Type runtime )
    {
        Assembly = assembly;
        _xform = xform;
        _boneDefinition = boneDefinition;
        _skeleton = skeleton;
        _retargeter = retargeter;
        _mappingResult = mappingResult;
        _mappingSource = mappingSource;
        _boneRole = boneRole;
        _targetRig = targetRig;
        _runtime = runtime;
        _pos = xform.GetField( "Pos" ) ?? throw new MissingFieldException( "XForm.Pos" );
        _rot = xform.GetField( "Rot" ) ?? throw new MissingFieldException( "XForm.Rot" );
    }

    /// <summary>
    /// Finds the library among the loaded assemblies. Returns null with a user-facing
    /// <paramref name="problem"/> when it is not installed or lacks the runtime retargeting API.
    /// </summary>
    public static RetargeterLibrary? Find( out string problem )
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var sawRetargeter = false;
        for ( var i = assemblies.Length - 1; i >= 0; i-- )
        {
            var assembly = assemblies[i];
            if ( assembly.IsDynamic )
                continue;
            Type? Get( string name )
            {
                try
                {
                    return assembly.GetType( name, throwOnError: false );
                }
                catch ( Exception )
                {
                    return null;
                }
            }
            var retargeter = Get( "HumanoidRetargeter.Core.Retargeter" );
            if ( retargeter is null )
                continue;
            sawRetargeter = true;
            var runtime = Get( "HumanoidRetargeter.Core.Solve.RuntimePoseRetargeter" );
            var xform = Get( "HumanoidRetargeter.Core.Maths.XForm" );
            var definition = Get( "HumanoidRetargeter.Core.Skeleton.BoneDefinition" );
            var skeleton = Get( "HumanoidRetargeter.Core.Skeleton.Skeleton" );
            var mapping = Get( "HumanoidRetargeter.Core.Mapping.MappingResult" );
            var source = Get( "HumanoidRetargeter.Core.Mapping.MappingSource" );
            var role = Get( "HumanoidRetargeter.Core.Mapping.BoneRole" );
            var rig = Get( "HumanoidRetargeter.Core.Target.TargetRig" );
            if ( runtime is null || xform is null || definition is null || skeleton is null || mapping is null || source is null || role is null || rig is null )
                continue;
            try
            {
                problem = "";
                return new RetargeterLibrary( assembly, xform, definition, skeleton, retargeter, mapping, source, role, rig, runtime );
            }
            catch ( Exception e ) when ( e is MissingMemberException )
            {
                continue;
            }
        }
        problem = sawRetargeter
            ? "The installed Humanoid Retargeter is too old for MotionBricks. Update it to the latest version."
            : "MotionBricks needs the Humanoid Retargeter library. Add it to your project's libraries: " + InstallUrl;
        return null;
    }

    // ------------------------------------------------------------------ values

    internal object XForm( Vector3 position, Quaternion rotation ) => Activator.CreateInstance( _xform, position, rotation )!;

    internal (Vector3 Position, Quaternion Rotation) Read( object xform ) => ((Vector3)_pos.GetValue( xform )!, (Quaternion)_rot.GetValue( xform )!);

    internal Array NewXForms( int count ) => Array.CreateInstance( _xform, count );

    internal void Write( Array array, int index, Vector3 position, Quaternion rotation ) => array.SetValue( XForm( position, rotation ), index );

    // ------------------------------------------------------------------ skeletons and mappings

    public RetargetSkeleton CreateSkeleton( IReadOnlyList<RetargetBone> bones )
    {
        var definitions = Array.CreateInstance( _boneDefinition, bones.Count );
        for ( var i = 0; i < bones.Count; i++ )
        {
            var b = bones[i];
            definitions.SetValue( Activator.CreateInstance( _boneDefinition, b.Name, b.Parent, XForm( b.Position, b.Rotation ) ), i );
        }
        var create = _skeleton.GetMethod( "Create", BindingFlags.Public | BindingFlags.Static ) ?? throw new MissingMethodException( "Skeleton.Create" );
        var handle = Unwrap( () => create.Invoke( null, new object[] { definitions } ) )!;
        var count = (int)_skeleton.GetProperty( "Count" )!.GetValue( handle )!;
        if ( count != bones.Count )
            throw new InvalidOperationException( "The retargeter reordered the skeleton unexpectedly." );
        // Bones must keep the caller's order: Skeleton.Create only reorders when a child comes first.
        var names = (IEnumerable)_skeleton.GetProperty( "Bones" )!.GetValue( handle )!;
        var index = 0;
        foreach ( var bone in names )
        {
            var name = (string)bone.GetType().GetProperty( "Name" )!.GetValue( bone )!;
            if ( name != bones[index++].Name )
                throw new InvalidOperationException( "Bones must be listed parents first." );
        }
        return new RetargetSkeleton( handle, count );
    }

    /// <summary>Runs the retargeter's full detection cascade (shipped presets, then automatic mapping).</summary>
    public RetargetMapping ResolveMapping( RetargetSkeleton skeleton )
    {
        var resolve = _retargeter.GetMethods( BindingFlags.Public | BindingFlags.Static ).First( m => m.Name == "ResolveMapping" && m.GetParameters().Length >= 1 );
        var parameters = resolve.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = skeleton.Handle;
        for ( var i = 1; i < args.Length; i++ )
            args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        var tuple = Unwrap( () => resolve.Invoke( null, args ) )!;
        var map = tuple.GetType().GetField( "Item1" )!.GetValue( tuple )!;
        var report = tuple.GetType().GetField( "Item2" )!.GetValue( tuple );
        var roles = new Dictionary<BoneRole, int>();
        foreach ( DictionaryEntry entry in (IDictionary)_mappingResult.GetProperty( "RoleToBone" )!.GetValue( map )! )
            if ( Enum.TryParse<BoneRole>( entry.Key.ToString(), out var role ) )
                roles[role] = (int)entry.Value!;
        var confidence = (float)_mappingResult.GetProperty( "Confidence" )!.GetValue( map )!;
        var profile = (string)_mappingResult.GetProperty( "ProfileName" )!.GetValue( map )!;
        var review = report?.GetType().GetProperty( "NeedsUserDecision" )?.GetValue( report ) is true;
        return new RetargetMapping( roles, confidence, profile, review ) { Handle = map };
    }

    /// <summary>A hand-made mapping (used for the fixed G1 skeleton).</summary>
    public RetargetMapping CreateMapping( string profile, IReadOnlyDictionary<BoneRole, int> roles )
    {
        var manual = Enum.Parse( _mappingSource, "Manual" );
        var map = Activator.CreateInstance( _mappingResult, profile, manual )!;
        var dictionary = (IDictionary)_mappingResult.GetProperty( "RoleToBone" )!.GetValue( map )!;
        // Roles the installed retargeter does not know (a newer or older enum) are left out.
        foreach ( var (role, bone) in roles )
            if ( Enum.TryParse( _boneRole, role.ToString(), out var parsed ) )
                dictionary[parsed!] = bone;
        _mappingResult.GetProperty( "Confidence" )!.SetValue( map, 1f );
        return new RetargetMapping( roles, 1f, profile, false ) { Handle = map };
    }

    /// <summary>Compiles a cached pose retargeter from <paramref name="source"/> to <paramref name="target"/>.</summary>
    public PoseRetargeter CreatePoseRetargeter( RetargetSkeleton source, RetargetMapping sourceMap, RetargetSkeleton target, RetargetMapping targetMap )
    {
        var rig = CreateTargetRig( target, targetMap );
        var constructor = _runtime.GetConstructors().OrderByDescending( c => c.GetParameters().Length ).First();
        var cp = constructor.GetParameters();
        var args = new object?[cp.Length];
        args[0] = source.Handle;
        args[1] = sourceMap.Handle;
        args[2] = rig;
        for ( var i = 3; i < args.Length; i++ )
            args[i] = cp[i].HasDefaultValue ? cp[i].DefaultValue : null;
        var runtime = Unwrap( () => constructor.Invoke( args ) )!;
        var retarget = _runtime.GetMethod( "Retarget" ) ?? throw new MissingMethodException( "RuntimePoseRetargeter.Retarget" );
        var delegateType = typeof( RetargetInto<> ).MakeGenericType( _xform );
        var call = Delegate.CreateDelegate( delegateType, runtime, retarget );
        var invoke = typeof( RetargeterLibrary ).GetMethod( nameof( InvokeRetarget ), BindingFlags.NonPublic | BindingFlags.Static )!.MakeGenericMethod( _xform );
        return new PoseRetargeter( this, call, invoke, source.Count, target.Count );
    }


    private object CreateTargetRig( RetargetSkeleton target, RetargetMapping targetMap )
    {
        var fromSkeleton = _targetRig.GetMethod( "FromSkeleton", BindingFlags.Public | BindingFlags.Static ) ?? throw new MissingMethodException( "TargetRig.FromSkeleton" );
        var args = fromSkeleton.GetParameters().Select( p => p.HasDefaultValue ? p.DefaultValue : null ).ToArray();
        args[0] = target.Handle;
        args[1] = targetMap.Handle;
        return Unwrap( () => fromSkeleton.Invoke( null, args ) )!;
    }

    private Type Find( string name ) => Assembly.GetType( name, throwOnError: false ) ?? throw new MissingMemberException( $"The Humanoid Retargeter has no {name}; update it." );
    private delegate void RetargetInto<T>( ReadOnlySpan<T> source, Span<T> destination );

    private static void InvokeRetarget<T>( Delegate call, T[] source, T[] destination ) => ((RetargetInto<T>)call)( source, destination );

    private static object? Unwrap( Func<object?> call )
    {
        try
        {
            return call();
        }
        catch ( TargetInvocationException e ) when ( e.InnerException is not null )
        {
            throw new InvalidOperationException( e.InnerException.Message, e.InnerException );
        }
    }
}

/// <summary>A compiled source-to-target pose retargeter living in the Humanoid Retargeter.</summary>
public sealed class PoseRetargeter
{
    private readonly RetargeterLibrary _library;
    private readonly Delegate _call;
    private readonly MethodInfo _invoke;
    private readonly Array _source;
    private readonly Array _target;

    public int SourceCount { get; }
    public int TargetCount { get; }

    internal PoseRetargeter( RetargeterLibrary library, Delegate call, MethodInfo invoke, int sourceCount, int targetCount )
    {
        _library = library;
        _call = call;
        _invoke = invoke;
        SourceCount = sourceCount;
        TargetCount = targetCount;
        _source = library.NewXForms( sourceCount );
        _target = library.NewXForms( targetCount );
    }

    /// <summary>Retargets one parent-local pose (retargeter conventions: cm, Y up).</summary>
    public void Retarget( ReadOnlySpan<Vector3> sourcePositions, ReadOnlySpan<Quaternion> sourceRotations, Span<Vector3> targetPositions, Span<Quaternion> targetRotations )
    {
        for ( var i = 0; i < SourceCount; i++ )
            _library.Write( _source, i, sourcePositions[i], sourceRotations[i] );
        try
        {
            _invoke.Invoke( null, new object[] { _call, _source, _target } );
        }
        catch ( TargetInvocationException e ) when ( e.InnerException is not null )
        {
            throw new InvalidOperationException( $"Retargeting failed: {e.InnerException.Message}", e.InnerException );
        }
        for ( var i = 0; i < TargetCount; i++ )
        {
            var (p, r) = _library.Read( _target.GetValue( i )! );
            targetPositions[i] = p;
            targetRotations[i] = r;
        }
    }
}
