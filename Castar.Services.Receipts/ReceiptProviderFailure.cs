using System.Net;
using Castar.Services.Receipts.Exceptions;

namespace Castar.Services.Receipts;

public sealed class ReceiptProviderUnavailableException(HttpStatusCode statusCode, DateTime? retryAfterUtc)
    : HttpRequestException("Поставщик чеков временно недоступен.", null, statusCode)
{
    public DateTime? RetryAfterUtc { get; } = retryAfterUtc;
}

public static class ReceiptProviderFailure
{
    public static bool IsTemporary(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && error switch
        {
            HttpRequestException http => http.StatusCode is null || IsTemporaryStatus(http.StatusCode.Value),
            TicketNotReadyException or TimeoutException or OperationCanceledException => true,
            _ => false
        };

    internal static bool IsTemporaryStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)status is >= 500 and <= 599;

    public static string? GetPermanentMessage(Exception error) => error switch
    {
        NotImplementedException => "Этот поставщик пока не поддерживает импорт чеков.",
        ArgumentException or InvalidDataException => error.Message,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "Поставщик чеков отклонил авторизацию. Проверьте настройки.",
        HttpRequestException => "Поставщик не принял запрос на получение чека.",
        _ => null
    };
}
