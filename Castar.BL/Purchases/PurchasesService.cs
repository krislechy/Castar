using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public sealed class PurchasesService(
        IPurchasesRepository purchaseRepository, IUsersRepository usersRepository) : IPurchasesService
    {
        public Task<IReadOnlyList<Purchase>> ListAsync(DateTime start, DateTime finish, CancellationToken cancellationToken) =>
            purchaseRepository.ListAsync(start, finish, cancellationToken);

        public Task<Purchase?> GetPurchaseById(Guid id) => purchaseRepository.Find(id);

        public async Task<Purchase> Add(Purchase purchase, long telegramUserId, CancellationToken cancellationToken = default)
        {
            await AssignOwnerAndDate(purchase, telegramUserId, cancellationToken);
            return await purchaseRepository.Add(purchase, cancellationToken);
        }

        public async Task<Purchase?> AddReceipt(Purchase purchase, long telegramUserId, string provider, string receiptId,
            CancellationToken cancellationToken = default)
        {
            await AssignOwnerAndDate(purchase, telegramUserId, cancellationToken);
            return await purchaseRepository.AddReceipt(purchase, provider, receiptId, cancellationToken);
        }

        public async Task<Purchase?> GetManualPurchaseByMessageId(
            long telegramUserId, long messageId, CancellationToken cancellationToken = default)
        {
            var owner = await usersRepository.RequireTelegramUserAsync(telegramUserId, cancellationToken);
            return await purchaseRepository.FindManualByMessage(owner.Id, messageId, cancellationToken);
        }

        public Task Update(Purchase purchase, CancellationToken cancellationToken = default) =>
            purchaseRepository.Update(purchase, cancellationToken);

        public Task Delete(Guid id, CancellationToken cancellationToken = default) =>
            purchaseRepository.Delete(id, cancellationToken);

        public Task<PurchaseMutationResult> DeletePurchaseAsync(Guid purchaseId, long expectedRevision,
            CancellationToken cancellationToken = default) =>
            purchaseRepository.DeletePurchaseAsync(purchaseId, expectedRevision, cancellationToken);

        private async Task AssignOwnerAndDate(Purchase purchase, long telegramUserId, CancellationToken cancellationToken)
        {
            var owner = await usersRepository.RequireTelegramUserAsync(telegramUserId, cancellationToken);
            purchase.UserId = owner.Id;
            purchase.AddedOn ??= DateTime.UtcNow;
        }

    }
}
