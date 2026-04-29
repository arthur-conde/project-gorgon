// Pre-merge CDN drift detector. Fetches every BundledData file from the live
// Project Gorgon CDN, runs every IParserSpec from Mithril.Reference over the
// fetched JSON, and exits non-zero if any unknown discriminator values appear
// or any file falls below its MinimumEntryCount floor.
//
// Designed to run from CI on a daily cron — see .github/workflows/cdn-drift-check.yml.

using Mithril.Reference;
using Mithril.Tools.RefreshAndValidate;

const string CdnRoot = "https://cdn.projectgorgon.com/";

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mithril.RefreshAndValidate/1.0");

var version = await CdnVersionDetector.TryDetectAsync(http, CdnRoot)
              ?? CdnVersionDetector.FallbackVersion;
Console.WriteLine($"CDN version: {version}");

var fetcher = new HttpFetcher(http, CdnRoot, version);
var specs = ParserRegistry.Discover();

return await CdnDriftValidator.RunAsync(fetcher, version, specs, Console.Out, Console.Error);
