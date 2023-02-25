using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Castar.Commands
{
    public static class MenuSelector
    {
        public static readonly RoutedUICommand MenuSelect = new RoutedUICommand("Select Menu with Content as Parameter", "MenuSelector", typeof(MainWindow));
    }
}
