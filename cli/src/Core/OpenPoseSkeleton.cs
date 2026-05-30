namespace FbxControlNetConverter.Core;

public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>One drawable keypoint: where it comes from on the rig + its color.</summary>
/// <param name="Index">Index within its group (body 0-17, hand 0-20, face 0-69).</param>
/// <param name="CanonicalBone">Canonical joint name (see BoneMapper). Null = derived
/// from the head bone by approximation (nose/eyes/ears have no Mixamo bone).</param>
public readonly record struct Keypoint(int Index, string? CanonicalBone, Rgb Color);

/// <summary>A limb drawn as a line between two keypoint indices.</summary>
public readonly record struct Limb(int A, int B, Rgb Color);

/// <summary>
/// Standard OpenPose topology (BODY_18 / COCO) + 21-point hands, with the canonical
/// rig bone each keypoint is sampled from. Colors follow the conventional OpenPose
/// palette so output is compatible with SD ControlNet OpenPose models.
/// </summary>
public static class OpenPoseSkeleton
{
    // BODY_18 palette (RGB), one per keypoint index.
    private static readonly Rgb[] BodyColors =
    {
        new(255,0,0),   new(255,85,0),  new(255,170,0), new(255,255,0),
        new(170,255,0), new(85,255,0),  new(0,255,0),   new(0,255,85),
        new(0,255,170), new(0,255,255), new(0,170,255), new(0,85,255),
        new(0,0,255),   new(85,0,255),  new(170,0,255), new(255,0,255),
        new(255,0,170), new(255,0,85),
    };

    // BODY_18 index -> canonical rig bone. Null => head-bone approximation.
    // 0 Nose,1 Neck,2 RSho,3 RElb,4 RWri,5 LSho,6 LElb,7 LWri,
    // 8 RHip,9 RKnee,10 RAnk,11 LHip,12 LKnee,13 LAnk,14 REye,15 LEye,16 REar,17 LEar
    private static readonly string?[] BodyBone =
    {
        null,           "neck",
        "rightarm",     "rightforearm", "righthand",
        "leftarm",      "leftforearm",  "lefthand",
        "rightupleg",   "rightleg",     "rightfoot",
        "leftupleg",    "leftleg",      "leftfoot",
        null, null, null, null,
    };

    public static readonly Keypoint[] Body = BuildBody();

    // Standard 17-limb connection set used by OpenPose body renderers.
    public static readonly Limb[] BodyLimbs =
    {
        L(1,2),  L(1,5),  L(2,3),  L(3,4),  L(5,6),  L(6,7),
        L(1,8),  L(8,9),  L(9,10), L(1,11), L(11,12),L(12,13),
        L(1,0),  L(0,14), L(14,16),L(0,15), L(15,17),
    };

    // ---- Hands (21 keypoints each): 0 wrist + 5 fingers x 4 joints -------------
    private static readonly string[] FingerOrder = { "thumb", "index", "middle", "ring", "pinky" };

    /// <summary>Builds the 21 hand keypoints for a side ("lefthand"/"righthand").</summary>
    public static Keypoint[] BuildHand(string handCanon)
    {
        var kp = new Keypoint[21];
        kp[0] = new Keypoint(0, handCanon, new Rgb(255, 255, 255)); // wrist
        int idx = 1;
        for (int f = 0; f < FingerOrder.Length; f++)
        {
            for (int j = 1; j <= 4; j++)
            {
                // BONE_MAP finger canonicals look like "lefthandthumb1".
                string bone = $"{handCanon}{FingerOrder[f]}{j}";
                kp[idx] = new Keypoint(idx, bone, HandFingerColor(f));
                idx++;
            }
        }
        return kp;
    }

    /// <summary>Hand bone connections (wrist->finger chains).</summary>
    public static readonly Limb[] HandLimbs = BuildHandLimbs();

    // ---- Face (70 keypoints) ----------------------------------------------------
    // Pure Mixamo FBX has no facial mocap; in phase 1 these are approximated from the
    // head bone via a frontal template. Count/structure reserved here; positions are
    // produced by FacePass (TODO) so the topology stays in one place.
    public const int FaceKeypointCount = 70;

    private static Keypoint[] BuildBody()
    {
        var kp = new Keypoint[18];
        for (int i = 0; i < 18; i++)
            kp[i] = new Keypoint(i, BodyBone[i], BodyColors[i]);
        return kp;
    }

    private static Limb L(int a, int b) => new(a, b, BodyColors[b]);

    private static Rgb HandFingerColor(int finger) => finger switch
    {
        0 => new Rgb(255, 0, 0),
        1 => new Rgb(255, 170, 0),
        2 => new Rgb(0, 255, 0),
        3 => new Rgb(0, 170, 255),
        _ => new Rgb(170, 0, 255),
    };

    private static Limb[] BuildHandLimbs()
    {
        var limbs = new List<Limb>();
        for (int f = 0; f < 5; f++)
        {
            int baseIdx = 1 + f * 4;
            limbs.Add(new Limb(0, baseIdx, HandFingerColor(f)));         // wrist -> knuckle
            for (int j = 0; j < 3; j++)
                limbs.Add(new Limb(baseIdx + j, baseIdx + j + 1, HandFingerColor(f)));
        }
        return limbs.ToArray();
    }
}
