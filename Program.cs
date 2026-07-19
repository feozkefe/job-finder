using System.Text.Json;
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

app.Run("http://localhost:5210");

static Profile LoadProfile(IWebHostEnvironment env)
{
    var path = Path.Combine(env.ContentRootPath, "profile.json");
    if (!File.Exists(path))
        return new Profile();
    return JsonSerializer.Deserialize<Profile>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Profile();
}
