using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Mithril.Reference;

/// <summary>
/// Reflection-based discovery of every <see cref="IParserSpec"/> implementation
/// shipped in the <c>Mithril.Reference</c> assembly. Used by the validation
/// theory test to drive a uniform set of gates across every BundledData
/// source without per-file test boilerplate.
/// </summary>
public static class ParserRegistry
{
    /// <summary>
    /// Discovers every concrete <see cref="IParserSpec"/> in the supplied
    /// assembly (or in <c>Mithril.Reference</c> by default), instantiates
    /// each via its parameterless constructor, and returns them sorted by
    /// <see cref="IParserSpec.FileName"/> for stable test ordering.
    /// </summary>
    public static IReadOnlyList<IParserSpec> Discover(Assembly? assembly = null)
    {
        assembly ??= typeof(IParserSpec).Assembly;

        return assembly.GetTypes()
            .Where(t => !t.IsAbstract
                        && !t.IsInterface
                        && typeof(IParserSpec).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IParserSpec)Activator.CreateInstance(t)!)
            .OrderBy(s => s.FileName, StringComparer.Ordinal)
            .ToArray();
    }
}
