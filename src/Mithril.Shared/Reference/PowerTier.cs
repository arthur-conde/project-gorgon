namespace Mithril.Shared.Reference;

/// <summary>
/// One tier within a <see cref="PowerEntry"/>. <see cref="Tier"/> is the numeric suffix
/// of the <c>id_N</c> key in tsysclientinfo.json; <see cref="EffectDescs"/> uses the same
/// <c>{TOKEN}{value}</c> format as items.json and resolves via <see cref="EffectDescsRenderer"/>.
/// </summary>
public sealed record PowerTier(
    int Tier,
    IReadOnlyList<string> EffectDescs,
    int MaxLevel);
