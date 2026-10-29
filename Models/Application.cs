namespace JobFinder.Models;

/// <summary>The job snapshot an application is built from, saved as data/applications/{key}/job.json.</summary>
public record ApplicationJob(
    string Key,
    Job? Job,
    string Description,
    string? ApplyUrl,
    DateTimeOffset SavedAt);

/// <summary>Claude's fit assessment of the CV against one job posting.</summary>
public record JobEvaluation(
    string Grade,
    int FitScore,
    string Verdict,
    string RoleSummary,
    IReadOnlyList<string> MatchedRequirements,
    IReadOnlyList<string> Gaps,
    IReadOnlyList<AtsKeyword> AtsKeywords,
    bool GermanRequired,
    string SeniorityFit,
    bool CoverLetterRecommended,
    string Notes);

public record AtsKeyword(string Keyword, bool InCv);

/// <summary>A CV rewritten for one job (or the base CV), rendered by DocumentRenderer.</summary>
public record TailoredCv(
    CvContact Contact,
    string Headline,
    string Summary,
    IReadOnlyList<CvSkillGroup> Skills,
    IReadOnlyList<CvEntry> Experience,
    IReadOnlyList<CvEntry> Education,
    string Languages,
    IReadOnlyList<string> Changes);

public record CvContact(
    string Name,
    string Location,
    string Phone,
    string Email,
    IReadOnlyList<string> Links,
    string WorkAuthorization);

public record CvSkillGroup(string Category, IReadOnlyList<string> Items);

public record CvEntry(
    string Organization,
    string Title,
    string Location,
    string Dates,
    IReadOnlyList<string> Bullets);

public record CoverLetter(
    string Greeting,
    IReadOnlyList<string> Paragraphs,
    string Closing,
    string Signature);

/// <summary>Everything stored for one job, returned by GET /api/ai/{key}.</summary>
public record ApplicationState(
    string Key,
    ApplicationJob? Job,
    JobEvaluation? Evaluation,
    bool HasCv,
    bool HasCoverLetter);
