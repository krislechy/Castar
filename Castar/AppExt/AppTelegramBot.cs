using Castar.Compontents;
using Castar.Logger;
using Castar.Services.TelegramBot;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Castar
{
    public partial class App: Application
    {
        private ICastarTelegramBotClient ResolveTelegramBot(ServiceProvider serviceProvider)
        {
            return serviceProvider.GetService<ICastarTelegramBotClient>();
        }
    }
}
