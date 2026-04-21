namespace Legolas.Domain;

public sealed record Survey(
    Guid Id,
    string Name,
    MetreOffset Offset,
    PixelPoint? PixelPos,
    PixelPoint? ManualOverride,
    int GridIndex,
    bool Collected,
    bool Skipped,
    int? RouteOrder)
{
    public PixelPoint? EffectivePixel => ManualOverride ?? PixelPos;

    public bool IsCorrected => ManualOverride.HasValue;

    public static Survey Create(string name, MetreOffset offset, int gridIndex) =>
        new(Guid.NewGuid(), name, offset, null, null, gridIndex, false, false, null);
}
