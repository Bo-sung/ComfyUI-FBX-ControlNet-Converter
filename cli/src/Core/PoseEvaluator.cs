using System.Numerics;

namespace FbxControlNetConverter.Core;

/// <summary>
/// Samples the animation at a given time, walks the hierarchy to world space, and maps
/// node names to canonical joints via <see cref="BoneMapper"/>. Pure math — the most
/// directly portable part of the pipeline.
/// </summary>
public sealed class PoseEvaluator
{
    private readonly LoadedScene _scene;
    private readonly BoneMapper _mapper;

    public PoseEvaluator(LoadedScene scene, BoneMapper mapper)
    {
        _scene = scene;
        _mapper = mapper;
    }

    /// <summary>Canonical joint name -> world position at <paramref name="timeSeconds"/>.
    /// When several nodes map to the same canonical joint, the first wins.</summary>
    public Dictionary<string, Vector3> EvaluateJoints(double timeSeconds, out List<Vector3> allNodeWorld)
    {
        EvaluateFull(timeSeconds, out var joints, out _, out allNodeWorld);
        return joints;
    }

    /// <summary>Evaluates canonical joints, every node's global transform (by node name, for
    /// skinning), and all node world positions, in one hierarchy walk.</summary>
    public void EvaluateFull(double timeSeconds, out Dictionary<string, Vector3> joints,
                             out Dictionary<string, Matrix4x4> nodeGlobals, out List<Vector3> allNodeWorld)
    {
        double ticks = timeSeconds * _scene.TicksPerSecond;
        joints = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        nodeGlobals = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        allNodeWorld = new List<Vector3>();
        Walk(_scene.Root, Matrix4x4.Identity, ticks, joints, nodeGlobals, allNodeWorld);
    }

    private void Walk(NodeData node, Matrix4x4 parentGlobal, double ticks,
                      Dictionary<string, Vector3> joints, Dictionary<string, Matrix4x4> nodeGlobals,
                      List<Vector3> nodes)
    {
        Matrix4x4 local = node.Channel is { } ch ? SampleLocal(ch, ticks) : node.LocalDefault;
        Matrix4x4 global = local * parentGlobal;       // row-vector: child first
        Vector3 world = global.Translation;
        nodes.Add(world);
        nodeGlobals[node.Name] = global;               // node names are unique in practice

        string canon = _mapper.Normalize(node.Name);
        if (_mapper.IsCanonical(canon) && !joints.ContainsKey(canon))
            joints[canon] = world;

        foreach (var child in node.Children)
            Walk(child, global, ticks, joints, nodeGlobals, nodes);
    }

    private static Matrix4x4 SampleLocal(AnimChannel ch, double ticks)
    {
        Vector3 t = SampleVec(ch.Positions, ticks);
        Quaternion r = SampleQuat(ch.Rotations, ticks);
        Vector3 s = ch.Scales.Length > 0 ? SampleVec(ch.Scales, ticks) : Vector3.One;
        return Matrix4x4.CreateScale(s) *
               Matrix4x4.CreateFromQuaternion(r) *
               Matrix4x4.CreateTranslation(t);
    }

    private static Vector3 SampleVec((double T, Vector3 V)[] keys, double t)
    {
        if (keys.Length == 0) return Vector3.Zero;
        if (keys.Length == 1 || t <= keys[0].T) return keys[0].V;
        if (t >= keys[^1].T) return keys[^1].V;
        int i = FindKey(keys.Length, j => keys[j].T, t);
        float f = Lerp(keys[i].T, keys[i + 1].T, t);
        return Vector3.Lerp(keys[i].V, keys[i + 1].V, f);
    }

    private static Quaternion SampleQuat((double T, Quaternion Q)[] keys, double t)
    {
        if (keys.Length == 0) return Quaternion.Identity;
        if (keys.Length == 1 || t <= keys[0].T) return keys[0].Q;
        if (t >= keys[^1].T) return keys[^1].Q;
        int i = FindKey(keys.Length, j => keys[j].T, t);
        float f = Lerp(keys[i].T, keys[i + 1].T, t);
        return Quaternion.Normalize(Quaternion.Slerp(keys[i].Q, keys[i + 1].Q, f));
    }

    private static int FindKey(int count, Func<int, double> timeOf, double t)
    {
        for (int i = 0; i < count - 1; i++)
            if (t < timeOf(i + 1)) return i;
        return count - 2;
    }

    private static float Lerp(double a, double b, double t) =>
        b > a ? (float)((t - a) / (b - a)) : 0f;
}
