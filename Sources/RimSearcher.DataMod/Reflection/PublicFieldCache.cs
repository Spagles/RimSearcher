using System.Collections.Concurrent;
using System.Reflection;

namespace RimSearcher.DataMod.Reflection;

internal static class PublicFieldCache
{
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new();

    public static FieldInfo[] Get(Type type) =>
        Fields.GetOrAdd(type, static currentType =>
            currentType.GetFields(BindingFlags.Public | BindingFlags.Instance));
}
