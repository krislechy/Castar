using Castar.Api;
using Castar.Compontents;
using Castar.Currency;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Castar.Extensions;
using Castar.ServerConnection;
using Castar.Settings;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Castar.Contents;

public partial class IncomesContent : UserControl
{
    private readonly CastarServerClient server;
    private readonly ClientSettings settings;
    private readonly HistoricalExchangeRates exchangeRates;
    private readonly IncomesContext context = new();
    private bool listeningForChanges;
    private CancellationTokenSource? activeRequest;
    private bool refreshPending;
    private bool dataStale = true;
    private Guid? notificationTarget;
    private RangeDate Range => SelectorRangeDatesElement.RangeDate;

    public IncomesContent(CastarServerClient server, ClientSettings settings, HistoricalExchangeRates exchangeRates)
    {
        this.server = server;
        this.settings = settings;
        this.exchangeRates = exchangeRates;
        DataContext = context;
        InitializeComponent();
        SelectorRangeDatesElement.DisplayTimeZone = () => settings.DisplayTimeZone;
        SelectorRangeDatesElement.SetCurrentMonth();
        server.ConnectionChanged += Server_ConnectionChanged;
        settings.PropertyChanged += Settings_PropertyChanged;
        IsVisibleChanged += Incomes_IsVisibleChanged;
    }

    private async void Incomes_Loaded(object sender, RoutedEventArgs e) => await StartListeningAsync();

    private void Incomes_Unloaded(object sender, RoutedEventArgs e) => StopListening();

    private async void Incomes_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded && IsVisible)
            await StartListeningAsync();
        else
            StopListening();
    }

    private async Task StartListeningAsync()
    {
        if (!IsVisible || listeningForChanges)
            return;
        listeningForChanges = true;
        if (dataStale)
            await RefreshAsync();
    }

    private void StopListening()
    {
        listeningForChanges = false;
        CancelRefresh();
        context.IsLoading = false;
    }

    private async void Server_ConnectionChanged(object? sender, EventArgs e)
    {
        notificationTarget = null;
        CancelRefresh();
        dataStale = true;
        context.Incomes = [];
        context.Status = "";
        if (listeningForChanges)
            await RefreshAsync();
    }

    private async void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ClientSettings.DisplayCurrency) or nameof(ClientSettings.DisplayTimeZoneId)))
            return;

        dataStale = true;
        if (listeningForChanges)
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
        dataStale = true;
        if (!listeningForChanges)
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
        dataStale = true;
        if (start > finish)
        {
            context.Status = "Дата начала позже даты окончания.";
            context.IsLoading = false;
            return;
        }
        using var request = new CancellationTokenSource();
        activeRequest = request;
        context.IsLoading = true;
        try
        {
            var incomes = await server.GetIncomesAsync(start, finish, request.Token);
            if (request != activeRequest || refreshPending)
                return;

            var target = settings.DisplayCurrency;
            var rates = await exchangeRates.GetAsync(incomes.Select(income =>
                (income.Currency, income.RateDate ?? BudgetTime.BelgradeDate(income.AddedOn))), target, request.Token);
            if (request != activeRequest || refreshPending)
                return;

            var rows = incomes.Select(income => CreateRow(income, target, rates)).ToArray();
            dataStale = false;
            UpdateRows(rows);
            SelectNotificationTarget();
            notificationTarget = null;
            var currencyStatus = rows.Any(row => !HistoricalExchangeRates.IsCurrencyCode(row.Income.Currency))
                ? "У части доходов не указана валюта." : "";
            var rateStatus = rows.Any(row => row.AggregateAmount is null && HistoricalExchangeRates.IsCurrencyCode(row.Income.Currency))
                ? "Для части доходов нет курса." : "";
            context.Status = string.Join(' ', new[] { currencyStatus, rateStatus }.Where(status => status.Length > 0));
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

    private IncomeDisplayRow CreateRow(Income income, string target,
        IReadOnlyDictionary<(string Currency, DateOnly Date), decimal> rates)
    {
        var displayDate = BudgetTime.InZone(income.AddedOn, settings.DisplayTimeZone);
        if (HistoricalExchangeRates.IsCurrencyCode(income.Currency))
        {
            if (target.Length == 0 || income.Currency == target)
                return new(income, MoneyAmountDisplay.Create(income.Sum, income.Currency), income.Sum, income.Currency, displayDate);
            if (rates.TryGetValue((income.Currency!, income.RateDate ?? BudgetTime.BelgradeDate(income.AddedOn)), out var rate))
            {
                var converted = HistoricalExchangeRates.ConvertAmount(income.Sum, rate);
                return new(income, MoneyAmountDisplay.Create(income.Sum, income.Currency, converted, target), converted, target, displayDate);
            }
        }

        return new(income, MoneyAmountDisplay.Create(income.Sum, income.Currency), null, null, displayDate);
    }

    private void UpdateRows(IncomeDisplayRow[] rows)
    {
        if (context.Incomes.Count == rows.Length && context.Incomes.Zip(rows).All(pair =>
            SameRow(pair.First, pair.Second)))
            return;

        var selectedIds = IncomesListView.SelectedItems.Cast<IncomeDisplayRow>().Select(row => row.Income.Id).ToHashSet();
        context.Incomes = rows;
        foreach (var row in rows.Where(row => selectedIds.Contains(row.Income.Id)))
        {
            if (IncomesListView.SelectionMode == SelectionMode.Single)
            {
                IncomesListView.SelectedItem = row;
                break;
            }
            IncomesListView.SelectedItems.Add(row);
        }
    }

    internal void RevealNotification(Guid id, DateTime? addedOn)
    {
        notificationTarget = id;
        if (!SelectorRangeDatesElement.IncludeOperationDate(addedOn) && !SelectNotificationTarget())
            _ = RefreshAfterServerChangeAsync();
    }

    private bool SelectNotificationTarget()
    {
        if (notificationTarget is not { } id || context.Incomes.FirstOrDefault(row => row.Income.Id == id) is not { } selected)
            return false;
        notificationTarget = null;
        IncomesListView.SelectedItems.Clear();
        IncomesListView.SelectedItem = selected;
        IncomesListView.ScrollIntoView(selected);
        return true;
    }

    private static bool SameRow(IncomeDisplayRow previous, IncomeDisplayRow next)
    {
        var first = previous.Income;
        var second = next.Income;
        return first.Id == second.Id && first.Sum == second.Sum && first.Currency == second.Currency
            && first.AddedOn == second.AddedOn && first.Source == second.Source && first.UserId == second.UserId
            && first.User.Name == second.User.Name && first.User.Sex == second.User.Sex
            && first.User.ColorRaw.AsSpan().SequenceEqual(second.User.ColorRaw)
            && first.User.ImageRaw.AsSpan().SequenceEqual(second.User.ImageRaw)
            && previous.Amount == next.Amount && previous.DisplayDate == next.DisplayDate
            && previous.AggregateAmount == next.AggregateAmount && previous.AggregateCurrency == next.AggregateCurrency;
    }

    private async void SelectorRangeDates_DateRangeChanged(object sender, EventArgs e)
    {
        dataStale = true;
        if (listeningForChanges && e is RangeDate range)
            await RefreshAsync(range.StartDate, range.EndDate);
    }

    private async void IncomesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { IsSelected: true, DataContext: IncomeDisplayRow row })
        {
            var draft = row.Income.Clone<Income>();
            draft.AddedOn = row.DisplayDate;
            await EditIncomeAsync(draft, row.Income.AddedOn);
        }
    }

    private async void AddNewIncomeButton_Click(object sender, RoutedEventArgs e) =>
        await EditIncomeAsync(new Income { AddedOn = BudgetTime.InZone(DateTime.UtcNow, settings.DisplayTimeZone) });

    private async Task EditIncomeAsync(Income income, DateTime? instant = null)
    {
        try
        {
            var initialDate = income.AddedOn;
            var isNew = income.Id == Guid.Empty;
            var currencyTask = LoadManualCurrencyAsync();
            var usersTask = server.GetUsersAsync();
            await Task.WhenAll(currencyTask, usersTask);
            var currency = await currencyTask;
            if (isNew && currency is not null)
                income.Currency = currency.DefaultCurrency;

            var editor = new IncomePanel(income, currency?.Currencies) { Users = await usersTask };
            var accepted = await PopupDialog.Show(editor, PopupDialog.TypeDialogEnum.Confirm, options =>
            {
                options.Background = (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
                options.SuccessButton.SuccessConfirmButtonText = isNew ? "Добавить" : "Сохранить";
                options.AllowMissClick = false;
                options.Title = isNew ? "Новый доход" : "Изменить доход";
                return options;
            }, () => ValidateIncome(income, editor));
            if (accepted != true)
                return;

            if (isNew)
                await server.AddIncomeAsync(income);
            else
                await server.UpdateIncomeAsync(income, income.AddedOn == initialDate ? instant : null);
            await RefreshAsync();
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            await PopupDialog.Show(CastarServerClient.DescribeError(error), PopupDialog.TypeDialogEnum.Default);
        }
    }

    private async Task<ManualCurrencySettingsDto?> LoadManualCurrencyAsync()
    {
        try
        {
            return (await server.GetServerSettingsAsync()).ManualCurrency;
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static ValidatePopupDialog ValidateIncome(Income income, IncomePanel editor)
    {
        if (income.User is null)
            return new ValidatePopupDialog(false, "Выберите пользователя.");
        if (string.IsNullOrWhiteSpace(income.Source))
            return new ValidatePopupDialog(false, "Укажите источник.");
        if (!editor.ValidateAmount() || income.Sum == 0)
            return new ValidatePopupDialog(false, "Укажите ненулевую сумму.");
        if (income.Id == Guid.Empty && !HistoricalExchangeRates.IsCurrencyCode(income.Currency))
            return new ValidatePopupDialog(false, "Выберите валюту.");
        if (!editor.ValidateDate() || income.AddedOn == default)
            return new ValidatePopupDialog(false, "Введите дату и время в формате ДД.ММ.ГГГГ ЧЧ:ММ.");
        return new ValidatePopupDialog(true, null);
    }

    private async void DeleteIncomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem
            { Parent: ContextMenu { PlacementTarget: ListViewItem { DataContext: IncomeDisplayRow row } } })
            return;

        var income = row.Income;
        var connection = (settings.ServerUrl, settings.ProtectedApiKey);
        var description = $"{income.Source} — {HistoricalExchangeRates.Format(income.Sum, income.Currency)}";
        var accepted = await PopupDialog.Show($"Удалить доход?\n{description}", PopupDialog.TypeDialogEnum.Confirm,
            options =>
            {
                options.SuccessButton.SuccessConfirmButtonText = "Удалить";
                options.SuccessButton.SuccessConfirmButtonForeground = Brushes.OrangeRed;
                return options;
            });
        if (accepted != true || connection != (settings.ServerUrl, settings.ProtectedApiKey))
            return;

        try
        {
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
        private IReadOnlyList<IncomeDisplayRow> incomes = [];

        public bool IsLoading { get => isLoading; set => SetField(ref isLoading, value); }
        public string Status { get => status; set => SetField(ref status, value); }
        public string TotalSum => IncomeDisplayRow.FormatTotals(Incomes);
        public IReadOnlyList<IncomeDisplayRow> Incomes
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

internal sealed record IncomeDisplayRow(
    Income Income, MoneyAmountDisplay Amount, decimal? AggregateAmount, string? AggregateCurrency, DateTime DisplayDate = default)
{
    internal static string FormatTotals(IEnumerable<IncomeDisplayRow> rows) => HistoricalExchangeRates.FormatTotals(rows
        .Where(row => row.AggregateAmount.HasValue).Select(row => (row.AggregateAmount!.Value, row.AggregateCurrency)));
}
