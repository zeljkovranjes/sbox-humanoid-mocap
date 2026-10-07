using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HumanoidMocap.Core.Inference;
using Vector2 = System.Numerics.Vector2;

public sealed record PalmDetection(float Score,float X,float Y,float Width,float Height,Vector2[] Points);
