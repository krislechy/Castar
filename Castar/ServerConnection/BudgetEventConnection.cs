using Castar.Api;
using Castar.Settings;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.ServerConnection;

public sealed class BudgetEventConnection(ClientSettings settings) : IDisposable
{
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private CancellationTokenSource? session;
    private SynchronizationContext? callbackContext;
    private bool started;
    private bool disposed;

    public event Action<BudgetEventDto>? Changed;
    public event Action<ServerSynchronizationDto>? ConfigurationChanged;
    public event Action<ServerUpdateStatusDto?>? UpdateStatusChanged;
    public event Action? UpdateEventsUnavailable;
    public event Action? Synchronized;
    public event Action? Disconnected;
    public bool IsSynchronized { get; private set; }
    public bool SupportsConfigurationEvents { get; private set; }

    public void Start(SynchronizationContext? callbackContext = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
                return;

            started = true;
            this.callbackContext = callbackContext;
            settings.ConnectionChanged += ConnectionChanged;
            Reconnect();
        }
    }

    private void ConnectionChanged(object? sender, EventArgs args)
    {
        lock (gate)
        {
            if (!disposed)
                Reconnect();
        }
    }

    private void Reconnect()
    {
        IsSynchronized = false;
        SupportsConfigurationEvents = false;
        session?.Cancel();
        session = null;
        var configuration = (settings.ServerUrl, settings.ProtectedApiKey);
        var key = settings.ApiKey;
        if (string.IsNullOrEmpty(key))
            return;

        Uri address;
        try
        {
            address = CastarServerClient.ParseServerUrl(configuration.ServerUrl);
        }
        catch (ArgumentException)
        {
            return;
        }

        var current = new CancellationTokenSource();
        session = current;
        _ = Task.Run(() => RunAsync(new Uri(address, BudgetEvents.Path), key, current, configuration));
    }

    private async Task RunAsync(Uri endpoint, string key, CancellationTokenSource current,
        (string ServerUrl, string? ProtectedApiKey) configuration)
    {
        var token = current.Token;
        var delay = TimeSpan.FromSeconds(1);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var connected = false;
                try
                {
                    await using var connection = new HubConnectionBuilder()
                        .WithUrl(endpoint, options =>
                        {
                            options.AccessTokenProvider = () => Task.FromResult<string?>(key);
                            options.HttpMessageHandlerFactory = ConfigureHttpHandler;
                            if (endpoint.IsLoopback)
                                options.WebSocketConfiguration = socket => socket.Proxy = null;
                        })
                        .Build();
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var updateGate = new object();
                    var updateSequence = -1L;
                    void ReceiveUpdate(ServerUpdateSnapshotDto snapshot)
                    {
                        Notify(() =>
                        {
                            lock (updateGate)
                            {
                                if (snapshot.Sequence <= updateSequence)
                                    return;
                                updateSequence = snapshot.Sequence;
                                SupportsConfigurationEvents = true;
                                UpdateStatusChanged?.Invoke(snapshot.Status);
                            }
                        });
                    }

                    connection.Closed += _ =>
                    {
                        closed.TrySetResult();
                        return Task.CompletedTask;
                    };
                    using var subscription = connection.On<BudgetEventDto>(BudgetEvents.Method,
                        change => Notify(() => Changed?.Invoke(change)));
                    using var configurationSubscription = connection.On<ServerSynchronizationDto>(ServerSynchronization.Changed,
                        change => Notify(() =>
                        {
                            SupportsConfigurationEvents = true;
                            ConfigurationChanged?.Invoke(change);
                        }));
                    using var updateSubscription = connection.On<ServerUpdateSnapshotDto>(ServerSynchronization.UpdateChanged,
                        ReceiveUpdate);
                    using var confirmation = connection.On(BudgetEvents.Ready, () => { ready.TrySetResult(); });
                    using (var startup = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        startup.CancelAfter(TimeSpan.FromSeconds(15));
                        await connection.StartAsync(startup.Token);
                        if (await Task.WhenAny(ready.Task, closed.Task).WaitAsync(startup.Token) != ready.Task)
                            throw new InvalidOperationException("The event connection closed before subscription completed.");
                    }
                    connected = true;
                    Notify(() =>
                    {
                        IsSynchronized = true;
                        Synchronized?.Invoke();
                    });
                    delay = TimeSpan.FromSeconds(1);
                    using (var updates = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        updates.CancelAfter(TimeSpan.FromSeconds(5));
                        try
                        {
                            var snapshot = await connection.InvokeAsync<ServerUpdateSnapshotDto>(
                                ServerSynchronization.SubscribeUpdates, updates.Token);
                            ReceiveUpdate(snapshot);
                        }
                        catch (HubException)
                        {
                            // Older servers still support budget changes through the same connection.
                            Notify(() => UpdateEventsUnavailable?.Invoke());
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            Notify(() => UpdateEventsUnavailable?.Invoke());
                        }
                    }
                    await closed.Task.WaitAsync(token);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or
                    InvalidDataException or WebSocketException or InvalidOperationException or TimeoutException or
                    AggregateException or NoTransportSupportedException or HubException)
                {
                    // REST refresh still works when the server doesn't expose events yet.
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // The connection attempt timed out. A later attempt gets a new transport.
                }
                finally
                {
                    if (connected)
                    {
                        Notify(() =>
                        {
                            IsSynchronized = false;
                            Disconnected?.Invoke();
                        });
                    }
                }

                await Task.Delay(delay, token);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            lock (gate)
            {
                if (session == current)
                    session = null;
                current.Dispose();
            }
        }

        void Notify(Action callback)
        {
            void Deliver()
            {
                if (!token.IsCancellationRequested && configuration == (settings.ServerUrl, settings.ProtectedApiKey))
                    callback();
            }

            if (callbackContext is { } context)
                context.Post(_ => Deliver(), null);
            else
                Deliver();
        }

        HttpMessageHandler ConfigureHttpHandler(HttpMessageHandler handler)
        {
            if (handler is HttpClientHandler http)
            {
                http.AllowAutoRedirect = false;
                if (endpoint.IsLoopback)
                    http.UseProxy = false;
            }
            return new EventOriginHandler(endpoint, handler);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            settings.ConnectionChanged -= ConnectionChanged;
            session?.Cancel();
        }
    }

    private sealed class EventOriginHandler(Uri origin, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var target = request.RequestUri;
            if (target is null || target.Scheme != origin.Scheme || target.IdnHost != origin.IdnHost
                || target.Port != origin.Port)
                throw new HttpRequestException("The event service redirected to a different origin.");

            return base.SendAsync(request, token);
        }
    }
}
