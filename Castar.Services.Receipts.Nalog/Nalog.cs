using Castar.BL.Settings;
using Castar.Domain.Models.SettingsModel;
using Castar.Services.Receipts.Exceptions;
using Castar.Services.Receipts.Models;
using Newtonsoft.Json;
using System.Dynamic;
using System.Net;
using System.Security;

namespace Castar.Services.Receipts
{
    public class Nalog:IReceipt<Ticket>,IDisposable
    {
        private const string CLIENT_SECRET = "IyvrAbKt9h/8p6a7QPh8gpkXYQ4=";
        private const string DEVICE_ID = "7C82010F-16CC-558A-8F66-FC4080C66521";
        private const string HOST = "irkkt-mobile.nalog.ru:8888";
        private const string VERSION = "v2";
        private readonly NetworkCredential credential;
        private readonly HttpClient client;
        private static string? sessionIdString;
        private static string? refreshTokenString;
        public Nalog(NetworkCredential credential)
        {
            if (credential != null)
            {
                this.credential = credential;
                client = new HttpClient();
                SetDefaultHeaders();
            }
        }
        private void Dispose()=>client?.Dispose();
        private void SetDefaultHeaders()
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Host", HOST);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Device-OS", "iOS");
            client.DefaultRequestHeaders.TryAddWithoutValidation("clientVersion", "2.25.0");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Device-Id", DEVICE_ID);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "billchecker/2.9.0 (iPhone; iOS 13.6; Scale/2.00)");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU;q=1, en-US;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json");
        }
        private void updateSessionHeader()
        {
            if (client.DefaultRequestHeaders.Any(x => x.Key == "sessionId"))
            {
                client.DefaultRequestHeaders.Remove("sessionId");
            }
            else
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("sessionId", sessionIdString);
            }
        }
        private async Task<Authenticate> Authenticate()
        {
            if (credential.UserName == null) throw new ArgumentNullException(nameof(credential.UserName));
            if (credential.Password == null) throw new ArgumentNullException(nameof(credential.Password));
            if (CLIENT_SECRET == null) throw new ArgumentNullException(nameof(CLIENT_SECRET));
            var data = getStringContent(new
            {
                inn = credential.UserName,
                password = credential.Password,
                client_secret = CLIENT_SECRET
            });
            var result = await PostDataAsync<Authenticate>(data, "mobile/users/lkfl/auth");
            sessionIdString = result.sessionId;
            refreshTokenString = result.refresh_token;
            updateSessionHeader();
            return result;
        }
        private async Task<bool> checkToken()
        {
            var get = await client.GetAsync($"https://{HOST}/{VERSION}/tickets/");
            return get.IsSuccessStatusCode;
        }
        private async Task<RefreshToken> refreshToken(string? refreshToken)
        {
            if (refreshToken == null) throw new ArgumentNullException(nameof(refreshToken));
            var data = getStringContent(new { refresh_token = refreshToken, client_secret= CLIENT_SECRET });
            var result = await PostDataAsync<RefreshToken>(data, "mobile/users/refresh");
            refreshTokenString = result.refresh_token;
            sessionIdString = result.sessionId;
            updateSessionHeader();
            return result;
        }
        private async Task<TicketId> registryTicketId(string query)
        {
            var data = getStringContent(new { qr=query });
            var result = await PostDataAsync<TicketId>(data, "ticket");
            return result;
        }
        private async Task<Ticket> getTicket(string query)
        {
            var registredTicket = await registryTicketId(query);
            if (registredTicket.id == null) throw new ArgumentNullException(nameof(registredTicket.id));
            var result = await GetDataAsync<Ticket>($"tickets/{registredTicket.id}");
            if (result.ticket == null)
                throw new TicketNotReadyException(result.status, "Чек еще не готов или не существует, попробуйте позже.");
            return result;
        }
        public async Task<Ticket> GetReceipt(string query)
        {
            if (!String.IsNullOrEmpty(sessionIdString))
            {
                var isValidToken = await checkToken();
                if (isValidToken)
                {
                    return await getTicket(query);
                }
                else
                {
                    await refreshToken(refreshTokenString);
                    return await getTicket(query);
                }
            }
            else
            {
                await Authenticate();
                return await getTicket(query);
            }
        }
        #region Helper
        private StringContent getStringContent(object content)=> 
            new StringContent(JsonConvert.SerializeObject(content), System.Text.Encoding.UTF8, "application/json");
        private async Task<T> PostDataAsync<T>(StringContent data,string pathController) where T:class
        {
            var post = await client.PostAsync($"https://{HOST}/{VERSION}/{pathController}", data);
            var content = await post.Content.ReadAsStringAsync();
            return ValidateStatusCode<T>(post.StatusCode, content);
        }
        private async Task<T> GetDataAsync<T>(string pathController) where T : class
        {
            var get = await client.GetAsync($"https://{HOST}/{VERSION}/{pathController}");
            var content = await get.Content.ReadAsStringAsync();
            return ValidateStatusCode<T>(get.StatusCode, content);
        }
        private T ValidateStatusCode<T>(HttpStatusCode statusCode,string content) where T:class =>
            statusCode switch
            {
                HttpStatusCode.OK => JsonConvert.DeserializeObject<T>(content)!,
                HttpStatusCode.BadRequest => throw new HttpRequestException($"🔴 Служба ФНС\n({(int)statusCode}) Неверный запрос: {content}"),
                HttpStatusCode.InternalServerError => throw new HttpRequestException($"🔴 Служба ФНС\n({(int)statusCode}) Ошибка внутри сервера: {content}"),
                _ => throw new HttpRequestException($"🔴 Служба ФНС\nНеожидаемый ответ от сервера - ({(int)statusCode}){statusCode}\n{content}"),
            };

        void IDisposable.Dispose()
        {
            throw new NotImplementedException();
        }
        #endregion
    }
}