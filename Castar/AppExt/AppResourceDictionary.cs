using Castar.BL.Settings;
using Castar.BL.Settings.Autorun;
using Castar.TrayNotify.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Castar
{
    public partial class App : Application
    {
        private void ResolveResourceDictionary()
        {
            AddResourceDictionary(new Uri("/Styles/Chrome.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/MainStyle.xaml", UriKind.Relative));

            AddResourceDictionary(new Uri("/Svgs/SystemControls.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Svgs/Menu.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Svgs/Pack_01.xaml", UriKind.Relative));

            AddResourceDictionary(new Uri("/Styles/Button.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/ToggleButton.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/ToggleButtonIOS.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/CheckBox.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/TextBox.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/PasswordBox.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/ContextMenu.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/Tooltip.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/Separator.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/ListView.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/RadioButton.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/ScrollViewer.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/DatePickerTextBox.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/DatePicker.xaml", UriKind.Relative));
            AddResourceDictionary(new Uri("/Styles/ProgressBar.xaml", UriKind.Relative));
        }
        private void AddResourceDictionary(Uri uri)
        {
            ResourceDictionary myResourceDictionary = new ResourceDictionary() { Source = uri };
            Application.Current.Resources.MergedDictionaries.Add(myResourceDictionary);
        }
    }
}
