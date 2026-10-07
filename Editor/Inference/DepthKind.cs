using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HumanoidMocap.EditorTools.Inference;

/// <summary>How much depth a recording carries beside its picture.</summary>
public enum DepthKind { None, Partial, Full }
