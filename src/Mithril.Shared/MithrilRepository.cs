namespace Mithril.Shared;

/// <summary>
/// Single source of truth for the GitHub URLs the app talks to (own repo + community-calibration repo).
/// Update here on owner / rename transfers; everywhere else reads through these constants.
/// </summary>
public static class MithrilRepository
{
    /// <summary>Canonical URL of the Mithril code repo on GitHub.</summary>
    public const string Url = "https://github.com/moumantai-gg/mithril";

    /// <summary>Canonical URL of the community-calibration data repo on GitHub.</summary>
    public const string CalibrationUrl = "https://github.com/moumantai-gg/mithril-calibration";

    /// <summary>
    /// <c>raw.githubusercontent</c> URL template for aggregated community-calibration JSON.
    /// <c>{0}</c> is the module key (e.g. <c>samwise</c>, <c>arwen</c>).
    /// </summary>
    public const string CalibrationDataUrlTemplate =
        "https://raw.githubusercontent.com/moumantai-gg/mithril-calibration/main/aggregated/{0}.json";

    /// <summary>
    /// New-issue URL template for the calibration repo's Share dialog.
    /// <c>{0}</c> = issue-template filename, <c>{1}</c> = URL-encoded body.
    /// </summary>
    public const string CalibrationIssueUrlTemplate =
        CalibrationUrl + "/issues/new?template={0}&body={1}";
}
