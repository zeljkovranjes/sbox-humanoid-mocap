#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using HumanoidMocap.Core.Mapping;
using HumanoidMocap.Core.Maths;
using HumanoidMocap.Core.Target;
using HumanoidMocap.Core.Skeleton;

namespace HumanoidMocap.Core;

/// <summary>Imported source after the public mapping and bind-rest preparation cascade.</summary>
public sealed record ResolvedSource( SourceScene Scene, MappingResult Mapping, MappingReportInfo Report );
