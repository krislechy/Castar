using Castar.Domain.Models.SettingsModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.DAL.Repositories.Settings
{
    public interface ISettingsRepository
    {
        public void Save(SettingsModel settings);
        public SettingsModel Load();
    }
}
