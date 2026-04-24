using System.Text.Json.Serialization;

namespace Gorgon.Shell.Updates;

public sealed class GitHubCommitResponse
{
    [JsonPropertyName("sha")] public string? Sha { get; set; }
    [JsonPropertyName("commit")] public GitHubCommitDetail? Commit { get; set; }
}

public sealed class GitHubCommitDetail
{
    [JsonPropertyName("committer")] public GitHubCommitAuthor? Committer { get; set; }
    [JsonPropertyName("author")] public GitHubCommitAuthor? Author { get; set; }
}

public sealed class GitHubCommitAuthor
{
    [JsonPropertyName("date")] public DateTimeOffset? Date { get; set; }
}

public sealed class GitHubCompareResponse
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("ahead_by")] public int AheadBy { get; set; }
    [JsonPropertyName("behind_by")] public int BehindBy { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubCommitResponse))]
[JsonSerializable(typeof(GitHubCompareResponse))]
public partial class GitHubApiJsonContext : JsonSerializerContext { }
