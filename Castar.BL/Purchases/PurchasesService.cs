using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Castar.BL
{
    public class PurchasesService : IPurchasesService
    {
        private readonly IPurchasesRepository purchaseRepository;
        //private readonly IItemsRepository itemsRepository;
        private readonly IUsersRepository usersRepository;
        public PurchasesService(IPurchasesRepository purchaseRepository, IUsersRepository usersRepository)
        {
            this.purchaseRepository = purchaseRepository;
            this.usersRepository = usersRepository;
            //this.itemsRepository = itemsRepository;
        }

        public async Task<ObservableCollection<Purchase>> GetEntities(DateTime start, DateTime finish) =>
             new ObservableCollection<Purchase>(await purchaseRepository.GetEntity()
                .Where(x => x.AddedOn >= start && x.AddedOn <= finish)
                .ToListAsync());

        public async Task<ObservableCollection<Purchase>> GetEntitiesWithAllIncludes(DateTime start, DateTime finish) =>
            new ObservableCollection<Purchase>(await purchaseRepository.GetEntity()
                .Where(x => x.AddedOn >= start && x.AddedOn <= finish)
                .Include(u => u.User)
                .Include(u => u.Items)
                .ToListAsync());
        public async Task<Purchase?> GetPurchaseById(Guid id)
        {
            return await purchaseRepository.Find(id);
        }
        public async Task<bool> IsExistTheSame(byte[] query)
        {
            return await purchaseRepository.SearchByRawData(query);
        }
        public async Task<Purchase> Add(Purchase purchase, long telegramUserId)
        {
            var user = await usersRepository.FindByTelegramId(telegramUserId);
            if (user == null) throw new Exception($"Пользователя с идентификатором {telegramUserId} не существует в базе");
            purchase.UserId = user!.Id;
            purchase.AddedOn ??= DateTime.Now;
            return await purchaseRepository.Add(purchase);
        }

        public async Task<IEnumerable<Purchase>> GetEntityByMessageId(long messageId)
        {
            return await purchaseRepository
                .GetEntity()
                .Where(x => x.MessageId == messageId)
                .Include(u => u.User)
                .Include(u=>u.Items)
                .ToListAsync();
        }
        public async Task Update(Purchase purchase)
        {
            await purchaseRepository.Update(purchase);
        }

        public async Task Delete(Guid id)
        {
            await purchaseRepository.Delete(id);
        }
    }
}
