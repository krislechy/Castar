using Castar.Api;
using Castar.Currency;
using Castar.Domain.Models.SettingsModel;
using Castar.ServerConnection;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

public partial class ManualCurrencyPanel : UserControl
{
    private readonly HistoricalExchangeRates exchangeRates;
    private readonly CurrencyEditor editor;
    private CancellationTokenSource? loadingCatalog;

    internal ManualCurrencyPanel(HistoricalExchangeRates exchangeRates, ManualCurrencySettingsDto settings)
    {
        this.exchangeRates = exchangeRates;
        editor = new CurrencyEditor(settings);
        InitializeComponent();
        DataContext = editor;
    }

    internal ManualCurrencySettingsDto CreateResult() => new(editor.DefaultCurrency!, editor.EnabledCurrencies.ToArray());

    internal ValidatePopupDialog Validate()
    {
        if (editor.EnabledCurrencies.Count == 0)
            return new(false, "Выберите хотя бы одну валюту.");
        if (editor.DefaultCurrency is not { } currency || !editor.EnabledCurrencies.Contains(currency))
            return new(false, "Выберите валюту ручного добавления.");
        return new(true, null);
    }

    private async void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        if (loadingCatalog is not null)
            return;

        using var request = new CancellationTokenSource();
        loadingCatalog = request;
        editor.CatalogStatus = "Загрузка валют…";
        try
        {
            var catalog = await exchangeRates.GetCurrencyCatalogAsync(request.Token);
            if (loadingCatalog != request)
                return;

            foreach (var code in catalog)
                editor.AddCurrency(code);
            editor.CatalogStatus = catalog.Count == 0 ? "Каталог валют недоступен." : "";
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (loadingCatalog == request)
                editor.CatalogStatus = "Не удалось загрузить каталог валют.";
        }
        finally
        {
            if (loadingCatalog == request)
                loadingCatalog = null;
        }
    }

    private void Panel_Unloaded(object sender, RoutedEventArgs e) => CancelCatalog();

    internal void CancelCatalog()
    {
        var request = loadingCatalog;
        loadingCatalog = null;
        request?.Cancel();
    }

    private sealed class CurrencyEditor : BaseSettingsModel
    {
        private string? defaultCurrency;
        private string catalogStatus = "";

        internal CurrencyEditor(ManualCurrencySettingsDto settings)
        {
            foreach (var code in settings.Currencies)
                AddCurrency(code, included: true);
            defaultCurrency = settings.DefaultCurrency;
        }

        public ObservableCollection<CurrencyChoice> Currencies { get; } = [];
        public ObservableCollection<string> EnabledCurrencies { get; } = [];

        public string? DefaultCurrency
        {
            get => defaultCurrency;
            set => SetField(ref defaultCurrency, value);
        }

        public string CatalogStatus
        {
            get => catalogStatus;
            set => SetField(ref catalogStatus, value);
        }

        internal void AddCurrency(string code, bool included = false)
        {
            var index = 0;
            while (index < Currencies.Count && string.CompareOrdinal(Currencies[index].Code, code) < 0)
                index++;
            if (index < Currencies.Count && Currencies[index].Code == code)
                return;

            var choice = new CurrencyChoice(code, included, UpdateEnabledCurrencies);
            Currencies.Insert(index, choice);
            if (included)
                UpdateEnabledCurrencies(code, included);
        }

        private void UpdateEnabledCurrencies(string code, bool included)
        {
            if (!included)
            {
                if (DefaultCurrency == code)
                    DefaultCurrency = null;
                EnabledCurrencies.Remove(code);
                return;
            }

            var index = EnabledCurrencies.TakeWhile(existing => string.CompareOrdinal(existing, code) < 0).Count();
            EnabledCurrencies.Insert(index, code);
        }
    }

    private sealed class CurrencyChoice(string code, bool included, Action<string, bool> changed) : BaseSettingsModel
    {
        private bool isIncluded = included;
        public string Code { get; } = code;

        public bool Included
        {
            get => isIncluded;
            set
            {
                if (SetField(ref isIncluded, value))
                    changed(Code, value);
            }
        }
    }
}
