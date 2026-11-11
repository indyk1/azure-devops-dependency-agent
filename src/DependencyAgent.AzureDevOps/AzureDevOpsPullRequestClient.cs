using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DependencyAgent.Core;
using DependencyAgent.Core.Model;
using Microsoft.Extensions.Logging;

namespace DependencyAgent.AzureDevOps;

/// <summary>A failed Azure DevOps REST call with an actionable message.</summary>
public sealed class AzureDevOpsApiException : Exception
{
    public AzureDevOpsApiException(string message, HttpStatusCode? statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>Creates or updates the single agent PR per repository through the Azure DevOps REST API (7.1).</summary>
public sealed class AzureDevOpsPullRequestClient : IPullRequestClient
{
    /// <summary>Marker identifying the thread that holds the full report.</summary>
    public const string ReportMarker = "<!-- dependency-agent:report -->";

    public const int MaxDescriptionLength = 4000;
    public const int MaxCommentLength = 100_000;
    private const string ApiVersion = "api-version=7.1";

    private readonly HttpClient _http;
    private readonly AzureDevOpsConnection _connection;
    private readonly IReportRenderer _renderer;

    public AzureDevOpsPullRequestClient(HttpClient httpClient, AzureDevOpsConnection connection, IReportRenderer renderer)
    {
        _http = httpClient;
        _connection = connection;
        _renderer = renderer;
    }

    public async Task<PullRequestInfo> CreateOrUpdateAsync(RepoContext context, RepoRunReport report, CancellationToken cancellationToken)
    {
        var project = context.Entry.Project;
        var repo = context.Entry.Repository;
        var baseBranch = context.Entry.BaseBranch;
        var branch = context.Settings.BranchName;
        var apiBase = _connection.GetRepositoryApiUrl(project, repo);
        var settings = context.Settings;

        var title = _renderer.RenderPullRequestTitle(report, settings);
        var description = _renderer.RenderPullRequestDescription(report, MaxDescriptionLength);
        if (description.Length > MaxDescriptionLength)
        {
            description = description[..MaxDescriptionLength];
        }

        var isDraft = report.Outcome != RunOutcome.Green;

        var findUrl = $"{apiBase}/pullrequests?searchCriteria.sourceRefName={Uri.EscapeDataString("refs/heads/" + branch)}"
            + $"&searchCriteria.targetRefName={Uri.EscapeDataString("refs/heads/" + baseBranch)}&searchCriteria.status=active&{ApiVersion}";
        var existing = (await SendAsync(HttpMethod.Get, findUrl, null, cancellationToken))?["value"]?.AsArray().FirstOrDefault();

        int id;
        bool created;
        if (existing is null)
        {
            var body = new JsonObject
            {
                ["sourceRefName"] = "refs/heads/" + branch,
                ["targetRefName"] = "refs/heads/" + baseBranch,
                ["title"] = title,
                ["description"] = description,
                ["isDraft"] = isDraft,
            };
            if (settings.PullRequest.WorkItemIds.Count > 0)
            {
                body["workItemRefs"] = new JsonArray(settings.PullRequest.WorkItemIds.Distinct()
                    .Select(w => (JsonNode)new JsonObject { ["id"] = w.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray());
            }

            var reviewers = await ResolveReviewersAsync(context, cancellationToken);
            if (reviewers.Count > 0)
            {
                body["reviewers"] = new JsonArray(reviewers.ToArray());
            }

            var response = await SendAsync(HttpMethod.Post, $"{apiBase}/pullrequests?{ApiVersion}", body, cancellationToken);
            id = response?["pullRequestId"]?.GetValue<int>() ?? throw new AzureDevOpsApiException("Azure DevOps did not return a pull request id.", null);
            created = true;
            await AddLabelsAsync(context, apiBase, id, cancellationToken);
        }
        else
        {
            id = existing["pullRequestId"]!.GetValue<int>();
            created = false;
            var body = new JsonObject { ["title"] = title, ["description"] = description, ["isDraft"] = isDraft };
            await SendAsync(HttpMethod.Patch, $"{apiBase}/pullrequests/{id}?{ApiVersion}", body, cancellationToken);
        }

        await UpsertReportCommentAsync(apiBase, id, _renderer.RenderFullReport(report), cancellationToken);
        return new PullRequestInfo(id, _connection.GetPullRequestWebUrl(project, repo, id), isDraft, created);
    }

    private async Task<List<JsonNode>> ResolveReviewersAsync(RepoContext context, CancellationToken cancellationToken)
    {
        var result = new List<JsonNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in context.Settings.PullRequest.Reviewers)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            string? id = null;
            if (Guid.TryParse(entry, out var guid))
            {
                id = guid.ToString();
            }
            else
            {
                try
                {
                    id = await ResolveIdentityAsync(entry, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    context.Logger.LogWarning("Could not resolve reviewer '{Reviewer}': {Message}", entry, ex.Message);
                    continue;
                }

                if (id is null)
                {
                    context.Logger.LogWarning("Reviewer '{Reviewer}' could not be resolved to an identity and was not added.", entry);
                }
            }

            if (id is not null && seen.Add(id))
            {
                result.Add(new JsonObject { ["id"] = id, ["isRequired"] = false });
            }
        }

        return result;
    }

    private async Task<string?> ResolveIdentityAsync(string name, CancellationToken cancellationToken)
    {
        if (_connection.IdentityBaseUrl is null)
        {
            throw new InvalidOperationException("identity lookup by name is only supported for dev.azure.com / visualstudio.com; use the reviewer's GUID");
        }

        var url = $"{_connection.IdentityBaseUrl}_apis/identities?searchFilter=General&filterValue={Uri.EscapeDataString(name)}&{ApiVersion}";
        var response = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
        return response?["value"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
    }

    private async Task AddLabelsAsync(RepoContext context, string apiBase, int id, CancellationToken cancellationToken)
    {
        foreach (var label in context.Settings.PullRequest.Labels.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await SendAsync(HttpMethod.Post, $"{apiBase}/pullrequests/{id}/labels?{ApiVersion}", new JsonObject { ["name"] = label.Trim() }, cancellationToken);
            }
            catch (AzureDevOpsApiException ex)
            {
                // Labels are cosmetic; never fail the run over them.
                context.Logger.LogWarning("Could not add label '{Label}' to PR {Id}: {Message}", label, id, ex.Message);
            }
        }
    }

    private async Task UpsertReportCommentAsync(string apiBase, int id, string report, CancellationToken cancellationToken)
    {
        var content = report.Contains(ReportMarker, StringComparison.Ordinal) ? report : ReportMarker + "\n" + report;
        if (content.Length > MaxCommentLength)
        {
            var keep = MaxCommentLength - 200;
            if (char.IsHighSurrogate(content[keep - 1]))
            {
                keep--;
            }

            content = content[..keep] + "\n\n_…report truncated (comment size limit); the full report is attached to the pipeline run._\n";
        }

        var threads = await SendAsync(HttpMethod.Get, $"{apiBase}/pullrequests/{id}/threads?{ApiVersion}", null, cancellationToken);
        foreach (var thread in threads?["value"]?.AsArray() ?? [])
        {
            if (thread?["isDeleted"]?.GetValue<bool>() == true)
            {
                continue;
            }

            var first = thread?["comments"]?.AsArray().FirstOrDefault();
            var text = first?["content"]?.GetValue<string>();
            if (first is null || text is null || !text.Contains(ReportMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var threadId = thread!["id"]!.GetValue<int>();
            var commentId = first["id"]!.GetValue<int>();
            await SendAsync(HttpMethod.Patch, $"{apiBase}/pullrequests/{id}/threads/{threadId}/comments/{commentId}?{ApiVersion}", new JsonObject { ["content"] = content }, cancellationToken);
            return;
        }

        var body = new JsonObject
        {
            ["comments"] = new JsonArray(new JsonObject { ["parentCommentId"] = 0, ["content"] = content, ["commentType"] = 1 }),
            ["status"] = "closed", // closed threads do not block PR completion
        };
        await SendAsync(HttpMethod.Post, $"{apiBase}/pullrequests/{id}/threads?{ApiVersion}", body, cancellationToken);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _connection.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AzureDevOpsApiException(DescribeFailure(method, url, response.StatusCode, text), response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Azure DevOps answers unauthenticated requests with an HTML sign-in page and status 200.
            throw new AzureDevOpsApiException(
                $"{method} {Redact(url)} returned a non-JSON response; the token was probably not accepted (check SYSTEM_ACCESSTOKEN is mapped into the step env).",
                response.StatusCode);
        }
    }

    private string DescribeFailure(HttpMethod method, string url, HttpStatusCode status, string responseBody)
    {
        var detail = responseBody;
        try
        {
            detail = JsonNode.Parse(responseBody)?["message"]?.GetValue<string>() ?? responseBody;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // keep raw body
        }

        detail = detail.Length > 500 ? detail[..500] + "…" : detail;
        var hint = status switch
        {
            HttpStatusCode.Unauthorized => " The access token was rejected; make sure SYSTEM_ACCESSTOKEN is mapped into the step environment (or AZURE_DEVOPS_TOKEN is valid for local runs).",
            HttpStatusCode.Forbidden => " The identity lacks permission. Grant the '<Project> Build Service (<Org>)' identity 'Contribute', 'Create branch' and 'Contribute to pull requests' on the target repository.",
            HttpStatusCode.NotFound => " The repository or pull request was not found or is not visible to the job identity. If it lives in another project, disable 'Limit job authorization scope to current project' (Project/Organization settings > Pipelines > Settings) and grant the build service access.",
            _ => "",
        };
        return $"Azure DevOps request {method} {Redact(url)} failed with {(int)status} {status}: {detail}{hint}";
    }

    private string Redact(string url) => url.Replace(_connection.Token, "***", StringComparison.Ordinal);
}
