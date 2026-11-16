namespace Castar.Services.TelegramBot.Commands;

internal sealed class TestPurchaseSessions
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly Dictionary<(long Chat, long User), Draft> drafts = new();

    public Draft Start(long chat, long user)
    {
        var now = DateTime.UtcNow;
        foreach (var expired in drafts.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
            drafts.Remove(expired);
        var draft = new Draft(Guid.NewGuid(), now.Add(Lifetime));
        drafts[(chat, user)] = draft;
        return draft;
    }

    public Draft? Find(long chat, long user)
    {
        if (drafts.GetValueOrDefault((chat, user)) is not { } draft)
            return null;
        if (draft.Expires > DateTime.UtcNow)
            return draft;
        drafts.Remove((chat, user));
        return null;
    }

    public bool Remove(long chat, long user) => drafts.Remove((chat, user));

    internal sealed record Draft(Guid Id, DateTime Expires)
    {
        public int PromptId { get; set; }
        public string? Name { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }
    }
}
