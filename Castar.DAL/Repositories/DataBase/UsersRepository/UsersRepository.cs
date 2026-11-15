using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase
{
    public sealed class UsersRepository(CastarDbContext db)
        : BaseRepository<User>(db), IUsersRepository
    {
        public async Task<IReadOnlyList<User>> List(bool hasTelegramId = false, CancellationToken cancellationToken = default)
        {
            var users = Context.Users.AsNoTracking();
            if (hasTelegramId)
                users = users.Where(x => x.TelegramId != 0);
            return await users.ToListAsync(cancellationToken);
        }

        public Task<User?> FindByTelegramId(long telegramId, CancellationToken cancellationToken = default) =>
            Context.Users.AsNoTracking().FirstOrDefaultAsync(x => x.TelegramId == telegramId, cancellationToken);

        public override async Task Update(User user, CancellationToken cancellationToken = default)
        {
            var current = await FindForUpdate(user.Id, cancellationToken);
            current.Name = user.Name;
            current.Sex = user.Sex;
            current.TelegramId = user.TelegramId;
            current.ColorRaw = user.ColorRaw;
            current.ImageRaw = user.ImageRaw;
            await Context.SaveChangesAsync(cancellationToken);
        }
    }
}
