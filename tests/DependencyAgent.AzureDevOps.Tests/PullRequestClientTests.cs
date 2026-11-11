using System.Net;
using System.Text.Json.Nodes;
using DependencyAgent.Core.Model;
using static DependencyAgent.AzureDevOps.Tests.TestSupport;

namespace DependencyAgent.AzureDevOps.Tests;

public class PullRequestClientTests
{
    private const string Api = "https://dev.azure.com/contoso/Proj%20One/_apis/git/repositories/My%20Repo";
    private const string Reviewer = "11111111-2222-3333-4444-555555555555";

    private static (AzureDevOpsPullRequestClient Client, FakeHandler Handler) Create(Func<FakeHandler.Recorded, (HttpStatusCode, string)>? responder = null)
    {
        var handler = new FakeHandler { Responder = responder ?? Default() };
        var connection = new AzureDevOpsConnection("https://dev.azure.com/contoso/", "job-token");
        return (new AzureDevOpsPullRequestClient(new HttpClient(handler), connection, new MarkdownReportRenderer()), handler);
    }

    private static Func<FakeHandler.Recorded, (HttpStatusCode, string)> Default(string existingPrs = "[]", string threads = "[]") => r =>
    {
        if (r.Method == HttpMethod.Get && r.Url.Contains("/pullrequests?"))
        {
            return (HttpStatusCode.OK, $"{{\"value\":{existingPrs}}}");
        }

        if (r.Method == HttpMethod.Post && r.Url.Contains("/pullrequests?"))
        {
            return (HttpStatusCode.OK, "{\"pullRequestId\":42}");
        }

        if (r.Method == HttpMethod.Get && r.Url.Contains("/threads?"))
        {
            return (HttpStatusCode.OK, $"{{\"value\":{threads}}}");
        }

        return (HttpStatusCode.OK, "{}");
    };

    [Fact]
    public async Task Creates_ready_pr_with_labels_workitems_reviewers_and_report_thread()
    {
        var (client, h) = Create();
        var ctx = Context(configure: s =>
        {
            s.PullRequest.WorkItemIds = [101];
            s.PullRequest.Reviewers = [Reviewer];
            s.PullRequest.Labels = ["dependency-agent", "net10"];
        });

        var info = await client.CreateOrUpdateAsync(ctx, SampleReport(), TestContext.Current.CancellationToken);

        Assert.Equal(new PullRequestInfo(42, "https://dev.azure.com/contoso/Proj%20One/_git/My%20Repo/pullrequest/42", false, true), info);
        Assert.All(h.Requests, r => Assert.Equal("Bearer job-token", r.Authorization));

        var find = h.Requests[0];
        Assert.Equal(HttpMethod.Get, find.Method);
        Assert.StartsWith($"{Api}/pullrequests?searchCriteria.sourceRefName=refs%2Fheads%2Fdependency-agent%2Fnet10-lts&searchCriteria.targetRefName=refs%2Fheads%2Fmain&searchCriteria.status=active&api-version=7.1", find.Url);

        var create = h.Requests.Single(r => r.Method == HttpMethod.Post && r.Url == $"{Api}/pullrequests?api-version=7.1");
        var body = JsonNode.Parse(create.Body!)!;
        Assert.Equal("refs/heads/dependency-agent/net10-lts", (string?)body["sourceRefName"]);
        Assert.Equal("refs/heads/main", (string?)body["targetRefName"]);
        Assert.False((bool)body["isDraft"]!);
        Assert.StartsWith("[dependency-agent] Upgrade to net10.0", (string?)body["title"]);
        Assert.True(((string)body["description"]!).Length <= 4000);
        Assert.Equal("101", (string?)body["workItemRefs"]![0]!["id"]);
        Assert.Equal(Reviewer, (string?)body["reviewers"]![0]!["id"]);
        Assert.False((bool)body["reviewers"]![0]!["isRequired"]!);

        var labels = h.Requests.Where(r => r.Url.Contains("/pullrequests/42/labels")).ToList();
        Assert.Equal(["dependency-agent", "net10"], labels.Select(l => (string?)JsonNode.Parse(l.Body!)!["name"]));

        var thread = h.Requests.Single(r => r.Method == HttpMethod.Post && r.Url.Contains("/pullrequests/42/threads?"));
        var tb = JsonNode.Parse(thread.Body!)!;
        Assert.Equal("closed", (string?)tb["status"]);
        Assert.Contains("<!-- dependency-agent:report -->", (string?)tb["comments"]![0]!["content"]);
    }

    [Fact]
    public async Task Creates_draft_when_not_green()
    {
        var (client, h) = Create();
        var info = await client.CreateOrUpdateAsync(Context(), SampleReport(RunOutcome.Draft), TestContext.Current.CancellationToken);
        Assert.True(info.IsDraft);
        var body = JsonNode.Parse(h.Requests.Single(r => r.Method == HttpMethod.Post && r.Url.Contains("/pullrequests?")).Body!)!;
        Assert.True((bool)body["isDraft"]!);
    }

    [Theory]
    [InlineData(RunOutcome.Green, false)]
    [InlineData(RunOutcome.Draft, true)]
    public async Task Updates_existing_pr_and_mirrors_draft_state(RunOutcome outcome, bool expectedDraft)
    {
        var (client, h) = Create(Default("[{\"pullRequestId\":9}]"));
        var info = await client.CreateOrUpdateAsync(Context(), SampleReport(outcome), TestContext.Current.CancellationToken);

        Assert.Equal(9, info.Id);
        Assert.False(info.Created);
        Assert.Equal(expectedDraft, info.IsDraft);
        Assert.DoesNotContain(h.Requests, r => r.Method == HttpMethod.Post && r.Url.Contains("/pullrequests?"));
        Assert.DoesNotContain(h.Requests, r => r.Url.Contains("/labels"));
        var patch = h.Requests.Single(r => r.Method == HttpMethod.Patch && r.Url == $"{Api}/pullrequests/9?api-version=7.1");
        var body = JsonNode.Parse(patch.Body!)!;
        Assert.Equal(expectedDraft, (bool)body["isDraft"]!);
        Assert.NotNull(body["title"]);
        Assert.NotNull(body["description"]);
    }

    [Fact]
    public async Task Updates_existing_report_comment_instead_of_creating_a_thread()
    {
        const string threads = "[{\"id\":3,\"comments\":[{\"id\":1,\"content\":\"hello\"}]},"
            + "{\"id\":7,\"comments\":[{\"id\":1,\"content\":\"<!-- dependency-agent:report -->\\nold\"},{\"id\":2,\"content\":\"reply\"}]}]";
        var (client, h) = Create(Default("[{\"pullRequestId\":9}]", threads));

        await client.CreateOrUpdateAsync(Context(), SampleReport(), TestContext.Current.CancellationToken);

        var patch = h.Requests.Single(r => r.Url.Contains("/threads/7/comments/1"));
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.Contains("### All changes", (string?)JsonNode.Parse(patch.Body!)!["content"]);
        Assert.DoesNotContain(h.Requests, r => r.Method == HttpMethod.Post && r.Url.Contains("/threads?"));
    }

    [Fact]
    public async Task Creates_thread_when_no_marker_thread_exists()
    {
        var (client, h) = Create(Default("[{\"pullRequestId\":9}]", "[{\"id\":3,\"comments\":[{\"id\":1,\"content\":\"hello\"}]}]"));
        await client.CreateOrUpdateAsync(Context(), SampleReport(), TestContext.Current.CancellationToken);
        Assert.Single(h.Requests, r => r.Method == HttpMethod.Post && r.Url.Contains("/pullrequests/9/threads?"));
    }

    [Fact]
    public async Task Report_comment_is_capped()
    {
        var report = SampleReport(RunOutcome.Draft);
        report.Build = new VerificationResult(VerificationStage.Build, false, [], null, new string('z', 5000), TimeSpan.Zero);
        for (var i = 0; i < 3000; i++)
        {
            report.Notes.Add(new string('n', 100) + i);
        }

        var (client, h) = Create();
        await client.CreateOrUpdateAsync(Context(), report, TestContext.Current.CancellationToken);
        var thread = h.Requests.Single(r => r.Method == HttpMethod.Post && r.Url.Contains("/threads?"));
        var content = (string)JsonNode.Parse(thread.Body!)!["comments"]![0]!["content"]!;
        Assert.True(content.Length <= AzureDevOpsPullRequestClient.MaxCommentLength);
        Assert.Contains("truncated", content);
        Assert.StartsWith("<!-- dependency-agent:report -->", content);
    }

    [Fact]
    public async Task Non_guid_reviewers_are_resolved_via_identity_service_and_failures_are_tolerated()
    {
        var (client, h) = Create(r =>
        {
            if (r.Url.StartsWith("https://vssps.dev.azure.com/contoso/_apis/identities", StringComparison.Ordinal))
            {
                return r.Url.Contains("filterValue=alice%40contoso.com")
                    ? (HttpStatusCode.OK, "{\"value\":[{\"id\":\"aaaaaaaa-0000-0000-0000-000000000001\"}]}")
                    : (HttpStatusCode.InternalServerError, "boom");
            }

            return Default()(r);
        });
        var ctx = Context(configure: s => s.PullRequest.Reviewers = ["alice@contoso.com", "ghost@contoso.com", Reviewer, Reviewer]);

        var info = await client.CreateOrUpdateAsync(ctx, SampleReport(), TestContext.Current.CancellationToken);

        Assert.True(info.Created);
        var body = JsonNode.Parse(h.Requests.Single(r => r.Method == HttpMethod.Post && r.Url.Contains("/pullrequests?")).Body!)!;
        Assert.Equal(["aaaaaaaa-0000-0000-0000-000000000001", Reviewer], body["reviewers"]!.AsArray().Select(x => (string?)x!["id"]));
        Assert.Contains(h.Requests, r => r.Url.Contains("filterValue=ghost%40contoso.com"));
    }

    [Fact]
    public async Task Label_failure_does_not_fail_the_run()
    {
        var (client, _) = Create(r => r.Url.Contains("/labels") ? (HttpStatusCode.Forbidden, "{\"message\":\"no\"}") : Default()(r));
        var info = await client.CreateOrUpdateAsync(Context(), SampleReport(), TestContext.Current.CancellationToken);
        Assert.Equal(42, info.Id);
    }

    [Fact]
    public async Task Forbidden_mentions_build_service_permissions()
    {
        var (client, _) = Create(_ => (HttpStatusCode.Forbidden, "{\"message\":\"TF401027: You need the GenericContribute permission\"}"));
        var ex = await Assert.ThrowsAsync<AzureDevOpsApiException>(() => client.CreateOrUpdateAsync(Context(), SampleReport(), TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("Contribute to pull requests", ex.Message);
        Assert.Contains("TF401027", ex.Message);
        Assert.DoesNotContain("job-token", ex.Message);
    }

    [Fact]
    public async Task NotFound_mentions_job_authorization_scope()
    {
        var (client, _) = Create(_ => (HttpStatusCode.NotFound, "{\"message\":\"repo missing\"}"));
        var ex = await Assert.ThrowsAsync<AzureDevOpsApiException>(() => client.CreateOrUpdateAsync(Context(), SampleReport(), TestContext.Current.CancellationToken));
        Assert.Contains("Limit job authorization scope", ex.Message);
    }

    [Fact]
    public async Task Html_sign_in_page_is_reported_as_auth_problem()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, "<html>Sign in</html>"));
        var ex = await Assert.ThrowsAsync<AzureDevOpsApiException>(() => client.CreateOrUpdateAsync(Context(), SampleReport(), TestContext.Current.CancellationToken));
        Assert.Contains("token", ex.Message);
    }
}
