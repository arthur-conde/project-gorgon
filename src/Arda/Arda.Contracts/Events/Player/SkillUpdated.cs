using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when a single skill gains XP or levels. Carries both the new
/// absolute values and the XP delta that triggered the update.
/// </summary>
public readonly record struct SkillUpdated(
    string SkillKey,
    int Raw,
    int Bonus,
    int Xp,
    int Tnl,
    int Max,
    int XpGained,
    LogLineMetadata Metadata);
