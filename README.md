# Job Finder

A local ASP.NET Core web app that hunts for **English-language developer jobs in Germany**
across multiple sources, filters out postings that require German, and ranks the rest
against your own skill profile. Built as a personal job-search tool — no accounts,
no database, no external packages; everything runs on your machine.

![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4) ![No dependencies](https://img.shields.io/badge/NuGet%20packages-0-brightgreen)

## What it does

The **For Me** tab runs a personalized scan:

| Source | What is collected | Rule |
|---|---|---|
| [WeAreDevelopers](https://www.wearedevelopers.com) | `.net` category search (configurable) | English postings, **all** seniority levels |
| Company career boards (~520 companies in `companies.json`) | Every job on each board | English AND (.NET at any level, OR non-senior roles in your fields: AI/ML, fullstack, Java, Python, …) |
| [Indeed](https://de.indeed.com) | Configurable queries with Indeed's English-language filter | .NET at any level, OR junior roles |

Then, for every candidate job:

- **German-language postings are dropped** (content language from the source where
  available, otherwise stopword-based detection).
- **Jobs that require German are dropped** — the job's required-languages field and
  description phrases like *"fluent German"*, *"Deutschkenntnisse"*, *"German C1"* are
  checked (phrases like *"German is a plus"* don't count). A UI toggle can bring them back.
- Each job is classified **Junior / Mid / Senior / Unspecified** from title keywords
  and the source's seniority tags, filterable with one click.
- Jobs are **scored** by overlap with the skills in `profile.json` and sorted;
  matched skills show as green chips, and duplicates appearing on several sources
  are collapsed into the best-scoring copy.

The **Search** tab is a plain manual search (site + keyword + city + English-only),
with no filtering magic — useful for one-off queries.

## Running

```bash
dotnet run        # then open http://localhost:5210
```

or open `JobFinder.csproj` in Visual Studio and press Ctrl+F5.

The first For Me scan fetches ~500 career boards and takes a minute or two;
responses are cached under `cache/` (6–12 h TTL), so later scans finish in seconds.
Delete the `cache/` folder to force a full refresh.

## Configuration

Everything personal lives in two JSON files, re-read on every request (no restart needed):

**`profile.json`** — your matching profile:
- `skills` — CV skills, used for scoring
- `wadSkillSlugs` / `indeedSearches` — searches to run per source
- `dotnetKeywords` — what counts as a ".NET job" (kept at any level)
- `juniorFieldKeywords` — fields where non-senior roles are also interesting
- `excludeTitleKeywords` / `preferTitleKeywords` — seniority classification
- `city`, `country`, `pagesPerSearch`

**`companies.json`** — the career boards to scan: `[name, city, ats]` rows, with an
optional 4th element to pin the board slug when the automatic guess fails
(check `GET /api/companies` for per-company status).

## How it works inside

```
Program.cs                 minimal API: /api/sites /api/jobs /api/match /api/companies /api/profile
├─ Providers/
│  ├─ IJobProvider         search interface every source implements
│  ├─ IJobDetailsProvider  optional: fetch one job's description/languages
│  ├─ WeAreDevelopersProvider   wearedevelopers.com public API (skills/locations resolved via
│  │                            its category-page metadata endpoint, ids cached in-process)
│  ├─ CompaniesProvider    public ATS APIs: greenhouse, lever, ashby, personio,
│  │                       recruitee, smartrecruiters, workable. Board slugs are guessed
│  │                       from company names and persisted in cache/company-ats.json
│  └─ IndeedProvider       scrapes de.indeed.com result pages (job cards are embedded JSON)
├─ Services/
│  ├─ MatchService         the For Me pipeline: collect → language filters → classify → score
│  └─ TextLanguage         cheap German/English stopword detector
└─ wwwroot/                vanilla HTML/CSS/JS UI, no build step
```

Notable quirks handled:

- **Indeed bot protection**: the .NET `HttpClient` TLS fingerprint gets a
  "Security Check" page, so Indeed requests shell out to Git's `curl.exe`.
  Paging is JS-gated, so only page 1 is read — twice per query (relevance +
  newest). Requests are serialized with a delay and cached 12 h; heavy use can
  still earn a temporary IP block that clears on its own.
- **Workday / Breezy / Softgarden / Teamtailor** boards have no public JSON API
  and are skipped (listed as `unsupported` in `/api/companies`).

## Adding a job source

1. Implement `IJobProvider` (and ideally `IJobDetailsProvider`) in `Providers/`.
2. Register it in `Program.cs`: `builder.Services.AddSingleton<IJobProvider, MyProvider>();`
3. The UI site selector picks it up automatically; add a rule branch in
   `MatchService.MatchAsync` if it should join the For Me scan.

## Notes

This is a personal tool that reads public job postings at a low request rate with
aggressive caching. Be considerate: don't crank up page counts or strip the delays.
