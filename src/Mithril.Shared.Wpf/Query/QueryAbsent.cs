namespace Mithril.Shared.Wpf.Query;

/// <summary>
/// Singleton sentinel returned by a polymorphic element-schema binding when the
/// queried property is <em>not declared on the runtime subtype</em> of the element
/// (e.g. <c>Level</c> queried against a <c>QuestRequirement</c> element whose actual
/// subtype has no <c>Level</c>).
/// </summary>
/// <remarks>
/// This is deliberately distinct from a present-but-<see langword="null"/> value.
/// "Absent" means the property does not exist on this element at all, so every
/// comparison against it — including <c>IS NULL</c> — yields <see langword="false"/>
/// (absent ≠ null). "Present but null" still flows the real <see langword="null"/>
/// through and <c>IS NULL</c> can match it. The two are disambiguated by reflecting
/// the property on the element's concrete runtime type, never by swallowing a
/// reflection exception.
/// </remarks>
internal sealed class QueryAbsent
{
    public static QueryAbsent Value { get; } = new();

    private QueryAbsent()
    {
    }
}
