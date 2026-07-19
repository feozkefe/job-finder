using JobFinder.Models;

namespace JobFinder.Providers;

public interface IJobProvider
{
    /// <summary>Stable identifier used by the UI, e.g. "wearedevelopers".</summary>
    string Id { get; }

    /// <summary>Human readable name shown in the site selector.</summary>
    string DisplayName { get; }

    Task<JobSearchResult> SearchAsync(JobQuery query, CancellationToken ct);
}
