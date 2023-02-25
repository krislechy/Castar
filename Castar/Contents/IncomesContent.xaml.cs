using Castar.BL;
using Castar.Compontents;
using Castar.Domain.Models.DataBase;
using Castar.Extensions;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
using Newtonsoft.Json.Linq;
using SkiaSharp;
using LiveChartsCore.SkiaSharpView.Painting;
using Castar.Helpers;

namespace Castar.Contents
{
    /// <summary>
    /// Логика взаимодействия для IncomesContent.xaml
    /// </summary>
    public partial class IncomesContent : UserControl
    {
        private sealed class IncomesContext : INotifyPropertyChanged
        {
            private bool isLoading = false;
            public bool IsLoading
            {
                get => this.isLoading;
                set
                {
                    this.isLoading = value;
                    OnPropertyChanged(nameof(IsLoading));
                }
            }
            public decimal TotalSum { get => Incomes == null ? 0 : Incomes.Sum(x => x.Sum); }

            private ObservableCollection<Income> incomes;
            public ObservableCollection<Income> Incomes
            {
                get => incomes;
                set
                {
                    incomes = value;
                    OnPropertyChanged(nameof(Incomes));
                    OnPropertyChanged(nameof(TotalSum));
                }
            }
            //private ISeries[]? seriesIncomes;
            //public ISeries[]? SeriesIncomes
            //{
            //    get => seriesIncomes;
            //    set
            //    {
            //        seriesIncomes = value;
            //        OnPropertyChanged(nameof(SeriesIncomes));
            //    }
            //}
            //private ObservableCollection<Axis>? xAxis;
            //public ObservableCollection<Axis>? XAxis
            //{
            //    get => xAxis;
            //    set
            //    {
            //        xAxis = value;
            //        OnPropertyChanged(nameof(xAxis));
            //    }
            //}
            public event PropertyChangedEventHandler? PropertyChanged;
            public void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        private IncomesContext context
        {
            get
            {
                return ((IncomesContext)this.DataContext);
            }
        }
        private DateTime startDate { get => SelectorRangeDatesElement.RangeDate.StartDate; }
        private DateTime endDate { get => SelectorRangeDatesElement.RangeDate.EndDate; }
        private readonly IIncomesService incomesService;
        private readonly IUsersService usersService;
        public IncomesContent(IIncomesService incomesService, IUsersService usersService)
        {
            this.incomesService = incomesService;
            this.usersService = usersService;

            InitializeComponent();
            InitializeContext();
            UpdateIncomes(startDate, endDate);
            //InitializeChart();
        }
        private void InitializeContext()
        {
            var dc = new IncomesContext();
            this.DataContext = dc;
        }
        //private void InitializeChart()
        //{
        //    LiveCharts.Configure(config =>
        //    {
        //        config.HasMap<Income>((income, point) =>
        //        {
        //            point.PrimaryValue = (float)income.Sum;
        //            point.SecondaryValue = point.Context.Index;
        //        });
        //    });
        //}

        //private void SetSeries()
        //{
        //    List<LineSeries<Income>> lineSeries = new List<LineSeries<Income>>();
        //    var lineUsers = context
        //        .Incomes
        //        .GroupBy(x => x.User);
        //    var labels = new List<string>();
        //    foreach (var user in lineUsers)
        //    {
        //        var data = user
        //            .GroupBy(x => new DateTime(x.AddedOn.Year, x.AddedOn.Month, 1))
        //            .Select(x => new Income()
        //            {
        //                Sum = x.Sum(y => y.Sum),
        //                User = user.Key,
        //                AddedOn = x.Key,
        //            })
        //            .ToList();

        //        //add missed months
        //        foreach (var date in DateTimeHelper.EachMonth(SelectorRangeDatesElement.RangeDate.StartDate, SelectorRangeDatesElement.RangeDate.EndDate))
        //        {
        //            if (!data.Any(x => x.AddedOn == date))
        //                data.Add(new Income()
        //                {
        //                    Sum=0,
        //                    User=user.Key,
        //                    AddedOn=date,
        //                });
        //        }
        //        var orderedData = data.OrderBy(x => x.AddedOn);

        //        if (!labels.Any())
        //            foreach (var date in orderedData)
        //                labels.Add(date.AddedOn.ToString("MMMM yyyy"));

        //        lineSeries.Add(new LineSeries<Income>()
        //        {
        //            Values = orderedData,
        //            Name = user.Key.Name,
        //            DataLabelsPaint = new SolidColorPaint(SKColors.LightGreen),
        //            DataLabelsFormatter = (point) => $"{point.Model.Sum.ToCurrency()}",
        //            Stroke = new SolidColorPaint(new SKColor(user.Key.Color.R, user.Key.Color.G, user.Key.Color.B, user.Key.Color.A))
        //            {
        //                StrokeThickness = 3
        //            },
        //            Fill = new SolidColorPaint(new SKColor(user.Key.Color.R, user.Key.Color.G, user.Key.Color.B, 0x10)),
        //            GeometryFill = new SolidColorPaint(new SKColor(user.Key.Color.R, user.Key.Color.G, user.Key.Color.B, 0x90))
        //            {
        //                StrokeThickness = 5
        //            },
        //            GeometryStroke = new SolidColorPaint(new SKColor(user.Key.Color.R, user.Key.Color.G, user.Key.Color.B, 0x70))
        //            {
        //                StrokeThickness = 10
        //            },
        //            GeometrySize = 10,
        //        });
        //    }
        //    context.XAxis = new() { new() { Labels = labels, } };
        //    context.SeriesIncomes = lineSeries.ToArray();
        //}
        private void Incomes_Loaded(object sender, RoutedEventArgs e)
        {
            if (context.Incomes == null)
                UpdateIncomes(startDate, endDate);
        }
        private async void UpdateIncomes(DateTime startDate, DateTime endDate)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                context.IsLoading = true;
                var incomes = await incomesService.GetEntitiesWithAllIncludes(startDate, endDate);
                await Task.Delay(100);
                context.Incomes = incomes;
                //SetSeries();
                context.IsLoading = false;
            });
        }
        private async void IncomesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var obj = (ListViewItem)sender;
            if (obj.IsSelected)
            {
                var selected = (Income)obj.DataContext;
                var incomeInfo = new IncomePanel();
                incomeInfo.Users = await usersService.GetEntities();
                var updatedIncome = selected.Clone<Income>();
                incomeInfo.DataContext = updatedIncome;
                var result = await PopupDialog.Show(incomeInfo, PopupDialog.TypeDialogEnum.Confirm, (o) =>
                {
                    o.Background = (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
                    o.SuccessButton.SuccessConfirmButtonText = "Сохранить";
                    o.AllowMissClick = false;
                    o.Title = "Редактирование";
                    return o;
                }, () => Validation(updatedIncome));
                if (result != null && result.Value)
                {
                    if (updatedIncome.Source == null) updatedIncome.Source = "NONAME";
                    await incomesService.Update(updatedIncome);
                    var source = context.Incomes;
                    for (int i = 0; i < source.Count(); i++)
                    {
                        if (source[i].Id == updatedIncome.Id)
                        {
                            source[i] = updatedIncome;
                            break;
                        }
                    }

                    UpdateListViewContext(IncomesListView);
                }
            }
        }
        private void UpdateListViewContext(ListView obj)
        {
            var lcv = (ListCollectionView)obj.ItemsSource;
            lcv.Refresh();
            context.OnPropertyChanged(nameof(context.TotalSum));
        }
        private async void AddNewIncomeButton_Click(object sender, RoutedEventArgs e)
        {
            var newIncomePanel = new IncomePanel();
            var newIncome = new Income()
            {
                AddedOn = DateTime.Now
            };
            newIncomePanel.DataContext = newIncome;
            newIncomePanel.Users = await usersService.GetEntities();
            var result = await PopupDialog.Show(newIncomePanel, PopupDialog.TypeDialogEnum.Confirm, (o) =>
            {
                o.Background = (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
                o.SuccessButton.SuccessConfirmButtonText = "Добавить";
                o.AllowMissClick = false;
                o.Title = "Новый";
                return o;
            }, () => Validation(newIncome));
            if (result != null && result.Value)
            {
                if (newIncome.Source == null) newIncome.Source = "NONAME";
                var selectedUser = newIncome.User;
                var addedIncome = await incomesService.Add(newIncome, newIncome.User.TelegramId, newIncome.User);
                var source = context.Incomes;
                source.Add(addedIncome);

                UpdateListViewContext(IncomesListView);
            }
        }
        private ValidatePopupDialog Validation(Income newIncome)
        {
            StringBuilder sb = new StringBuilder();
            if (newIncome.User == null)
                sb.AppendLine("- Выберите пользователя");
            if (String.IsNullOrEmpty(newIncome.Source?.Trim()))
                sb.AppendLine("- Укажите источник дохода");
            if (newIncome.Sum == 0)
                sb.AppendLine("- Укажите сумму дохода");
            if (newIncome.AddedOn == default)
                sb.AppendLine("- Укажите дату дохода");
            if (sb.Length > 0)
                return new ValidatePopupDialog(false, sb.ToString().Trim());
            return new ValidatePopupDialog(true, null);
        }
        private void SelectorRangeDates_DateRangeChanged(object sender, EventArgs e)
        {
            var args = (RangeDate)e;
            Trace.WriteLine($"Income: {args.StartDate} -  {args.EndDate}");
            UpdateIncomes(args.StartDate, args.EndDate);
        }

        private async void DeleteIncomeButton_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = (MenuItem)sender;
            var contextMenu = (ContextMenu)menuItem.Parent;
            var listView = (ListView)contextMenu.PlacementTarget;
            var selectedItems = listView.SelectedItems.Cast<Income>();
            if (selectedItems != null && selectedItems.Count()>0)
            {
                var str = String.Join(";\n", selectedItems.Select(x=>$"{x.Source} - {x.Sum.ToCurrency()}"));
                var result = await PopupDialog.Show($"Вы уверены, что хотите удалить начисление:\n{str} ?",
                    PopupDialog.TypeDialogEnum.Confirm, (o) =>
                    {
                        o.SuccessButton.SuccessConfirmButtonText = "Удалить";
                        o.SuccessButton.SuccessConfirmButtonForeground = Brushes.OrangeRed;
                        o.AllowMissClick = true;
                        return o;
                    });
                if (result != null && result.Value)
                {
                    foreach (var item in selectedItems.ToList())
                    {
                        var resultDeleted = await incomesService.Delete(item.Id);
                        if (resultDeleted)
                        {
                            context.Incomes.Remove(item);
                        }
                        else
                        {
                            await PopupDialog.Show($"Не удалось удалить начисление: {item.Source} - {item.Sum.ToCurrency()}", PopupDialog.TypeDialogEnum.Default);
                        }
                    }
                    UpdateListViewContext(listView);
                }
            }
        }
    }
}
