using System.Runtime.CompilerServices;

namespace Microsoft.UI.Xaml.HotReload;

internal static class HotReloadTypeMappings
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Type, Type> OriginalToLatest = new();
    private static readonly Dictionary<Type, Type> ReplacementToOriginal = new();

    public static Type Normalize(Type type)
    {
        return type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
    }

    public static IReadOnlyList<Type> RegisterAndGetOriginalTypes(IEnumerable<Type> updatedTypes)
    {
        var originals = new List<Type>();
        var seen = new HashSet<Type>();

        lock (Gate)
        {
            foreach (var rawType in updatedTypes)
            {
                var type = Normalize(rawType);
                var declaredOriginal = type.GetCustomAttributes(typeof(MetadataUpdateOriginalTypeAttribute), inherit: false)
                    .OfType<MetadataUpdateOriginalTypeAttribute>()
                    .FirstOrDefault()
                    ?.OriginalType;

                Type original;
                if (declaredOriginal != null)
                {
                    original = Normalize(declaredOriginal);
                    if (ReplacementToOriginal.TryGetValue(original, out var firstOriginal))
                    {
                        original = firstOriginal;
                    }

                    ReplacementToOriginal[type] = original;
                    OriginalToLatest[original] = type;
                }
                else if (ReplacementToOriginal.TryGetValue(type, out var mappedOriginal))
                {
                    original = mappedOriginal;
                    OriginalToLatest[original] = type;
                }
                else
                {
                    original = type;
                    OriginalToLatest[type] = type;
                }

                if (seen.Add(original))
                {
                    originals.Add(original);
                }
            }
        }

        return originals;
    }

    public static Type GetLatestType(Type originalOrReplacement)
    {
        var normalized = Normalize(originalOrReplacement);
        lock (Gate)
        {
            if (ReplacementToOriginal.TryGetValue(normalized, out var original))
            {
                normalized = original;
            }

            return OriginalToLatest.TryGetValue(normalized, out var latest) ? latest : originalOrReplacement;
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            OriginalToLatest.Clear();
            ReplacementToOriginal.Clear();
        }
    }
}
