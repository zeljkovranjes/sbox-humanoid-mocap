#nullable enable annotations

// Global usings for the s&box in-engine compiler.
//
// The plain net8.0 dev harness gets these automatically via <ImplicitUsings>,
// but s&box's compiler injects no BCL usings at all - without this file the
// library fails to compile inside the editor (CS0246 on List<>, IEnumerable<>,
// FormatException, ...). Duplicating the SDK's implicit set is harmless there
// (verified: no warnings).

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

// NOTE on Vector3: s&box declares its own Vector3 in the *global namespace*,
// which wins over `using System.Numerics;` imports during name lookup and
// breaks this System.Numerics-based core (.X/.Y/.Z, static helpers, delegate
// signatures). A global using-alias does NOT fix this (CS0576: alias conflicts
// with the global-namespace type at every use site). The working fix is a
// *namespace-scoped* alias, declared after the file-scoped namespace line:
//
//     namespace HumanoidMocap.Xyz;
//     using Vector3 = System.Numerics.Vector3;
//
// Every file in this tree that uses the simple name Vector3 carries that line.
// (Quaternion and Matrix4x4 are not global-namespace types in s&box and need
// no alias.)
