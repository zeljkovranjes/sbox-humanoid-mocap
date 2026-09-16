#nullable enable annotations

using System;
using System.Collections.Generic;

namespace HumanoidMocap.Target;

/// <summary>
/// Report entry for one directional clip family found by
/// <see cref="LocomotionSetDetector.Detect"/> — emitted on
/// <see cref="RetargetBatchResult.LocomotionSets"/> whether or not a blend node was produced
/// (incomplete families are reported with <see cref="Emitted"/> false and a note).
/// </summary>
public sealed class LocomotionSetReport
{
    /// <summary>Common stem of the family's clip names (e.g. <c>Walk</c> for
    /// <c>Walk_N</c>…<c>Walk_NW</c>).</summary>
    public required string Stem { get; init; }

    /// <summary>True when the family was complete (all four cardinals) and a 2DBlend +
    /// Folder were emitted into the generated/augmented vmdl.</summary>
    public required bool Emitted { get; init; }

    /// <summary>Name of the emitted 2DBlend node (collision-suffixed); empty when
    /// <see cref="Emitted"/> is false.</summary>
    public string BlendNodeName { get; init; } = "";

    /// <summary>Name of the emitted Folder node; empty when <see cref="Emitted"/> is false.</summary>
    public string FolderName { get; init; } = "";

    /// <summary>Clip name per canonical direction token (<c>N</c>, <c>NE</c>, <c>E</c>,
    /// <c>SE</c>, <c>S</c>, <c>SW</c>, <c>W</c>, <c>NW</c>); only detected members present.</summary>
    public required IReadOnlyDictionary<string, string> Members { get; init; }

    /// <summary>Sequence name placed in the blend grid's center cell (an idle clip from the
    /// batch when one matched, else the forward member as fallback); null when nothing was
    /// emitted.</summary>
    public string? CenterClipName { get; init; }

    /// <summary>Detection notes: missing cardinals (incomplete family), center-cell
    /// fallback, duplicate direction members, diagonal fill-ins.</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Detects directional locomotion families among a batch's converted clip names and shapes
/// them into <see cref="LocomotionSetSpec"/>s replicating the shipped citizen 2DBlend layout.
/// </summary>
/// <remarks>
/// <para><b>Shipped-data findings</b> (dev fixture <c>citizen_animationlist.vmdl_prefab</c>):
/// every citizen locomotion family (<c>Walk</c>, <c>Run</c>, <c>Run2X</c>,
/// <c>CrouchWalk</c>, …) groups eight directional AnimFiles
/// (<c>&lt;stem&gt;_N</c>…<c>&lt;stem&gt;_NW</c>) under a Folder named by the stem together
/// with a <c>2DBlend</c> node. The 2DBlend holds NO per-child coordinates — it is a dense
/// 3×3 grid: <c>row_pose_param_name = "move_x"</c>, <c>col_pose_param_name = "move_y"</c>,
/// <c>row_weight_list = col_weight_list = [-1.0, 0.0, 1.0]</c>, and
/// <c>blend_anim_list = [[SW, S, SE], [W, idle, E], [NW, N, NE]]</c> — +<c>move_x</c> is
/// forward (N), +<c>move_y</c> is right (E), and the center cell is an idle sequence
/// (<c>IdlePose_Default</c> / <c>CrouchIdlePose_Default</c>). The pose parameters come from
/// the citizen base models' PoseParamList prefab, which Base-Model child vmdls inherit.</para>
/// <para><b>Family matching:</b> a clip joins a family when its name ends in
/// <c>_&lt;token&gt;</c> (case-insensitive) where token is a compass direction
/// (<c>N</c>/<c>NE</c>/<c>E</c>/<c>SE</c>/<c>S</c>/<c>SW</c>/<c>W</c>/<c>NW</c>) or a word
/// form (<c>Forward</c>→N, <c>Backward</c>/<c>Back</c>→S, <c>Left</c>→W, <c>Right</c>→E);
/// the part before the separator is the stem. A family is COMPLETE when all four cardinals
/// are present (≥ 4 members); diagonals are optional — a missing diagonal's grid corner
/// reuses the row's cardinal (NE/NW → N, SE/SW → S). The center cell prefers a batch clip
/// named <c>&lt;stem&gt;_Idle</c>, <c>&lt;stem&gt;Idle</c>, <c>&lt;stem&gt;</c> or
/// <c>Idle</c> (first match, case-insensitive) and falls back to the forward member.</para>
/// </remarks>
public static class LocomotionSetDetector
{
    /// <summary>Canonical direction tokens in grid-independent order (index 0–7).</summary>
    private static readonly string[] DirectionTokens = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    private const int N = 0, NE = 1, E = 2, SE = 3, S = 4, SW = 5, W = 6, NW = 7;

    /// <summary>
    /// Scans <paramref name="entries"/> (a batch's successful AnimFile entries, in batch
    /// order) for directional families. Complete families yield a
    /// <see cref="LocomotionSetSpec"/> whose Folder/2DBlend names are made unique against
    /// <paramref name="usedNames"/> (the batch's clip-name collision set; new names are added
    /// to it). Families with two or more directional members that are missing a cardinal are
    /// reported (not emitted). Single directional clips are ignored as noise.
    /// </summary>
    public static (List<LocomotionSetSpec> Sets, List<LocomotionSetReport> Reports) Detect(
        IReadOnlyList<AnimEntry> entries, ISet<string> usedNames, bool autoSuffixCollisions)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(usedNames);

        var sets = new List<LocomotionSetSpec>();
        var reports = new List<LocomotionSetReport>();

        // ---- group by stem (case-insensitive, first-seen casing wins, batch order kept) ----
        var groupIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<(string Stem, AnimEntry?[] Members, List<string> Notes)>();
        foreach (var entry in entries)
        {
            if (ParseDirection(entry.Name) is not { } parsed)
                continue;
            if (!groupIndex.TryGetValue(parsed.Stem, out var index))
            {
                index = groups.Count;
                groupIndex.Add(parsed.Stem, index);
                groups.Add((parsed.Stem, new AnimEntry?[8], new List<string>()));
            }

            var group = groups[index];
            if (group.Members[parsed.Direction] is { } existing)
            {
                group.Notes.Add(
                    $"'{entry.Name}' also maps to direction {DirectionTokens[parsed.Direction]} — "
                    + $"keeping '{existing.Name}'.");
            }
            else
            {
                group.Members[parsed.Direction] = entry;
            }
        }

        foreach (var (stem, members, groupNotes) in groups)
        {
            var memberCount = 0;
            foreach (var member in members)
            {
                if (member is not null)
                    memberCount++;
            }
            if (memberCount < 2)
                continue; // a single directional clip is noise, not a family

            var report = BuildFamily(stem, members, entries, usedNames, autoSuffixCollisions,
                out var set);
            report.Notes.AddRange(groupNotes);
            reports.Add(report);
            if (set is not null)
                sets.Add(set);
        }

        return (sets, reports);
    }

    /// <summary>
    /// Lightweight name-only dry-run of <see cref="Detect"/>: groups
    /// <paramref name="clipNames"/> into directional families under the SAME parsing
    /// (<c>_N</c>…<c>_NW</c> compass tokens and the Forward/Back(ward)/Left/Right word
    /// forms), grouping (case-insensitive stems, first-seen casing/order kept, duplicate
    /// directions counted once) and completeness rules (Complete = all four cardinals
    /// present). Families with two or more members are listed whether complete or not;
    /// single directional clips are ignored as noise. UIs use this to decide whether
    /// enabling <see cref="BatchOptions.DetectLocomotionSets"/> could produce anything
    /// before any conversion has run.
    /// </summary>
    public static IReadOnlyList<(string Stem, bool Complete, int MemberCount)> ScanNames(
        IEnumerable<string> clipNames)
    {
        ArgumentNullException.ThrowIfNull(clipNames);

        var groupIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<(string Stem, bool[] Members)>();
        foreach (var name in clipNames)
        {
            if (string.IsNullOrEmpty(name) || ParseDirection(name) is not { } parsed)
                continue;
            if (!groupIndex.TryGetValue(parsed.Stem, out var index))
            {
                index = groups.Count;
                groupIndex.Add(parsed.Stem, index);
                groups.Add((parsed.Stem, new bool[8]));
            }
            groups[index].Members[parsed.Direction] = true;
        }

        var families = new List<(string Stem, bool Complete, int MemberCount)>();
        foreach (var (stem, members) in groups)
        {
            var memberCount = 0;
            foreach (var present in members)
            {
                if (present)
                    memberCount++;
            }
            if (memberCount < 2)
                continue; // a single directional clip is noise, not a family

            var complete = members[N] && members[E] && members[S] && members[W];
            families.Add((stem, complete, memberCount));
        }

        return families;
    }

    /// <summary>Builds the report (and, when the family is complete, the emission spec)
    /// for one stem group.</summary>
    private static LocomotionSetReport BuildFamily(
        string stem, AnimEntry?[] members, IReadOnlyList<AnimEntry> entries,
        ISet<string> usedNames, bool autoSuffixCollisions, out LocomotionSetSpec? set)
    {
        var memberMap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var d = 0; d < members.Length; d++)
        {
            if (members[d] is { } m)
                memberMap.Add(DirectionTokens[d], m.Name);
        }

        var missingCardinals = new List<string>();
        foreach (var cardinal in new[] { N, E, S, W })
        {
            if (members[cardinal] is null)
                missingCardinals.Add(DirectionTokens[cardinal]);
        }

        if (missingCardinals.Count > 0)
        {
            set = null;
            var report = new LocomotionSetReport
            {
                Stem = stem,
                Emitted = false,
                Members = memberMap,
            };
            report.Notes.Add(
                $"Locomotion family '{stem}' is incomplete — missing cardinal direction(s) "
                + $"{string.Join(", ", missingCardinals)}; no 2D blend emitted.");
            return report;
        }

        var notes = new List<string>();

        // ---- center cell: an idle clip from the batch, else the forward member ----
        var center = FindCenterClip(stem, entries);
        if (center is null)
        {
            center = members[N]!.Name;
            notes.Add(
                $"Locomotion family '{stem}': no idle clip found for the blend center "
                + $"(looked for '{stem}_Idle', '{stem}Idle', '{stem}', 'Idle'); the center "
                + $"cell falls back to '{center}'.");
        }

        // ---- diagonals: missing corners reuse the row's cardinal (shipped grid layout) ----
        string Cell(int diagonal, int rowCardinal)
        {
            if (members[diagonal] is { } m)
                return m.Name;
            notes.Add(
                $"Locomotion family '{stem}': diagonal {DirectionTokens[diagonal]} missing — "
                + $"grid corner reuses '{members[rowCardinal]!.Name}'.");
            return members[rowCardinal]!.Name;
        }

        // Shipped layout: rows = move_x (-1, 0, +1), cols = move_y (-1, 0, +1);
        // row 0 = [SW, S, SE], row 1 = [W, center, E], row 2 = [NW, N, NE].
        var grid = new[]
        {
            new[] { Cell(SW, S), members[S]!.Name, Cell(SE, S) },
            new[] { members[W]!.Name, center, members[E]!.Name },
            new[] { Cell(NW, N), members[N]!.Name, Cell(NE, N) },
        };

        var memberNames = new List<string>();
        var looping = true;
        foreach (var member in members)
        {
            if (member is null)
                continue;
            memberNames.Add(member.Name);
            looping &= member.Looping;
        }

        var folderName = UniqueName(stem, usedNames, autoSuffixCollisions);
        var blendName = UniqueName(stem + "_2D", usedNames, autoSuffixCollisions);

        set = new LocomotionSetSpec
        {
            FolderName = folderName,
            BlendName = blendName,
            Looping = looping,
            BlendGrid = grid,
            MemberNames = memberNames,
        };
        var emitted = new LocomotionSetReport
        {
            Stem = stem,
            Emitted = true,
            BlendNodeName = blendName,
            FolderName = folderName,
            Members = memberMap,
            CenterClipName = center,
        };
        emitted.Notes.AddRange(notes);
        return emitted;
    }

    /// <summary>The blend center candidates among the batch's clip names, in priority order:
    /// <c>&lt;stem&gt;_Idle</c>, <c>&lt;stem&gt;Idle</c>, <c>&lt;stem&gt;</c>, <c>Idle</c>
    /// (case-insensitive); null when none exists.</summary>
    private static string? FindCenterClip(string stem, IReadOnlyList<AnimEntry> entries)
    {
        foreach (var candidate in new[] { stem + "_Idle", stem + "Idle", stem, "Idle" })
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry.Name, candidate, StringComparison.OrdinalIgnoreCase))
                    return entry.Name;
            }
        }
        return null;
    }

    /// <summary>Splits a clip name into stem + direction: the text after the LAST underscore
    /// must be a compass token or word form; the (non-empty) text before it is the stem.</summary>
    private static (string Stem, int Direction)? ParseDirection(string name)
    {
        var separator = name.LastIndexOf('_');
        if (separator <= 0 || separator == name.Length - 1)
            return null;

        var token = name[(separator + 1)..];
        int? direction = token.ToLowerInvariant() switch
        {
            "n" or "forward" => N,
            "ne" => NE,
            "e" or "right" => E,
            "se" => SE,
            "s" or "backward" or "back" => S,
            "sw" => SW,
            "w" or "left" => W,
            "nw" => NW,
            _ => null,
        };
        if (direction is not { } d)
            return null;
        return (name[..separator], d);
    }

    /// <summary>Same collision auto-suffixing the batch applies to clip names
    /// (<c>name</c>, <c>name_2</c>, …); registers the result in <paramref name="usedNames"/>.</summary>
    private static string UniqueName(string name, ISet<string> usedNames, bool autoSuffix)
    {
        if (usedNames.Add(name) || !autoSuffix)
            return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name}_{i}";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }
}
