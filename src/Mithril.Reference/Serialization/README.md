# Serialization/

All Newtonsoft + JsonSubTypes usage is confined to this folder. Public surface
exposed to consumers is the `ReferenceDeserializer` entry points; everything
else is `internal`.

- `Converters/` — reusable `JsonConverter`s. The bridge primitives that turn
  loose Unity-shaped JSON (single-or-list, string-or-int, fallback-on-unknown
  discriminator) into clean POCOs.
- `Discriminators/` — fluent `JsonSubTypes` registration per polymorphic family.
  Maps `T` discriminator strings to the concrete subclasses defined in
  `Models/`.
- `ReferenceDeserializer.cs` — public `Parse<File>` entry points.
- `SerializerSettings.cs` — configured `JsonSerializerSettings` factory:
  contract resolver, converter list, default-value handling, missing-member
  tolerance.

The `Models/` layer never references anything in this folder. If you find
yourself wanting to add a `[JsonConverter]` attribute to a POCO, push the
mapping into a converter or contract resolver here instead.
