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
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Castar.Compontents
{
    /// <summary>
    /// Логика взаимодействия для WarningPanel.xaml
    /// </summary>
    [ContentProperty("ObjectContent")]
    public partial class WarningPanel : UserControl
    {
        public static DependencyProperty ObjectContentProperty = DependencyProperty.Register(nameof(ObjectContent), typeof(string), typeof(WarningPanel));
        public string ObjectContent
        {
            get
            {
                return (string)GetValue(ObjectContentProperty);
            }
            set
            {
                SetValue(ObjectContentProperty, value);
            }
        }
        public static DependencyProperty TextColorProperty = DependencyProperty.Register(nameof(TextColor), typeof(SolidColorBrush), typeof(WarningPanel));
        public SolidColorBrush TextColor
        {
            get
            {
                return (SolidColorBrush)GetValue(TextColorProperty);
            }
            set
            {
                SetValue(TextColorProperty, value);
            }
        }
        public static DependencyProperty SvgColorProperty = DependencyProperty.Register(nameof(SvgColor), typeof(SolidColorBrush), typeof(WarningPanel));
        public SolidColorBrush SvgColor
        {
            get
            {
                return (SolidColorBrush)GetValue(SvgColorProperty);
            }
            set
            {
                SetValue(SvgColorProperty, value);
            }
        }
        public WarningPanel()
        {
            InitializeComponent();
        }
    }
}
