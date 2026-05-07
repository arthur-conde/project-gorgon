namespace Legolas.Domain;

/// <summary>
/// Step magnitude tier for on-screen nudge pad clicks. Mirrors the three
/// tiers users can hot-key (NudgePinUp/Fast/Fine etc.). Each tier resolves
/// to a magnitude via <c>LegolasSettings.NudgeStepDefault/Fast/Fine</c>.
/// </summary>
public enum NudgeStepSize
{
    Fine,
    Default,
    Fast,
}
