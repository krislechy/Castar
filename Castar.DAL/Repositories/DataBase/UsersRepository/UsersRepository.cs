using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase
{
    public sealed class UsersRepository(IDbContextFactory<CastarDbContext> contextFactory)
        : BaseRepository<User>(contextFactory), IUsersRepository
    {
        public async Task<IReadOnlyList<User>> List(bool hasTelegramId = false, CancellationToken cancellationToken = default)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            var users = db.Users.AsNoTracking();
            if (hasTelegramId)
                users = users.Where(x => x.TelegramId != 0);
            return await users.ToListAsync(cancellationToken);
        }

        public async Task<User?> FindByTelegramId(long telegramId, CancellationToken cancellationToken = default)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            return await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.TelegramId == telegramId, cancellationToken);
        }
    }
}
