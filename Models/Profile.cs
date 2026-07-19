namespace JobFinder.Models;

/// <summary>
/// The user's personal matching profile, loaded from profile.json.
/// Edit that file to match your CV — no CV parsing involved.
///
/// "For Me" rule:
///  - WeAreDevelopers: only the searches in <see cref="WadSkillSlugs"/> (English, ALL levels).
///  - Company boards: English jobs that are either .NET (any level, <see cref="DotnetKeywords"/>)
///    OR non-senior jobs in fields from <see cref="JuniorFieldKeywords"/>.
///  - Jobs that require German are always dropped (unless the UI toggle is on).
/// </summary>
public class Profile
{
    /// <summary>Skills from your CV — used only for scoring/sorting, not filtering.</summary>
    public List<string> Skills { get; set; } = [];

    /// <summary>WeAreDevelopers searches (slugs as in wearedevelopers URLs).</summary>
    public List<string> WadSkillSlugs { get; set; } = [".net"];

    /// <summary>Indeed search queries (free text, run against de.indeed.com).</summary>
    public List<string> IndeedSearches { get; set; } = [".net", "junior software developer"];

    /// <summary>Keywords that mark a job as .NET — kept at ANY seniority level.</summary>
    public List<string> DotnetKeywords { get; set; } = [".net", "asp.net", "c#", "csharp", "dotnet"];

    /// <summary>Fields where junior/non-senior roles are also interesting.</summary>
    public List<string> JuniorFieldKeywords { get; set; } =
        ["ai", "machine learning", "deep learning", "computer vision",
         "fullstack", "full stack", "full-stack", "java", "python", "backend"];

    /// <summary>Title words that classify a job as Senior.</summary>
    public List<string> ExcludeTitleKeywords { get; set; } =
        ["senior", "sr.", "lead", "principal", "staff", "head of", "architect", "director", "expert"];

    /// <summary>Title words that classify a job as Junior.</summary>
    public List<string> PreferTitleKeywords { get; set; } = ["junior", "entry", "graduate", "trainee", "intern", "working student", "werkstudent"];

    public string City { get; set; } = "berlin";
    public string Country { get; set; } = "germany";

    /// <summary>How many result pages to scan per WeAreDevelopers search.</summary>
    public int PagesPerSearch { get; set; } = 6;
}
