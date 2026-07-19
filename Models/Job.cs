namespace JobFinder.Models;

public record Job(
    string Source,
    string Title,
    string Company,
    string Location,
    string Url,
    IReadOnlyList<string> Skills,
    string? Language,
    bool Remote,
    string? Salary,
    DateTimeOffset? PublishedAt,
    string? LogoUrl,
    IReadOnlyList<string> Seniorities,
    long SiteJobId,
    string Slug,
    bool External);

public record JobQuery(
    string Skill,
    string City,
    string Country,
    bool EnglishOnly,
    int Page);

public record JobSearchResult(
    IReadOnlyList<Job> Jobs,
    bool HasMore,
    int? TotalJobs);
