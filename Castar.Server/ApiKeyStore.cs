using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Castar.Server;

internal sealed record ApiKeyInfo(Guid Id, string Label, string Suffix, bool CanCopy);

internal sealed class ApiKeyStore : IDisposable
{
    internal const int MaxLabelLength = 80;
    internal const int MaxActiveKeys = 256;
    private const int KeyBytes = 32;
    private const int SuffixLength = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string path;
    private readonly string configuredKey;
    private readonly byte[] configuredHash;
    private readonly SemaphoreSlim writes = new(1);
    private State current = new([], []);

    public ApiKeyStore(IConfiguration configuration)
    {
        path = CastarData.GetSqlitePath(configuration) + ".keys.json";
        configuredKey = configuration["Castar:ApiKey"]?.Trim()
            ?? throw new InvalidOperationException("Castar:ApiKey is required.");
        if (configuredKey.Length < SuffixLength)
            throw new InvalidOperationException("Castar:ApiKey is too short.");
        configuredHash = Hash(configuredKey);
    }

    public async Task InitializeAsync()
    {
        await writes.WaitAsync();
        try
        {
            var saved = await ReadAsync();
            var next = ReconcileConfiguredKey(saved ?? new State([], []));
            if (!ReferenceEquals(saved, next))
                await SaveAsync(next, CancellationToken.None);
            Volatile.Write(ref current, next);
        }
        finally
        {
            writes.Release();
        }
    }

    public Guid? GetAuthorizedKeyId(string presentedKey)
    {
        var presentedHash = Hash(presentedKey);
        Guid? authorized = null;
        foreach (var entry in Volatile.Read(ref current).Keys)
        {
            if (Matches(entry.Hash, presentedHash))
                authorized = entry.Id;
        }
        return authorized;
    }

    public Guid[] GetActiveKeyIds() => Volatile.Read(ref current).Keys.Select(entry => entry.Id).ToArray();

    public IReadOnlyList<ApiKeyInfo> List() => Volatile.Read(ref current).Keys
        .Select(entry => new ApiKeyInfo(entry.Id, entry.Label, entry.Suffix, CanCopy(entry)))
        .ToArray();

    public async Task<string> CreateAsync(string? label, CancellationToken token)
    {
        var name = label?.Trim();
        if (name is not null && (name.Length > MaxLabelLength || name.Any(char.IsControl)))
            throw new ArgumentException("Недопустимое имя ключа.", nameof(label));
        if (string.IsNullOrEmpty(name))
            name = "Ключ API";

        await writes.WaitAsync(token);
        try
        {
            if (current.Keys.Length >= MaxActiveKeys)
                throw new ApiKeyLimitException();
            var value = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(KeyBytes));
            var entry = NewEntry(value, name, configured: false);
            var next = current with { Keys = [.. current.Keys, entry] };
            await SaveAsync(next, token);
            Volatile.Write(ref current, next);
            return value;
        }
        finally
        {
            writes.Release();
        }
    }

    public string? CopyConfigured(Guid id) =>
        Volatile.Read(ref current).Keys.Any(entry => entry.Id == id && CanCopy(entry)) ? configuredKey : null;

    public async Task<bool> RevokeAsync(Guid id, CancellationToken token)
    {
        await writes.WaitAsync(token);
        try
        {
            var entry = current.Keys.FirstOrDefault(key => key.Id == id);
            if (entry is null)
                return false;

            var next = WithoutKey(current, entry);
            await SaveAsync(next, token);
            Volatile.Write(ref current, next);
            return true;
        }
        finally
        {
            writes.Release();
        }
    }

    private State ReconcileConfiguredKey(State stored)
    {
        var previous = stored.Keys.SingleOrDefault(key => key.Configured);
        if (previous is not null && CanCopy(previous))
            return stored;

        var next = previous is null ? stored : WithoutKey(stored, previous);
        if (next.RevokedHashes.Any(hash => Matches(hash, configuredHash))
            || next.Keys.Any(key => Matches(key.Hash, configuredHash)))
            return next;

        var imported = NewEntry(configuredKey, "Первоначальный ключ", configured: true);
        return next with { Keys = [imported, .. next.Keys] };
    }

    private bool CanCopy(KeyEntry entry) => entry.Configured && Matches(entry.Hash, configuredHash);

    private static State WithoutKey(State state, KeyEntry entry) => new(
        state.Keys.Where(key => key.Id != entry.Id).ToArray(),
        [.. state.RevokedHashes, entry.Hash]);

    private static KeyEntry NewEntry(string key, string label, bool configured) =>
        new(Guid.NewGuid(), label, key[^SuffixLength..], Hash(key), configured);

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
    private static bool Matches(byte[] left, byte[] right) => CryptographicOperations.FixedTimeEquals(left, right);

    private async Task<State?> ReadAsync()
    {
        FileStream file;
        try
        {
            file = File.OpenRead(path);
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        await using (file)
        {
            State? saved;
            try
            {
                saved = await JsonSerializer.DeserializeAsync<State>(file, JsonOptions);
            }
            catch (JsonException)
            {
                throw InvalidRegistry();
            }

            if (saved is null || saved.Keys is null || saved.RevokedHashes is null)
                throw InvalidRegistry();

            var identifiers = new HashSet<Guid>();
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            var configuredCount = 0;
            foreach (var entry in saved.Keys)
            {
                if (entry is null || entry.Id == Guid.Empty || !identifiers.Add(entry.Id)
                    || string.IsNullOrWhiteSpace(entry.Label) || entry.Label.Length > MaxLabelLength
                    || entry.Suffix is not { Length: SuffixLength } || entry.Hash is not { Length: KeyBytes }
                    || !hashes.Add(Convert.ToHexString(entry.Hash)))
                    throw InvalidRegistry();
                if (entry.Configured && ++configuredCount > 1)
                    throw InvalidRegistry();
            }

            foreach (var hash in saved.RevokedHashes)
            {
                if (hash is not { Length: KeyBytes } || !hashes.Add(Convert.ToHexString(hash)))
                    throw InvalidRegistry();
            }

            return saved;
        }
    }

    private static InvalidDataException InvalidRegistry() => new("Повреждён файл реестра ключей API.");

    private async Task SaveAsync(State value, CancellationToken token)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            await using (var file = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(file, value, JsonOptions, token);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }

    public void Dispose() => writes.Dispose();

    private sealed record State(
        [property: JsonRequired] KeyEntry[] Keys,
        [property: JsonRequired] byte[][] RevokedHashes);

    private sealed record KeyEntry(
        Guid Id,
        string Label,
        string Suffix,
        byte[] Hash,
        [property: JsonRequired] bool Configured);
}

internal sealed class ApiKeyLimitException : Exception;
