using Castar.Compontents;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Castar.Extensions;
using Castar.ServerConnection;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Castar.Contents;

public partial class IncomesContent : UserControl
{
    private readonly CastarServerClient server;
    private readonly IncomesContext context = new();
    private readonly DispatcherTimer polling;
    private CancellationTokenSource? activeRequest;
    private bool refreshPending;
    private RangeDate Range => SelectorRangeDatesElement.RangeDate;

    public IncomesContent(CastarServerClient server)
    {
        this.server = server;
        polling = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        polling.Tick += async (_, _) =>
        {
            if (activeRequest is null)
                await RefreshAsync();
        };
        DataContext = context;
        InitializeComponent();
        server.ConnectionChanged += Server_ConnectionChanged;
        IsVisibleChanged += Incomes_IsVisibleChanged;
    }

    private async void Incomes_Loaded(object sender, RoutedEventArgs e) => await StartPollingAsync();

    private void Incomes_Unloaded(object sender, RoutedEventArgs e) => StopPolling();

    private async void Incomes_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded && IsVisible)
            await StartPollingAsync();
        else
            StopPolling();
    }

    private async Task StartPollingAsync()
    {
        if (!IsVisible || polling.IsEnabled)
            return;
        polling.Start();
        await RefreshAsync();
    }

    private void StopPolling()
    {
        polling.Stop();
        CancelRefresh();
        context.IsLoading = false;
    }

    private async void Server_ConnectionChanged(object? sender, EventArgs e)
    {
        CancelRefresh();
        context.Incomes = [];
        context.Status = "";
        if (polling.IsEnabled)
            await RefreshAsync();
    }

    private void CancelRefresh()
    {
        refreshPending = false;
        var previous = activeRequest;
        activeRequest = null;
        previous?.Cancel();
    }

    internal Task RefreshAfterServerChangeAsync()
    {
        if (!polling.IsEnabled)
            return Task.CompletedTask;
        if (activeRequest is null)
            return RefreshAsync();

        refreshPending = true;
        return Task.CompletedTask;
    }

    private Task RefreshAsync() => RefreshAsync(Range.StartDate, Range.EndDate);

    private async Task RefreshAsync(DateTime start, DateTime finish)
    {
        CancelRefresh();
        using var request = new CancellationTokenSource();
        activeRequest = request;
        context.IsLoading = true;
        try
        {
            var rows = await server.GetIncomesAsync(start, finish, request.Token);
            if (request != activeRequest || refreshPending)
                return;

            if (context.Incomes.Count != rows.Count || !context.Incomes.Zip(rows).All(pair =>
                pair.First.Id == pair.Second.Id && pair.First.Sum == pair.Second.Sum
                && pair.First.AddedOn == pair.Second.AddedOn && pair.First.Source == pair.Second.Source
                && pair.First.UserId == pair.Second.UserId && pair.First.User.Name == pair.Second.User.Name
                && pair.First.User.Sex == pair.Second.User.Sex
                && pair.First.User.ColorRaw.AsSpan().SequenceEqual(pair.Second.User.ColorRaw)
                && pair.First.User.ImageRaw.AsSpan().SequenceEqual(pair.Second.User.ImageRaw)))
            {
                context.Incomes = rows;
            }
            context.Status = "";
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (request == activeRequest && !refreshPending)
                context.Status = CastarServerClient.DescribeError(error);
        }
        finally
        {
            if (request == activeRequest)
            {
                activeRequest = null;
                if (refreshPending)
                    _ = RefreshAsync();
                else
                    context.IsLoading = false;
            }
        }
    }

    private async void SelectorRangeDates_DateRangeChanged(object sender, EventArgs e)
    {
        if (polling.IsEnabled && e is RangeDate range)
            await RefreshAsync(range.StartDate, range.EndDate);
    }

    private async void IncomesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { IsSelected: true, DataContext: Income income })
            await EditIncomeAsync(income.Clone<Income>());
    }

    private async void AddNewIncomeButton_Click(object sender, RoutedEventArgs e) =>
        await EditIncomeAsync(new Income { AddedOn = DateTime.Now });

    private async Task EditIncomeAsync(Income income)
    {
        try
        {
            var isNew = income.Id == Guid.Empty;
            var editor = new IncomePanel { DataContext = income, Users = await server.GetUsersAsync() };
            var accepted = await PopupDialog.Show(editor, PopupDialog.TypeDialogEnum.Confirm, options =>
            {
                options.Background = (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
                options.SuccessButton.SuccessConfirmButtonText = isNew ? "Добавить" : "Сохранить";
                options.AllowMissClick = false;
                options.Title = isNew ? "Новый доход" : "Изменить доход";
                return options;
            }, () => ValidateIncome(income));
            if (accepted != true)
                return;

            if (isNew)
                await server.AddIncomeAsync(income);
            else
                await server.UpdateIncomeAsync(income);
            await RefreshAsync();
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            await PopupDialog.Show(CastarServerClient.DescribeError(error), PopupDialog.TypeDialogEnum.Default);
        }
    }

    private static ValidatePopupDialog ValidateIncome(Income income)
    {
        if (income.User is null)
            return new ValidatePopupDialog(false, "Выберите пользователя.");
        if (string.IsNullOrWhiteSpace(income.Source))
            return new ValidatePopupDialog(false, "Укажите источник.");
        if (income.Sum == 0)
            return new ValidatePopupDialog(false, "Укажите ненулевую сумму.");
        if (income.AddedOn == default)
            return new ValidatePopupDialog(false, "Укажите дату.");
        return new ValidatePopupDialog(true, null);
    }

    private async void DeleteIncomeButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)((MenuItem)sender).Parent;
        var rows = ((ListView)menu.PlacementTarget).SelectedItems.Cast<Income>().ToArray();
        if (rows.Length == 0)
            return;

        var description = string.Join("\n", rows.Select(income => $"{income.Source} — {income.Sum.ToCurrency()}"));
        var question = rows.Length == 1 ? "Удалить доход?" : "Удалить выбранные доходы?";
        var accepted = await PopupDialog.Show($"{question}\n{description}", PopupDialog.TypeDialogEnum.Confirm,
            options =>
            {
                options.SuccessButton.SuccessConfirmButtonText = "Удалить";
                options.SuccessButton.SuccessConfirmButtonForeground = Brushes.OrangeRed;
                return options;
            });
        if (accepted != true)
            return;

        try
        {
            foreach (var income in rows)
                await server.DeleteIncomeAsync(income.Id);
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            await PopupDialog.Show(CastarServerClient.DescribeError(error), PopupDialog.TypeDialogEnum.Default);
        }
        await RefreshAsync();
    }

    private sealed class IncomesContext : BaseSettingsModel
    {
        private bool isLoading;
        private string status = "";
        private ObservableCollection<Income> incomes = [];

        public bool IsLoading { get => isLoading; set => SetField(ref isLoading, value); }
        public string Status { get => status; set => SetField(ref status, value); }
        public decimal TotalSum => Incomes.Sum(item => item.Sum);
        public ObservableCollection<Income> Incomes
        {
            get => incomes;
            set
            {
                if (SetField(ref incomes, value))
                    OnPropertyChanged(nameof(TotalSum));
            }
        }
    }
}
