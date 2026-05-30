using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FbxControlNetConverter.Core;

/// <summary>
/// Normalizes arbitrary rig bone names (Mixamo, Unreal, Unity, Blender, DAZ, etc.) to
/// canonical joint names: strip the namespace, drop known tool prefixes/suffixes, collapse
/// separators, then match against the synonym table. The table lives in data/bone_map.json
/// (independently compiled from public rig naming conventions) so a future C++ core reuses it.
/// </summary>
public sealed class BoneMapper
{
    private readonly Dictionary<string, string> _map;        // synonym -> canonical
    private readonly string[] _keysByLengthDesc;             // for endsWith fallback
    private readonly Dictionary<string, string> _special;    // exact-match special cases

    private static readonly Regex PrefixRe =
        new(@"^(b_|j_bip_|bip_|cc_base_|def_|org_|mch_|mixamorig\d*_?|mixamo_?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SuffixRe =
        new(@"(ik|fk|nub|end|twist\d*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StripRe =
        new(@"[\s\-_.\[\]]+", RegexOptions.Compiled);

    private BoneMapper(Dictionary<string, string> map, Dictionary<string, string> special)
    {
        _map = map;
        _special = special;
        _keysByLengthDesc = map.Keys.OrderByDescending(k => k.Length).ToArray();
    }

    public static BoneMapper Load(string boneMapJsonPath)
    {
        using var fs = File.OpenRead(boneMapJsonPath);
        var data = JsonSerializer.Deserialize<BoneMapData>(fs, JsonOpts)
                   ?? throw new InvalidDataException($"Failed to parse {boneMapJsonPath}");
        return FromData(data);
    }

    internal static BoneMapper FromData(BoneMapData data)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (canonical, synList) in data.Synonyms)
        {
            map[canonical] = canonical;
            foreach (var syn in synList) map[syn] = canonical;
        }

        // Finger expansion (matches the JS side/finger loop).
        var sides = new (string Canon, string[] Shorts)[]
        {
            ("lefthand",  new[] { "l", "left" }),
            ("righthand", new[] { "r", "right" }),
        };
        foreach (var (canon, shorts) in sides)
        {
            foreach (var finger in data.Fingers)
            {
                for (int i = 1; i <= 4; i++)
                {
                    var canonical = $"{canon}{finger}{i}";
                    map[canonical] = canonical;
                    foreach (var s in shorts)
                    {
                        map[$"{s}{finger}{i}"] = canonical;
                        map[$"{finger}{i}{s}"] = canonical;
                        map[$"{s}{finger}0{i}"] = canonical;
                        if (finger == "pinky")
                        {
                            map[$"{s}little{i}"] = canonical;
                            map[$"little{i}{s}"] = canonical;
                            map[$"{s}pinkie{i}"] = canonical;
                        }
                    }
                }
            }
        }

        return new BoneMapper(map, data.SpecialCases);
    }

    /// <summary>Returns the canonical joint name for a raw bone name, or the cleaned
    /// name if unmapped (caller treats unmapped names as non-joint nodes).</summary>
    public string Normalize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";

        // Take the segment after the last :, / or | (e.g. "Armature|mixamorig:Hips").
        string clean = name;
        int sep = clean.LastIndexOfAny(new[] { ':', '/', '|' });
        if (sep >= 0) clean = clean[(sep + 1)..];

        string lower = clean.ToLowerInvariant();
        if (_special.TryGetValue(lower, out var sc)) return sc;

        clean = PrefixRe.Replace(clean, "");
        clean = SuffixRe.Replace(clean, "");
        clean = StripRe.Replace(clean, "");
        clean = clean.ToLowerInvariant();

        if (_map.TryGetValue(clean, out var direct)) return direct;
        foreach (var key in _keysByLengthDesc)
            if (clean.EndsWith(key, StringComparison.Ordinal)) return _map[key];

        return clean;
    }

    /// <summary>True if the normalized name is a recognized canonical joint.</summary>
    public bool IsCanonical(string normalized) => _map.ContainsKey(normalized);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    internal sealed class BoneMapData
    {
        [JsonPropertyName("synonyms")]
        public Dictionary<string, string[]> Synonyms { get; set; } = new();
        [JsonPropertyName("fingers")]
        public string[] Fingers { get; set; } = Array.Empty<string>();
        [JsonPropertyName("specialCases")]
        public Dictionary<string, string> SpecialCases { get; set; } = new();
    }
}
