using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Boardwalk.Services;

public sealed class GitHubIssue
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>open or closed.</summary>
    public string State { get; set; } = "open";
    public List<string> Labels { get; set; } = new();
}

/// <summary>A GitHub issue or pull request address, parsed.</summary>
public sealed record GitHubRef(string Owner, string Repo, bool IsPull, int Number);

/// <summary>
/// Talks to the GitHub REST API: lists a repo's open issues, checks whether a linked issue or pull request
/// is open, closed or merged, and closes an issue. Public repos work without a token; private repos and
/// closing issues need a personal access token (Settings › GitHub).
/// </summary>
public static class GitHubService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Boardwalk", App.Version));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        h.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return h;
    }

    public static bool HasToken => SettingsService.GitHubToken.Length > 0;

    private static HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        var token = SettingsService.GitHubToken;
        if (token.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>"owner/repo", "https://github.com/owner/repo(.git)" or "github.com/owner/repo/..." → (owner, repo).</summary>
    public static bool TryParseRepo(string text, out string owner, out string repo)
    {
        owner = repo = "";
        var t = (text ?? "").Trim();
        var m = Regex.Match(t, @"^(?:https?://)?(?:www\.)?github\.com/([\w.-]+)/([\w.-]+?)(?:\.git)?(?:/.*)?$", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(t, @"^([\w.-]+)/([\w.-]+?)(?:\.git)?$");
        if (!m.Success) return false;
        owner = m.Groups[1].Value;
        repo = m.Groups[2].Value;
        return true;
    }

    public static GitHubRef ParseLink(string url)
    {
        var m = Regex.Match((url ?? "").Trim(), @"^(?:https?://)?(?:www\.)?github\.com/([\w.-]+)/([\w.-]+)/(issues|pull)/(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return new GitHubRef(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.Equals("pull", StringComparison.OrdinalIgnoreCase), int.Parse(m.Groups[4].Value));
    }

    private static async Task<JsonDocument> GetJson(string path)
    {
        using var resp = await Http.SendAsync(Request(HttpMethod.Get, path));
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(Explain(resp, body));
        return JsonDocument.Parse(body);
    }

    private static string Explain(HttpResponseMessage resp, string body)
    {
        string msg = "";
        try { msg = JsonDocument.Parse(body).RootElement.GetProperty("message").GetString(); } catch { }
        return (int)resp.StatusCode switch
        {
            401 => "GitHub didn't accept the token. Check it in Settings › GitHub.",
            403 when msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase) =>
                "GitHub's limit for requests without a token was reached. Add a token in Settings › GitHub, or try again in an hour.",
            403 => "GitHub refused: " + msg + (HasToken ? "" : " (Adding a token in Settings › GitHub may help.)"),
            404 => HasToken ? "GitHub couldn't find that repository or issue, or the token can't see it."
                            : "GitHub couldn't find that repository or issue. If it's private, add a token in Settings › GitHub.",
            _ => $"GitHub said {(int)resp.StatusCode}: {msg}",
        };
    }

    /// <summary>Open issues (not pull requests), newest first, up to 300.</summary>
    public static async Task<List<GitHubIssue>> GetOpenIssues(string owner, string repo)
    {
        var list = new List<GitHubIssue>();
        for (int page = 1; page <= 3; page++)
        {
            using var doc = await GetJson($"repos/{owner}/{repo}/issues?state=open&per_page=100&page={page}");
            int count = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                count++;
                if (e.TryGetProperty("pull_request", out _)) continue;
                list.Add(new GitHubIssue
                {
                    Number = e.GetProperty("number").GetInt32(),
                    Title = e.GetProperty("title").GetString() ?? "",
                    Body = e.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : "",
                    Url = e.GetProperty("html_url").GetString() ?? "",
                    State = e.GetProperty("state").GetString() ?? "open",
                    Labels = e.TryGetProperty("labels", out var labels)
                        ? labels.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.Object && l.TryGetProperty("name", out var n) ? n.GetString() : l.GetString()).Where(n => n != null).ToList()
                        : new(),
                });
            }
            if (count < 100) break;
        }
        return list;
    }

    /// <summary>open, closed or merged.</summary>
    public static async Task<string> GetState(GitHubRef r)
    {
        if (r.IsPull)
        {
            using var doc = await GetJson($"repos/{r.Owner}/{r.Repo}/pulls/{r.Number}");
            if (doc.RootElement.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True) return "merged";
            return doc.RootElement.GetProperty("state").GetString() ?? "";
        }
        using (var doc = await GetJson($"repos/{r.Owner}/{r.Repo}/issues/{r.Number}"))
            return doc.RootElement.GetProperty("state").GetString() ?? "";
    }

    public static async Task CloseIssue(GitHubRef r)
    {
        if (r.IsPull) return;
        var req = Request(HttpMethod.Patch, $"repos/{r.Owner}/{r.Repo}/issues/{r.Number}");
        req.Content = new StringContent("{\"state\":\"closed\",\"state_reason\":\"completed\"}", Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(Explain(resp, await resp.Content.ReadAsStringAsync()));
    }

    /// <summary>Refreshes the state of GitHub links on a board (oldest checks first, at most <paramref name="max"/>). Returns how many changed.</summary>
    public static async Task<int> RefreshBoard(Board b, int max = 40)
    {
        var cards = b.AllCards.Where(c => !c.Archived && ParseLink(c.Link) != null)
            .OrderBy(c => c.LinkChecked ?? DateTime.MinValue).Take(max).ToList();
        int changed = 0;
        foreach (var c in cards)
        {
            var state = await GetState(ParseLink(c.Link));
            c.LinkChecked = DateTime.Now;
            if (state != c.LinkState) { c.LinkState = state; changed++; }
        }
        return changed;
    }
}
