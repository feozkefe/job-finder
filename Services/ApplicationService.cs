using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.Models.Beta.Messages;
using JobFinder.Models;
using JobFinder.Providers;

namespace JobFinder.Services;

/// <summary>
/// The per-job AI workflow: evaluate fit, tailor the CV, write a cover letter.
/// The master CV is cv.md (falls back to cv.example.md); everything generated is stored under
/// data/applications/{key}/ so it can be reviewed, re-opened and printed later.
/// </summary>
public class ApplicationService(IEnumerable<IJobProvider> providers, ClaudeService claude, IWebHostEnvironment env)
{
    public const string BaseKey = "base";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string HonestyRules = """
        Hard rules — never break them:
        - Use ONLY facts from the candidate's master CV. Never invent employers, titles, dates, degrees,
          certifications, metrics, team sizes, tools or skills. If the job asks for something the CV
          does not show, it is a gap: do not paper over it.
        - You may rephrase, reorder, merge or shorten bullets, drop less relevant details, and use the
          job posting's terminology where it truthfully describes the same experience
          (e.g. "LLM integration" → "LLM-powered features" is fine; adding "Kubernetes" when the CV
          never mentions it is not).
        - Keep every employer and the education history so there are no unexplained gaps; older or
          less relevant roles may get fewer bullets.
        - Write in English, plain and specific. No buzzword filler ("passionate", "rockstar",
          "synergy", "results-driven"), no first person in CV bullets.
        """;

    // ---------- master CV & job ----------

    public string LoadMasterCv()
    {
        foreach (var name in new[] { "cv.md", "cv.example.md" })
        {
            var path = Path.Combine(env.ContentRootPath, name);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new InvalidOperationException("No CV found — create cv.md (see cv.example.md).");
    }

    public static string KeyFor(Job job) => SafeKey($"{job.Source}:{job.SiteJobId}");

    public static string SafeKey(string key) =>
        Regex.Replace(key.ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');

    /// <summary>
    /// Returns the stored job snapshot, or fetches the job's description and stores it.
    /// <paramref name="descriptionOverride"/> lets the user paste a description when the source has none.
    /// </summary>
    public async Task<ApplicationJob> GetOrCreateJobAsync(Job job, string? descriptionOverride, CancellationToken ct)
    {
        var key = KeyFor(job);
        var stored = Load<ApplicationJob>(key, "job.json");
        if (stored is not null && string.IsNullOrWhiteSpace(descriptionOverride))
            return stored;

        var description = descriptionOverride?.Trim();
        string? applyUrl = null;
        if (string.IsNullOrEmpty(description))
        {
            var provider = providers.FirstOrDefault(p => p.DisplayName == job.Source) as IJobDetailsProvider;
            var details = provider is null ? null : await provider.GetDetailsAsync(job, ct);
            description = details?.PlainDescription.Trim();
            applyUrl = details?.ApplyUrl;
        }
        if (string.IsNullOrEmpty(description))
            throw new InvalidOperationException(
                "Could not read this job's description — paste it in and try again.");

        var snapshot = new ApplicationJob(key, job, description, applyUrl ?? stored?.ApplyUrl, DateTimeOffset.UtcNow);
        Save(key, "job.json", snapshot);
        return snapshot;
    }

    // ---------- evaluation ----------

    public async Task<JobEvaluation> EvaluateAsync(ApplicationJob app, CancellationToken ct)
    {
        var system = $"""
            You are a senior technical recruiter in Berlin who screens software engineering candidates
            for ATS-driven hiring pipelines. You assess honestly: a weak fit is more useful to the
            candidate than false encouragement.

            {HonestyRules}

            Grading: A = strong fit, apply now; B = good fit with minor gaps; C = stretch, apply only
            with a tailored CV; D = significant gaps; F = not a fit (wrong field, hard requirement
            missing, e.g. fluent German or a degree/clearance the candidate lacks).
            fitScore is 0–100 and consistent with the grade. verdict is "apply", "maybe" or "skip".
            atsKeywords: the 10–20 most important hard skills/terms an ATS would scan for in this
            posting, each marked inCv=true only if the master CV genuinely shows it.
            germanRequired: true only if German is a hard requirement (not "nice to have").
            seniorityFit: one sentence comparing the role's level to the candidate's ~5 years.
            coverLetterRecommended: true if the posting asks for one or the fit needs explaining.
            Keep every list item short (under 20 words).
            """;
        var prompt = $"""
            <master_cv>
            {LoadMasterCv()}
            </master_cv>

            {JobBlock(app)}

            Evaluate the candidate's fit for this job.
            """;

        var evaluation = await claude.GenerateJsonAsync<JobEvaluation>(system, prompt, EvaluationSchema, Effort.Medium, ct);
        Save(app.Key, "evaluation.json", evaluation);
        return evaluation;
    }

    // ---------- CV ----------

    /// <summary>Tailors the master CV to <paramref name="app"/>; with no job, produces a clean ATS base CV.</summary>
    public async Task<TailoredCv> TailorCvAsync(ApplicationJob? app, CancellationToken ct)
    {
        var system = $"""
            You are an expert CV writer for software engineers applying to companies in Germany
            (mostly Berlin startups and scale-ups) through applicant tracking systems such as
            Greenhouse, Lever, Ashby, Personio and Workday.

            {HonestyRules}

            ATS & format rules:
            - The output is rendered into a single-column, standard-heading template, so give plain
              text only: no markdown, no emojis, no tables.
            - headline: the target role title plus 2–3 core technologies, e.g.
              "AI Engineer | LLM Applications | .NET & TypeScript".
            - summary: 3–4 sentences tailored to the role; mention years of experience, the most
              relevant domains, and that the candidate is authorized to work in Germany.
            - skills: 4–6 categories, most relevant first; within each, the job's must-have skills
              that the candidate has come first. Use the exact spelling the posting uses
              (e.g. "TypeScript", "Node.js", "PostgreSQL").
            - experience: reverse chronological, same employers/titles/dates as the master CV.
              Each bullet starts with a strong past-tense verb (present tense for current roles),
              states what was built and with which technology, and is at most ~25 words.
              Most relevant roles get 3–5 bullets, older/less relevant ones 1–2.
            - Target length: fits on 2 A4 pages.
            - contact: copy from the master CV exactly; links without "https://".
            - changes: 3–8 short notes for the candidate explaining what you emphasized, reworded or
              cut and why, plus any important gap you could NOT cover truthfully.
            """;
        var prompt = app is null
            ? $"""
                <master_cv>
                {LoadMasterCv()}
                </master_cv>

                Produce a clean, general-purpose ATS-friendly version of this CV for AI engineer,
                AI full-stack / AI-native developer, forward deployed engineer, product engineer and
                full-stack (.NET / TypeScript) roles in Berlin. Fix spacing, typos and inconsistent formatting.
                """
            : $"""
                <master_cv>
                {LoadMasterCv()}
                </master_cv>

                {JobBlock(app)}

                Tailor the CV to this job.
                """;

        var key = app?.Key ?? BaseKey;
        var cv = await claude.GenerateJsonAsync<TailoredCv>(system, prompt, CvSchema, Effort.High, ct);
        Save(key, "cv.json", cv);
        return cv;
    }

    // ---------- cover letter ----------

    public async Task<CoverLetter> WriteCoverLetterAsync(ApplicationJob app, CancellationToken ct)
    {
        var system = $"""
            You write short, specific cover letters for software engineers applying to tech
            companies in Berlin.

            {HonestyRules}

            Letter rules:
            - 3–4 paragraphs, 220–320 words in total, in English.
            - Paragraph 1: the role, and one concrete reason this company/product is interesting,
              taken from the posting (never invented company facts).
            - Middle: two or three pieces of evidence from the CV that map directly to the posting's
              most important requirements — name the project, what was built and the stack.
            - Never claim a skill the CV doesn't show, but don't volunteer weaknesses either: do not
              mention missing nice-to-haves at all. Only if a MUST-HAVE requirement is missing, address
              it once, in at most one sentence, framed by the closest adjacent experience
              (e.g. "My integration work so far has been REST, webhook and SOA based; ...").
            - Last paragraph: work authorization in Germany (no sponsorship needed), Berlin-based,
              availability for a conversation. No begging, no clichés.
            - greeting: "Dear <name>," if a hiring manager is named in the posting, otherwise
              "Dear <Company> Hiring Team,". closing: "Kind regards,". signature: the candidate's name.
            - paragraphs contain plain text only.
            """;
        var prompt = $"""
            <master_cv>
            {LoadMasterCv()}
            </master_cv>

            {JobBlock(app)}

            Write the cover letter for this job.
            """;

        var letter = await claude.GenerateJsonAsync<CoverLetter>(system, prompt, CoverLetterSchema, Effort.High, ct);
        Save(app.Key, "cover-letter.json", letter);
        return letter;
    }

    // ---------- storage ----------

    public ApplicationState GetState(string key)
    {
        key = SafeKey(key);
        return new ApplicationState(
            key,
            Load<ApplicationJob>(key, "job.json"),
            Load<JobEvaluation>(key, "evaluation.json"),
            File.Exists(PathFor(key, "cv.json")),
            File.Exists(PathFor(key, "cover-letter.json")));
    }

    /// <summary>Evaluations of every stored job, keyed like the UI's job keys (for showing grades on cards).</summary>
    public Dictionary<string, JobEvaluation> AllEvaluations()
    {
        var root = Path.Combine(env.ContentRootPath, "data", "applications");
        var result = new Dictionary<string, JobEvaluation>();
        if (!Directory.Exists(root))
            return result;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var key = Path.GetFileName(dir);
            if (Load<JobEvaluation>(key, "evaluation.json") is { } evaluation)
                result[key] = evaluation;
        }
        return result;
    }

    public T? Load<T>(string key, string file) where T : class
    {
        var path = PathFor(SafeKey(key), file);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save<T>(string key, string file, T value)
    {
        var path = PathFor(key, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    private string PathFor(string key, string file) =>
        Path.Combine(env.ContentRootPath, "data", "applications", key, file);

    private static string JobBlock(ApplicationJob app) => $"""
        <job>
        Title: {app.Job?.Title}
        Company: {app.Job?.Company}
        Location: {app.Job?.Location}
        Source: {app.Job?.Source}

        {app.Description}
        </job>
        """;

    // ---------- output schemas (structured outputs) ----------

    private const string StringArray = """{ "type": "array", "items": { "type": "string" } }""";

    private const string EvaluationSchema = $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["grade", "fitScore", "verdict", "roleSummary", "matchedRequirements", "gaps",
                       "atsKeywords", "germanRequired", "seniorityFit", "coverLetterRecommended", "notes"],
          "properties": {
            "grade": { "type": "string", "enum": ["A", "B", "C", "D", "F"] },
            "fitScore": { "type": "integer" },
            "verdict": { "type": "string", "enum": ["apply", "maybe", "skip"] },
            "roleSummary": { "type": "string" },
            "matchedRequirements": {{StringArray}},
            "gaps": {{StringArray}},
            "atsKeywords": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["keyword", "inCv"],
                "properties": { "keyword": { "type": "string" }, "inCv": { "type": "boolean" } }
              }
            },
            "germanRequired": { "type": "boolean" },
            "seniorityFit": { "type": "string" },
            "coverLetterRecommended": { "type": "boolean" },
            "notes": { "type": "string" }
          }
        }
        """;

    private const string CvEntrySchema = $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["organization", "title", "location", "dates", "bullets"],
          "properties": {
            "organization": { "type": "string" },
            "title": { "type": "string" },
            "location": { "type": "string" },
            "dates": { "type": "string" },
            "bullets": {{StringArray}}
          }
        }
        """;

    private const string CvSchema = $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["contact", "headline", "summary", "skills", "experience", "education", "languages", "changes"],
          "properties": {
            "contact": {
              "type": "object",
              "additionalProperties": false,
              "required": ["name", "location", "phone", "email", "links", "workAuthorization"],
              "properties": {
                "name": { "type": "string" },
                "location": { "type": "string" },
                "phone": { "type": "string" },
                "email": { "type": "string" },
                "links": {{StringArray}},
                "workAuthorization": { "type": "string" }
              }
            },
            "headline": { "type": "string" },
            "summary": { "type": "string" },
            "skills": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["category", "items"],
                "properties": { "category": { "type": "string" }, "items": {{StringArray}} }
              }
            },
            "experience": { "type": "array", "items": {{CvEntrySchema}} },
            "education": { "type": "array", "items": {{CvEntrySchema}} },
            "languages": { "type": "string" },
            "changes": {{StringArray}}
          }
        }
        """;

    private const string CoverLetterSchema = $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["greeting", "paragraphs", "closing", "signature"],
          "properties": {
            "greeting": { "type": "string" },
            "paragraphs": {{StringArray}},
            "closing": { "type": "string" },
            "signature": { "type": "string" }
          }
        }
        """;
}
