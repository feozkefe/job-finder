using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace JobFinder.Services;

/// <summary>
/// Thin wrapper over the Anthropic SDK: one request in, one schema-validated JSON object out.
/// The API key comes from ANTHROPIC_API_KEY (or an `ant auth login` profile).
/// </summary>
public class ClaudeService
{
    public const string Model = "claude-opus-5-5";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // created on first use so the app still starts (and scans jobs) without an API key
    private readonly Lazy<AnthropicClient> client = new(() => new AnthropicClient());

    public async Task<T> GenerateJsonAsync<T>(
        string system, string prompt, string schemaJson, Effort effort, CancellationToken ct)
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schemaJson)!;

        BetaMessage response = await client.Value.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 16000,
            System = system,
            Messages = [new BetaUserMessageParam { Content = prompt }],
            OutputConfig = new BetaOutputConfig
            {
                Effort = effort,
                Format = new BetaJsonOutputFormat { Schema = schema },
            },
            // a policy refusal is re-served by the fallback model inside the same call
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
        }, ct);

        if (response.StopReason == "refusal")
            throw new InvalidOperationException("Claude declined this request.");
        if (response.StopReason == "max_tokens")
            throw new InvalidOperationException("Claude's answer was cut off (max_tokens).");

        var text = string.Concat(response.Content
            .Select(b => b.TryPickText(out var t) ? t.Text : ""));
        return JsonSerializer.Deserialize<T>(text, JsonOptions)
            ?? throw new InvalidOperationException("Claude returned an empty answer.");
    }
}
