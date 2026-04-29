namespace Mithril.Reference.Models;

/// <summary>
/// Marker for sentinel POCO subclasses created when a discriminator value
/// (e.g. a quest requirement's <c>T</c> field) doesn't match any known concrete
/// subclass. The validation harness (<c>BundledDataValidationTests</c>) walks
/// every parsed object and fails on the first <see cref="IUnknownDiscriminator"/>
/// it finds — that's the CDN-drift alarm. Lives in <c>Models/</c> because the
/// sentinel types are POCOs; the *creation* of those instances during
/// deserialization is a Serialization-layer concern.
/// </summary>
public interface IUnknownDiscriminator
{
    /// <summary>
    /// The unrecognized discriminator value as it appeared in the JSON. Settable
    /// so the deserializer can populate it after instantiation; consumers should
    /// treat the property as read-only at runtime.
    /// </summary>
    string DiscriminatorValue { get; set; }
}
