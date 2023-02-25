using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.DAL.Repositories.DataBase
{
    public class IncomesRepository : BaseRepository<Income>, IIncomesRepository
    {
        public IncomesRepository(string connectionString):base(connectionString)
        {

        }
        public override DbSet<Income> GetEntity() => base.table;
    }
}
