using FbxControlNetConverter.Core.Rendering;

namespace FbxControlNetConverter.Core.Passes;

/// <summary>
/// Plugin registry. Every output is a registered <see cref="IRenderPass"/>; the CLI
/// selects passes by name. Phase-2 mesh passes (depth/normal/alpha/canny) register here.
/// </summary>
public static class PassRegistry
{
    private static readonly Dictionary<string, Func<IRenderPass>> Factories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["openpose"] = () => new OpenPosePass(),
            ["depth"] = () => new DepthPass(),     // mesh passes (RequiresMesh)
            ["normal"] = () => new NormalPass(),
            ["alpha"] = () => new AlphaPass(),
            ["canny"] = () => new CannyPass(),
        };

    public static IEnumerable<string> Names => Factories.Keys;

    public static bool IsKnown(string name) => Factories.ContainsKey(name);

    public static IRenderPass Create(string name) =>
        Factories.TryGetValue(name, out var f)
            ? f()
            : throw new ArgumentException($"Unknown pass '{name}'. Known: {string.Join(", ", Names)}");
}
