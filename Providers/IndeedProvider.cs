using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobFinder.Models;
using JobFinder.Services;

namespace JobFinder.Providers;

/// <summary>
/// Scrapes de.indeed.com search pages. Job cards are embedded in the HTML as
/// JSON (window.mosaic.providerData["mosaic-provider-jobcards"]); the full
/// description comes from the job's viewjob page. Everything is cached on disk
/// because Indeed rate-limits aggressive scraping.
///
/// Requests go through the system curl.exe: Indeed's bot protection fingerprints
/// the .NET HttpClient TLS stack and answers it with a "Security Check" page,
/// while Windows curl (Schannel) passes.
/// </summary>
public class IndeedProvider(IWebHostEnvironment env) : IJobProvider, IJobDetailsProvider
{
    private const string Base = "https://de.indeed.com";
    private const string CardsMarker = "window.mosaic.providerData[\"mosaic-provider-jobcards\"]=";
    private static readonly TimeSpan SearchTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan DetailsTtl = TimeSpan.FromDays(7);

    // one Indeed request at a time with a pause in between — bursts trip its bot protection
    private static readonly SemaphoreSlim FetchGate = new(1, 1);

    // snippet + metadata captured from the search card, used until details are fetched
    private static readonly ConcurrentDictionary<string, JobDetails> StubDetails = new();

    public string Id => "indeed";
    public string DisplayName => "Indeed";

    public async Task<JobSearchResult> SearchAsync(JobQuery query, CancellationToken ct)
    {
        // Indeed blocks paging (start=N) without a JS challenge, so we only ever read
        // page 1 — but twice: page 1 = relevance order, "page 2" = newest first.
        // lang=en makes Indeed itself return only English-language postings.
        var q = string.IsNullOrWhiteSpace(query.Skill) ? ".net" : query.Skill.Trim();
        var page = Math.Max(1, query.Page);
        if (page > 2)
            return new JobSearchResult([], false, null);

        var url = $"{Base}/jobs?q={Uri.EscapeDataString(q)}&l={Uri.EscapeDataString(query.City)}&radius=35"
                + (query.EnglishOnly ? "&lang=en" : "")
                + (page == 2 ? "&sort=date" : "");

        var html = await CachedGetAsync(
            $"indeed-search-{Sanitize(q)}-{query.City}{(query.EnglishOnly ? "-en" : "")}{(page == 2 ? "-date" : "")}",
            url, SearchTtl, ct);
        if (html is null)
            return new JobSearchResult([], false, null);

        var results = ExtractJobCards(html);
        var jobs = new List<Job>();
        foreach (var card in results)
        {
            var job = MapCard(card);
            if (job is null)
                continue;
            if (query.EnglishOnly && job.Language == "German")
                continue;
            jobs.Add(job);
        }
        return new JobSearchResult(jobs, page < 2 && results.Count > 0, null);
    }

    public async Task<JobDetails?> GetDetailsAsync(Job job, CancellationToken ct)
    {
        var jobkey = job.Slug;
        var html = await CachedGetAsync($"indeed-job-{jobkey}", $"{Base}/viewjob?jk={jobkey}", DetailsTtl, ct);
        if (html is null)
            return StubDetails.GetValueOrDefault(job.Slug);

        var description = ExtractDescription(html);
        if (description.Length == 0)
            return StubDetails.GetValueOrDefault(job.Slug);

        return new JobDetails(
            ContentLanguage: TextLanguage.Detect(job.Title + " " + description),
            RequiredLanguages: [],
            PlainDescription: description,
            ApplyUrl: $"{Base}/viewjob?jk={jobkey}",
            ContractType: null,
            EmploymentType: null,
            Seniorities: []);
    }

    // ---------- parsing ----------

    private static List<JsonElement> ExtractJobCards(string html)
    {
        var i = html.IndexOf(CardsMarker, StringComparison.Ordinal);
        if (i < 0)
            return [];
        var json = ExtractBalancedJson(html, i + CardsMarker.Length);
        if (json is null)
            return [];

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("metaData", out var meta)
            || !meta.TryGetProperty("mosaicProviderJobCardsModel", out var model)
            || !model.TryGetProperty("results", out var results))
            return [];
        return results.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private Job? MapCard(JsonElement card)
    {
        var jobkey = Str(card, "jobkey");
        var title = Str(card, "displayTitle");
        if (title.Length == 0) title = Str(card, "title");
        if (jobkey.Length == 0 || title.Length == 0)
            return null;

        var snippet = StripHtml(Str(card, "snippet"));
        var remote = card.TryGetProperty("remoteLocation", out var r) && r.ValueKind == JsonValueKind.True;
        DateTimeOffset? published = card.TryGetProperty("pubDate", out var pd) && pd.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(pd.GetInt64()) : null;
        string? salary = null;
        if (card.TryGetProperty("extractedSalary", out var sal) && sal.ValueKind == JsonValueKind.Object)
        {
            var min = sal.TryGetProperty("min", out var mn) && mn.ValueKind == JsonValueKind.Number ? mn.GetDouble() : 0;
            var max = sal.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetDouble() : 0;
            if (max > 0) salary = $"{min:0}–{max:0} EUR";
        }

        var language = TextLanguage.Detect(title + " " + snippet);
        var job = new Job(
            Source: DisplayName,
            Title: title,
            Company: Str(card, "company"),
            Location: Str(card, "formattedLocation"),
            Url: $"{Base}/viewjob?jk={jobkey}",
            Skills: [],
            Language: language,
            Remote: remote,
            Salary: salary,
            PublishedAt: published,
            LogoUrl: null,
            Seniorities: [],
            SiteJobId: StableId(jobkey),
            Slug: jobkey,
            External: true);

        StubDetails[jobkey] = new JobDetails(language, [], snippet, job.Url, null, null, []);
        return job;
    }

    /// <summary>Pulls the plain text of the div#jobDescriptionText block (balanced div scan).</summary>
    private static string ExtractDescription(string html)
    {
        var i = html.IndexOf("id=\"jobDescriptionText\"", StringComparison.Ordinal);
        if (i < 0)
            return "";
        var open = html.LastIndexOf("<div", i, StringComparison.Ordinal);
        if (open < 0)
            return "";

        var depth = 0;
        var pos = open;
        while (pos < html.Length)
        {
            var nextOpen = html.IndexOf("<div", pos, StringComparison.Ordinal);
            var nextClose = html.IndexOf("</div>", pos, StringComparison.Ordinal);
            if (nextClose < 0)
                break;
            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                pos = nextOpen + 4;
            }
            else
            {
                depth--;
                pos = nextClose + 6;
                if (depth == 0)
                    return StripHtml(html[open..pos]);
            }
        }
        return "";
    }

    /// <summary>Extracts a balanced JSON object starting at <paramref name="start"/>, respecting strings.</summary>
    private static string? ExtractBalancedJson(string s, int start)
    {
        int depth = 0;
        var inString = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return s[start..(i + 1)];
                    break;
            }
        }
        return null;
    }

    // ---------- http & cache ----------

    private async Task<string?> CachedGetAsync(string key, string url, TimeSpan ttl, CancellationToken ct)
    {
        var dir = Path.Combine(env.ContentRootPath, "cache", "boards");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Sanitize(key) + ".json");

        string? stale = null;
        if (File.Exists(file))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
                var fetchedAt = doc.RootElement.GetProperty("fetchedAt").GetDateTime();
                var body = doc.RootElement.GetProperty("body").GetString() ?? "";
                if (DateTime.UtcNow - fetchedAt < ttl && body.Length > 0)
                    return body;
                stale = body.Length > 0 ? body : null;
            }
            catch { }
        }

        await FetchGate.WaitAsync(ct);
        try
        {
            var body = await CurlGetAsync(url, ct);
            await Task.Delay(800, ct);
            // a blocked/captcha response has no job data — don't cache it over good data
            if (body is not null && (body.Contains(CardsMarker) || body.Contains("jobDescriptionText")))
            {
                await File.WriteAllTextAsync(file,
                    JsonSerializer.Serialize(new { fetchedAt = DateTime.UtcNow, body }), ct);
                return body;
            }
            return stale;
        }
        catch
        {
            return stale;
        }
        finally
        {
            FetchGate.Release();
        }
    }

    private static async Task<string?> CurlGetAsync(string url, CancellationToken ct)
    {
        // prefer Git's curl — its TLS handshake passes Indeed's bot check more reliably
        var curl = new[]
        {
            @"C:\Program Files\Git\mingw64\bin\curl.exe",
            @"C:\Program Files (x86)\Git\mingw64\bin\curl.exe",
        }.FirstOrDefault(File.Exists) ?? "curl.exe";

        var psi = new ProcessStartInfo
        {
            FileName = curl,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[]
        {
            "-s", "-L", "--max-time", "25", "--compressed",
            "-H", "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36",
            "-H", "Accept-Language: en-US,en;q=0.9,de;q=0.8",
            "-H", "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            url,
        })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null)
            return null;
        var body = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? body : null;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static string StripHtml(string html) =>
        Regex.Replace(Regex.Replace(System.Net.WebUtility.HtmlDecode(html), "<[^>]+>", " "), @"\s+", " ").Trim();

    private static string Sanitize(string s) =>
        Regex.Replace(s, "[^a-zA-Z0-9._-]", "_");

    private static long StableId(string s) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(s)), 0) & long.MaxValue;
}
