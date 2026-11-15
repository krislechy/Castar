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

    internal static string DescribeError(Exception error) => error switch
    {
        ArgumentException => error.Message,
        HttpRequestException requestError when requestError.StatusCode is not null => requestError.Message,
        OperationCanceledException => "Сервер не ответил вовремя. Проверьте связь и обновите данные.",
        _ => "Не удалось получить ответ Castar Server. Проверьте адрес и доступность сервера."
    };

    public async Task TestConnectionAsync(Uri server, string apiKey)
    {
        using var request = CreateRequest(HttpMethod.Get, server, apiKey, "api/status");
        using var response = await TransportFor(request).SendAsync(request);
        EnsureSuccess(response);
        var status = await response.Content.ReadFromJsonAsync<ServerStatus>();
        if (status?.ApiVersion != 1)
            throw new HttpRequestException("Версия API Castar Server не поддерживается.");
    }

    public async Task<ObservableCollection<User>> GetUsersAsync()
    {
        var users = await SendAsync<UserDto[]>(HttpMethod.Get, "api/users");
        return new ObservableCollection<User>(users.Select(ToUser));
    }

    public async Task<User> AddUserAsync(User user) =>
        ToUser(await SendAsync<UserDto>(HttpMethod.Post, "api/users", UserWrite(user)));

    public async Task<User> UpdateUserAsync(User user) =>
        ToUser(await SendAsync<UserDto>(HttpMethod.Put, $"api/users/{user.Id:D}", UserWrite(user)));

    public Task<bool> DeleteUserAsync(Guid id) => DeleteAsync($"api/users/{id:D}");

    public async Task<ObservableCollection<Income>> GetIncomesAsync(DateTime start, DateTime finish)
    {
        var incomes = SendAsync<IncomeDto[]>(HttpMethod.Get, $"api/incomes?{DateQuery(start, finish)}");
        var users = GetUsersAsync();
        await Task.WhenAll(incomes, users);
        var usersById = (await users).ToDictionary(user => user.Id);
        return new ObservableCollection<Income>((await incomes).Select(income =>
            ToIncome(income, usersById.GetValueOrDefault(income.UserId))));
    }

    public Task<PurchaseDto[]> GetPurchasesAsync(DateTime start, DateTime finish, CancellationToken cancellationToken = default) =>
        SendAsync<PurchaseDto[]>(HttpMethod.Get, $"api/purchases?{DateQuery(start, finish)}", cancellationToken: cancellationToken);

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
        CancellationToken cancellationToken = default) where T : class
    {
        using var request = CreateRequest(method, ParseServerUrl(settings.ServerUrl), settings.ApiKey, path);
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        using var response = await TransportFor(request).SendAsync(request, cancellationToken);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new HttpRequestException("Сервер вернул пустой ответ.");
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
            HttpStatusCode.BadRequest => "Сервер отклонил данные. Проверьте заполненные поля.",
            HttpStatusCode.NotFound => "Запись или путь API не найдены.",
            HttpStatusCode.Conflict => "Изменение конфликтует с данными на сервере.",
            _ => $"Сервер Castar вернул HTTP {(int)response.StatusCode}."
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
