using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace RemoteManager.Core.Extensibility;

/// <summary>
/// Finds the implementations of an extension interface (connection handlers, importers, exporters,
/// credential sources) in the given assemblies: every public, non-abstract class with a public
/// parameterless constructor. Adding one is adding a file; no list to edit, nothing to merge.
/// Ordered by type name so the result is stable.
/// </summary>
public static class PluginCatalog
{
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The app is not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "The app is not trimmed.")]
    public static IReadOnlyList<T> Create<T>(params Assembly[] assemblies) where T : class =>
        [.. assemblies.Distinct()
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && typeof(T).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (T)Activator.CreateInstance(t)!)];

    /// <summary>The Core assembly, which holds the built-in importers and credential sources.</summary>
    public static Assembly CoreAssembly => typeof(PluginCatalog).Assembly;
}
