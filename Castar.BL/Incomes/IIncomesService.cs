using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Castar.BL
{
    public interface IIncomesService
    {
        Task<ObservableCollection<Income>> GetEntities(DateTime start, DateTime finish);
        Task<ObservableCollection<Income>> GetEntitiesWithAllIncludes(DateTime start, DateTime finish);
        Task<Income> Add(Income income, long telegramUserId, [Optional] User user);
        Task<bool> Delete(Guid id);
        Task Update(Income income);
        Task<IEnumerable<Income>> GetEntityByMessageId(long messageId);
    }
}
