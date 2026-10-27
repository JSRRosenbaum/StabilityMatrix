using System.Text.Json.Serialization;

namespace StabilityMatrix.Core.Models.Api.Comfy;

public sealed class ComfyGraftJob
{
    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("active_attempt")]
    public ComfyGraftAttempt? ActiveAttempt { get; init; }

    public string EffectiveState => ActiveAttempt?.State ?? State;
}

public sealed class ComfyGraftAttempt
{
    [JsonPropertyName("state")]
    public required string State { get; init; }
}

public sealed class ComfyGraftPromptInfo
{
    [JsonPropertyName("state")]
    public required string State { get; init; }
}

public sealed class ComfyGraftCancelResponse
{
    [JsonPropertyName("cancelled")]
    public required bool Cancelled { get; init; }
}
