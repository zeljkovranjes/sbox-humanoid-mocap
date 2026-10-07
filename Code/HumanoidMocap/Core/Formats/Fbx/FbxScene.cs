#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;

namespace HumanoidMocap.Core.Formats.Fbx;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/HumanoidMocap/Assembly.cs)

/// <summary>
/// Semantic object graph built from an FBX token tree: typed objects, the Model hierarchy
/// (OO connections), animation bindings (OP connections), Properties70 lookup with
/// Definitions-template defaults, bind poses, and GlobalSettings.
/// </summary>
public sealed class FbxScene
{
    /// <summary>All parsed objects by id.</summary>
    public IReadOnlyDictionary<long, FbxObject> ObjectsById => _objectsById;

    /// <summary>All Model objects in document order.</summary>
    public IReadOnlyList<FbxObject> Models => _models;

    /// <summary>All animation stacks in document order.</summary>
    public IReadOnlyList<FbxAnimStack> Stacks => _stacks;

    /// <summary>World-space bind matrices from Pose/BindPose nodes, by model id (row-vector layout).</summary>
    public IReadOnlyDictionary<long, Matrix4x4> BindPose => _bindPose;

    /// <summary>GlobalSettings UnitScaleFactor: source-unit → centimeter factor (FBX default 1 = cm).</summary>
    public double UnitScaleFactor { get; private set; } = 1.0;

    /// <summary>GlobalSettings UpAxis (default 1 = Y).</summary>
    public int UpAxis { get; private set; } = 1;

    /// <summary>GlobalSettings UpAxisSign (default +1).</summary>
    public int UpAxisSign { get; private set; } = 1;

    /// <summary>GlobalSettings FrontAxis (default 2 = Z).</summary>
    public int FrontAxis { get; private set; } = 2;

    /// <summary>GlobalSettings FrontAxisSign (default +1).</summary>
    public int FrontAxisSign { get; private set; } = 1;

    /// <summary>GlobalSettings CoordAxis (default 0 = X).</summary>
    public int CoordAxis { get; private set; } = 0;

    /// <summary>GlobalSettings CoordAxisSign (default +1).</summary>
    public int CoordAxisSign { get; private set; } = 1;

    /// <summary>GlobalSettings OriginalUpAxis (-1 when not recorded).</summary>
    public int OriginalUpAxis { get; private set; } = -1;

    /// <summary>GlobalSettings TimeMode (FbxTime::EMode enum value; 0 = default mode).</summary>
    public int TimeMode { get; private set; }

    /// <summary>GlobalSettings CustomFrameRate (-1 when not recorded).</summary>
    public double CustomFrameRate { get; private set; } = -1.0;

    /// <summary>
    /// Native frame rate (fps) implied by <see cref="TimeMode"/> /
    /// <see cref="CustomFrameRate"/>. This is the rate the source timeline was authored at —
    /// external frame ranges (e.g. Unity <c>.meta</c> clipAnimations) are expressed in it.
    /// The default mode (0) and unknown values map to 30 fps (this importer's resample default).
    /// </summary>
    public double FrameRate => TimeModeFrameRate(TimeMode, CustomFrameRate);

    /// <summary>FbxTime::EMode → frames per second (eCustom uses the custom rate).</summary>
    internal static double TimeModeFrameRate(int timeMode, double customFrameRate) => timeMode switch
    {
        1 => 120.0,            // eFrames120
        2 => 100.0,            // eFrames100
        3 => 60.0,             // eFrames60
        4 => 50.0,             // eFrames50
        5 => 48.0,             // eFrames48
        6 => 30.0,             // eFrames30
        7 => 30.0,             // eFrames30Drop
        8 => 30.0 / 1.001,     // eNTSCDropFrame (29.97)
        9 => 30.0 / 1.001,     // eNTSCFullFrame (29.97)
        10 => 25.0,            // ePAL
        11 => 24.0,            // eFrames24
        12 => 1000.0,          // eFrames1000
        13 => 24.0 / 1.001,    // eFilmFullFrame (23.976)
        14 => customFrameRate > 0 ? customFrameRate : 30.0, // eCustom
        15 => 96.0,            // eFrames96
        16 => 72.0,            // eFrames72
        17 => 60.0 / 1.001,    // eFrames59dot94
        18 => 120.0 / 1.001,   // eFrames119dot88
        _ => 30.0,             // eDefaultMode / unknown
    };

    private readonly Dictionary<long, FbxObject> _objectsById = new();
    private readonly List<FbxObject> _models = new();
    private readonly List<FbxAnimStack> _stacks = new();
    private readonly Dictionary<long, Matrix4x4> _bindPose = new();
    private readonly Dictionary<string, Dictionary<string, FbxProperty70>> _templates = new();

    private FbxScene()
    {
    }

    /// <summary>
    /// Looks up a property on an object: its own Properties70 first, then the Definitions
    /// property template for its node type. Returns null when neither defines it.
    /// </summary>
    public FbxProperty70? FindProperty(FbxObject obj, string name)
    {
        if (obj.Properties.TryGetValue(name, out var own))
            return own;
        if (_templates.TryGetValue(obj.NodeType, out var template) &&
            template.TryGetValue(name, out var def))
            return def;
        return null;
    }

    /// <summary>Vector property with template fallback and a hard default.</summary>
    public Vector3 GetVector3(FbxObject obj, string name, Vector3 fallback)
    {
        var p = FindProperty(obj, name);
        return p is { Values.Count: >= 3 } ? p.GetVector3() : fallback;
    }

    /// <summary>Double property with template fallback and a hard default.</summary>
    public double GetDouble(FbxObject obj, string name, double fallback)
    {
        var p = FindProperty(obj, name);
        return p is { Values.Count: >= 1 } ? p.GetDouble() : fallback;
    }

    /// <summary>Int property with template fallback and a hard default.</summary>
    public int GetInt(FbxObject obj, string name, int fallback)
    {
        var p = FindProperty(obj, name);
        return p is { Values.Count: >= 1 } ? p.GetInt() : fallback;
    }

    /// <summary>Builds the semantic graph from a tokenized FBX document.</summary>
    public static FbxScene Build(FbxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var scene = new FbxScene();
        scene.ReadGlobalSettings(root.Child("GlobalSettings"));
        scene.ReadTemplates(root.Child("Definitions"));
        scene.ReadObjects(root.Child("Objects"));
        scene.ReadConnections(root.Child("Connections"));
        return scene;
    }

    // ------------------------------------------------------------------ objects

    private void ReadObjects(FbxNode? objects)
    {
        if (objects is null)
            return;

        long syntheticId = -1000; // for exotic files whose objects lack numeric ids

        foreach (var node in objects.Children)
        {
            // Standard 7x object layout: (id:int64, "Name\0\x01Class":string, "SubClass":string).
            long id;
            int nameProp;
            if (node.Properties.Count >= 1 && node.Properties[0] is long or int)
            {
                id = node.Prop<long>(0);
                nameProp = 1;
            }
            else
            {
                id = syntheticId--;
                nameProp = 0;
            }

            string name = "", subClass = "";
            if (node.Properties.Count > nameProp && node.Properties[nameProp] is string rawName)
                (name, _) = FbxNode.SplitName(rawName);
            if (node.Properties.Count > nameProp + 1 && node.Properties[nameProp + 1] is string sub)
                subClass = sub;

            var obj = new FbxObject(id, node.Name, name, subClass, node, ReadProperties70(node));
            _objectsById.TryAdd(obj.Id, obj);

            switch (node.Name)
            {
                case "Model":
                    _models.Add(obj);
                    break;
                case "Pose":
                    if (subClass == "BindPose")
                        ReadBindPose(node);
                    break;
            }
        }
    }

    private void ReadBindPose(FbxNode pose)
    {
        foreach (var poseNode in pose.ChildrenNamed("PoseNode"))
        {
            var nodeId = poseNode.Child("Node");
            var matrix = poseNode.Child("Matrix");
            if (nodeId is null || matrix is null)
                continue;

            double[] m = matrix.AsDoubleArray(0);
            if (m.Length < 16)
                continue;

            // FBX matrices are stored as 16 doubles with translation in elements 12..14 —
            // the same memory layout as System.Numerics row-vector matrices.
            _bindPose[nodeId.Prop<long>(0)] = new Matrix4x4(
                (float)m[0], (float)m[1], (float)m[2], (float)m[3],
                (float)m[4], (float)m[5], (float)m[6], (float)m[7],
                (float)m[8], (float)m[9], (float)m[10], (float)m[11],
                (float)m[12], (float)m[13], (float)m[14], (float)m[15]);
        }
    }

    private static IReadOnlyDictionary<string, FbxProperty70> ReadProperties70(FbxNode node)
    {
        var result = new Dictionary<string, FbxProperty70>(StringComparer.Ordinal);
        var block = node.Child("Properties70") ?? node.Child("Properties60");
        if (block is null)
            return result;

        foreach (var p in block.ChildrenNamed("P"))
        {
            if (p.Properties.Count < 2 || p.Properties[0] is not string name)
                continue;
            string type = p.Properties[1] as string ?? "";
            int valueStart = Math.Min(4, p.Properties.Count);
            var values = p.Properties.GetRange(valueStart, p.Properties.Count - valueStart);
            result[name] = new FbxProperty70(name, type, values);
        }
        return result;
    }

    // ------------------------------------------------------------------ templates

    private void ReadTemplates(FbxNode? definitions)
    {
        if (definitions is null)
            return;

        foreach (var objectType in definitions.ChildrenNamed("ObjectType"))
        {
            if (objectType.Properties.Count < 1 || objectType.Properties[0] is not string typeName)
                continue;
            var template = objectType.Child("PropertyTemplate");
            if (template is null)
                continue;
            _templates[typeName] = new Dictionary<string, FbxProperty70>(
                (Dictionary<string, FbxProperty70>)ReadProperties70(template), StringComparer.Ordinal);
        }
    }

    // ------------------------------------------------------------------ global settings

    private void ReadGlobalSettings(FbxNode? globalSettings)
    {
        if (globalSettings is null)
            return;

        var props = ReadProperties70(globalSettings);

        double D(string name, double fallback)
            => props.TryGetValue(name, out var p) && p.Values.Count > 0 ? p.GetDouble() : fallback;
        int I(string name, int fallback)
            => props.TryGetValue(name, out var p) && p.Values.Count > 0 ? p.GetInt() : fallback;

        UnitScaleFactor = D("UnitScaleFactor", 1.0);
        UpAxis = I("UpAxis", 1);
        UpAxisSign = I("UpAxisSign", 1);
        FrontAxis = I("FrontAxis", 2);
        FrontAxisSign = I("FrontAxisSign", 1);
        CoordAxis = I("CoordAxis", 0);
        CoordAxisSign = I("CoordAxisSign", 1);
        OriginalUpAxis = I("OriginalUpAxis", -1);
        TimeMode = I("TimeMode", 0);
        CustomFrameRate = D("CustomFrameRate", -1.0);
    }

    // ------------------------------------------------------------------ connections

    private void ReadConnections(FbxNode? connections)
    {
        if (connections is null)
            return;

        // First pass: typed object wiring that doesn't depend on other connections.
        // OO  child → parent:   Model→Model (hierarchy), AnimationLayer→AnimationStack,
        //                       AnimationCurveNode→AnimationLayer
        // OP  child → parent.property:
        //                       AnimationCurveNode→Model ("Lcl Translation"/"Lcl Rotation"/"Lcl Scaling")
        //                       AnimationCurve→AnimationCurveNode ("d|X"/"d|Y"/"d|Z")
        var layerToStack = new Dictionary<long, FbxObject>();        // layer id → stack object
        var curveNodeToLayers = new Dictionary<long, List<long>>();  // curve node id → layer ids
        var curveNodeTargets = new Dictionary<long, (long ModelId, string Property)>();
        var curveNodes = new Dictionary<long, FbxAnimCurveNode>();
        var stacksById = new Dictionary<long, FbxAnimStack>();

        foreach (var c in connections.ChildrenNamed("C"))
        {
            if (c.Properties.Count < 3 ||
                c.Properties[0] is not string kind ||
                c.Properties[1] is not (long or int) ||
                c.Properties[2] is not (long or int))
                continue;

            long srcId = c.Prop<long>(1); // child
            long dstId = c.Prop<long>(2); // parent
            if (!_objectsById.TryGetValue(srcId, out var src))
                continue;
            _objectsById.TryGetValue(dstId, out var dst); // dst id 0 = scene root (no object)

            if (kind == "OO")
            {
                if (src.NodeType == "Model" && dst?.NodeType == "Model")
                {
                    if (src.ModelParent is null) // first parent wins on instancing
                    {
                        src.ModelParent = dst;
                        dst.ModelChildren.Add(src);
                    }
                }
                else if (src.NodeType == "AnimationLayer" && dst?.NodeType == "AnimationStack")
                {
                    layerToStack[srcId] = dst;
                }
                else if (src.NodeType == "AnimationCurveNode" && dst?.NodeType == "AnimationLayer")
                {
                    if (!curveNodeToLayers.TryGetValue(srcId, out var layers))
                        curveNodeToLayers[srcId] = layers = new List<long>();
                    layers.Add(dstId);
                }
            }
            else if (kind == "OP" && c.Properties.Count >= 4 && c.Properties[3] is string property)
            {
                if (src.NodeType == "AnimationCurveNode" && dst?.NodeType == "Model")
                {
                    curveNodeTargets[srcId] = (dstId, property);
                }
                else if (src.NodeType == "AnimationCurve" && dst?.NodeType == "AnimationCurveNode")
                {
                    // Channel name "d|X" → axis 'X'.
                    char axis = property.Length > 0 ? property[^1] : '?';
                    if (axis is 'X' or 'Y' or 'Z')
                    {
                        if (!curveNodes.TryGetValue(dstId, out var cn))
                            curveNodes[dstId] = cn = new FbxAnimCurveNode(dst);
                        var curve = ReadCurve(src.Node);
                        if (curve is not null && !cn.Channels.ContainsKey(axis))
                            cn.Channels[axis] = curve;
                    }
                }
            }
        }

        // Second pass: assemble stacks (document order) with flattened bindings.
        foreach (var obj in _objectsById.Values)
        {
            if (obj.NodeType != "AnimationStack")
                continue;
            var stack = new FbxAnimStack(obj)
            {
                LocalStart = ReadTimeProperty(obj, "LocalStart"),
                LocalStop = ReadTimeProperty(obj, "LocalStop"),
            };
            stacksById[obj.Id] = stack;
            _stacks.Add(stack);
        }

        foreach (var (curveNodeId, target) in curveNodeTargets)
        {
            if (!curveNodeToLayers.TryGetValue(curveNodeId, out var layerIds))
                continue;
            if (!curveNodes.TryGetValue(curveNodeId, out var cn))
            {
                // Curve node with defaults but no connected curves still drives the property.
                if (!_objectsById.TryGetValue(curveNodeId, out var cnObj))
                    continue;
                cn = new FbxAnimCurveNode(cnObj);
            }

            foreach (var layerId in layerIds)
            {
                if (!layerToStack.TryGetValue(layerId, out var stackObj) ||
                    !stacksById.TryGetValue(stackObj.Id, out var stack))
                    continue;
                stack.Bindings.TryAdd((target.ModelId, target.Property), cn);
            }
        }
    }

    private long ReadTimeProperty(FbxObject obj, string name)
    {
        var p = FindProperty(obj, name);
        if (p is null || p.Values.Count < 1)
            return 0;
        try
        {
            return Convert.ToInt64(p.Values[0], System.Globalization.CultureInfo.InvariantCulture);
        }
        // ArithmeticException covers OverflowException, which is not s&box-whitelisted
        catch (Exception ex) when (ex is InvalidCastException or ArithmeticException or FormatException)
        {
            return 0;
        }
    }

    private static FbxAnimCurve? ReadCurve(FbxNode curveNode)
    {
        var timesNode = curveNode.Child("KeyTime");
        var valuesNode = curveNode.Child("KeyValueFloat") ?? curveNode.Child("KeyValue");
        if (timesNode is null || valuesNode is null)
            return null;

        long[] times = timesNode.AsLongArray(0);
        float[] values = valuesNode.AsFloatArray(0);
        if (times.Length == 0 || times.Length != values.Length)
            return null;
        return new FbxAnimCurve(times, values);
    }
}
