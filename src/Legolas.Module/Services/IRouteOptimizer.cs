using Legolas.Domain;

namespace Legolas.Services;

public interface IRouteOptimizer
{
    /// <summary>
    /// Returns indices into <paramref name="points"/> giving an open-path route
    /// from <paramref name="start"/> that visits every point.
    /// </summary>
    IReadOnlyList<int> Optimize(
        PixelPoint start,
        IReadOnlyList<PixelPoint> points,
        CancellationToken cancellationToken = default);
}
