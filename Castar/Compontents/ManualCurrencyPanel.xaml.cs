using Castar.Api;
using Castar.Currency;
using Castar.Domain.Models.SettingsModel;
using Castar.ServerConnection;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

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

            foreach (var currency in catalog)
                editor.AddCurrency(currency.Code, name: currency.Name);
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
        private string searchText = "";
        private static readonly IReadOnlyDictionary<string, string> CurrencyNames = CreateCurrencyNames();

        internal CurrencyEditor(ManualCurrencySettingsDto settings)
        {
            FilteredCurrencies = CollectionViewSource.GetDefaultView(Currencies);
            FilteredCurrencies.Filter = item => item is CurrencyChoice currency &&
                (currency.Label.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
                 currency.CatalogName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
            foreach (var code in settings.Currencies)
                AddCurrency(code, included: true);
            defaultCurrency = settings.DefaultCurrency;
        }

        public ObservableCollection<CurrencyChoice> Currencies { get; } = [];
        public ICollectionView FilteredCurrencies { get; }
        public ObservableCollection<string> EnabledCurrencies { get; } = [];

        public string SearchText
        {
            get => searchText;
            set
            {
                if (SetField(ref searchText, value))
                    FilteredCurrencies.Refresh();
            }
        }

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

        internal void AddCurrency(string code, bool included = false, string? name = null)
        {
            var index = 0;
            while (index < Currencies.Count && string.CompareOrdinal(Currencies[index].Code, code) < 0)
                index++;
            if (index < Currencies.Count && Currencies[index].Code == code)
            {
                if (name is not null)
                {
                    Currencies[index].CatalogName = name;
                    FilteredCurrencies.Refresh();
                }
                return;
            }

            var choice = new CurrencyChoice(code, CurrencyNames.GetValueOrDefault(code),
                name ?? code, included, UpdateEnabledCurrencies);
            Currencies.Insert(index, choice);
            if (included)
                UpdateEnabledCurrencies(code, included);
        }

        private static IReadOnlyDictionary<string, string> CreateCurrencyNames()
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
            {
                try
                {
                    var region = new RegionInfo(culture.Name);
                    var localized = new RegionInfo($"ru-{region.TwoLetterISORegionName}");
                    names.TryAdd(localized.ISOCurrencySymbol, localized.CurrencyNativeName);
                }
                catch (ArgumentException) { }
            }
            return names;
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

    private sealed class CurrencyChoice(string code, string? name, string catalogName, bool included, Action<string, bool> changed) : BaseSettingsModel
    {
        private bool isIncluded = included;
        private string catalogNameValue = catalogName;
        public string Code { get; } = code;
        public string Label => (name ?? CatalogName) == Code ? Code : $"{Code} — {name ?? CatalogName}";
        public string CatalogName
        {
            get => catalogNameValue;
            set
            {
                if (SetField(ref catalogNameValue, value))
                    OnPropertyChanged(nameof(Label));
            }
        }

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
