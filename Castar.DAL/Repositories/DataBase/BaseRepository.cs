using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Castar.DAL.Repositories.DataBase
{
    public abstract class BaseRepository<T> : DbContext where T : class
    {
        private string connectionString;
        public BaseRepository(string connectionString)
        {
            this.connectionString = connectionString;
        }

        protected DbSet<T> table { get; set; }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlServer(connectionString, (e) =>
            {
                e.EnableRetryOnFailure();
            });
        }
        public virtual async Task<int> Save() => await base.SaveChangesAsync();
        public virtual async Task<T> Add(T entity)
        {
            base.ChangeTracker.Clear();
            await base.AddAsync(entity);
            await base.SaveChangesAsync();
            return entity;
        }
        public virtual async void Reload(T entity)
        {
            await base.Entry(entity).ReloadAsync();
        }
        public virtual async Task<bool> Delete(Guid Id)
        {
            base.ChangeTracker.Clear();
            var found=await FindAsync(typeof(T), Id);
            if (found != null)
            {
                base.Remove(found);
                await base.SaveChangesAsync();
                return true;
            }
            return false;
        }
        public virtual async Task Update(T entity)
        {
            base.ChangeTracker.Clear();
            //base.Entry<T>(entity).State = EntityState.Modified;
            base.Update(entity);
            await base.SaveChangesAsync();
        }
        public virtual async Task<T?> Find(Guid Id)=> await FindAsync<T>(Id);

        public abstract DbSet<T> GetEntity();
    }
}
