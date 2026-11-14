namespace aida.Data;

public class UsageEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserId { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public string Model { get; set; } = string.Empty;

    public string Mode { get; set; } = string.Empty;

    public long? PromptTokens { get; set; }
    public long? CompletionTokens { get; set; }

    public decimal? CostUsd { get; set; }

    public long DurationMs { get; set; }

    public int ToolCalls { get; set; }

    public string? Step { get; set; }

    public string? Error { get; set; }
}
