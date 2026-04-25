namespace Mithril.Shared.Reference;

public enum ReferenceFileSource
{
    Bundled,
    Cache,
    Cdn,
}

public sealed record ReferenceFileSnapshot(
    string Key,
    ReferenceFileSource Source,
    string CdnVersion,
    DateTimeOffset? FetchedAtUtc,
    int EntryCount);
