using System.Text.Json;
using Anthropic.Exceptions;
using JobFinder.Models;
using JobFinder.Providers;
using JobFinder.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("wad", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) JobFinder/1.0");
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient("ats", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) JobFinder/1.0");
    c.Timeout = TimeSpan.FromSeconds(20);
});
// Register every job site provider here; the UI picks them up automatically.
builder.Services.AddSingleton<IJobProvider, WeAreDevelopersProvider>();
builder.Services.AddSingleton<IJobProvider, CompaniesProvider>();
builder.Services.AddSingleton<IJobProvider, IndeedProvider>();
builder.Services.AddSingleton<MatchService>();
builder.Services.AddSingleton<ClaudeService>();
builder.Services.AddSingleton<ApplicationService>();

var app = builder.Build();

app.UseDefaultFiles();
// local dev tool: never let the browser cache the UI, so changes always show up
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate",
});

app.MapGet("/api/sites", (IEnumerable<IJobProvider> providers) =>
    providers.Select(p => new { id = p.Id, name = p.DisplayName }));

app.MapGet("/api/jobs", async (
    string site,
    IEnumerable<IJobProvider> providers,
    CancellationToken ct,
    string skill = ".net",
    string city = "berlin",
    string country = "germany",
    bool englishOnly = true,
    int page = 1) =>
{
    var provider = providers.FirstOrDefault(p => p.Id.Equals(site, StringComparison.OrdinalIgnoreCase));
    if (provider is null)
        return Results.BadRequest(new { error = $"Unknown site '{site}'." });

    try
    {
        var result = await provider.SearchAsync(new JobQuery(skill, city, country, englishOnly, Math.Max(1, page)), ct);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/profile", (IWebHostEnvironment env) =>
    Results.Ok(LoadProfile(env)));

app.MapGet("/api/companies", (IEnumerable<IJobProvider> providers) =>
{
    var companies = providers.OfType<CompaniesProvider>().FirstOrDefault();
    return companies is null
        ? Results.NotFound()
        : Results.Ok(companies.GetStatus().OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value));
});

app.MapGet("/api/match", async (
    MatchService matcher,
    IWebHostEnvironment env,
    CancellationToken ct,
    string site = "wearedevelopers",
    bool includeGermanRequired = false) =>
{
    try
    {
        var profile = LoadProfile(env);
        return Results.Ok(await matcher.MatchAsync(site, profile, includeGermanRequired, ct));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ---- per-job status (applied / saved / rejected), persisted to data/job-statuses.json ----

var statusLock = new object();

app.MapGet("/api/statuses", (IWebHostEnvironment env) =>
{
    lock (statusLock)
        return Results.Ok(LoadStatuses(env));
});

app.MapPost("/api/statuses", async (HttpRequest request, IWebHostEnvironment env) =>
{
    using var body = await JsonDocument.ParseAsync(request.Body);
    var root = body.RootElement;
    if (!root.TryGetProperty("key", out var keyEl) || keyEl.GetString() is not { Length: > 0 } key)
        return Results.BadRequest(new { error = "key is required" });

    var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    if (status is not (null or "applied" or "saved" or "rejected"))
        return Results.BadRequest(new { error = "status must be applied, saved, rejected or null" });

    lock (statusLock)
    {
        var statuses = LoadStatuses(env);
        if (status is null)
            statuses.Remove(key);
        else
            statuses[key] = new JobStatusEntry(
                status,
                root.TryGetProperty("title", out var t) ? t.GetString() : null,
                root.TryGetProperty("company", out var c) ? c.GetString() : null,
                DateTimeOffset.UtcNow);
        SaveStatuses(env, statuses);
        return Results.Ok(statuses);
    }
});

// ---- AI workflow: evaluate fit, tailor CV, cover letter (stored under data/applications/{key}) ----

app.MapGet("/api/ai/evaluations", (ApplicationService apps) => Results.Ok(apps.AllEvaluations()));

app.MapGet("/api/ai/{key}", (string key, ApplicationService apps) => Results.Ok(apps.GetState(key)));

app.MapPost("/api/ai/evaluate", (AiRequest req, ApplicationService apps, CancellationToken ct) =>
    RunAi(async () =>
    {
        var job = await apps.GetOrCreateJobAsync(req.Job, req.Description, ct);
        if (!req.Force && apps.Load<JobEvaluation>(job.Key, "evaluation.json") is { } cached)
            return cached;
        return await apps.EvaluateAsync(job, ct);
    }));

app.MapPost("/api/ai/cv", (AiRequest req, ApplicationService apps, CancellationToken ct) =>
    RunAi(async () =>
    {
        var job = await apps.GetOrCreateJobAsync(req.Job, req.Description, ct);
        await apps.TailorCvAsync(job, ct);
        return new { key = job.Key, url = $"/docs/{job.Key}/cv" };
    }));

app.MapPost("/api/ai/cover-letter", (AiRequest req, ApplicationService apps, CancellationToken ct) =>
    RunAi(async () =>
    {
        var job = await apps.GetOrCreateJobAsync(req.Job, req.Description, ct);
        await apps.WriteCoverLetterAsync(job, ct);
        return new { key = job.Key, url = $"/docs/{job.Key}/cover-letter" };
    }));

app.MapPost("/api/ai/base-cv", (ApplicationService apps, CancellationToken ct) =>
    RunAi(async () =>
    {
        await apps.TailorCvAsync(null, ct);
        return new { key = ApplicationService.BaseKey, url = $"/docs/{ApplicationService.BaseKey}/cv" };
    }));

app.MapGet("/docs/{key}/cv", (string key, ApplicationService apps) =>
    apps.Load<TailoredCv>(key, "cv.json") is { } cv
        ? Results.Content(DocumentRenderer.RenderCv(cv, apps.Load<ApplicationJob>(key, "job.json")), "text/html")
        : Results.NotFound("No CV generated for this job yet."));

app.MapGet("/docs/{key}/cover-letter", (string key, ApplicationService apps) =>
    apps.Load<CoverLetter>(key, "cover-letter.json") is { } letter
    && apps.Load<ApplicationJob>(key, "job.json") is { } job
        ? Results.Content(DocumentRenderer.RenderCoverLetter(letter, job,
            apps.Load<TailoredCv>(key, "cv.json") ?? apps.Load<TailoredCv>(ApplicationService.BaseKey, "cv.json")), "text/html")
        : Results.NotFound("No cover letter generated for this job yet."));

app.Run("http://localhost:5210");

// AI calls fail for user-fixable reasons (no API key, no description, refusal): report them to the UI
static async Task<IResult> RunAi<T>(Func<Task<T>> action)
{
    try
    {
        return Results.Ok(await action());
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (AnthropicUnauthorizedException)
    {
        return Results.BadRequest(new { error = "No valid Anthropic API key — set ANTHROPIC_API_KEY and restart the app." });
    }
    catch (AnthropicRateLimitException)
    {
        return Results.Json(new { error = "Claude rate limit reached — wait a minute and try again." }, statusCode: 429);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Json(new { error = $"Claude request failed: {ex.Message}" }, statusCode: 502);
    }
}

static string StatusFile(IWebHostEnvironment env) =>
    Path.Combine(env.ContentRootPath, "data", "job-statuses.json");

static Dictionary<string, JobStatusEntry> LoadStatuses(IWebHostEnvironment env)
{
    try
    {
        if (File.Exists(StatusFile(env)))
            return JsonSerializer.Deserialize<Dictionary<string, JobStatusEntry>>(
                File.ReadAllText(StatusFile(env)),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }
    catch { }
    return [];
}

static void SaveStatuses(IWebHostEnvironment env, Dictionary<string, JobStatusEntry> statuses)
{
    Directory.CreateDirectory(Path.GetDirectoryName(StatusFile(env))!);
    File.WriteAllText(StatusFile(env),
        JsonSerializer.Serialize(statuses, new JsonSerializerOptions { WriteIndented = true }));
}

static Profile LoadProfile(IWebHostEnvironment env)
{
    var path = Path.Combine(env.ContentRootPath, "profile.json");
    if (!File.Exists(path))
        return new Profile();
    return JsonSerializer.Deserialize<Profile>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Profile();
}

record AiRequest(Job Job, string? Description, bool Force = false);

record JobStatusEntry(string Status, string? Title, string? Company, DateTimeOffset UpdatedAt);
