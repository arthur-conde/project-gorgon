using System;
using System.Collections.Generic;

namespace Mithril.Reference.Serialization.Discriminators;

/// <summary>
/// A public projection of one discriminated-union family: its abstract base type,
/// the JSON discriminator field name, and the discriminator-value → concrete-subtype
/// map. The per-family <c>*Discriminators</c> classes keep their maps private and
/// expose only this read-only view, so the query engine's polymorphic-schema
/// classifier (in <c>Mithril.Shared.Wpf</c>) can enumerate subtypes for
/// <c>WITH ANY|ALL</c> element-schema building without taking a dependency on the
/// serializer internals.
/// </summary>
public sealed record PolymorphicHierarchy(
    Type BaseType,
    string DiscriminatorField,
    IReadOnlyDictionary<string, Type> KnownTypes);
