#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;

using System.Collections;
using System.Numerics;
using System.Reflection;
using HumanoidMocap.Core.Mapping;

namespace HumanoidMocap.EditorTools.MotionBricks;

using Vector3 = System.Numerics.Vector3;

/// <summary>One bone handed to the retargeter: parent-local rest transform in its conventions (cm, Y up).</summary>
public readonly record struct RetargetBone( string Name, string? Parent, Vector3 Position, Quaternion Rotation );
