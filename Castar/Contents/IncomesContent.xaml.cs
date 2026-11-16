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
    private Guid? notificationTarget;
    private RangeDate Range => SelectorRangeDatesElement.RangeDate;

    public IncomesContent(CastarServerClient server, ClientSettings settings, HistoricalExchangeRates exchangeRates)
    {
        this.server = server;
        this.settings = settings;
        this.exchangeRates = exchangeRates;
        DataContext = context;
        InitializeComponent();
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
        context.Incomes = [];
        context.Status = "";
        if (listeningForChanges)
            await RefreshAsync();
    }

    private async void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClientSettings.DisplayCurrency) && listeningForChanges)
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
                (income.Currency, DateOnly.FromDateTime(income.AddedOn))), target, request.Token);
            if (request != activeRequest || refreshPending)
                return;

            var rows = incomes.Select(income => CreateRow(income, target, rates)).ToArray();
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

    private static IncomeDisplayRow CreateRow(Income income, string target,
        IReadOnlyDictionary<(string Currency, DateOnly Date), decimal> rates)
    {
        if (HistoricalExchangeRates.IsCurrencyCode(income.Currency))
        {
            if (target.Length == 0 || income.Currency == target)
                return new(income, MoneyAmountDisplay.Create(income.Sum, income.Currency), income.Sum, income.Currency);
            if (rates.TryGetValue((income.Currency!, DateOnly.FromDateTime(income.AddedOn)), out var rate))
            {
                var converted = HistoricalExchangeRates.ConvertAmount(income.Sum, rate);
                return new(income, MoneyAmountDisplay.Create(income.Sum, income.Currency, converted, target), converted, target);
            }
        }

        return new(income, MoneyAmountDisplay.Create(income.Sum, income.Currency), null, null);
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
            && previous.Amount == next.Amount
            && previous.AggregateAmount == next.AggregateAmount && previous.AggregateCurrency == next.AggregateCurrency;
    }

    private async void SelectorRangeDates_DateRangeChanged(object sender, EventArgs e)
    {
        if (listeningForChanges && e is RangeDate range)
            await RefreshAsync(range.StartDate, range.EndDate);
    }

    private async void IncomesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { IsSelected: true, DataContext: IncomeDisplayRow row })
            await EditIncomeAsync(row.Income.Clone<Income>());
    }

    private async void AddNewIncomeButton_Click(object sender, RoutedEventArgs e) =>
        await EditIncomeAsync(new Income { AddedOn = DateTime.Now });

    private async Task EditIncomeAsync(Income income)
    {
        try
        {
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
                await server.UpdateIncomeAsync(income);
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
        if (income.AddedOn == default)
            return new ValidatePopupDialog(false, "Укажите дату.");
        return new ValidatePopupDialog(true, null);
    }

    private async void DeleteIncomeButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)((MenuItem)sender).Parent;
        var rows = ((ListView)menu.PlacementTarget).SelectedItems.Cast<IncomeDisplayRow>().ToArray();
        if (rows.Length == 0)
            return;

        var description = string.Join("\n", rows.Select(row =>
            $"{row.Income.Source} — {HistoricalExchangeRates.Format(row.Income.Sum, row.Income.Currency)}"));
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
            foreach (var row in rows)
                await server.DeleteIncomeAsync(row.Income.Id);
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
    Income Income, MoneyAmountDisplay Amount, decimal? AggregateAmount, string? AggregateCurrency)
{
    internal static string FormatTotals(IEnumerable<IncomeDisplayRow> rows) => HistoricalExchangeRates.FormatTotals(rows
        .Where(row => row.AggregateAmount.HasValue).Select(row => (row.AggregateAmount!.Value, row.AggregateCurrency)));
}
