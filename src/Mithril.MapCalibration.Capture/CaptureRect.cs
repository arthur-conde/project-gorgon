namespace Mithril.MapCalibration.Capture;

/// <summary>
/// A desktop-pixel rectangle: the persisted map bbox and the resolved game
/// client rect both use it. Deliberately a plain BCL struct (no
/// <see cref="System.Windows.Rect"/>) so it can cross into the BCL-only
/// detection core without dragging a WPF dependency through the boundary.
/// </summary>
public readonly record struct CaptureRect(int X, int Y, int Width, int Height)
{
    /// <summary>An empty rect carries no capturable pixels.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Right edge (exclusive).</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge (exclusive).</summary>
    public int Bottom => Y + Height;

    /// <summary>
    /// The overlapping region of this rect and <paramref name="other"/>, clamped
    /// to their shared extent. A non-overlapping pair yields an
    /// <see cref="IsEmpty"/> rect (non-positive width/height).
    /// </summary>
    public CaptureRect Intersect(CaptureRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return new CaptureRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// True when <paramref name="inner"/> lies fully within this rect.
    /// </summary>
    public bool Contains(CaptureRect inner) =>
        inner.X >= X && inner.Y >= Y && inner.Right <= Right && inner.Bottom <= Bottom;
}
