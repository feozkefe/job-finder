using System.Collections.Concurrent;
using System.Text.Json;
using JobFinder.Models;

namespace JobFinder.Providers;

/// <summary>
/// Fetches jobs from wearedevelopers.com via its public API
/// (the same one the category pages like /en/jobs/ls/germany/berlin/.net use).
/// </summary>
public class WeAreDevelopersProvider(IHttpClientFactory httpClientFactory, IWebHostEnvironment env) : IJobProvider, IJobDetailsProvider
{
    private const string ApiBase = "https://wad-api.wearedevelopers.com/api";
    private const string SiteBase = "https://www.wearedevelopers.com";
    private const int EnglishLanguageId = 15;

    // slug combination -> resolved filter ids, so we only hit the meta-data endpoint once per filter set
    private static readonly ConcurrentDictionary<string, ResolvedFilters> FilterCache = new();

    public string Id => "wearedevelopers";
    public string DisplayName => "WeAreDevelopers";

    public async Task<JobSearchResult> SearchAsync(JobQuery query, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("wad");
        var filters = await ResolveFiltersAsync(http, query, ct);

        var p = new List<KeyValuePair<string, string>>
        {
            new("hybrid_option", "any"),
            new("orderBy", "newest"),
            new("jobs_page", query.Page.ToString()),
            new("job_listings_page", query.Page.ToString()),
            new("jobs_per_page", "36"),
        };
        if (filters.SkillId is int skillId)
            p.Add(new("skills[]", skillId.ToString()));
        if (filters.LocationId is string locId)
            p.Add(new("locations[]", locId));
        var languages = query.EnglishOnly ? [EnglishLanguageId] : filters.AllLanguageIds;
        foreach (var lang in languages)
            p.Add(new("languages[]", lang.ToString()));

        var url = $"{ApiBase}/v2/jobs?{await new FormUrlEncodedContent(p).ReadAsStringAsync(ct)}";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        var root = doc.RootElement;

        var jobs = new List<Job>();
        if (root.TryGetProperty("job_ads", out var ads))
            foreach (var ad in ads.EnumerateArray())
                jobs.Add(MapJobAd(ad));
        if (root.TryGetProperty("job_listings", out var listings))
            foreach (var listing in listings.EnumerateArray())
                jobs.Add(MapJobListing(listing));

        // job_ads carry an explicit language; drop non-English ones defensively even
        // though the API filter should already have excluded them
        if (query.EnglishOnly)
            jobs = jobs.Where(j => j.Language is null or "English").ToList();

        var hasMore = false;
        if (root.TryGetProperty("pagination", out var pg))
        {
            hasMore = ReadInt(pg, "jobs_next_page") > query.Page
                   || ReadInt(pg, "job_listings_next_page") > query.Page;
        }

        return new JobSearchResult(jobs, hasMore, filters.TotalJobs);
    }

    private static async Task<ResolvedFilters> ResolveFiltersAsync(HttpClient http, JobQuery query, CancellationToken ct)
    {
        var cacheKey = $"{query.Country}/{query.City}/{query.Skill}".ToLowerInvariant();
        if (FilterCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // The meta-data endpoint resolves URL slugs (e.g. germany/berlin/.net) into filter ids.
        // Page type mirrors the site's URLs: "ls" = location+skill, "l" = location only, "s" = skill only.
        var hasLocation = !string.IsNullOrWhiteSpace(query.City) || !string.IsNullOrWhiteSpace(query.Country);
        var hasSkill = !string.IsNullOrWhiteSpace(query.Skill);
        var type = (hasLocation, hasSkill) switch
        {
            (true, true) => "ls",
            (true, false) => "l",
            _ => "s",
        };
        var p = new List<KeyValuePair<string, string>> { new("type", type) };
        foreach (var segment in new[] { query.Country, query.City, query.Skill })
            if (!string.IsNullOrWhiteSpace(segment))
                p.Add(new("params[]", segment.Trim().ToLowerInvariant()));

        var url = $"{ApiBase}/v2/filter/jobs/category-page/meta-data?{await new FormUrlEncodedContent(p).ReadAsStringAsync(ct)}";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        var root = doc.RootElement;

        if (!root.TryGetProperty("valid", out var valid) || !valid.GetBoolean())
            throw new InvalidOperationException(
                $"WeAreDevelopers could not resolve the filter '{cacheKey}'. " +
                "Check the city/skill spelling (they must match the slugs used in wearedevelopers.com URLs, e.g. 'berlin', '.net').");

        int? skillId = root.TryGetProperty("skill", out var skill) && skill.ValueKind == JsonValueKind.Object
            ? skill.GetProperty("id").GetInt32() : null;
        string? locationId = root.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
            ? loc.GetProperty("id").GetString() : null;
        var allLanguages = root.TryGetProperty("language", out var langs) && langs.ValueKind == JsonValueKind.Array
            ? langs.EnumerateArray().Select(l => l.GetInt32()).ToArray() : [EnglishLanguageId];
        int? total = root.TryGetProperty("total_jobs", out var t) && int.TryParse(t.GetString(), out var n) ? n : null;

        var resolved = new ResolvedFilters(skillId, locationId, allLanguages, total);
        FilterCache[cacheKey] = resolved;
        return resolved;
    }

    /// <summary>
    /// Fetches the job's detail page for description language, required spoken
    /// languages and the apply link. Responses are cached on disk (cache/details/).
    /// </summary>
    public async Task<JobDetails?> GetDetailsAsync(Job job, CancellationToken ct)
    {
        var cacheDir = Path.Combine(env.ContentRootPath, "cache", "details");
        Directory.CreateDirectory(cacheDir);
        var cacheFile = Path.Combine(cacheDir, $"wad-{(job.External ? "ext-" : "")}{job.SiteJobId}.json");

        string json;
        if (File.Exists(cacheFile))
        {
            json = await File.ReadAllTextAsync(cacheFile, ct);
        }
        else
        {
            var url = $"{ApiBase}/v2/jobs/details?job_slug={Uri.EscapeDataString(job.Slug)}&job_id={job.SiteJobId}"
                    + (job.External ? "&external=true" : "");
            var http = httpClientFactory.CreateClient("wad");
            var res = await http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
                return null;
            json = await res.Content.ReadAsStringAsync(ct);
            await File.WriteAllTextAsync(cacheFile, json, ct);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var description = string.Join(" ",
            new[] { "employer_description", "description", "candidate_description", "conditions_description" }
                .Select(f => ReadString(root, f)));

        return new JobDetails(
            ContentLanguage: root.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.Object
                ? lang.GetProperty("name").GetString() : null,
            RequiredLanguages: ReadStringArray(root, "languages_skills"),
            PlainDescription: StripHtml(description),
            ApplyUrl: NullIfEmpty(ReadString(root, "apply_url")),
            ContractType: NullIfEmpty(ReadString(root, "contract_type")),
            EmploymentType: NullIfEmpty(ReadString(root, "employment_type")),
            Seniorities: ReadStringArray(root, "seniorities"));
    }

    private Job MapJobAd(JsonElement ad) => new(
        Source: DisplayName,
        Title: ReadString(ad, "title"),
        Company: ReadString(ad, "company_name"),
        Location: ReadString(ad, "location"),
        Url: $"{SiteBase}/en/jobs/{ReadInt(ad, "id")}/{ReadString(ad, "slug")}",
        Skills: ReadStringArray(ad, "skills"),
        Language: ad.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.Object
            ? lang.GetProperty("name").GetString() : null,
        Remote: ad.TryGetProperty("remote", out var r) && r.ValueKind == JsonValueKind.True,
        Salary: NullIfEmpty(ReadString(ad, "salary")),
        PublishedAt: ReadDate(ad, "last_published_at"),
        LogoUrl: NullIfEmpty(ReadString(ad, "logo_url")),
        Seniorities: ReadStringArray(ad, "seniorities"),
        SiteJobId: ReadInt(ad, "id"),
        Slug: ReadString(ad, "slug"),
        External: false);

    private Job MapJobListing(JsonElement listing) => new(
        Source: DisplayName,
        Title: ReadString(listing, "title"),
        Company: ReadString(listing, "company_name"),
        Location: ReadString(listing, "location"),
        Url: $"{SiteBase}/en/jobs/ext/{ReadInt(listing, "id")}/{ReadString(listing, "slug")}",
        Skills: ReadStringArray(listing, "skills"),
        Language: null,
        Remote: listing.TryGetProperty("remote", out var r) && r.ValueKind == JsonValueKind.True,
        Salary: NullIfEmpty(ReadString(listing, "salary")),
        PublishedAt: ReadDate(listing, "last_published_at"),
        LogoUrl: null,
        Seniorities: ReadStringArray(listing, "seniorities"),
        SiteJobId: ReadInt(listing, "id"),
        Slug: ReadString(listing, "slug"),
        External: true);

    private static string StripHtml(string html) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Net.WebUtility.HtmlDecode(html), "<[^>]+>", " ")
            .Replace((char)0xA0, ' ');

    private static string ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static int ReadInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static DateTimeOffset? ReadDate(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private record ResolvedFilters(int? SkillId, string? LocationId, int[] AllLanguageIds, int? TotalJobs);
}
