using System;
using System.Collections.Generic;

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
    public const string DefaultPrompt = """
        Classify each entry into one of the supplied categories. Start with the meaning of the product or income source.
        Use category tags as examples and synonyms to help recognize abbreviations, brands, and names in other languages.
        Prefer the category whose name and tags best fit the entry as a whole. A partial tag match alone is insufficient.
        When classifying purchases, use the store as context. Leave an entry uncategorized when the information is insufficient
        or none of the categories fit.
        """;

    public string? Prompt { get; init; }

    public static LocalAiOptions Default { get; } = new();

    public LocalAiOptions() : this(LocalAiProvider.Ollama, LocalAiMode.External, "http://127.0.0.1:11434",
        "", null, null, 5)
    {
    }
}

public sealed record LocalAiItem(Guid Id, string Name);
public sealed record LocalAiCategory(Guid Id, string Name, IReadOnlyList<string>? Tags = null);
public sealed record LocalAiAssignment(Guid ItemId, Guid? CategoryId);
