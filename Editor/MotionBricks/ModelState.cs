#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.Json;

namespace HumanoidMocap.EditorTools.MotionBricks;

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
