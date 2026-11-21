using System.Collections.ObjectModel;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public sealed class IncomesService(IIncomesRepository incomesRepository, IUsersRepository usersRepository)
        : IIncomesService
    {
        public async Task<ObservableCollection<Income>> GetEntities(DateTime start, DateTime finish) =>
            new(await incomesRepository.List(start, finish, includeUser: false));

        public async Task<ObservableCollection<Income>> GetEntitiesWithAllIncludes(DateTime start, DateTime finish) =>
            new(await incomesRepository.List(start, finish, includeUser: true));

        public async Task<Income> Add(Income income, long telegramUserId, User? user = null)
        {
            var owner = user is { Id: var id } && id != Guid.Empty
                ? user
                : await GetUser(telegramUserId, CancellationToken.None);
            income.UserId = owner.Id;
            income.User = owner;
            income.AddedOn = income.AddedOn == default ? DateTime.UtcNow : income.AddedOn;
            RoundLegacyAmount(income);
            return await incomesRepository.Add(income);
        }

        public async Task<IReadOnlyList<Income>> GetEntityByMessageId(
            long telegramUserId, long messageId, CancellationToken cancellationToken = default)
        {
            var user = await GetUser(telegramUserId, cancellationToken);
            return await incomesRepository.ListByMessage(user.Id, messageId, cancellationToken);
        }

        public async Task SaveMessageIncomes(long telegramUserId, long messageId, IReadOnlyList<Income> incomes,
            CancellationToken cancellationToken = default)
        {
            var user = await GetUser(telegramUserId, cancellationToken);
            var now = DateTime.UtcNow;
            foreach (var income in incomes)
            {
                if (income.AddedOn == default)
                    income.AddedOn = now;
                RoundLegacyAmount(income);
            }

            await incomesRepository.SaveMessageIncomes(user.Id, messageId, incomes, cancellationToken);
        }

        public Task Update(Income income, bool updateCategory = false)
        {
            if (income.User is { } owner && owner.Id != income.UserId)
            {
                income.UserId = owner.Id;
                income.MessageId = 0;
            }

            RoundLegacyAmount(income);
            return incomesRepository.Update(income, updateCategory);
        }

        public Task<bool> Delete(Guid id) => incomesRepository.Delete(id);

        private async Task<User> GetUser(long telegramUserId, CancellationToken cancellationToken) =>
            await usersRepository.FindByTelegramId(telegramUserId, cancellationToken)
            ?? throw new InvalidOperationException($"Пользователя с идентификатором {telegramUserId} нет в базе.");

        private static void RoundLegacyAmount(Income income)
        {
            if (income.Currency is null)
                income.Sum = decimal.Round(income.Sum, 2, MidpointRounding.AwayFromZero);
        }
    }
}
