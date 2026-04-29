using Newtonsoft.Json.Serialization;

namespace Mithril.Reference.Serialization;

/// <summary>
/// Default contract resolver for the reference layer. POCO property names
/// match JSON property names exactly (including non-idiomatic underscores
/// like <c>Reward_Favor</c>, <c>Rewards_Items</c>, <c>ReuseTime_Days</c>),
/// so no name remapping is needed today. Kept as a named subclass so future
/// per-type customisation has a single place to live.
/// </summary>
internal sealed class BundledDataContractResolver : DefaultContractResolver
{
}
