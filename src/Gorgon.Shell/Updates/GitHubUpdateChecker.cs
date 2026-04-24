using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Gorgon.Shared.Diagnostics;

namespace Gorgon.Shell.Updates;

public sealed class GitHubUpdateChecker : IUpdateChecker
{
    private const string RepoOwner = "arthur-conde";
    private const string RepoName = "project-gorgon";
    private const string ApiBase = "https://api.github.com";

    private readonly HttpClient _http;
    private readonly IUpdateStatusService _status;
    private readonly IDiagnosticsSink _diag;

    public GitHubUpdateChecker(HttpClient http, IUpdateStatusService status, IDiagnosticsSink diag)
    {
        _http = http;
        _status = status;
        _diag = diag;
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        if (!_status.Local.HasCommitSha)
        {
            _status.ReportNotApplicable();
            _diag.Info("updates", "Skipping update check — assembly has no git commit SHA (release build?).");
            return;
        }

        _status.BeginCheck();

        try
        {
            var head = await GetMainHeadAsync(ct).ConfigureAwait(false);
            if (head is null || string.IsNullOrEmpty(head.Sha))
            {
                _status.ReportError("GitHub returned no commit info.");
                return;
            }

            if (string.Equals(head.Sha, _status.Local.CommitSha, StringComparison.OrdinalIgnoreCase) ||
                head.Sha.StartsWith(_status.Local.CommitSha!, StringComparison.OrdinalIgnoreCase) ||
                _status.Local.CommitSha!.StartsWith(head.Sha, StringComparison.OrdinalIgnoreCase))
            {
                _status.ReportResult(
                    remoteSha: head.Sha,
                    remoteCommittedAt: head.Commit?.Committer?.Date ?? head.Commit?.Author?.Date,
                    status: UpdateComparisonStatus.Identical,
                    behindBy: 0,
                    compareUrl: null);
                _diag.Info("updates", $"Build is up to date ({_status.Local.ShortCommitSha}).");
                return;
            }

            var compare = await GetCompareAsync(_status.Local.CommitSha!, head.Sha, ct).ConfigureAwait(false);
            if (compare is null)
            {
                _status.ReportError("GitHub compare endpoint returned no data.");
                return;
            }

            var status = compare.Status switch
            {
                "identical" => UpdateComparisonStatus.Identical,
                "behind"    => UpdateComparisonStatus.Behind,
                "ahead"     => UpdateComparisonStatus.Ahead,
                "diverged"  => UpdateComparisonStatus.Diverged,
                _            => UpdateComparisonStatus.Unknown,
            };

            _status.ReportResult(
                remoteSha: head.Sha,
                remoteCommittedAt: head.Commit?.Committer?.Date ?? head.Commit?.Author?.Date,
                status: status,
                behindBy: compare.BehindBy,
                compareUrl: compare.HtmlUrl);

            _diag.Info("updates",
                $"Update check: local={_status.Local.ShortCommitSha} remote={head.Sha[..Math.Min(10, head.Sha.Length)]} status={compare.Status} behind={compare.BehindBy}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _status.ReportError(ex.Message);
            _diag.Warn("updates", $"Update check failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private async Task<GitHubCommitResponse?> GetMainHeadAsync(CancellationToken ct)
    {
        using var req = BuildRequest($"{ApiBase}/repos/{RepoOwner}/{RepoName}/commits/main");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(
            stream,
            GitHubApiJsonContext.Default.GitHubCommitResponse,
            ct).ConfigureAwait(false);
    }

    private async Task<GitHubCompareResponse?> GetCompareAsync(string baseSha, string headSha, CancellationToken ct)
    {
        using var req = BuildRequest($"{ApiBase}/repos/{RepoOwner}/{RepoName}/compare/{baseSha}...{headSha}");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Local SHA is unknown to GitHub (fork, unpushed branch, rewritten history).
            // Treat as "ahead/diverged" — we don't nag.
            return new GitHubCompareResponse { Status = "diverged" };
        }
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(
            stream,
            GitHubApiJsonContext.Default.GitHubCompareResponse,
            ct).ConfigureAwait(false);
    }

    private HttpRequestMessage BuildRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var version = string.IsNullOrWhiteSpace(_status.Local.SemanticVersion) ? "dev" : _status.Local.SemanticVersion;
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Gorgon", version));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }
}
