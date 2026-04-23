namespace Smaug.Domain;

/// <summary>
/// Coarse Civic Pride skill buckets used in calibration rate keys. Individual
/// observations across a small range are bucketed together so the community
/// aggregate stays statistically useful.
/// </summary>
public static class CivicPrideBucket
{
    public const string Under5 = "0-4";
    public const string To14 = "5-14";
    public const string To24 = "15-24";
    public const string To34 = "25-34";
    public const string To44 = "35-44";
    public const string AtLeast45 = "45+";

    public static string FromLevel(int effectiveLevel) => effectiveLevel switch
    {
        <= 4 => Under5,
        <= 14 => To14,
        <= 24 => To24,
        <= 34 => To34,
        <= 44 => To44,
        _ => AtLeast45,
    };
}
