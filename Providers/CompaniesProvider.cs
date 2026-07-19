using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JobFinder.Models;
using JobFinder.Services;

namespace JobFinder.Providers;

/// <summary>
/// Fetches jobs directly from company career boards listed in companies.json.
/// Supports the public APIs of greenhouse, lever, ashby, personio, recruitee,
/// smartrecruiters and workable. Board slugs are guessed from the company name
/// and the result (found or not) is persisted in cache/company-ats.json —
/// fix a wrong guess by adding a 4th element (the slug) in companies.json.
/// </summary>
public class CompaniesProvider(IHttpClientFactory httpClientFactory, IWebHostEnvironment env) : IJobProvider, IJobDetailsProvider
{
    private static readonly string[] SupportedAts =
        ["greenhouse", "lever", "ashby", "personio", "recruitee", "smartrecruiters", "workable"];

    private static readonly TimeSpan BoardTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan NotFoundRetry = TimeSpan.FromDays(7);
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(15);

    private record CompanyEntry(string Name, string City, string Ats, string? SlugOverride);
    private record FetchedJob(Job Job, JobDetails Details);
    private record SlugEntry(string Status, string? Slug, DateTime CheckedAt);

    private static List<FetchedJob>? _snapshot;
    private static DateTime _snapshotAt;
    private static readonly SemaphoreSlim SnapshotLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, JobDetails> DetailsByKey = new();
    private static readonly ConcurrentDictionary<string, string> LazyDetailUrls = new();
    private static readonly ConcurrentDictionary<string, string> CompanyStatus = new();

    public string Id => "companies";
    public string DisplayName => "Company boards";

    public async Task<JobSearchResult> SearchAsync(JobQuery query, CancellationToken ct)
    {
        var all = await GetSnapshotAsync(ct);
        IEnumerable<FetchedJob> filtered = all;

        if (!string.IsNullOrWhiteSpace(query.City))
            filtered = filtered.Where(f => LocationOk(f.Job, query.City));
        if (query.EnglishOnly)
            filtered = filtered.Where(f => f.Details.ContentLanguage != "German");
        if (!string.IsNullOrWhiteSpace(query.Skill))
        {
            var needle = query.Skill.Trim().ToLowerInvariant() switch
            {
                "c-sharp" => "c#",
                var s => s.Replace('-', ' '),
            };
            filtered = filtered.Where(f =>
                f.Job.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || f.Job.Skills.Any(sk => sk.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || f.Details.PlainDescription.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered
            .OrderByDescending(f => f.Job.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();
        const int pageSize = 30;
        var page = Math.Max(1, query.Page);
        var jobs = list.Skip((page - 1) * pageSize).Take(pageSize).Select(f => f.Job).ToList();
        return new JobSearchResult(jobs, page * pageSize < list.Count, list.Count);
    }

    public async Task<JobDetails?> GetDetailsAsync(Job job, CancellationToken ct)
    {
        if (!DetailsByKey.TryGetValue(job.Slug, out var details))
            return null;
        if (details.PlainDescription.Length > 0 || !LazyDetailUrls.TryGetValue(job.Slug, out var url))
            return details;

        // smartrecruiters / workable descriptions are fetched per job on demand
        var body = await CachedGetAsync($"lazy-{Sha(job.Slug)}", url, BodyLooksLikeJson, ct);
        if (body is null)
            return details;

        var text = ExtractLazyDescription(job.Slug, body);
        var merged = details with
        {
            PlainDescription = text,
            ContentLanguage = details.ContentLanguage ?? TextLanguage.Detect(job.Title + " " + text),
        };
        DetailsByKey[job.Slug] = merged;
        return merged;
    }

    /// <summary>Per-company fetch status for /api/companies.</summary>
    public IReadOnlyDictionary<string, string> GetStatus() => CompanyStatus;

    // ---------- snapshot ----------

    private async Task<List<FetchedJob>> GetSnapshotAsync(CancellationToken ct)
    {
        if (_snapshot is not null && DateTime.UtcNow - _snapshotAt < SnapshotTtl)
            return _snapshot;

        await SnapshotLock.WaitAsync(ct);
        try
        {
            if (_snapshot is not null && DateTime.UtcNow - _snapshotAt < SnapshotTtl)
                return _snapshot;

            var companies = LoadCompanies();
            var slugMap = LoadSlugMap();
            var gate = new SemaphoreSlim(10);
            var results = await Task.WhenAll(companies.Select(async c =>
            {
                await gate.WaitAsync(ct);
                try { return await FetchCompanyAsync(c, slugMap, ct); }
                catch { CompanyStatus[c.Name] = "error"; return []; }
                finally { gate.Release(); }
            }));

            SaveSlugMap(slugMap);
            _snapshot = results.SelectMany(r => r).ToList();
            _snapshotAt = DateTime.UtcNow;
            return _snapshot;
        }
        finally
        {
            SnapshotLock.Release();
        }
    }

    private List<CompanyEntry> LoadCompanies()
    {
        var path = Path.Combine(env.ContentRootPath, "companies.json");
        if (!File.Exists(path))
            return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<CompanyEntry>();
        foreach (var row in doc.RootElement.GetProperty("companies").EnumerateArray())
        {
            var cells = row.EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
            if (cells.Length >= 3)
                list.Add(new CompanyEntry(cells[0], cells[1], cells[2].ToLowerInvariant(),
                    cells.Length >= 4 && cells[3].Length > 0 ? cells[3] : null));
        }
        return list;
    }

    private async Task<List<FetchedJob>> FetchCompanyAsync(
        CompanyEntry company, ConcurrentDictionary<string, SlugEntry> slugMap, CancellationToken ct)
    {
        if (!SupportedAts.Contains(company.Ats))
        {
            CompanyStatus[company.Name] = $"unsupported ({company.Ats})";
            return [];
        }

        var candidates = company.SlugOverride is not null
            ? [company.SlugOverride]
            : SlugCandidates(company.Name);

        var mapKey = $"{company.Ats}:{company.Name}";
        if (slugMap.TryGetValue(mapKey, out var known))
        {
            if (known.Status == "ok" && known.Slug is not null)
                candidates = [known.Slug];
            else if (DateTime.UtcNow - known.CheckedAt < NotFoundRetry)
            {
                CompanyStatus[company.Name] = "not found (slug guess failed)";
                return [];
            }
        }

        foreach (var slug in candidates)
        {
            var body = await FetchBoardAsync(company.Ats, slug, ct);
            if (body is null)
                continue;

            slugMap[mapKey] = new SlugEntry("ok", slug, DateTime.UtcNow);
            var jobs = ParseBoard(company, slug, body);
            CompanyStatus[company.Name] = $"ok ({jobs.Count} jobs)";
            foreach (var f in jobs)
                DetailsByKey[f.Job.Slug] = f.Details;
            return jobs;
        }

        slugMap[mapKey] = new SlugEntry("notfound", null, DateTime.UtcNow);
        CompanyStatus[company.Name] = "not found (slug guess failed)";
        return [];
    }

    // ---------- per-ATS fetch ----------

    private async Task<string?> FetchBoardAsync(string ats, string slug, CancellationToken ct)
    {
        var (url, validate) = ats switch
        {
            "greenhouse" => ($"https://boards-api.greenhouse.io/v1/boards/{slug}/jobs?content=true", (Func<string, bool>)(b => b.Contains("\"jobs\""))),
            "lever" => ($"https://api.lever.co/v0/postings/{slug}?mode=json", b => b.TrimStart().StartsWith('[')),
            "ashby" => ($"https://api.ashbyhq.com/posting-api/job-board/{slug}", b => b.Contains("\"jobs\"")),
            "personio" => ($"https://{slug}.jobs.personio.de/xml", b => b.Contains("<position")),
            "recruitee" => ($"https://{slug}.recruitee.com/api/offers/", b => b.Contains("\"offers\"")),
            "smartrecruiters" => ($"https://api.smartrecruiters.com/v1/companies/{slug}/postings?limit=100", b => b.Contains("\"content\"")),
            "workable" => ($"https://apply.workable.com/api/v1/widget/accounts/{slug}", b => b.Contains("\"jobs\"")),
            _ => (null!, null!),
        };
        if (url is null)
            return null;

        var body = await CachedGetAsync($"{ats}-{slug}", url, validate, ct);
        if (body is null || !validate(body))
            return null;

        // smartrecruiters paginates; append remaining pages into a combined array
        if (ats == "smartrecruiters")
            body = await FetchAllSmartRecruitersAsync(slug, body, ct);
        return body;
    }

    private async Task<string> FetchAllSmartRecruitersAsync(string slug, string firstPage, CancellationToken ct)
    {
        var all = new List<string> { firstPage };
        try
        {
            using var doc = JsonDocument.Parse(firstPage);
            var total = doc.RootElement.TryGetProperty("totalFound", out var t) ? t.GetInt32() : 0;
            for (var offset = 100; offset < Math.Min(total, 400); offset += 100)
            {
                var page = await CachedGetAsync($"smartrecruiters-{slug}-{offset}",
                    $"https://api.smartrecruiters.com/v1/companies/{slug}/postings?limit=100&offset={offset}",
                    b => b.Contains("\"content\""), ct);
                if (page is not null)
                    all.Add(page);
            }
        }
        catch { }
        return "[" + string.Join(",", all) + "]"; // array of page objects
    }

    /// <summary>GET with a disk cache under cache/boards/ (TTL 6h, stale kept on failure).</summary>
    private async Task<string?> CachedGetAsync(string key, string url, Func<string, bool> validate, CancellationToken ct)
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
                var isMiss = body == "";
                if (DateTime.UtcNow - fetchedAt < (isMiss ? NotFoundRetry : BoardTtl))
                    return isMiss ? null : body;
                stale = isMiss ? null : body;
            }
            catch { }
        }

        try
        {
            var http = httpClientFactory.CreateClient("ats");
            var res = await http.GetAsync(url, ct);
            if (res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                if (validate(body))
                {
                    await WriteCacheAsync(file, body, ct);
                    return body;
                }
            }
            // remember misses too, so bad slug guesses aren't retried every run
            if ((int)res.StatusCode is 404 or 410)
                await WriteCacheAsync(file, "", ct);
            return stale;
        }
        catch
        {
            return stale;
        }
    }

    private static async Task WriteCacheAsync(string file, string body, CancellationToken ct) =>
        await File.WriteAllTextAsync(file,
            JsonSerializer.Serialize(new { fetchedAt = DateTime.UtcNow, body }), ct);

    private static bool BodyLooksLikeJson(string b) => b.TrimStart().StartsWith('{') || b.TrimStart().StartsWith('[');

    // ---------- per-ATS parse ----------

    private List<FetchedJob> ParseBoard(CompanyEntry company, string slug, string body)
    {
        try
        {
            return company.Ats switch
            {
                "greenhouse" => ParseGreenhouse(company, slug, body),
                "lever" => ParseLever(company, slug, body),
                "ashby" => ParseAshby(company, slug, body),
                "personio" => ParsePersonio(company, slug, body),
                "recruitee" => ParseRecruitee(company, slug, body),
                "smartrecruiters" => ParseSmartRecruiters(company, slug, body),
                "workable" => ParseWorkable(company, slug, body),
                _ => [],
            };
        }
        catch
        {
            CompanyStatus[company.Name] = "parse error";
            return [];
        }
    }

    private List<FetchedJob> ParseGreenhouse(CompanyEntry c, string slug, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<FetchedJob>();
        foreach (var j in doc.RootElement.GetProperty("jobs").EnumerateArray())
        {
            var title = Str(j, "title");
            var desc = StripHtml(System.Net.WebUtility.HtmlDecode(Str(j, "content")));
            var location = j.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object ? Str(loc, "name") : "";
            list.Add(Make(c, "greenhouse", slug, Str(j, "id", true), title, location,
                Str(j, "absolute_url"), desc, Date(j, "updated_at"), [], null));
        }
        return list;
    }

    private List<FetchedJob> ParseLever(CompanyEntry c, string slug, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<FetchedJob>();
        foreach (var j in doc.RootElement.EnumerateArray())
        {
            var location = j.TryGetProperty("categories", out var cat) ? Str(cat, "location") : "";
            var desc = Str(j, "descriptionPlain");
            if (desc.Length == 0) desc = StripHtml(Str(j, "description"));
            DateTimeOffset? created = j.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(ca.GetInt64()) : null;
            list.Add(Make(c, "lever", slug, Str(j, "id"), Str(j, "text"), location,
                Str(j, "hostedUrl"), desc, created, [], null));
        }
        return list;
    }

    private List<FetchedJob> ParseAshby(CompanyEntry c, string slug, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<FetchedJob>();
        foreach (var j in doc.RootElement.GetProperty("jobs").EnumerateArray())
        {
            if (j.TryGetProperty("isListed", out var listed) && listed.ValueKind == JsonValueKind.False)
                continue;
            var location = Str(j, "location");
            if (location.Length == 0) location = Str(j, "locationName");
            var desc = Str(j, "descriptionPlain");
            if (desc.Length == 0) desc = StripHtml(Str(j, "descriptionHtml"));
            var remote = j.TryGetProperty("isRemote", out var r) && r.ValueKind == JsonValueKind.True;
            list.Add(Make(c, "ashby", slug, Str(j, "id"), Str(j, "title"), location,
                Str(j, "jobUrl"), desc, Date(j, "publishedAt"), [], remote));
        }
        return list;
    }

    private List<FetchedJob> ParsePersonio(CompanyEntry c, string slug, string body)
    {
        var doc = XDocument.Parse(body);
        var list = new List<FetchedJob>();
        foreach (var pos in doc.Descendants().Where(e => e.Name.LocalName == "position"))
        {
            string El(string name) => pos.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
            var id = El("id");
            if (id.Length == 0) continue;
            var desc = StripHtml(string.Join(" ",
                pos.Descendants().Where(e => e.Name.LocalName == "value").Select(e => e.Value)));
            var seniorities = MapSeniority(El("seniority"));
            list.Add(Make(c, "personio", slug, id, El("name"), El("office"),
                $"https://{slug}.jobs.personio.de/job/{id}", desc, null, seniorities, null));
        }
        return list;
    }

    private List<FetchedJob> ParseRecruitee(CompanyEntry c, string slug, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<FetchedJob>();
        foreach (var j in doc.RootElement.GetProperty("offers").EnumerateArray())
        {
            var desc = StripHtml(Str(j, "description") + " " + Str(j, "requirements"));
            var location = Str(j, "location");
            if (location.Length == 0) location = Str(j, "city");
            var remote = Str(j, "remote") == "true"
                || (j.TryGetProperty("remote", out var r) && r.ValueKind == JsonValueKind.True);
            var tags = j.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array
                ? tg.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
            list.Add(Make(c, "recruitee", slug, Str(j, "id", true), Str(j, "title"), location,
                Str(j, "careers_url"), desc, Date(j, "created_at"), [], remote, tags));
        }
        return list;
    }

    private List<FetchedJob> ParseSmartRecruiters(CompanyEntry c, string slug, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<FetchedJob>();
        // body is an array of page objects (see FetchAllSmartRecruitersAsync)
        var pages = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var pageEl in pages)
        {
            if (!pageEl.TryGetProperty("content", out var content))
                continue;
            foreach (var j in content.EnumerateArray())
            {
                var id = Str(j, "id");
                var location = j.TryGetProperty("location", out var loc) ? Str(loc, "city") : "";
                var remote = j.TryGetProperty("location", out var loc2)
                    && loc2.TryGetProperty("remote", out var rm)
                    && (rm.ValueKind == JsonValueKind.True || rm.ToString() == "true");
                string? lang = j.TryGetProperty("language", out var lg) ? Str(lg, "code") : null;
                var seniorities = j.TryGetProperty("experienceLevel", out var ex) ? MapSeniority(Str(ex, "label")) : [];
                var f = Make(c, "smartrecruiters", slug, id, Str(j, "name"), location,
                    $"https://jobs.smartrecruiters.com/{slug}/{id}", "", Date(j, "releasedDate"), seniorities, remote);
                f = f with
                {
                    Details = f.Details with
                    {
                        ContentLanguage = lang switch { "en" => "English", "de" => "German", _ => null },
                    },
                };
                f = f with { Job = f.Job with { Language = f.Details.ContentLanguage } };
                DetailsByKey[f.Job.Slug] = f.Details;
                LazyDetailUrls[f.Job.Slug] = $"https://api.smartrecruiters.com/v1/companies/{slug}/postings/{id}";
                list.Add(f);
            }
        }
        return list;
    }

    private List<FetchedJob> ParseWorkable(CompanyEntry c, string slug, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<FetchedJob>();
        foreach (var j in doc.RootElement.GetProperty("jobs").EnumerateArray())
        {
            var shortcode = Str(j, "shortcode");
            var location = string.Join(", ", new[] { Str(j, "city"), Str(j, "country") }.Where(x => x.Length > 0));
            var remote = j.TryGetProperty("telecommuting", out var t) && t.ValueKind == JsonValueKind.True;
            var f = Make(c, "workable", slug, shortcode, Str(j, "title"), location,
                Str(j, "url"), "", Date(j, "published_on"), [], remote);
            LazyDetailUrls[f.Job.Slug] = $"https://apply.workable.com/api/v1/widget/accounts/{slug}/jobs/{shortcode}";
            list.Add(f);
        }
        return list;
    }

    private string ExtractLazyDescription(string key, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (key.StartsWith("smartrecruiters:"))
            {
                var parts = new List<string>();
                if (root.TryGetProperty("jobAd", out var ad) && ad.TryGetProperty("sections", out var sections))
                    foreach (var section in sections.EnumerateObject())
                        if (section.Value.TryGetProperty("text", out var text))
                            parts.Add(text.GetString() ?? "");
                return StripHtml(string.Join(" ", parts));
            }
            // workable
            return StripHtml(Str(root, "description") + " " + Str(root, "requirements"));
        }
        catch
        {
            return "";
        }
    }

    // ---------- mapping helpers ----------

    private FetchedJob Make(CompanyEntry c, string ats, string slug, string jobId, string title,
        string location, string url, string description, DateTimeOffset? published,
        IReadOnlyList<string> seniorities, bool? remote, IReadOnlyList<string>? tags = null)
    {
        var key = $"{ats}:{slug}:{jobId}";
        if (location.Length == 0)
            location = c.City;
        var language = TextLanguage.Detect(title + " " + description);
        var job = new Job(
            Source: DisplayName,
            Title: title,
            Company: c.Name,
            Location: location,
            Url: url,
            Skills: tags ?? [],
            Language: language,
            Remote: remote ?? location.Contains("remote", StringComparison.OrdinalIgnoreCase),
            Salary: null,
            PublishedAt: published,
            LogoUrl: null,
            Seniorities: seniorities,
            SiteJobId: StableId(key),
            Slug: key,
            External: true);
        var details = new JobDetails(language, [], description, url, null, null, seniorities);
        return new FetchedJob(job, details);
    }

    private static IReadOnlyList<string> MapSeniority(string raw)
    {
        var s = raw.ToLowerInvariant();
        if (s.Length == 0) return [];
        if (s.Contains("entry") || s.Contains("junior") || s.Contains("student") || s.Contains("intern"))
            return ["Junior"];
        if (s.Contains("mid") || s.Contains("associate") || s.Contains("experienced") || s.Contains("intermediate"))
            return ["Intermediate"];
        if (s.Contains("senior") || s.Contains("lead") || s.Contains("director") || s.Contains("executive"))
            return ["Senior"];
        return [];
    }

    private static bool LocationOk(Job job, string city)
    {
        if (job.Remote)
            return true;
        var loc = job.Location.Trim().ToLowerInvariant();
        return loc.Length == 0
            || loc.Contains(city, StringComparison.OrdinalIgnoreCase)
            || loc.Contains("remote")
            || loc is "germany" or "deutschland";
    }

    private static string[] SlugCandidates(string name)
    {
        var lower = name.ToLowerInvariant();
        var alnumDots = Regex.Replace(lower, "[^a-z0-9.]", "");
        var alnum = Regex.Replace(lower, "[^a-z0-9]", "");
        var hyphen = Regex.Replace(lower, "[^a-z0-9]+", "-").Trim('-');
        return new[] { alnum, hyphen, alnumDots }.Distinct().Where(s => s.Length > 0).ToArray();
    }

    private ConcurrentDictionary<string, SlugEntry> LoadSlugMap()
    {
        var path = Path.Combine(env.ContentRootPath, "cache", "company-ats.json");
        var map = new ConcurrentDictionary<string, SlugEntry>();
        if (!File.Exists(path))
            return map;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, SlugEntry>>(File.ReadAllText(path));
            foreach (var (k, v) in parsed ?? [])
                map[k] = v;
        }
        catch { }
        return map;
    }

    private void SaveSlugMap(ConcurrentDictionary<string, SlugEntry> map)
    {
        var path = Path.Combine(env.ContentRootPath, "cache", "company-ats.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(
            map.ToDictionary(kv => kv.Key, kv => kv.Value),
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Str(JsonElement e, string name, bool allowNumber = false)
    {
        if (!e.TryGetProperty(name, out var v))
            return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!.Trim(),
            JsonValueKind.Number when allowNumber => v.GetRawText(),
            _ => "",
        };
    }

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;

    private static string StripHtml(string html) =>
        Regex.Replace(Regex.Replace(System.Net.WebUtility.HtmlDecode(html), "<[^>]+>", " "), @"\s+", " ").Trim();

    private static string Sanitize(string s) =>
        Regex.Replace(s, "[^a-zA-Z0-9._-]", "_");

    private static string Sha(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    private static long StableId(string s) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(s)), 0) & long.MaxValue;
}
