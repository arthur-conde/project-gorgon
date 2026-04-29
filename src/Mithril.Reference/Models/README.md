# Models/

POCOs mirroring the BundledData JSON files. **No Newtonsoft references** here —
no `[JsonProperty]`, no `[JsonConverter]`, no `JToken` fields, no
`using Newtonsoft.Json;`. JSON-name divergence and polymorphic discrimination
are handled in [`../Serialization/`](../Serialization/).

One folder per CDN source (`Quests/`, `Recipes/`, `Items/`, ...). Abstract base
classes for polymorphic families live alongside their concrete subclasses.

This folder is intentionally dependency-free and would be a one-step `git mv`
into a separate `Mithril.Reference.Models` project if a non-Newtonsoft consumer
ever appears. Don't take that step prematurely.
