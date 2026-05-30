using System.Numerics;
using System.Text;
using Silk.NET.Assimp;
using SilkAssimp = Silk.NET.Assimp.Assimp;
using AScene = Silk.NET.Assimp.Scene;
using ANode = Silk.NET.Assimp.Node;
using AAnimation = Silk.NET.Assimp.Animation;
using ANodeAnim = Silk.NET.Assimp.NodeAnim;
using AMesh = Silk.NET.Assimp.Mesh;
using ABone = Silk.NET.Assimp.Bone;

namespace FbxControlNetConverter.Core;

/// <summary>One node of the rig hierarchy, decoupled from Assimp types (eases a C++ port).</summary>
public sealed class NodeData
{
    public required string Name { get; init; }
    public Matrix4x4 LocalDefault { get; init; } = Matrix4x4.Identity;
    public AnimChannel? Channel { get; set; }
    public List<NodeData> Children { get; } = new();
}

/// <summary>Sampled TRS keyframes for one node.</summary>
public sealed class AnimChannel
{
    public required (double T, Vector3 V)[] Positions { get; init; }
    public required (double T, Quaternion Q)[] Rotations { get; init; }
    public required (double T, Vector3 V)[] Scales { get; init; }
}

/// <summary>One mesh with up-to-4 bone influences per vertex (for skinning).</summary>
public sealed class MeshData
{
    public required Vector3[] Positions { get; init; }   // mesh-local (bind) space
    public required Vector3[] Normals { get; init; }
    public required int[] Indices { get; init; }         // triangles
    public required string OwnerNode { get; init; }      // node that holds the mesh (static fallback)
    public bool HasBones { get; init; }
    public string[] BoneNames { get; init; } = Array.Empty<string>();
    public Matrix4x4[] BoneOffsets { get; init; } = Array.Empty<Matrix4x4>(); // inverse bind, Numerics
    public int[] BoneIdx4 { get; init; } = Array.Empty<int>();   // 4 per vertex, index into BoneNames
    public float[] BoneW4 { get; init; } = Array.Empty<float>(); // 4 per vertex
}

/// <summary>Self-contained scene: hierarchy + one animation track (+ meshes when requested).</summary>
public sealed class LoadedScene
{
    public required NodeData Root { get; init; }
    public double DurationTicks { get; init; }
    public double TicksPerSecond { get; init; }
    public MeshData[] Meshes { get; init; } = Array.Empty<MeshData>();
    public double DurationSeconds => TicksPerSecond > 0 ? DurationTicks / TicksPerSecond : 0;
    public bool HasAnimation => DurationTicks > 0;
}

/// <summary>Loads FBX/GLB/BVH via Silk.NET.Assimp and converts to a numerics-friendly tree.</summary>
public static unsafe class SkeletonLoader
{
    /// <param name="animPath">Optional separate animation file (Mixamo "without skin" clip).
    /// Its channels are applied to the rig/mesh from <paramref name="path"/> by matching bone
    /// names — the standard Mixamo rig + animation-clip workflow.</param>
    public static LoadedScene Load(string path, bool loadMeshes = false, string? animPath = null)
    {
        var assimp = SilkAssimp.GetApi();

        // Collapse FBX pre/post-rotation pivot helper nodes into each bone's transform.
        // Without this, Mixamo rigs split a joint across "_$AssimpFbx$_*" nodes and the
        // hierarchy accumulation comes out wrong (e.g. legs inverted).
        var props = assimp.CreatePropertyStore();
        assimp.SetImportPropertyInteger(props, "IMPORT_FBX_PRESERVE_PIVOTS", 0);
        uint flags = (uint)(loadMeshes ? PostProcessSteps.Triangulate : PostProcessSteps.None);

        AScene* scene = assimp.ImportFileExWithProperties(path, flags, null, props);
        if (scene == null || scene->MRootNode == null)
        {
            string err = assimp.GetErrorStringS();
            assimp.ReleasePropertyStore(props);
            throw new InvalidDataException($"Assimp failed to load '{path}': {err}");
        }

        AScene* animScene = null;
        try
        {
            Dictionary<string, AnimChannel> channels;
            double durTicks, tps;

            if (animPath != null)
            {
                animScene = assimp.ImportFileExWithProperties(animPath, (uint)PostProcessSteps.None, null, props);
                if (animScene == null)
                    throw new InvalidDataException(
                        $"Assimp failed to load anim '{animPath}': {assimp.GetErrorStringS()}");
                (channels, durTicks, tps) = ParseAnim(animScene);
                if (channels.Count == 0)
                    throw new InvalidDataException($"Animation file has no animation track: {animPath}");
            }
            else
            {
                (channels, durTicks, tps) = ParseAnim(scene);
            }

            var meshOwner = loadMeshes ? new Dictionary<uint, string>() : null;
            NodeData root = Build(scene->MRootNode, channels, meshOwner);
            MeshData[] meshes = loadMeshes ? ParseMeshes(scene, meshOwner!) : Array.Empty<MeshData>();

            return new LoadedScene
            {
                Root = root, DurationTicks = durTicks, TicksPerSecond = tps, Meshes = meshes,
            };
        }
        finally
        {
            assimp.ReleasePropertyStore(props);
            if (animScene != null) assimp.ReleaseImport(animScene);
            assimp.ReleaseImport(scene);
            assimp.Dispose();
        }
    }

    /// <summary>Parses the first animation track's channels (by node name) + timing.</summary>
    private static (Dictionary<string, AnimChannel>, double, double) ParseAnim(AScene* scene)
    {
        var channels = new Dictionary<string, AnimChannel>(StringComparer.Ordinal);
        double durTicks = 0, tps = 25.0;
        if (scene->MNumAnimations > 0)
        {
            AAnimation* anim = scene->MAnimations[0];
            durTicks = anim->MDuration;
            tps = anim->MTicksPerSecond > 0 ? anim->MTicksPerSecond : 25.0;
            for (uint i = 0; i < anim->MNumChannels; i++)
            {
                ANodeAnim* ch = anim->MChannels[i];
                channels[Str(&ch->MNodeName)] = Convert(ch);
            }
        }
        return (channels, durTicks, tps);
    }

    private static NodeData Build(ANode* n, Dictionary<string, AnimChannel> channels,
                                  Dictionary<uint, string>? meshOwner)
    {
        string name = Str(&n->MName);
        if (meshOwner != null)
            for (uint i = 0; i < n->MNumMeshes; i++)
                meshOwner.TryAdd(n->MMeshes[i], name);

        var node = new NodeData
        {
            Name = name,
            LocalDefault = ToNumerics(n->MTransformation),
            Channel = channels.TryGetValue(name, out var c) ? c : null,
        };
        for (uint i = 0; i < n->MNumChildren; i++)
            node.Children.Add(Build(n->MChildren[i], channels, meshOwner));
        return node;
    }

    private static MeshData[] ParseMeshes(AScene* scene, Dictionary<uint, string> meshOwner)
    {
        var meshes = new MeshData[scene->MNumMeshes];
        for (uint mi = 0; mi < scene->MNumMeshes; mi++)
        {
            AMesh* m = scene->MMeshes[mi];
            int vcount = (int)m->MNumVertices;

            var positions = new Vector3[vcount];
            var normals = new Vector3[vcount];
            for (int v = 0; v < vcount; v++)
            {
                positions[v] = new Vector3(m->MVertices[v].X, m->MVertices[v].Y, m->MVertices[v].Z);
                if (m->MNormals != null)
                    normals[v] = new Vector3(m->MNormals[v].X, m->MNormals[v].Y, m->MNormals[v].Z);
            }

            var indices = new List<int>((int)m->MNumFaces * 3);
            for (uint f = 0; f < m->MNumFaces; f++)
            {
                var face = m->MFaces[f];
                if (face.MNumIndices != 3) continue; // post-triangulation should be 3
                indices.Add((int)face.MIndices[0]);
                indices.Add((int)face.MIndices[1]);
                indices.Add((int)face.MIndices[2]);
            }

            // Skinning: top-4 influences per vertex.
            bool hasBones = m->MNumBones > 0;
            var boneNames = new string[m->MNumBones];
            var boneOffsets = new Matrix4x4[m->MNumBones];
            var infl = new List<(int Bone, float W)>[vcount];
            for (uint b = 0; b < m->MNumBones; b++)
            {
                ABone* bone = m->MBones[b];
                boneNames[b] = Str(&bone->MName);
                boneOffsets[b] = ToNumerics(bone->MOffsetMatrix);
                for (uint w = 0; w < bone->MNumWeights; w++)
                {
                    var vw = bone->MWeights[w];
                    int vid = (int)vw.MVertexId;
                    (infl[vid] ??= new List<(int, float)>()).Add(((int)b, vw.MWeight));
                }
            }

            var boneIdx4 = new int[vcount * 4];
            var boneW4 = new float[vcount * 4];
            for (int v = 0; v < vcount; v++)
            {
                var list = infl[v];
                if (list == null) continue;
                list.Sort((a, c) => c.W.CompareTo(a.W)); // desc
                float sum = 0;
                int take = Math.Min(4, list.Count);
                for (int k = 0; k < take; k++) { boneIdx4[v * 4 + k] = list[k].Bone; boneW4[v * 4 + k] = list[k].W; sum += list[k].W; }
                if (sum > 1e-6f) for (int k = 0; k < take; k++) boneW4[v * 4 + k] /= sum; // renormalize
            }

            meshes[mi] = new MeshData
            {
                Positions = positions,
                Normals = normals,
                Indices = indices.ToArray(),
                OwnerNode = meshOwner.TryGetValue(mi, out var on) ? on : "",
                HasBones = hasBones,
                BoneNames = boneNames,
                BoneOffsets = boneOffsets,
                BoneIdx4 = boneIdx4,
                BoneW4 = boneW4,
            };
        }
        return meshes;
    }

    private static AnimChannel Convert(ANodeAnim* ch)
    {
        var pos = new (double, Vector3)[ch->MNumPositionKeys];
        for (uint i = 0; i < ch->MNumPositionKeys; i++)
        {
            var k = ch->MPositionKeys[i];
            pos[i] = (k.MTime, new Vector3(k.MValue.X, k.MValue.Y, k.MValue.Z));
        }
        var rot = new (double, Quaternion)[ch->MNumRotationKeys];
        for (uint i = 0; i < ch->MNumRotationKeys; i++)
        {
            var k = ch->MRotationKeys[i];
            rot[i] = (k.MTime, new Quaternion(k.MValue.X, k.MValue.Y, k.MValue.Z, k.MValue.W));
        }
        var scl = new (double, Vector3)[ch->MNumScalingKeys];
        for (uint i = 0; i < ch->MNumScalingKeys; i++)
        {
            var k = ch->MScalingKeys[i];
            scl[i] = (k.MTime, new Vector3(k.MValue.X, k.MValue.Y, k.MValue.Z));
        }
        return new AnimChannel { Positions = pos, Rotations = rot, Scales = scl };
    }

    private static string Str(AssimpString* s) =>
        s->Length == 0 ? "" : Encoding.UTF8.GetString(s->Data, (int)s->Length);

    /// <summary>Silk.NET.Assimp fills System.Numerics.Matrix4x4 field-for-field from
    /// aiMatrix4x4 (column-vector, row-major). Transpose to match Numerics' row-vector
    /// convention so v_world = v_local * (m_node * m_parent * ...).</summary>
    private static Matrix4x4 ToNumerics(Matrix4x4 a) => Matrix4x4.Transpose(a);
}
