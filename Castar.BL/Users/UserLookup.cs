using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    internal static class UserLookup
    {
        internal static async Task<User> RequireTelegramUserAsync(this IUsersRepository usersRepository,
            long telegramUserId, CancellationToken cancellationToken) =>
            await usersRepository.FindByTelegramId(telegramUserId, cancellationToken)
            ?? throw new InvalidOperationException($"Пользователя с идентификатором {telegramUserId} нет в базе.");
    }
}
