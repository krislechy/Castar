namespace Castar.Api;

public sealed record ServerSynchronizationDto(string Kind, long? Revision = null);

public static class ServerSynchronization
{
    public const string Categories = "categories";
    public const string RecurringPurchases = "recurring-purchases";
    public const string PurchaseClassification = "purchase-classification";
    public const string PurchaseClassificationRequested = "purchase-classification-requested";
    public const string IncomeClassification = "income-classification";
    public const string IncomeClassificationRequested = "income-classification-requested";
    public const string Users = "users";
    public const string Settings = "settings";
    public const string Changed = "ServerConfigurationChanged";
}
