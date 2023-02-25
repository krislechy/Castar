using Castar.BL;
using Castar.BL.Settings;
using Castar.Domain.Models.SettingsModel;
using Castar.Services.Receipts;
using Castar.Services.Receipts.Barcode;
using Castar.Services.Receipts.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.TelegramBot.Commands
{
    public partial class CommandHandler
    {
        protected ISettingsService settingsService;
        protected SettingsModel settings;
        protected IUsersService usersService;
        protected IIncomesService incomesService;
        protected IPurchasesService purchasesService;
        protected IItemsService itemsService;
    }
}
