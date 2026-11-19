namespace Castar.Services.TelegramBot.Commands;

internal sealed class BudgetEntrySessions
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly Dictionary<Conversation, Draft> drafts = new();

    public Draft Start(Conversation conversation, EntryKind kind)
    {
        var now = DateTime.UtcNow;
        foreach (var expired in drafts.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
            drafts.Remove(expired);
        var draft = new Draft(Guid.NewGuid(), now.Add(Lifetime), kind);
        drafts[conversation] = draft;
        return draft;
    }

    public Draft? Find(Conversation conversation)
    {
        if (drafts.GetValueOrDefault(conversation) is not { } draft)
            return null;
        if (draft.Expires > DateTime.UtcNow)
            return draft;
        drafts.Remove(conversation);
        return null;
    }

    public void Remove(Conversation conversation) => drafts.Remove(conversation);

    internal readonly record struct Conversation(long ChatId, long UserId, int? ThreadId);

    internal enum EntryKind { Purchase, Income }

    internal sealed record Draft(Guid Id, DateTime Expires, EntryKind Kind)
    {
        public int PromptId { get; set; }
        public string? Name { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }
        public string? Outcome { get; set; }
    }
}
