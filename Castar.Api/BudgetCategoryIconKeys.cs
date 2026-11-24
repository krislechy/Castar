namespace Castar.Api;

public static class BudgetCategoryIconKeys
{
    public static IReadOnlyList<string> Income { get; } = Array.AsReadOnly<string>(
        ["briefcase-business", "laptop", "building-2", "chart-no-axes-combined", "store", "rotate-ccw", "gift",
         "landmark", "ellipsis", "star", "house", "percent", "banknote", "coins", "hand-coins",
         "badge-dollar-sign", "credit-card", "piggy-bank", "receipt-text", "users",
         "archive", "arrow-left-right", "banknote-arrow-up", "badge-check", "chart-candlestick", "circle-percent",
         "file-check", "handshake", "key-round", "medal", "package-check", "rocket", "trending-up", "vault",
         "wallet-cards", "calendar-check"]);

    public static IReadOnlyList<string> Purchase { get; } = Array.AsReadOnly<string>(
        ["shopping-cart", "utensils", "house", "car-front", "heart-pulse", "shirt", "smartphone", "monitor", "repeat",
         "gamepad-2", "plane", "dumbbell", "graduation-cap", "shopping-bag", "scissors", "landmark", "wallet",
         "paw-print", "gift", "ellipsis", "coffee", "bus", "fuel", "zap", "pill", "cross", "baby", "shield-check", "receipt-text",
         "package", "banknote", "wine",
         "archive", "ice-cream-cone", "candy", "cookie", "can", "bike", "book-open", "car-taxi-front", "cat",
         "clapperboard", "flower-2", "guitar", "hammer", "hand-heart", "key-round", "plug", "ticket",
         "washing-machine", "wrench"]);

    private static readonly IReadOnlyDictionary<string, string> IncomeAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["salary"] = "briefcase-business", ["bonus"] = "star", ["freelance"] = "laptop", ["business"] = "store",
        ["rent"] = "house", ["investments"] = "chart-no-axes-combined", ["refund"] = "rotate-ccw",
        ["interest"] = "percent", ["other"] = "ellipsis"
    };

    private static readonly IReadOnlyDictionary<string, string> PurchaseAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["groceries"] = "shopping-cart", ["restaurant"] = "utensils", ["transport"] = "bus", ["car"] = "car-front",
        ["home"] = "house", ["utilities"] = "zap", ["health"] = "heart-pulse", ["pharmacy"] = "pill",
        ["clothing"] = "shirt", ["beauty"] = "scissors", ["education"] = "graduation-cap", ["leisure"] = "gamepad-2",
        ["travel"] = "plane", ["sport"] = "dumbbell", ["electronics"] = "monitor", ["subscriptions"] = "repeat",
        ["communication"] = "smartphone", ["pets"] = "paw-print", ["children"] = "baby", ["insurance"] = "shield-check",
        ["taxes"] = "receipt-text", ["other"] = "ellipsis"
    };

    public static IReadOnlyList<string> ForKind(string kind) => kind switch
    {
        BudgetEvents.Income => Income,
        BudgetEvents.Purchase => Purchase,
        _ => []
    };

    public static string? Canonicalize(string kind, string? key)
    {
        if (key is null)
            return null;
        if (ForKind(kind).Contains(key, StringComparer.Ordinal))
            return key;
        return kind switch
        {
            BudgetEvents.Income => IncomeAliases.GetValueOrDefault(key),
            BudgetEvents.Purchase => PurchaseAliases.GetValueOrDefault(key),
            _ => null
        };
    }

    public static string? Canonicalize(string? key) =>
        Canonicalize(BudgetEvents.Purchase, key) ?? Canonicalize(BudgetEvents.Income, key);
}
