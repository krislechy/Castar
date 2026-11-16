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
    /// Логика взаимодействия для IslandPanel.xaml
    /// </summary>
    [ContentProperty("ObjectContent")]
    public partial class IslandPanel : UserControl
    {
        private static DependencyProperty ObjectContentProperty = DependencyProperty.Register(nameof(ObjectContent), typeof(object), typeof(IslandPanel));
        public object ObjectContent
        {
            get
            {
                return (object)GetValue(ObjectContentProperty);
            }
            set
            {
                SetValue(ObjectContentProperty, value);
            }
        }
        private static DependencyProperty ImageSvgProperty = DependencyProperty.Register(nameof(ImageSvg), typeof(Viewbox), typeof(IslandPanel));
        public Viewbox? ImageSvg
        {
            get
            {
                return (Viewbox?)GetValue(ImageSvgProperty);
            }
            set
            {
                SetValue(ImageSvgProperty, value);
            }
        }
        private static DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(IslandPanel));
        public string Title
        {
            get
            {
                return (string)GetValue(TitleProperty);
            }
            set
            {
                SetValue(TitleProperty, value);
            }
        }
        public static readonly DependencyProperty ShowTitleProperty =
            DependencyProperty.Register(nameof(ShowTitle), typeof(bool), typeof(IslandPanel), new PropertyMetadata(true));

        public bool ShowTitle
        {
            get => (bool)GetValue(ShowTitleProperty);
            set => SetValue(ShowTitleProperty, value);
        }

        public static readonly DependencyProperty HeaderMarginProperty = DependencyProperty.Register(
            nameof(HeaderMargin), typeof(Thickness), typeof(IslandPanel), new PropertyMetadata(new Thickness(0, 0, 0, 10)));

        public Thickness HeaderMargin
        {
            get => (Thickness)GetValue(HeaderMarginProperty);
            set => SetValue(HeaderMarginProperty, value);
        }

        private static DependencyProperty IsMainTitleProperty = DependencyProperty.Register(nameof(IsMainTitle), typeof(bool), typeof(IslandPanel));
        public bool IsMainTitle
        {
            get
            {
                return (bool)GetValue(IsMainTitleProperty);
            }
            set
            {
                SetValue(IsMainTitleProperty, value);
            }
        }
        public IslandPanel()
        {
            SetCurrentValue(PaddingProperty, new Thickness(10));
            InitializeComponent();
        }
    }
}
