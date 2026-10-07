#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HumanoidMocap.Core.Target;

/// <summary>A parsed KV3 document: the verbatim header comment plus the root value.</summary>
public sealed class Kv3Document
{
    /// <summary>The header comment line, verbatim (e.g.
    /// <c>&lt;!-- kv3 encoding:text:... --&gt;</c>).</summary>
    public string Header { get; }

    /// <summary>The root value (an object for vmdl files).</summary>
    public KvValue Root { get; }

    /// <summary>Creates a document.</summary>
    public Kv3Document(string header, KvValue root)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Root = root ?? throw new ArgumentNullException(nameof(root));
    }
}
