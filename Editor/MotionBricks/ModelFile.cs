#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace HumanoidMocap.EditorTools.MotionBricks;

/// <summary>One file of a downloadable model package, pinned by size and SHA-256.</summary>
public sealed record ModelFile( string RemotePath, string LocalName, long Size, string Sha256 );
