using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Castar.Compontents
{
    /// <summary>
    /// Логика взаимодействия для IncomePanel.xaml
    /// </summary>
    public partial class IncomePanel : UserControl
    {
        private static DependencyProperty UsersProperty = DependencyProperty.Register(nameof(Users), typeof(IEnumerable<User>), typeof(IncomePanel));
        public IEnumerable<User> Users
        {
            get
            {
                return (IEnumerable<User>)GetValue(UsersProperty);
            }
            set
            {
                SetValue(UsersProperty, value);
            }
        }
        public IncomePanel()
        {
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            var dc = (Income)DataContext;
            if (dc == null) return;
            var user = Users.SingleOrDefault(x => x.Id == dc.UserId);
            UsersListView.SelectedValue = user;
        }
    }
}
