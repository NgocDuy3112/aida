using Microsoft.Extensions.AI;

namespace aida.Services.Agents;

internal static class CrewOptions
{
    internal const string Section = "Crew";

    internal static readonly string[] ThinkingLevels = ["off", "low", "medium", "high", "extrahigh"];

    internal static string DirectorModel(IConfiguration config) =>
        NonEmpty(config[$"{Section}:DirectorModel"])
        ?? NonEmpty(config["OpenRouter:StrongModel"])
        ?? throw new InvalidOperationException("Missing OpenRouter:StrongModel");

    internal static string ResearcherModel(IConfiguration config) =>
        NonEmpty(config[$"{Section}:ResearcherModel"])
        ?? NonEmpty(config["OpenRouter:ModerateModel"])
        ?? throw new InvalidOperationException("Missing OpenRouter:ModerateModel");

    internal static string WriterModel(IConfiguration config) =>
        NonEmpty(config[$"{Section}:WriterModel"])
        ?? NonEmpty(config["OpenRouter:StrongModel"])
        ?? throw new InvalidOperationException("Missing OpenRouter:StrongModel");

    internal static string Thinking(IConfiguration config, string role)
    {
        var raw = config[$"{Section}:{role}Thinking"]?.Trim().ToLowerInvariant();
        return raw is not null && ThinkingLevels.Contains(raw) ? raw : "off";
    }

    internal static ReasoningOptions? Reasoning(string? level) =>
        level?.Trim().ToLowerInvariant() switch
        {
            "low" => new ReasoningOptions { Effort = ReasoningEffort.Low },
            "medium" => new ReasoningOptions { Effort = ReasoningEffort.Medium },
            "high" => new ReasoningOptions { Effort = ReasoningEffort.High },
            "extrahigh" => new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            _ => null, 
        };

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed record CrewState(
    string DirectorModel, string ResearcherModel, string WriterModel,
    string DirectorThinking, string ResearcherThinking, string WriterThinking)
{
    public static CrewState FromConfig(IConfiguration config) => new(
        CrewOptions.DirectorModel(config),
        CrewOptions.ResearcherModel(config),
        CrewOptions.WriterModel(config),
        CrewOptions.Thinking(config, "Director"),
        CrewOptions.Thinking(config, "Researcher"),
        CrewOptions.Thinking(config, "Writer"));
}
