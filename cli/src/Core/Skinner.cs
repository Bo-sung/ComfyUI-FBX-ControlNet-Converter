using System.Numerics;
using FbxControlNetConverter.Core.Rendering;

namespace FbxControlNetConverter.Core;

/// <summary>
/// Linear-blend skinning: turns bind-pose meshes + per-frame node global transforms into a
/// single world-space triangle soup, then applies the pipeline's space transform (up-axis /
/// scale / centering) to positions and normals so meshes line up with the OpenPose joints.
/// </summary>
public static class Skinner
{
    public static SkinnedMeshState Skin(
        MeshData[] meshes,
        IReadOnlyDictionary<string, Matrix4x4> nodeGlobals,
        Func<Vector3, Vector3> transformPoint,
        Func<Vector3, Vector3> transformDir)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<int>();

        foreach (var mesh in meshes)
        {
            int baseIndex = positions.Count;

            // Precompute the skinning matrix per bone (row-vector: offset then node global).
            Matrix4x4[]? skin = null;
            if (mesh.HasBones)
            {
                skin = new Matrix4x4[mesh.BoneNames.Length];
                for (int b = 0; b < mesh.BoneNames.Length; b++)
                    skin[b] = nodeGlobals.TryGetValue(mesh.BoneNames[b], out var g)
                        ? mesh.BoneOffsets[b] * g
                        : Matrix4x4.Identity;
            }
            Matrix4x4 ownerGlobal = nodeGlobals.TryGetValue(mesh.OwnerNode, out var og)
                ? og : Matrix4x4.Identity;

            for (int v = 0; v < mesh.Positions.Length; v++)
            {
                Matrix4x4 m;
                if (skin != null)
                {
                    m = default; // zero
                    float total = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = mesh.BoneW4[v * 4 + k];
                        if (w <= 0f) continue;
                        m += Mul(skin[mesh.BoneIdx4[v * 4 + k]], w);
                        total += w;
                    }
                    if (total <= 1e-6f) m = ownerGlobal; // unskinned vertex fallback
                }
                else m = ownerGlobal;

                Vector3 world = Vector3.Transform(mesh.Positions[v], m);
                Vector3 n = Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[v], m));

                positions.Add(transformPoint(world));
                normals.Add(Vector3.Normalize(transformDir(n)));
            }

            foreach (var idx in mesh.Indices) indices.Add(baseIndex + idx);
        }

        return new SkinnedMeshState
        {
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            Indices = indices.ToArray(),
        };
    }

    private static Matrix4x4 Mul(Matrix4x4 m, float s) => new(
        m.M11 * s, m.M12 * s, m.M13 * s, m.M14 * s,
        m.M21 * s, m.M22 * s, m.M23 * s, m.M24 * s,
        m.M31 * s, m.M32 * s, m.M33 * s, m.M34 * s,
        m.M41 * s, m.M42 * s, m.M43 * s, m.M44 * s);
}
