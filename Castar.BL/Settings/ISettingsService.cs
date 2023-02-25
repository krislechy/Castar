using Castar.Domain.Models.SettingsModel;
using Castar.DAL.Repositories.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Castar.BL.Settings
{
    public interface ISettingsService
    {
        public void Save(object settings);
        public SettingsModel Get();
        void Apply([Optional]Action<SettingsModel> action);
        void Dispose([Optional] Action setDisposeAction);
    }
}
