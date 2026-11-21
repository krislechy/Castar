using System;

namespace Castar.LocalAi;

public enum LocalAiProvider
{
    Ollama,
    LlamaCpp
}

public enum LocalAiMode
{
    External,
    Managed
}

public sealed record LocalAiOptions(LocalAiProvider Provider, LocalAiMode Mode, string Endpoint, string Model,
    string? ExecutablePath, string? ModelDirectory, int IdleMinutes)
{
    public const string RecommendedOllamaModel = "qwen3:4b-instruct-2507-q4_K_M";

    public static LocalAiOptions Default { get; } = new();

    public LocalAiOptions() : this(LocalAiProvider.Ollama, LocalAiMode.External, "http://127.0.0.1:11434",
        RecommendedOllamaModel, null, null, 5)
    {
    }
}

public sealed record LocalAiItem(Guid Id, string Name);
public sealed record LocalAiCategory(Guid Id, string Name);
public sealed record LocalAiAssignment(Guid ItemId, Guid? CategoryId);
