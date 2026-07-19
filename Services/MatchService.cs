using System.Text.RegularExpressions;
using JobFinder.Models;
using JobFinder.Providers;

namespace JobFinder.Services;

/// <summary>
/// Runs the "For Me" search:
///  - WeAreDevelopers: only the profile's WAD searches (default ".net"), English, ALL levels.
///  - Company boards: English jobs that are .NET (any level) OR non-senior jobs
///    in the profile's junior fields (AI/ML, fullstack, Java, Python, ...).
/// Jobs requiring German are dropped in both sources (unless toggled on),
/// and everything is scored against the profile skills.
/// </summary>
public class MatchService(IEnumerable<IJobProvider> providers)
{
    // Strong signals in a job description that German is a hard requirement.
    private static readonly Regex GermanRequiredRx = new(
        @"deutschkenntnisse|verhandlungssicher\w*\s+deutsch|flie(ß|ss)end\w*\s+deutsch|" +
        @"german\s*(\(|at\s+|language\s+)?(level\s*)?(b2|c1|c2)|" +
        @"(fluent|fluency|proficien\w+|business[- ]fluent|excellent|native)[^.]{0,25}german|" +
        @"german[^.]{0,40}(required|mandatory|must|essential)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ...unless the same sentence says it is optional.
    private static readonly Regex GermanOptionalRx = new(
        @"german[^.]{0,60}(plus|advantage|bonus|nice[- ]to[- ]have|not required|optional|desirable|beneficial|helpful)|" +
        @"(plus|advantage|bonus|nice[- ]to[- ]have|optional)[^.]{0,40}german",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<MatchResult> MatchAsync(string siteId, Profile profile, bool includeGermanRequired, CancellationToken ct)
    {
        var wantWad = siteId is "all" or "wearedevelopers";
        var wantCompanies = siteId is "all" or "companies";
        var wantIndeed = siteId is "all" or "indeed";
        if (!wantWad && !wantCompanies && !wantIndeed)
            throw new InvalidOperationException($"Unknown site '{siteId}'.");

        var matched = new List<MatchedJob>();
        int scanned = 0, excludedGermanRequired = 0, excludedGermanContent = 0;

        if (wantWad && Find("wearedevelopers") is (IJobProvider wad and IJobDetailsProvider wadDetails))
        {
            var candidates = await CollectWadCandidatesAsync(wad, profile, ct);
            scanned += candidates.Count;
            await ProcessAsync(wadDetails, candidates, profile, includeGermanRequired,
                keep: static (_, _, _) => true,
                matched, c => excludedGermanRequired += c, c => excludedGermanContent += c, ct);
        }

        if (wantCompanies && Find("companies") is (IJobProvider comp and IJobDetailsProvider compDetails))
        {
            var candidates = await CollectCompanyCandidatesAsync(comp, profile, ct);
            scanned += candidates.Count;
            await ProcessAsync(compDetails, candidates, profile, includeGermanRequired,
                keep: (job, details, seniority) =>
                    IsDotnet(job, details, profile)
                    || (seniority != "Senior" && IsJuniorField(job, profile)),
                matched, c => excludedGermanRequired += c, c => excludedGermanContent += c, ct);
        }

        if (wantIndeed && Find("indeed") is (IJobProvider indeed and IJobDetailsProvider indeedDetails))
        {
            var candidates = await CollectIndeedCandidatesAsync(indeed, profile, ct);
            scanned += candidates.Count;
            await ProcessAsync(indeedDetails, candidates, profile, includeGermanRequired,
                keep: (job, details, seniority) =>
                    IsDotnet(job, details, profile) || seniority == "Junior",
                matched, c => excludedGermanRequired += c, c => excludedGermanContent += c, ct);
        }

        // the same posting often appears on several sources — keep the best-scoring copy
        var deduped = matched
            .OrderByDescending(m => m.Score)
            .DistinctBy(m => (m.Job.Title.Trim().ToLowerInvariant(), m.Job.Company.Trim().ToLowerInvariant()))
            .OrderByDescending(m => m.Score).ThenByDescending(m => m.Job.PublishedAt)
            .ToList();

        return new MatchResult(deduped, scanned, excludedGermanRequired, excludedGermanContent);
    }

    private IJobProvider? Find(string id) =>
        providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ---------- candidate collection ----------

    private static async Task<List<Job>> CollectWadCandidatesAsync(IJobProvider provider, Profile profile, CancellationToken ct)
    {
        var seen = new HashSet<(bool, long)>();
        var jobs = new List<Job>();
        foreach (var skill in profile.WadSkillSlugs.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct())
        {
            for (var page = 1; page <= Math.Max(1, profile.PagesPerSearch); page++)
            {
                var result = await provider.SearchAsync(
                    new JobQuery(skill, profile.City, profile.Country, EnglishOnly: true, page), ct);
                foreach (var job in result.Jobs)
                    if (seen.Add((job.External, job.SiteJobId)))
                        jobs.Add(job);
                if (!result.HasMore)
                    break;
            }
        }
        return jobs;
    }

    private static async Task<List<Job>> CollectIndeedCandidatesAsync(IJobProvider provider, Profile profile, CancellationToken ct)
    {
        var seen = new HashSet<(bool, long)>();
        var jobs = new List<Job>();
        foreach (var search in profile.IndeedSearches.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct())
        {
            for (var page = 1; page <= Math.Min(4, Math.Max(1, profile.PagesPerSearch)); page++)
            {
                var result = await provider.SearchAsync(
                    new JobQuery(search, profile.City, profile.Country, EnglishOnly: true, page), ct);
                foreach (var job in result.Jobs)
                    if (seen.Add((job.External, job.SiteJobId)))
                        jobs.Add(job);
                if (!result.HasMore)
                    break;
            }
        }
        return jobs;
    }

    private static async Task<List<Job>> CollectCompanyCandidatesAsync(IJobProvider provider, Profile profile, CancellationToken ct)
    {
        var jobs = new List<Job>();
        for (var page = 1; page <= 200; page++)
        {
            var result = await provider.SearchAsync(
                new JobQuery("", profile.City, profile.Country, EnglishOnly: true, page), ct);
            jobs.AddRange(result.Jobs);
            if (!result.HasMore)
                break;
        }
        return jobs;
    }

    // ---------- filtering & scoring ----------

    private async Task ProcessAsync(
        IJobDetailsProvider detailsProvider, IReadOnlyList<Job> candidates, Profile profile,
        bool includeGermanRequired, Func<Job, JobDetails?, string, bool> keep,
        List<MatchedJob> matched, Action<int> addGermanRequired, Action<int> addGermanContent,
        CancellationToken ct)
    {
        var gate = new SemaphoreSlim(6);
        var tasks = candidates.Select(async job =>
        {
            await gate.WaitAsync(ct);
            try { return (job, details: await TryGetDetailsAsync(detailsProvider, job, ct)); }
            finally { gate.Release(); }
        });

        int germanRequired = 0, germanContent = 0;
        foreach (var (job, details) in await Task.WhenAll(tasks))
        {
            if (details?.ContentLanguage == "German")
            {
                germanContent++;
                continue;
            }

            var requiresGerman = RequiresGerman(details);
            if (requiresGerman && !includeGermanRequired)
            {
                germanRequired++;
                continue;
            }

            var seniorities = job.Seniorities.Count > 0 ? job.Seniorities : details?.Seniorities ?? [];
            var seniority = ClassifySeniority(job.Title, seniorities, profile);
            if (!keep(job, details, seniority))
                continue;

            matched.Add(Score(job, details, seniority, requiresGerman, profile));
        }
        addGermanRequired(germanRequired);
        addGermanContent(germanContent);
    }

    private static bool IsDotnet(Job job, JobDetails? details, Profile profile) =>
        profile.DotnetKeywords.Any(k =>
            ContainsWord(job.Title, k)
            || job.Skills.Any(s => s.Equals(k, StringComparison.OrdinalIgnoreCase))
            || (details is not null && ContainsWord(details.PlainDescription, k)));

    private static bool IsJuniorField(Job job, Profile profile) =>
        profile.JuniorFieldKeywords.Any(k =>
            ContainsWord(job.Title, k)
            || job.Skills.Any(s => s.Equals(k, StringComparison.OrdinalIgnoreCase)));

    private static async Task<JobDetails?> TryGetDetailsAsync(IJobDetailsProvider provider, Job job, CancellationToken ct)
    {
        try { return await provider.GetDetailsAsync(job, ct); }
        catch { return null; }
    }

    private static bool RequiresGerman(JobDetails? details)
    {
        if (details is null)
            return false;
        if (details.RequiredLanguages.Any(l => l.Contains("German", StringComparison.OrdinalIgnoreCase)))
            return true;
        return GermanRequiredRx.IsMatch(details.PlainDescription)
            && !GermanOptionalRx.IsMatch(details.PlainDescription);
    }

    /// <summary>Classifies a job as Junior / Mid / Senior / Any for the UI filter.</summary>
    private static string ClassifySeniority(string title, IReadOnlyList<string> seniorities, Profile profile)
    {
        if (profile.PreferTitleKeywords.Any(k => ContainsWord(title, k)))
            return "Junior";
        if (profile.ExcludeTitleKeywords.Any(k => ContainsWord(title, k)))
            return "Senior";
        if (seniorities.Any(s => s.Equals("Junior", StringComparison.OrdinalIgnoreCase)))
            return "Junior";
        if (seniorities.Any(s => s.Equals("Intermediate", StringComparison.OrdinalIgnoreCase)))
            return "Mid";
        if (seniorities.Any(s => s.Equals("Senior", StringComparison.OrdinalIgnoreCase)))
            return "Senior";
        return "Any";
    }

    private static MatchedJob Score(Job job, JobDetails? details, string seniority, bool requiresGerman, Profile profile)
    {
        var score = 0;
        var reasons = new List<string>();

        var matchedSkills = job.Skills
            .Where(s => profile.Skills.Any(p => SkillMatches(p, s)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // boards without skill tags: match profile skills against title + description
        if (matchedSkills.Count == 0)
        {
            var text = job.Title + " " + (details?.PlainDescription ?? "");
            matchedSkills = profile.Skills.Where(p => ContainsWord(text, p)).ToList();
        }
        score += matchedSkills.Count * 2;

        if (seniority == "Junior")
            score += 3;
        else if (seniority == "Mid")
            score += 1;
        if (!requiresGerman && details is not null && details.PlainDescription.Length > 0)
            reasons.Add("No German required");
        if (job.Remote)
            score += 1;

        return new MatchedJob(
            job, score, matchedSkills, reasons, requiresGerman,
            details?.ContentLanguage,
            details?.ApplyUrl,
            details is null ? null : string.Join(" · ", new[] { details.ContractType, details.EmploymentType }.Where(x => x is not null)),
            details is not null,
            seniority);
    }

    private static bool SkillMatches(string profileSkill, string jobSkill) =>
        jobSkill.Equals(profileSkill, StringComparison.OrdinalIgnoreCase)
        || jobSkill.Contains(profileSkill, StringComparison.OrdinalIgnoreCase)
        || profileSkill.Contains(jobSkill, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, $@"(^|[\s\-/(),]){Regex.Escape(word)}($|[\s\-/(),.:;])", RegexOptions.IgnoreCase);
}
