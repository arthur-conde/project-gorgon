namespace Saruman.Domain;

public sealed class KnownWord
{
    public required string Code { get; init; }
    public required string EffectName { get; set; }
    public required string Description { get; set; }
    public required DateTime FirstDiscoveredAt { get; init; }
    public int DiscoveryCount { get; set; } = 1;
    public WordOfPowerState State { get; set; } = WordOfPowerState.Known;
    public DateTime? SpentAt { get; set; }
}
