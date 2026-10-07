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

/// <summary>Why an install did not finish.</summary>
public sealed class ModelInstallException : Exception
{
    public ModelInstallException( string message, Exception? inner = null ) : base( message, inner ) { }
}
