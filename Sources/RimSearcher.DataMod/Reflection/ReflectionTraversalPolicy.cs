namespace RimSearcher.DataMod.Reflection;

internal static class ReflectionTraversalPolicy
{
    private static readonly string[] ExcludedNamespacePrefixes =
    {
        "UnityEngine",
        "UnityEditor",
        "Microsoft.",
        "Mono."
    };

    public static bool IsExcludedNamespace(Type type)
    {
        var typeNamespace = type.Namespace;
        if (typeNamespace == null)
            return false;

        foreach (var prefix in ExcludedNamespacePrefixes)
        {
            if (typeNamespace.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
