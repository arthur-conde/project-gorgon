using CommunityToolkit.Mvvm.ComponentModel;

namespace Arwen.Domain;

/// <summary>
/// A gift observation parked because the inventory tracker didn't know the
/// stack size at delete time (typical: carryover stack from a prior PG session).
/// Holds everything <see cref="CalibrationService.RecordObservation"/> already
/// resolved before bailing on the missing quantity, so confirming the user-supplied
/// size is a one-shot transition into <see cref="GiftObservation"/> without a
/// second reference-data round-trip.
/// </summary>
public sealed partial class PendingGiftObservation : ObservableObject
{
    public required Guid Id { get; init; }
    public required string NpcKey { get; init; }
    public required long InstanceId { get; init; }
    public required string InternalName { get; init; }
    public required string DisplayName { get; init; }
    public required int IconId { get; init; }
    public required double FavorDelta { get; init; }
    public required double ItemValue { get; init; }
    public required int MaxStackSize { get; init; }
    public required IReadOnlyList<MatchedPreference> MatchedPreferences { get; init; }
    public required IReadOnlyList<string> ItemKeywords { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    [ObservableProperty] private int _quantity = 1;
}
