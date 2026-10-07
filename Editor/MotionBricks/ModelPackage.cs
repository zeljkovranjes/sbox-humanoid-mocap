#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace HumanoidMocap.EditorTools.MotionBricks;

/// <summary>
/// A pinned, downloadable model package. Everything needed to fetch and verify it is fixed
/// here, so the tool never downloads anything that was not reviewed.
/// </summary>
public sealed record ModelPackage
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required string Description { get; init; }
    public required string License { get; init; }
    public required string LicenseUrl { get; init; }
    public required string SourceUrl { get; init; }
    public required string DownloadBase { get; init; }
    public required IReadOnlyList<ModelFile> Files { get; init; }

    public long TotalBytes => Files.Sum( f => f.Size );

    public string UrlOf( ModelFile file ) => $"{DownloadBase}/{file.RemotePath}";

    /// <summary>
    /// NVIDIA MotionBricks (G1, F32), as converted to GGUF by motion-bricks.cpp and published on
    /// Hugging Face. Pinned to one repository commit; hashes from its MANIFEST.json.
    /// </summary>
    public static ModelPackage MotionBricks { get; } = new()
    {
        Id = "motionbricks-g1-f32",
        DisplayName = "MotionBricks",
        Version = "G1 · a0732b6",
        Description = "NVIDIA's motion model that fills the motion between your poses.",
        License = "NVIDIA Open Model License",
        LicenseUrl = "https://huggingface.co/LocalAI-io/MotionBricks-G1-GGML/blob/cc2a47603dbc203a4f18f35dd06ed3611833f506/UPSTREAM_LICENSE",
        SourceUrl = "https://huggingface.co/LocalAI-io/MotionBricks-G1-GGML",
        DownloadBase = "https://huggingface.co/LocalAI-io/MotionBricks-G1-GGML/resolve/cc2a47603dbc203a4f18f35dd06ed3611833f506",
        Files = new[]
        {
            new ModelFile( "g1-f32/support.gguf", "support.gguf", 5472, "5d41cae4bc494e612ef19e25f599351aafbc9ef1cb46d4ac3f1e42bb7ab07200" ),
            new ModelFile( "g1-f32/manifest.json", "manifest.json", 1101, "35321e8f760316e9a0e73619e23af4089deeedcc8efe7e7266fddbdb8d3c3cfc" ),
            new ModelFile( "g1-f32/vq-decoder.gguf", "vq-decoder.gguf", 49753440, "6a1114b6906b07fab9deb18d602b4e731e1b5794318b1643ffd0605692f62d1e" ),
            new ModelFile( "g1-f32/root.gguf", "root.gguf", 136504000, "46268d77e5c15449d68a39ce6ec7145e9eb457c5114d5dca934bd3ab539b29db" ),
            new ModelFile( "g1-f32/pose.gguf", "pose.gguf", 546369984, "6db82e5a67a355052f283bbcab104b71ecfc333dc8d9017c3812eb6f9650756e" ),
            new ModelFile( "NOTICE", "NOTICE", 232, "f6c0e67baae1e9dcb94c77d61cd452f52976090bdf52f874654d2d548c3baf31" ),
            new ModelFile( "UPSTREAM_LICENSE", "UPSTREAM_LICENSE", 8886, "24ab66be50d1aca4fc5e029ef76ce4ceaac6557ea21665caf4b140695a76ffee" ),
        },
    };
}
