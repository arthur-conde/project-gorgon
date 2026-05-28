namespace Mithril.Shared.Telemetry.Abstractions;

/// <summary>
/// DI contract: each subsystem with tagged spans/metrics registers an
/// <see cref="ITagDescriptorProvider"/> singleton. The telemetry catalog
/// resolves <c>IEnumerable&lt;ITagDescriptorProvider&gt;</c> at construction
/// to build the union of all known tag keys.
/// </summary>
public interface ITagDescriptorProvider
{
    IReadOnlyCollection<TagDescriptor> Describe();
}
