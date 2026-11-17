#nullable enable
using System;
using System.Reflection;

namespace ArenaBoxBot;

internal static class RecommendationMemberAccess
{
    internal static string? ReadText(object? recommendation, string name)
    {
        if (recommendation == null) return null;
        var type = recommendation.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        var field = type.GetField(name, flags);
        if (field != null) return field.FieldType == typeof(string) ? field.GetValue(recommendation) as string : null;
        // HsMod's normalized ExecutableAction getter is internal, while its
        // Action and RequestId are public fields. Both shapes are deliberate.
        var property = type.GetProperty(name, flags | BindingFlags.NonPublic);
        return property?.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0
            ? property.GetValue(recommendation, null) as string : null;
    }

    internal static string? ReadAction(object? recommendation)
        => ReadText(recommendation, "ExecutableAction") ?? ReadText(recommendation, "Action");
}
