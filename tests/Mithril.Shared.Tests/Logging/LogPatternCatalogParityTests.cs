using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests.Logging;

/// <summary>
/// Asserts that every entry in <c>log-patterns.json</c> matches the
/// corresponding C# <c>[GeneratedRegex]</c> attribute character-for-character.
/// This is the contract that lets the TS port (<c>tools/MithrilLogMcp</c>) read
/// the JSON and stay in lockstep with the Mithril parsers.
/// </summary>
public class LogPatternCatalogParityTests
{
    [Fact]
    public void Every_Catalog_Entry_Resolves_To_A_Real_GeneratedRegex_Method()
    {
        // Force-load every module assembly that hosts a parser referenced by
        // the catalog. Project references in Mithril.Shared.Tests.csproj guarantee
        // these are next to the test DLL at runtime.
        TouchAssemblyForType("Samwise.Parsing.GardenLogParser");
        TouchAssemblyForType("Arwen.Parsing.FavorLogParser");
        TouchAssemblyForType("Smaug.Parsing.VendorLogParser");
        TouchAssemblyForType("Pippin.Parsing.GourmandLogParser");
        TouchAssemblyForType("Saruman.Parsing.WordOfPowerChatParser");
        TouchAssemblyForType("Saruman.Parsing.WordOfPowerDiscoveredParser");
        TouchAssemblyForType("Legolas.Services.ChatLogParser");

        foreach (var (key, entry) in LogPatternCatalog.Current.Regexes)
        {
            entry.CSharp.Should().NotBeNull(
                $"catalog entry '{key}' must declare its C# binding");

            var (declaringType, attribute) = ResolveGeneratedRegex(entry.CSharp!);

            declaringType.Should().NotBeNull(
                $"catalog entry '{key}' targets type '{entry.CSharp!.Type}' which could not be loaded");
            attribute.Should().NotBeNull(
                $"catalog entry '{key}' targets {entry.CSharp!.Type}.{entry.CSharp!.Method} but no [GeneratedRegex] attribute was found there");

            attribute!.Pattern.Should().Be(
                entry.Pattern,
                $"catalog entry '{key}' must match the [GeneratedRegex] pattern on {entry.CSharp!.Type}.{entry.CSharp!.Method}");

            // RegexOptions are informational metadata in the catalog (so the TS port knows
            // which flags to translate). When declared, assert they match the C# attribute;
            // when omitted, no assertion — entries are free to leave options undocumented.
            if (entry.CSharp!.Options is { Count: > 0 } expectedOptions)
            {
                var actualOptions = SplitFlags(attribute.Options);
                actualOptions.Should().BeEquivalentTo(
                    expectedOptions,
                    $"catalog entry '{key}' declares RegexOptions {string.Join('|', expectedOptions)}");
            }
        }
    }

    [Fact]
    public void Catalog_Document_Loads_With_Sensible_Shape()
    {
        var doc = LogPatternCatalog.Current;
        doc.Version.Should().Be(1);
        doc.Regexes.Should().NotBeEmpty();
        doc.Shared.Should().NotBeNull();
        doc.Shared!.SessionMarker.Should().NotBeNull();
        doc.Shared!.SessionMarker!.Literal.Should().Be("ProcessAddPlayer(");
    }

    private static void TouchAssemblyForType(string typeName)
    {
        // Type.GetType only searches loaded assemblies; this triggers a load
        // of the module DLL via the project reference's deployed copy.
        _ = Type.GetType($"{typeName}, {InferAssemblyName(typeName)}", throwOnError: false);
    }

    private static string InferAssemblyName(string typeName) => typeName switch
    {
        var t when t.StartsWith("Samwise.", StringComparison.Ordinal) => "Samwise.Module",
        var t when t.StartsWith("Arwen.", StringComparison.Ordinal) => "Arwen.Module",
        var t when t.StartsWith("Smaug.", StringComparison.Ordinal) => "Smaug.Module",
        var t when t.StartsWith("Pippin.", StringComparison.Ordinal) => "Pippin.Module",
        var t when t.StartsWith("Saruman.", StringComparison.Ordinal) => "Saruman.Module",
        var t when t.StartsWith("Legolas.", StringComparison.Ordinal) => "Legolas.Module",
        _ => "Mithril.Shared",
    };

    private static (Type? declaringType, GeneratedRegexAttribute? attr) ResolveGeneratedRegex(LogPatternCSharpRef csharp)
    {
        Type? type = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = asm.GetType(csharp.Type, throwOnError: false);
            if (type is not null) break;
        }
        if (type is null) return (null, null);

        // [GeneratedRegex] is applied to a partial method declaration. Reflection
        // surfaces a regular MethodInfo for it (the source-generated companion is
        // the implementation but the attribute lives on the declaring partial).
        var method = type.GetMethod(
            csharp.Method,
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy,
            binder: null,
            types: Array.Empty<Type>(),
            modifiers: null);

        if (method is null) return (type, null);

        var attr = method.GetCustomAttribute<GeneratedRegexAttribute>(inherit: false);
        return (type, attr);
    }

    private static IReadOnlyList<string> SplitFlags(RegexOptions options)
    {
        if (options == RegexOptions.None) return Array.Empty<string>();
        return options.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
