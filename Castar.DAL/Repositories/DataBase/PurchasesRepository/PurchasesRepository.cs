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
    public class PurchasesRepository : BaseRepository<Purchase>, IPurchasesRepository
    {
        public PurchasesRepository(string connectionString) : base(connectionString)
        {

        }

        public override DbSet<Purchase> GetEntity()=>base.table;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder
                .Entity<Item>()
                .HasMany<User>(u => u.Users)
                .WithMany(c => c.Items);
        }
        public async Task<bool> SearchByRawData(byte[] query)
        {
            if (query != null)
            {
                return await base.table.AnyAsync(x => x.RawData != null && x.RawData == query);
            }
            return false;
        }
    }
}
