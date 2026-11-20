using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace DeficitSnatcher.Api.Deploy;

/// <summary>
/// The site's Deploy tab: runs the repository's deploy workflow (GitHub Actions) and shows its runs, the steps of the
/// one running, and the commits on the branch that aren't deployed yet. Off until Deploy__GitHubToken is set: a
/// fine-grained token for this repository with Actions (read and write) and Contents (read).
/// </summary>
public sealed class DeployOptions
{
    public string GitHubToken { get; set; } = "";
    /// <summary>owner/repo</summary>
    public string Repository { get; set; } = "";
    public string Workflow { get; set; } = "Deploy.yml";
    public string Branch { get; set; } = "main";

    public bool Configured => GitHubToken.Length > 0 && Repository.Contains('/');
}

/// <summary>What's running on this server, from the image (Dockerfile: GIT_SHA, BUILT_AT).</summary>
public sealed class ServerVersion(IConfiguration config)
{
    public string? Sha { get; } = config["GIT_SHA"] is { Length: >= 7 } sha && sha != "dev" ? sha : null;
    public DateTimeOffset? BuiltAt { get; } = DateTimeOffset.TryParse(config["BUILT_AT"], out var at) ? at : null;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
}

public sealed record DeployRun(
    long Id,
    int RunNumber,
    string Status,
    string? Conclusion,
    string Title,
    string Event,
    string? Actor,
    string HeadSha,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RunStartedAt,
    DateTimeOffset UpdatedAt,
    string Url);

public sealed record DeployCommit(string Sha, string Message, string? Author, DateTimeOffset? Date, string Url, bool Deployed, bool Pending);

public sealed record DeployStatus(
    bool Configured,
    string Repository,
    string Workflow,
    string Branch,
    string? ServerSha,
    DateTimeOffset? BuiltAt,
    DateTimeOffset StartedAt,
    List<DeployRun> Runs,
    List<DeployCommit> Commits,
    string? Error);

public sealed record DeployStep(int Number, string Name, string Status, string? Conclusion, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);
public sealed record DeployJob(long Id, string Name, string Status, string? Conclusion, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string Url, List<DeployStep> Steps);

[ApiController]
[Route("api/deploy")]
public sealed class DeployController(DeployOptions options, ServerVersion version, IHttpClientFactory httpFactory) : ControllerBase
{
    public const string ClientName = "github";

    public static void Configure(HttpClient c)
    {
        c.BaseAddress = new Uri("https://api.github.com/");
        c.Timeout = TimeSpan.FromSeconds(20);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DeficitSnatcher");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    [HttpGet]
    public async Task<DeployStatus> Status(CancellationToken ct)
    {
        var runs = new List<DeployRun>();
        var commits = new List<DeployCommit>();
        string? error = null;
        if (options.Configured)
        {
            try
            {
                var runsTask = Get($"repos/{options.Repository}/actions/workflows/{options.Workflow}/runs?per_page=15", ct);
                var commitsTask = Get($"repos/{options.Repository}/commits?sha={Uri.EscapeDataString(options.Branch)}&per_page=30", ct);
                using var runsDoc = await runsTask;
                using var commitsDoc = await commitsTask;
                foreach (var r in runsDoc.RootElement.GetProperty("workflow_runs").EnumerateArray())
                    runs.Add(new DeployRun(
                        r.GetProperty("id").GetInt64(),
                        r.GetProperty("run_number").GetInt32(),
                        r.GetProperty("status").GetString() ?? "",
                        r.GetProperty("conclusion").GetString(),
                        r.TryGetProperty("display_title", out var t) ? t.GetString() ?? "" : "",
                        r.GetProperty("event").GetString() ?? "",
                        r.TryGetProperty("triggering_actor", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetProperty("login").GetString() : null,
                        r.GetProperty("head_sha").GetString() ?? "",
                        r.GetProperty("created_at").GetDateTimeOffset(),
                        r.TryGetProperty("run_started_at", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetDateTimeOffset() : null,
                        r.GetProperty("updated_at").GetDateTimeOffset(),
                        r.GetProperty("html_url").GetString() ?? ""));

                var list = commitsDoc.RootElement.EnumerateArray().ToList();
                var deployedAt = version.Sha == null ? -1 : list.FindIndex(c => c.GetProperty("sha").GetString() == version.Sha);
                for (var i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    var commit = c.GetProperty("commit");
                    commits.Add(new DeployCommit(
                        c.GetProperty("sha").GetString() ?? "",
                        commit.GetProperty("message").GetString() ?? "",
                        commit.GetProperty("author").GetProperty("name").GetString(),
                        commit.GetProperty("author").TryGetProperty("date", out var d) ? d.GetDateTimeOffset() : null,
                        c.GetProperty("html_url").GetString() ?? "",
                        i == deployedAt,
                        deployedAt < 0 ? version.Sha == null && i == 0 : i < deployedAt));
                }
            }
            catch (Exception e)
            {
                error = e.Message;
            }
        }
        return new DeployStatus(options.Configured, options.Repository, options.Workflow, options.Branch, version.Sha, version.BuiltAt,
            version.StartedAt, runs, commits, error);
    }

    [HttpGet("runs/{id:long}")]
    public async Task<ActionResult<List<DeployJob>>> Jobs(long id, CancellationToken ct)
    {
        if (!options.Configured)
            return NotFound();
        using var doc = await Get($"repos/{options.Repository}/actions/runs/{id}/jobs", ct);
        return doc.RootElement.GetProperty("jobs").EnumerateArray().Select(j => new DeployJob(
            j.GetProperty("id").GetInt64(),
            j.GetProperty("name").GetString() ?? "",
            j.GetProperty("status").GetString() ?? "",
            j.GetProperty("conclusion").GetString(),
            Date(j, "started_at"),
            Date(j, "completed_at"),
            j.GetProperty("html_url").GetString() ?? "",
            j.TryGetProperty("steps", out var steps)
                ? steps.EnumerateArray().Select(s => new DeployStep(s.GetProperty("number").GetInt32(), s.GetProperty("name").GetString() ?? "",
                    s.GetProperty("status").GetString() ?? "", s.GetProperty("conclusion").GetString(), Date(s, "started_at"), Date(s, "completed_at"))).ToList()
                : [])).ToList();
    }

    /// <summary>Deploys the branch as it is now.</summary>
    [HttpPost]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        if (!options.Configured)
            return Problem("Deploying from here is off: set the DEPLOY_GITHUB_TOKEN secret.", statusCode: 400);
        using var response = await Send(HttpMethod.Post, $"repos/{options.Repository}/actions/workflows/{options.Workflow}/dispatches",
            new { @ref = options.Branch, inputs = new { deploy = true } }, ct);
        return response.IsSuccessStatusCode ? NoContent() : Problem($"GitHub refused: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}", statusCode: 502);
    }

    [HttpPost("runs/{id:long}/cancel")]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct)
    {
        if (!options.Configured)
            return NotFound();
        using var response = await Send(HttpMethod.Post, $"repos/{options.Repository}/actions/runs/{id}/cancel", null, ct);
        return response.IsSuccessStatusCode ? NoContent() : Problem($"GitHub refused: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}", statusCode: 502);
    }

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset() : null;

    private async Task<JsonDocument> Get(string path, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, path, null, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} for {path.Split('?')[0]}: {await response.Content.ReadAsStringAsync(ct)}");
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.GitHubToken);
        if (body != null)
            request.Content = JsonContent.Create(body);
        return httpFactory.CreateClient(ClientName).SendAsync(request, ct);
    }
}
