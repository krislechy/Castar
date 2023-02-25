using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System.Runtime.InteropServices;

namespace Castar.BL
{
    public class IncomesService : IIncomesService
    {
        private readonly IIncomesRepository incomesRepository;
        private readonly IUsersRepository usersRepository;
        public IncomesService(IIncomesRepository incomesRepository, IUsersRepository usersRepository)
        {
            this.incomesRepository = incomesRepository;
            this.usersRepository = usersRepository;
        }

        public async Task<ObservableCollection<Income>> GetEntities(DateTime start, DateTime finish) =>
             new ObservableCollection<Income>(await incomesRepository.GetEntity()
                .Where(x => x.AddedOn >= start && x.AddedOn <= finish)
                .ToListAsync());

        public async Task<ObservableCollection<Income>> GetEntitiesWithAllIncludes(DateTime start, DateTime finish) =>
            new ObservableCollection<Income>(await incomesRepository.GetEntity()
                 .Where(x => x.AddedOn >= start && x.AddedOn <= finish)
                .Include(u => u.User).ToListAsync());

        public async Task<Income> Add(Income income, long telegramUserId, [Optional] User user)
        {
            var _user = await usersRepository.FindByTelegramId(telegramUserId);
            if (_user == null) throw new Exception($"Пользователя с идентификатором {telegramUserId} не существует в базе");
            income.UserId = _user!.Id;
            income.AddedOn = income.AddedOn == default ? DateTime.Now : income.AddedOn;
            income.User = null;
            var result = await incomesRepository.Add(income);
            result.User = user == null ? _user : user;
            return result;
        }

        public async Task<IEnumerable<Income>> GetEntityByMessageId(long messageId)
        {
            return await incomesRepository.GetEntity().Where(x => x.MessageId == messageId).ToListAsync();
        }
        public async Task Update(Income income)
        {
            await incomesRepository.Update(income);
        }
        public async Task<bool> Delete(Guid id) => await incomesRepository.Delete(id);
    }
}
