namespace Saruman.Domain;

public static class TierInference
{
    public static WordOfPowerTier FromCode(string code)
    {
        var len = code?.Length ?? 0;
        var t = len switch
        {
            <= 6 => 1,
            <= 8 => 2,
            <= 10 => 3,
            <= 12 => 4,
            <= 14 => 5,
            _ => 6,
        };
        return (WordOfPowerTier)t;
    }
}
