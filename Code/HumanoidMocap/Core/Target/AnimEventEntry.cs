#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HumanoidMocap.Core.Target;

/// <summary>
/// One AnimEvent to attach as a child of a vmdl AnimFile node. The node shape replicates the
/// shipped citizen animation list prefab exactly (all 28 shipped events are
/// <c>AE_FOOTSTEP</c>): <c>_class = "AnimEvent"</c>, <c>event_class</c>, <c>event_frame</c>
/// (integer frame on the clip's grid), and an <c>event_keys</c> object carrying
/// <c>Attachment</c> (<c>"foot_L"</c>/<c>"foot_R"</c>), <c>Foot</c> (STRING <c>"0"</c> =
/// left, <c>"1"</c> = right) and <c>Volume</c> (<c>0.7</c> throughout the shipped data).
/// </summary>
public sealed class AnimEventEntry
{
    /// <summary>Event class name (e.g. <c>AE_FOOTSTEP</c>).</summary>
    public string EventClass { get; set; } = "";

    /// <summary>Frame the event fires on (the clip's own frame grid).</summary>
    public int Frame { get; set; }

    /// <summary>event_keys <c>Attachment</c> value (<c>"foot_L"</c>/<c>"foot_R"</c> in the
    /// shipped footstep data); null omits the key.</summary>
    public string? Attachment { get; set; }

    /// <summary>event_keys <c>Foot</c> value — the shipped data encodes the side as a STRING:
    /// <c>"0"</c> = left, <c>"1"</c> = right; null omits the key.</summary>
    public string? Foot { get; set; }

    /// <summary>event_keys <c>Volume</c> value (<c>0.7</c> in all shipped footstep events);
    /// null omits the key.</summary>
    public double? Volume { get; set; }

    /// <summary>
    /// Additional string event_keys emitted after the fixed keys, in insertion order.
    /// (W3b addition, southpaw project: punch clips carry semantic contact tags such as
    /// <c>Zone</c>/<c>Family</c>/<c>Hand</c> on their contact-frame AnimEvent; the shape
    /// stays the shipped citizen AnimEvent node shape, only extra keys appear.) Null or
    /// empty adds nothing, so existing callers are unaffected.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraKeys { get; set; }
}
