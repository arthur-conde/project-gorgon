namespace Legolas.Domain;

public readonly record struct MetreOffset(double East, double North)
{
    public static MetreOffset Zero => new(0, 0);

    public double Magnitude => Math.Sqrt(East * East + North * North);
}
