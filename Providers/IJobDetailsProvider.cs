using JobFinder.Models;

namespace JobFinder.Providers;

/// <summary>
/// Optional capability: providers that can fetch a job's detail page
/// (description, required languages, apply link) implement this too.
/// </summary>
public interface IJobDetailsProvider
{
    Task<JobDetails?> GetDetailsAsync(Job job, CancellationToken ct);
}
