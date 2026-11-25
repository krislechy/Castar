using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public sealed class IncomesService(IIncomesRepository incomesRepository, IUsersRepository usersRepository)
        : IIncomesService
    {
        public async Task<Income> Add(Income income, long telegramUserId, User? user = null,
            CancellationToken cancellationToken = default)
        {
            var owner = user is { Id: var id } && id != Guid.Empty
                ? user
                : await usersRepository.RequireTelegramUserAsync(telegramUserId, cancellationToken);
            income.UserId = owner.Id;
            income.User = owner;
            income.AddedOn = income.AddedOn == default ? DateTime.UtcNow : income.AddedOn;
            RoundLegacyAmount(income);
            return await incomesRepository.Add(income, cancellationToken);
        }

        public async Task<IReadOnlyList<Income>> GetEntityByMessageId(
            long telegramUserId, long messageId, CancellationToken cancellationToken = default)
        {
            var user = await usersRepository.RequireTelegramUserAsync(telegramUserId, cancellationToken);
            return await incomesRepository.ListByMessage(user.Id, messageId, cancellationToken);
        }

        public async Task SaveMessageIncomes(long telegramUserId, long messageId, IReadOnlyList<Income> incomes,
            CancellationToken cancellationToken = default)
        {
            var user = await usersRepository.RequireTelegramUserAsync(telegramUserId, cancellationToken);
            var now = DateTime.UtcNow;
            foreach (var income in incomes)
            {
                if (income.AddedOn == default)
                    income.AddedOn = now;
                RoundLegacyAmount(income);
            }

            await incomesRepository.SaveMessageIncomes(user.Id, messageId, incomes, cancellationToken);
        }

        public Task Update(Income income, bool updateCategory = false, CancellationToken cancellationToken = default)
        {
            if (income.User is { } owner && owner.Id != income.UserId)
            {
                income.UserId = owner.Id;
                income.MessageId = 0;
            }

            RoundLegacyAmount(income);
            return incomesRepository.Update(income, updateCategory, cancellationToken);
        }

        public Task<bool> Delete(Guid id, CancellationToken cancellationToken = default) =>
            incomesRepository.Delete(id, cancellationToken);

        private static void RoundLegacyAmount(Income income)
        {
            if (income.Currency is null)
                income.Sum = decimal.Round(income.Sum, 2, MidpointRounding.AwayFromZero);
        }
    }
}
