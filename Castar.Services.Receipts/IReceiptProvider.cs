using System.Collections.Frozen;

namespace Castar.Services.Receipts;

public sealed record ReceiptCredentialField(string Key, string Label, bool Required, bool Secret);

public sealed record ReceiptProviderAuthentication(bool SupportsAnonymous, bool SupportsCredentials,
    IReadOnlyList<ReceiptCredentialField> Fields)
{
    public static ReceiptProviderAuthentication AnonymousOnly { get; } = new(true, false, []);
}

public sealed class ReceiptAuthenticationSettings(bool useAnonymous, IReadOnlyDictionary<string, string>? values)
{
    public bool UseAnonymous { get; } = useAnonymous;
    public IReadOnlyDictionary<string, string> Values { get; } = !useAnonymous && values is not null
        ? values.ToFrozenDictionary(StringComparer.Ordinal)
        : FrozenDictionary<string, string>.Empty;
}

public interface IReceiptProvider
{
    string Id { get; }

    ReceiptProviderAuthentication Authentication => ReceiptProviderAuthentication.AnonymousOnly;

    void ConfigureAuthentication(ReceiptAuthenticationSettings settings)
    {
        if (!settings.UseAnonymous)
            throw new NotImplementedException();
    }

    bool CanHandle(string query);

    Task<Receipt> GetReceiptAsync(string query, CancellationToken cancellationToken);

    string GetReceiptIdentity(ReceiptIdentitySource source);
}
