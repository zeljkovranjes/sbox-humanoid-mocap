#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace HumanoidMocap.EditorTools.MotionBricks;

/// <summary>
/// Downloads a pinned <see cref="ModelPackage"/> into its <see cref="ModelStore"/> folder. Only
/// ever runs when the user asks for it. Each file streams into a <c>.part</c> file (resumed with
/// an HTTP range request after an interruption), is checked against its pinned size and SHA-256,
/// and only then moved into place, so a crash or a bad network never leaves a file that looks
/// installed. A lock file keeps two editors from installing into the same folder at once.
/// </summary>
public sealed class ModelInstaller
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds( 45 );
    private const int Attempts = 4;

    private readonly ModelStore _store;

    public ModelInstaller( ModelStore store )
    {
        _store = store;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient( new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None } )
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd( "sbox-humanoid-mocap/1.0" );
        return client;
    }

    /// <summary>Downloads every missing or damaged file. Throws <see cref="ModelInstallException"/> with a readable message.</summary>
    public async Task InstallAsync( IProgress<InstallProgress>? progress = null, CancellationToken cancel = default )
    {
        Directory.CreateDirectory( _store.Directory );
        using var installLock = await AcquireLockAsync( progress, cancel );
        var package = _store.Package;
        var total = package.TotalBytes;
        long done = 0;
        var watch = Stopwatch.StartNew();
        long bytesAtStart = -1;

        void Report( long fileBytes, string name, string phase )
        {
            var current = done + fileBytes;
            if ( bytesAtStart < 0 )
                bytesAtStart = current;
            var seconds = watch.Elapsed.TotalSeconds;
            var speed = seconds > 0.5 ? (current - bytesAtStart) / seconds : 0;
            progress?.Report( new InstallProgress( current, total, name, speed, phase ) );
        }

        foreach ( var file in package.Files )
        {
            cancel.ThrowIfCancellationRequested();
            var path = _store.PathOf( file );
            if ( File.Exists( path ) && new FileInfo( path ).Length == file.Size )
            {
                Report( 0, file.LocalName, "Checking" );
                var existing = await ModelStore.HashAsync( path, b => Report( b, file.LocalName, "Checking" ), cancel );
                if ( existing == file.Sha256 )
                {
                    _store.Stamp( file );
                    done += file.Size;
                    continue;
                }
            }
            if ( File.Exists( path ) )
                File.Delete( path );

            await DownloadWithRetriesAsync( file, b => Report( b, file.LocalName, "Downloading" ), cancel );
            _store.Stamp( file );
            done += file.Size;
        }
        progress?.Report( new InstallProgress( total, total, "", 0, "Installed" ) );
    }

    private async Task DownloadWithRetriesAsync( ModelFile file, Action<long> report, CancellationToken cancel )
    {
        Exception? last = null;
        for ( var attempt = 1; attempt <= Attempts; attempt++ )
        {
            try
            {
                await DownloadAsync( file, report, cancel );
                return;
            }
            catch ( OperationCanceledException ) when ( cancel.IsCancellationRequested )
            {
                throw;
            }
            catch ( Exception e ) when ( e is HttpRequestException or IOException or OperationCanceledException or ModelInstallException )
            {
                last = e;
                if ( attempt < Attempts )
                    await Task.Delay( TimeSpan.FromSeconds( 2 * attempt ), cancel );
            }
        }
        throw new ModelInstallException( Friendly( last ), last );
    }

    private async Task DownloadAsync( ModelFile file, Action<long> report, CancellationToken cancel )
    {
        var part = _store.PartialPathOf( file );
        long existing = File.Exists( part ) ? new FileInfo( part ).Length : 0;
        if ( existing > file.Size )
        {
            File.Delete( part );
            existing = 0;
        }

        using var request = new HttpRequestMessage( HttpMethod.Get, _store.Package.UrlOf( file ) );
        if ( existing > 0 && existing < file.Size )
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue( existing, null );

        using var stall = CancellationTokenSource.CreateLinkedTokenSource( cancel );
        stall.CancelAfter( StallTimeout );
        using var response = await Http.SendAsync( request, HttpCompletionOption.ResponseHeadersRead, stall.Token );
        if ( response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable )
        {
            File.Delete( part );
            throw new ModelInstallException( "The download restarted." );
        }
        response.EnsureSuccessStatusCode();
        var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if ( !resumed )
            existing = 0;

        using var sha = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );
        await using ( var output = new FileStream( part, resumed ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, useAsync: true ) )
        {
            var buffer = new byte[1 << 20];
            if ( resumed )
            {
                // Re-hash what is already on disk so the final checksum covers the whole file.
                int read;
                while ( output.Position < existing && (read = await output.ReadAsync( buffer.AsMemory( 0, (int)Math.Min( buffer.Length, existing - output.Position ) ), cancel )) > 0 )
                    sha.AppendData( buffer, 0, read );
                output.SetLength( existing );
            }

            await using var input = await response.Content.ReadAsStreamAsync( cancel );
            var written = existing;
            report( written );
            while ( true )
            {
                stall.CancelAfter( StallTimeout );
                var read = await input.ReadAsync( buffer, stall.Token );
                if ( read <= 0 )
                    break;
                if ( written + read > file.Size )
                    throw new ModelInstallException( $"{file.LocalName} is larger than expected." );
                await output.WriteAsync( buffer.AsMemory( 0, read ), cancel );
                sha.AppendData( buffer, 0, read );
                written += read;
                report( written );
            }
            await output.FlushAsync( cancel );
            if ( written != file.Size )
                throw new ModelInstallException( $"The download of {file.LocalName} ended early." );
        }

        var hash = Convert.ToHexString( sha.GetHashAndReset() ).ToLowerInvariant();
        if ( hash != file.Sha256 )
        {
            File.Delete( part );
            throw new ModelInstallException( $"{file.LocalName} failed its integrity check, so it was discarded. Try again.", new CryptographicException( "checksum mismatch" ) );
        }
        File.Move( part, _store.PathOf( file ), overwrite: true );
    }

    /// <summary>Waits while another editor installs into the same folder.</summary>
    private async Task<IDisposable> AcquireLockAsync( IProgress<InstallProgress>? progress, CancellationToken cancel )
    {
        var path = System.IO.Path.Combine( _store.Directory, "install.lock" );
        while ( true )
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                return new FileStream( path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose );
            }
            catch ( IOException )
            {
                progress?.Report( new InstallProgress( 0, _store.Package.TotalBytes, "", 0, "Waiting for another s&box window to finish installing" ) );
                await Task.Delay( 1000, cancel );
            }
        }
    }

    private static string Friendly( Exception? e ) => e switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "The model files were not found on the server. The download link may have changed; update AI Animator.",
        HttpRequestException { StatusCode: { } code } => $"The server refused the download ({(int)code}). Try again later.",
        HttpRequestException => "Could not reach the download server. Check your internet connection and try again.",
        OperationCanceledException => "The download stalled. Check your internet connection and try again.",
        IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) => "There is not enough free disk space for the model (about 750 MB).",
        IOException => "The model could not be written to disk. Check that the folder is writable.",
        ModelInstallException m => m.Message,
        _ => "The download failed. Try again.",
    };
}
