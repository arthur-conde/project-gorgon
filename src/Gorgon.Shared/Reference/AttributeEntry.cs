namespace Gorgon.Shared.Reference;

/// <summary>
/// Slim projection of one entry in attributes.json. Resolves the <c>{TOKEN}{value}</c>
/// placeholders found in <see cref="ItemEntry.EffectDescs"/> (and in tsysclientinfo.json
/// power entries) to a human-readable <see cref="Label"/> plus a <see cref="DisplayType"/>
/// hint that drives numeric formatting, and a <see cref="DisplayRule"/> gating whether to
/// render at all.
/// </summary>
public sealed record AttributeEntry(
    string Token,
    string Label,
    string DisplayType,
    string DisplayRule,
    double? DefaultValue,
    IReadOnlyList<int> IconIds);
