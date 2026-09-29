#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.Json;

namespace HumanoidMocap.MotionBricks;

/// <summary>Install state of a model package, as shown on the setup page.</summary>
public enum ModelState
{
    /// <summary>Nothing (or only leftovers) on disk.</summary>
    NotInstalled,

    /// <summary>A download was interrupted; it resumes where it stopped.</summary>
    Incomplete,

    /// <summary>All files are present with the right sizes but were never verified.</summary>
    Unverified,

    /// <summary>Every file matches its pinned SHA-256.</summary>
    Ready,

    /// <summary>A file is present but does not match (damaged or tampered with).</summary>
    Damaged,
}

/// <summary>Result of inspecting an install folder.</summary>
public sealed record ModelStatus( ModelState State, string Directory, long BytesOnDisk, long TotalBytes, string? Problem )
{
    public bool IsUsable => State is ModelState.Ready or ModelState.Unverified;
}

/// <summary>
/// Where model packages live and whether they are intact. Packages are installed once per user
/// (not per project) under the platform's local application-data folder, e.g.
/// <c>%LOCALAPPDATA%\AIAnimator\Models\motionbricks-g1-f32</c> on Windows, so every s&amp;box
/// project shares one copy. <c>AIANIMATOR_MODEL_ROOT</c> overrides the root (tests, shared drives).
/// </summary>
public sealed class ModelStore
{
    public const string RootOverrideVariable = "AIANIMATOR_MODEL_ROOT";
    private const string StampName = "verified.json";

    public ModelPackage Package { get; }
    public string Directory { get; }

    public ModelStore( ModelPackage package, string? root = null )
    {
        Package = package;
        Directory = System.IO.Path.Combine( root ?? DefaultRoot, package.Id );
    }

    /// <summary>Per-user models folder shared by every project.</summary>
    public static string DefaultRoot
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable( RootOverrideVariable );
            if ( !string.IsNullOrWhiteSpace( overridden ) )
                return overridden;
            var local = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create );
            if ( string.IsNullOrEmpty( local ) )
                local = System.IO.Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".local", "share" );
            return System.IO.Path.Combine( local, "AIAnimator", "Models" );
        }
    }

    public string PathOf( ModelFile file ) => System.IO.Path.Combine( Directory, file.LocalName );
    public string PartialPathOf( ModelFile file ) => PathOf( file ) + ".part";

    /// <summary>
    /// Fast status from file sizes and the verification stamp (no hashing). A file whose size,
    /// timestamp and hash were recorded by a previous verification is trusted until it changes.
    /// </summary>
    public ModelStatus Inspect()
    {
        long onDisk = 0;
        var complete = 0;
        var partial = false;
        string? problem = null;
        var stamp = ReadStamp();
        var stamped = 0;
        foreach ( var file in Package.Files )
        {
            var path = PathOf( file );
            if ( File.Exists( path ) )
            {
                var info = new FileInfo( path );
                onDisk += info.Length;
                if ( info.Length != file.Size )
                {
                    problem ??= $"{file.LocalName} has the wrong size.";
                    continue;
                }
                complete++;
                if ( stamp.TryGetValue( file.LocalName, out var entry ) && entry.Sha256 == file.Sha256 && entry.Size == info.Length && entry.Ticks == info.LastWriteTimeUtc.Ticks )
                    stamped++;
            }
            else if ( File.Exists( PartialPathOf( file ) ) )
            {
                partial = true;
                onDisk += new FileInfo( PartialPathOf( file ) ).Length;
            }
        }

        var total = Package.TotalBytes;
        if ( problem is not null )
            return new ModelStatus( ModelState.Damaged, Directory, onDisk, total, problem );
        if ( complete == Package.Files.Count )
            return new ModelStatus( stamped == complete ? ModelState.Ready : ModelState.Unverified, Directory, onDisk, total, null );
        if ( partial || complete > 0 )
            return new ModelStatus( ModelState.Incomplete, Directory, onDisk, total, null );
        return new ModelStatus( ModelState.NotInstalled, Directory, onDisk, total, null );
    }

    /// <summary>Hashes every file (a few seconds for ~730 MB) and records the result.</summary>
    public async Task<ModelStatus> VerifyAsync( IProgress<float>? progress = null, CancellationToken cancel = default )
    {
        var status = Inspect();
        if ( status.State is ModelState.NotInstalled or ModelState.Incomplete )
            return status;
        var total = Package.TotalBytes;
        long done = 0;
        var stamp = new Dictionary<string, StampEntry>();
        foreach ( var file in Package.Files )
        {
            var path = PathOf( file );
            var hash = await HashAsync( path, bytes => progress?.Report( (done + bytes) / (float)total ), cancel );
            done += file.Size;
            if ( !string.Equals( hash, file.Sha256, StringComparison.OrdinalIgnoreCase ) )
                return new ModelStatus( ModelState.Damaged, Directory, status.BytesOnDisk, total, $"{file.LocalName} is damaged (its checksum does not match)." );
            stamp[file.LocalName] = new StampEntry( file.Sha256, file.Size, new FileInfo( path ).LastWriteTimeUtc.Ticks );
        }
        WriteStamp( stamp );
        return Inspect();
    }

    /// <summary>Records one verified file in the stamp (used by the installer after each download).</summary>
    internal void Stamp( ModelFile file )
    {
        var stamp = ReadStamp();
        stamp[file.LocalName] = new StampEntry( file.Sha256, file.Size, new FileInfo( PathOf( file ) ).LastWriteTimeUtc.Ticks );
        WriteStamp( stamp );
    }

    /// <summary>Deletes the package (files, partial downloads and the stamp).</summary>
    public void Remove()
    {
        if ( !System.IO.Directory.Exists( Directory ) )
            return;
        foreach ( var file in Package.Files )
        {
            TryDelete( PathOf( file ) );
            TryDelete( PartialPathOf( file ) );
        }
        TryDelete( System.IO.Path.Combine( Directory, StampName ) );
    }

    public static async Task<string> HashAsync( string path, Action<long>? progress, CancellationToken cancel )
    {
        using var sha = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );
        await using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true );
        var buffer = new byte[1 << 20];
        long total = 0;
        int read;
        while ( (read = await stream.ReadAsync( buffer, cancel )) > 0 )
        {
            sha.AppendData( buffer, 0, read );
            total += read;
            progress?.Invoke( total );
        }
        return Convert.ToHexString( sha.GetHashAndReset() ).ToLowerInvariant();
    }

    private sealed record StampEntry( string Sha256, long Size, long Ticks );

    private Dictionary<string, StampEntry> ReadStamp()
    {
        try
        {
            var path = System.IO.Path.Combine( Directory, StampName );
            if ( File.Exists( path ) )
                return JsonSerializer.Deserialize<Dictionary<string, StampEntry>>( File.ReadAllText( path ) ) ?? new();
        }
        catch ( Exception e ) when ( e is IOException or JsonException or UnauthorizedAccessException )
        {
            // A broken stamp only costs a re-verification.
        }
        return new();
    }

    private void WriteStamp( Dictionary<string, StampEntry> stamp )
    {
        System.IO.Directory.CreateDirectory( Directory );
        var path = System.IO.Path.Combine( Directory, StampName );
        File.WriteAllText( path + ".tmp", JsonSerializer.Serialize( stamp ) );
        File.Move( path + ".tmp", path, overwrite: true );
    }

    private static void TryDelete( string path )
    {
        try
        {
            if ( File.Exists( path ) )
                File.Delete( path );
        }
        catch ( IOException )
        {
        }
    }
}
