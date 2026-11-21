using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.LocalAi;

/// <summary>Client-side API access and ownership of locally launched model servers.</summary>
public sealed class LocalAiService : IDisposable
{
    private const int ItemsPerBatch = 16;
    private const int MaxIdleMinutes = (int)((uint.MaxValue - 1) / 60000);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan InferenceTimeout = TimeSpan.FromSeconds(120);

    private readonly object state = new();
    private readonly SemaphoreSlim operations = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken stopped;
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(4)
    }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };
    private readonly LocalAiProtocol protocol;
    private readonly Dictionary<IdleTarget, CancellationTokenSource> pendingIdle = [];
    private ManagedLocalAiHost? ownedHost;
    private CancellationTokenSource? activeManagedOperation;
    private bool disposed;

    public LocalAiService()
    {
        stopped = lifetime.Token;
        protocol = new(http);
    }

    public event EventHandler? ProcessStateChanged;

    public bool HasOwnedProcess
    {
        get { lock (state) return ownedHost is { IsRunning: true }; }
    }

    public Task<IReadOnlyList<string>> DiscoverModelsAsync(LocalAiOptions options, CancellationToken token = default) =>
        RunAsync<IReadOnlyList<string>>(async current =>
        {
            ValidateOptions(options);
            if (options.Mode == LocalAiMode.Managed && options.Provider == LocalAiProvider.LlamaCpp)
                return await Task.Run(() => ManagedLocalAiHost.DiscoverGguf(options.ModelDirectory), current)
                    .ConfigureAwait(false);

            var endpoint = await OpenEndpointAsync(options, current).ConfigureAwait(false);
            var target = new IdleTarget(endpoint, null);
            CancelIdle(target);
            try
            {
                return await WithTimeoutAsync(ct => protocol.DiscoverAsync(endpoint, options.Provider, ct),
                    DiscoveryTimeout, current).ConfigureAwait(false);
            }
            finally
            {
                if (options.Mode == LocalAiMode.Managed)
                    QueueIdle(target, options.IdleMinutes, _ => StopOwnedHostAsync(endpoint));
            }
        }, token, managesProcess: options.Mode == LocalAiMode.Managed && options.Provider == LocalAiProvider.Ollama);

    public Task StartAsync(LocalAiOptions options, CancellationToken token = default) => RunAsync(async current =>
    {
        ValidateOptions(options);
        if (options.Mode != LocalAiMode.Managed)
            throw new ArgumentException("Локальный процесс доступен в управляемом режиме.");

        var endpoint = await OpenEndpointAsync(options, current).ConfigureAwait(false);
        var target = new IdleTarget(endpoint, null);
        CancelIdle(target);
        // An explicit start stays available until the first request or until the configured idle timeout.
        if (options.IdleMinutes > 0)
            QueueIdle(target, options.IdleMinutes, _ => StopOwnedHostAsync(endpoint));
        return true;
    }, token, managesProcess: options.Mode == LocalAiMode.Managed);

    public Task<IReadOnlyList<LocalAiAssignment>> ClassifyAsync(LocalAiOptions options, string? shop,
        IReadOnlyList<LocalAiItem> items, IReadOnlyList<LocalAiCategory> categories, CancellationToken token = default) =>
        RunAsync<IReadOnlyList<LocalAiAssignment>>(async current =>
        {
            if (items.Count == 0 || categories.Count == 0)
                return [];

            ValidateOptions(options);
            if (string.IsNullOrWhiteSpace(options.Model))
                throw new ArgumentException("Выберите модель для автоматической категоризации.");
            if (options.Provider == LocalAiProvider.Ollama && !LocalAiProtocol.IsLocalOllamaName(options.Model))
                throw new ArgumentException("Выберите локальную модель Ollama.");
            if (items.Select(item => item.Id).Distinct().Count() != items.Count
                || categories.Select(category => category.Id).Distinct().Count() != categories.Count)
                throw new ArgumentException("Товары и категории должны иметь уникальные идентификаторы.");

            var endpoint = await OpenEndpointAsync(options, current).ConfigureAwait(false);
            var managed = options.Mode == LocalAiMode.Managed;
            var target = new IdleTarget(endpoint, managed ? null : options.Model);
            bool existingLease = CancelIdle(target);
            var ownsExternalLoad = !managed && options.Provider == LocalAiProvider.LlamaCpp && options.IdleMinutes >= 0
                && (existingLease || await CanLeaseExternalModelAsync(endpoint, options.Model, current).ConfigureAwait(false));

            try
            {
                var result = new List<LocalAiAssignment>(items.Count);
                foreach (var batch in items.Chunk(ItemsPerBatch))
                {
                    var batchOptions = options.Provider == LocalAiProvider.Ollama && options.IdleMinutes == 0
                        && result.Count + batch.Length < items.Count ? options with { IdleMinutes = 1 } : options;
                    result.AddRange(await WithTimeoutAsync(
                        ct => protocol.ClassifyAsync(endpoint, batchOptions, shop, batch, categories, ct),
                        InferenceTimeout, current).ConfigureAwait(false));
                }
                return result;
            }
            finally
            {
                if (managed)
                    QueueIdle(target, options.IdleMinutes, _ => StopOwnedHostAsync(endpoint));
                else if (ownsExternalLoad)
                    QueueIdle(target, options.IdleMinutes, ct => protocol.UnloadLlamaRouterAsync(endpoint, options.Model, ct));
            }
        }, token, managesProcess: options.Mode == LocalAiMode.Managed);

    public Task StopAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (state)
            activeManagedOperation?.Cancel();
        return RunAsync(_ =>
        {
            StopOwnedHost();
            return Task.FromResult(true);
        }, token);
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token,
        bool managesProcess = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopped);
        await operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (managesProcess)
                lock (state) activeManagedOperation = linked;
            linked.Token.ThrowIfCancellationRequested();
            return await action(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (managesProcess)
                lock (state) activeManagedOperation = null;
            operations.Release();
        }
    }

    private async Task<Uri> OpenEndpointAsync(LocalAiOptions options, CancellationToken token)
    {
        if (options.Mode == LocalAiMode.External)
            return ParseEndpoint(options.Endpoint, options.Provider);

        var host = await Task.Run(() => GetOrStartHost(ManagedLocalAiHost.Resolve(options)), token).ConfigureAwait(false);
        try
        {
            return await WithTimeoutAsync(async startup =>
            {
                while (host.IsRunning)
                {
                    try
                    {
                        if (await WithTimeoutAsync(ct => protocol.ReadyAsync(host.Endpoint, options.Provider, ct),
                            TimeSpan.FromSeconds(2), startup).ConfigureAwait(false) && host.IsRunning)
                            return host.Endpoint;
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or TimeoutException)
                    {
                        // A process that has just been started can still be loading its model.
                    }
                    await Task.Delay(400, startup).ConfigureAwait(false);
                }

                throw new InvalidOperationException("Сервер локальной модели завершился до готовности. Проверьте путь и параметры модели.");
            }, StartupTimeout, token).ConfigureAwait(false);
        }
        catch
        {
            StopOwnedHost(host.Endpoint);
            throw;
        }
    }

    private ManagedLocalAiHost GetOrStartHost(ManagedLocalAiHost.Launch configuration)
    {
        lock (state)
        {
            if (ownedHost is { IsRunning: true } current && current.Configuration == configuration)
                return current;
        }

        StopOwnedHost();
        ManagedLocalAiHost started;
        lock (state)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            started = new(configuration, (_, _) => ProcessStateChanged?.Invoke(this, EventArgs.Empty));
            ownedHost = started;
        }
        ProcessStateChanged?.Invoke(this, EventArgs.Empty);
        return started;
    }

    private Task StopOwnedHostAsync(Uri endpoint)
    {
        StopOwnedHost(endpoint);
        return Task.CompletedTask;
    }

    private void StopOwnedHost(Uri? endpoint = null)
    {
        ManagedLocalAiHost? host;
        lock (state)
        {
            host = ownedHost;
            if (host is null || (endpoint is not null && endpoint != host.Endpoint))
                return;
            ownedHost = null;
        }

        CancelIdle(new(host.Endpoint, null));
        host.Dispose();
        ProcessStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> CanLeaseExternalModelAsync(Uri endpoint, string model, CancellationToken token)
    {
        try
        {
            return await WithTimeoutAsync(ct => protocol.IsUnloadedLlamaRouterModelAsync(endpoint, model, ct),
                DiscoveryTimeout, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or TimeoutException)
        {
            // Standalone external llama-server cannot be unloaded through HTTP.
            return false;
        }
    }

    private bool CancelIdle(IdleTarget target)
    {
        lock (state)
        {
            if (!pendingIdle.Remove(target, out var previous))
                return false;
            previous.Cancel();
            return true;
        }
    }

    private void QueueIdle(IdleTarget target, int minutes, Func<CancellationToken, Task> release)
    {
        if (minutes < 0)
            return;

        lock (state)
        {
            if (disposed)
                return;
            var pending = CancellationTokenSource.CreateLinkedTokenSource(stopped);
            if (pendingIdle.Remove(target, out var previous))
                previous.Cancel();
            pendingIdle[target] = pending;
            _ = RunIdleAsync(target, pending, TimeSpan.FromMinutes(minutes), release);
        }
    }

    private async Task RunIdleAsync(IdleTarget target, CancellationTokenSource pending, TimeSpan delay,
        Func<CancellationToken, Task> release)
    {
        try
        {
            await Task.Delay(delay, pending.Token).ConfigureAwait(false);
            await operations.WaitAsync(pending.Token).ConfigureAwait(false);
            try
            {
                lock (state)
                {
                    if (!pendingIdle.TryGetValue(target, out var latest) || latest != pending)
                        return;
                    pendingIdle.Remove(target);
                }

                await WithTimeoutAsync(async ct =>
                {
                    await release(ct).ConfigureAwait(false);
                    return true;
                }, DiscoveryTimeout, pending.Token).ConfigureAwait(false);
            }
            finally
            {
                operations.Release();
            }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (stopped.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or TimeoutException)
        {
            Trace.TraceWarning("Не удалось разгрузить локальную модель ({0}).", error.GetType().Name);
        }
        finally
        {
            lock (state)
            {
                if (pendingIdle.TryGetValue(target, out var latest) && latest == pending)
                    pendingIdle.Remove(target);
                pending.Dispose();
            }
        }
    }

    private static void ValidateOptions(LocalAiOptions options)
    {
        if (!Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.Provider))
            throw new ArgumentException("Выберите провайдера и режим запуска локальной модели.");
        // Task.Delay uses a finite duration; keeping the model loaded is represented separately.
        if (options.IdleMinutes is < -1 or > MaxIdleMinutes)
            throw new ArgumentOutOfRangeException(nameof(options), "Укажите допустимое время освобождения памяти.");
    }

    private static Uri ParseEndpoint(string? address, LocalAiProvider provider)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Укажите HTTP- или HTTPS-адрес локального сервера модели.");

        var path = endpoint.AbsolutePath.TrimEnd('/');
        var apiSuffix = provider == LocalAiProvider.Ollama ? "/api" : "/v1";
        if (path.EndsWith(apiSuffix, StringComparison.Ordinal))
            path = path[..^apiSuffix.Length];
        return new UriBuilder(endpoint) { Path = path + "/" }.Uri;
    }

    private static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, TimeSpan timeout,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            return await action(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Локальная модель не ответила вовремя.", error);
        }
    }

    public void Dispose()
    {
        lock (state)
        {
            if (disposed)
                return;
            disposed = true;
            lifetime.Cancel();
        }
        StopOwnedHost();
        http.Dispose();
        lifetime.Dispose();
    }

    private readonly record struct IdleTarget(Uri Endpoint, string? Model);
}
