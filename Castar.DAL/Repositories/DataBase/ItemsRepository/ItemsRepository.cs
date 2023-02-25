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
    public class ItemsRepository : BaseRepository<Item>, IItemsRepository
    {
        public ItemsRepository(string connectionString):base(connectionString)
        {

        }

        public override DbSet<Item> GetEntity() => base.table;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder
                .Entity<Purchase>()
                .HasMany<Item>(u => u.Items)
                .WithOne(x => x.Purchase);
            modelBuilder
                .Entity<Item>()
                .HasMany<User>(u => u.Users)
                .WithMany(c => c.Items);
        }
    }
}
