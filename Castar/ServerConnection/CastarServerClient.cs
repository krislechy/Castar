using Castar.Api;
using Castar.Domain.Models.DataBase;
using Castar.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.ServerConnection;

public sealed class CastarServerClient(ClientSettings settings) : IDisposable
{
    private readonly HttpClient httpsClient = CreateHttpClient(useProxy: true);
    private readonly HttpClient localClient = CreateHttpClient(useProxy: false);

    internal event EventHandler? ConnectionChanged
    {
        add => settings.ConnectionChanged += value;
        remove => settings.ConnectionChanged -= value;
    }

    private static HttpClient CreateHttpClient(bool useProxy) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = useProxy,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = 32 * 1024 * 1024
    };

    public static Uri ParseServerUrl(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp))
            || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("Укажите HTTPS-адрес Castar Server. Для локального сервера доступен HTTP.");
        }
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    internal static bool IsConnectionError(Exception error) =>
        error is ArgumentException or HttpRequestException or OperationCanceledException or JsonException;

    internal static string DescribeError(Exception error, Uri? server = null) => error switch
    {
        ArgumentException => error.Message,
        HttpRequestException requestError => DescribeRequestError(requestError, server),
        OperationCanceledException { InnerException: TimeoutException } => "Истекло время ожидания ответа сервера.",
        OperationCanceledException => "Запрос отменён.",
        JsonException => "Ответ сервера имеет неверный формат.",
        _ => "Не удалось получить ответ Castar Server. Проверьте адрес и доступность сервера."
    };

    private static string DescribeRequestError(HttpRequestException error, Uri? server)
    {
        var message = error.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "Не удалось найти сервер по имени. Проверьте адрес и DNS.",
            HttpRequestError.SecureConnectionError => "Сбой TLS. Проверьте HTTPS-адрес и сертификат сервера.",
            HttpRequestError.ProxyTunnelError => "Прокси не установил соединение с сервером.",
            HttpRequestError.ResponseEnded => "Соединение оборвалось до получения полного ответа.",
            HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError or HttpRequestError.VersionNegotiationError =>
                "Сервер вернул некорректный ответ HTTP. Проверьте адрес и протокол.",
            HttpRequestError.ConfigurationLimitExceeded => "Ответ сервера превышает допустимый размер.",
            _ when error.StatusCode is not null => error.Message,
            _ => DescribeTransportError(error, server)
        };

        return error.StatusCode is { } status ? $"{message.TrimEnd('.')} (HTTP {(int)status})." : message;
    }

    private static string DescribeTransportError(HttpRequestException error, Uri? server)
    {
        return (error.GetBaseException() as SocketException)?.SocketErrorCode switch
        {
            SocketError.ConnectionRefused when server is { IsLoopback: true } =>
                $"На порте {server.Port} нет соединения. Проверьте, что Castar Server запущен.",
            SocketError.ConnectionRefused => "Сервер отклонил TCP-подключение. Проверьте адрес и порт.",
            SocketError.TimedOut => "Истекло время ожидания TCP-подключения.",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
                "Сервер недоступен по сети. Проверьте подключение.",
            SocketError.ConnectionReset or SocketError.ConnectionAborted => "TCP-соединение оборвалось.",
            _ when error.HttpRequestError == HttpRequestError.ConnectionError => "Не удалось установить TCP-соединение с сервером.",
            _ => "Не удалось получить ответ Castar Server. Проверьте адрес и доступность сервера."
        };
    }

    public async Task TestConnectionAsync(Uri server, string apiKey)
    {
        using var request = CreateRequest(HttpMethod.Get, server, apiKey, "api/status");
        using var response = await TransportFor(request).SendAsync(request);
        EnsureSuccess(response);
        var status = await ReadResponseAsync<ServerStatus>(response);
        if (status.ApiVersion != 1)
            throw new HttpRequestException("Версия API Castar Server не поддерживается.", null, response.StatusCode);
    }

    public async Task<ObservableCollection<User>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var users = await SendAsync<UserDto[]>(HttpMethod.Get, "api/users", cancellationToken: cancellationToken);
        return new ObservableCollection<User>(users.Select(ToUser));
    }

    public async Task<User> AddUserAsync(User user) =>
        ToUser(await SendAsync<UserDto>(HttpMethod.Post, "api/users", UserWrite(user)));

    public async Task<User> UpdateUserAsync(User user) =>
        ToUser(await SendAsync<UserDto>(HttpMethod.Put, $"api/users/{user.Id:D}", UserWrite(user)));

    public Task<bool> DeleteUserAsync(Guid id) => DeleteAsync($"api/users/{id:D}");

    public async Task<ObservableCollection<Income>> GetIncomesAsync(DateTime start, DateTime finish,
        CancellationToken cancellationToken = default)
    {
        var incomes = SendAsync<IncomeDto[]>(HttpMethod.Get, $"api/incomes?{DateQuery(start, finish)}",
            cancellationToken: cancellationToken);
        var users = GetUsersAsync(cancellationToken);
        await Task.WhenAll(incomes, users);
        var usersById = (await users).ToDictionary(user => user.Id);
        return new ObservableCollection<Income>((await incomes).Select(income =>
            ToIncome(income, usersById.GetValueOrDefault(income.UserId))));
    }

    public Task<PurchaseDto[]> GetPurchasesAsync(DateTime start, DateTime finish, CancellationToken cancellationToken = default) =>
        SendAsync<PurchaseDto[]>(HttpMethod.Get, $"api/purchases?{DateQuery(start, finish)}", cancellationToken: cancellationToken);

    public Task<ServerSettingsDto> GetServerSettingsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ServerSettingsDto>(HttpMethod.Get, "api/settings", cancellationToken: cancellationToken);

    public Task<ServerSettingsDto> SaveServerSettingsAsync(ServerSettingsWriteDto settings, CancellationToken cancellationToken = default) =>
        SendAsync<ServerSettingsDto>(HttpMethod.Put, "api/settings", settings, cancellationToken, readProblemDetails: true);

    public Task<ServerUpdateStatusDto> GetServerUpdateAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ServerUpdateStatusDto>(HttpMethod.Get, "api/server-update", cancellationToken: cancellationToken);

    public Task<ServerUpdateStatusDto> StartServerUpdateAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ServerUpdateStatusDto>(HttpMethod.Post, "api/server-update",
            cancellationToken: cancellationToken, readProblemDetails: true);

    private static string DateQuery(DateTime start, DateTime finish)
    {
        const string format = "yyyy-MM-ddTHH:mm:ss.fffffff";
        var first = Uri.EscapeDataString(start.ToString(format, CultureInfo.InvariantCulture));
        var last = Uri.EscapeDataString(finish.ToString(format, CultureInfo.InvariantCulture));
        return $"start={first}&finish={last}";
    }

    public async Task<Income> AddIncomeAsync(Income income) =>
        ToIncome(await SendAsync<IncomeDto>(HttpMethod.Post, "api/incomes", IncomeWrite(income)), income.User);

    public async Task<Income> UpdateIncomeAsync(Income income) =>
        ToIncome(await SendAsync<IncomeDto>(HttpMethod.Put, $"api/incomes/{income.Id:D}", IncomeWrite(income)), income.User);

    public Task<bool> DeleteIncomeAsync(Guid id) => DeleteAsync($"api/incomes/{id:D}");

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? payload = null,
        CancellationToken cancellationToken = default, bool readProblemDetails = false) where T : class
    {
        using var request = CreateRequest(method, ParseServerUrl(settings.ServerUrl), settings.ApiKey, path);
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        using var response = await TransportFor(request).SendAsync(request, cancellationToken);
        if (readProblemDetails && (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict) &&
            response.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            ServerSettingsProblem? problem = null;
            try
            {
                problem = await response.Content.ReadFromJsonAsync<ServerSettingsProblem>(cancellationToken);
            }
            catch (JsonException)
            {
                // Use the HTTP status if the server returned an unreadable problem.
            }
            if (!string.IsNullOrWhiteSpace(problem?.Detail))
                throw new HttpRequestException(problem.Detail, null, response.StatusCode);
        }
        EnsureSuccess(response);
        return await ReadResponseAsync<T>(response, cancellationToken);
    }

    private sealed record ServerSettingsProblem(string? Detail);

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new HttpRequestException("Сервер вернул пустой ответ.", null, response.StatusCode);
        }
        catch (JsonException error)
        {
            throw new HttpRequestException("Ответ сервера имеет неверный формат.", error, response.StatusCode);
        }
    }

    private async Task<bool> DeleteAsync(string path)
    {
        using var request = CreateRequest(HttpMethod.Delete, ParseServerUrl(settings.ServerUrl), settings.ApiKey, path);
        using var response = await TransportFor(request).SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        EnsureSuccess(response);
        return true;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri server, string apiKey, string path)
    {
        if (apiKey.Length < 32 || apiKey.Any(char.IsWhiteSpace))
            throw new ArgumentException("Укажите ключ API Castar Server длиной от 32 символов.");
        return new HttpRequestMessage(method, new Uri(server, path))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", apiKey) }
        };
    }

    private HttpClient TransportFor(HttpRequestMessage request) =>
        request.RequestUri!.Scheme == Uri.UriSchemeHttp ? localClient : httpsClient;

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Сервер отклонил ключ API.",
            HttpStatusCode.Forbidden => "Сервер отказал в доступе.",
            HttpStatusCode.BadRequest => "Сервер отклонил данные. Проверьте заполненные поля.",
            HttpStatusCode.NotFound => "Запись или путь API не найдены.",
            HttpStatusCode.Conflict => "Изменение конфликтует с данными на сервере.",
            HttpStatusCode.ServiceUnavailable => "Сервер временно недоступен.",
            _ when (int)response.StatusCode is >= 300 and < 400 => "Сервер перенаправил запрос. Укажите конечный адрес.",
            _ => "Сервер вернул ошибку."
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static UserWriteDto UserWrite(User user) =>
        new(user.TelegramId, user.Name, user.Sex, user.ColorRaw, user.ImageRaw);

    private static IncomeWriteDto IncomeWrite(Income income) =>
        new(income.User?.Id ?? income.UserId, income.Sum, income.Source, income.AddedOn);

    private static User ToUser(UserDto dto) => new()
    {
        Id = dto.Id, TelegramId = dto.TelegramId, Name = dto.Name, Sex = dto.Sex,
        ColorRaw = dto.ColorRaw, ImageRaw = dto.ImageRaw
    };

    private static Income ToIncome(IncomeDto dto, User? user) => new()
    {
        Id = dto.Id, UserId = dto.UserId, User = user ?? ToUser(dto.User), MessageId = dto.MessageId,
        Sum = dto.Sum, Source = dto.Source, AddedOn = dto.AddedOn
    };

    public void Dispose()
    {
        localClient.Dispose();
        httpsClient.Dispose();
    }

    private sealed record ServerStatus(int ApiVersion);
}
