namespace JobFinder.Models;

/// <summary>Extra information available only on a job's detail page.</summary>
public record JobDetails(
    string? ContentLanguage,
    IReadOnlyList<string> RequiredLanguages,
    string PlainDescription,
    string? ApplyUrl,
    string? ContractType,
    string? EmploymentType,
    IReadOnlyList<string> Seniorities);

public record MatchedJob(
    Job Job,
    int Score,
    IReadOnlyList<string> MatchedSkills,
    IReadOnlyList<string> Reasons,
    bool RequiresGerman,
    string? ContentLanguage,
    string? ApplyUrl,
    string? Contract,
    bool DetailsFetched,
    string Seniority);

public record MatchResult(
    IReadOnlyList<MatchedJob> Jobs,
    int Scanned,
    int ExcludedGermanRequired,
    int ExcludedGermanContent);
