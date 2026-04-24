using System.Reflection;

namespace Gorgon.Shell.Updates;

/// <summary>
/// Reflects the Velopack update channel embedded in the entry assembly via
/// <c>AssemblyMetadataAttribute("GorgonUpdateChannel", ...)</c>. CI publishes with
/// <c>-p:GorgonUpdateChannel=selfcontained</c> or <c>fxdep</c>; local F5 builds get the
/// <see cref="DevChannel"/> sentinel and the update checker short-circuits.
/// </summary>
public sealed record UpdateChannelInfo(string Name, string DisplayName)
{
    public const string DevChannel = "dev";
    public const string SelfContainedChannel = "selfcontained";
    public const string FrameworkDependentChannel = "fxdep";

    public bool IsDevelopment => string.Equals(Name, DevChannel, StringComparison.OrdinalIgnoreCase);
    public bool IsFrameworkDependent => string.Equals(Name, FrameworkDependentChannel, StringComparison.OrdinalIgnoreCase);
    public bool IsSelfContained => string.Equals(Name, SelfContainedChannel, StringComparison.OrdinalIgnoreCase);

    public static UpdateChannelInfo FromEmbedded() =>
        FromAssembly(Assembly.GetEntryAssembly() ?? typeof(UpdateChannelInfo).Assembly);

    public static UpdateChannelInfo FromAssembly(Assembly asm)
    {
        var raw = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "GorgonUpdateChannel", StringComparison.Ordinal))?
            .Value;

        var name = string.IsNullOrWhiteSpace(raw) ? DevChannel : raw.Trim().ToLowerInvariant();
        return new UpdateChannelInfo(name, DisplayFor(name));
    }

    private static string DisplayFor(string name) => name switch
    {
        SelfContainedChannel       => "Self-contained",
        FrameworkDependentChannel  => "Framework-dependent",
        DevChannel                 => "Development build",
        _                          => name,
    };
}
