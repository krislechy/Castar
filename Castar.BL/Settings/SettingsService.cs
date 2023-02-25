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
    public class SettingsService : ISettingsService
    {
        private SettingsModel SettingsModel;
        private readonly ISettingsRepository settingsRepository;
        public SettingsService(ISettingsRepository settingsRepository)
        {
            this.settingsRepository = settingsRepository;
        }
        public void Save(object settings) {
            SettingsModel = (SettingsModel)settings;
            settingsRepository.Save(SettingsModel);
        }
        public SettingsModel Get()
        {
            if (SettingsModel is null)
                SettingsModel = settingsRepository.Load();
            return SettingsModel;
        }

        public Action<SettingsModel> action;
        public void Apply([Optional] Action<SettingsModel> action)
        {
            this.action ??= action;
            this.action(SettingsModel);
        }

        private Action disposeAction;
        public void Dispose([Optional] Action setDisposeAction)
        {
            this.disposeAction ??= setDisposeAction;
            this.disposeAction();
        }
    }
}
