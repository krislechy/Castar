using Castar.Api;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

public partial class PurchaseEditor : UserControl
{
    private const string DisplayDateFormat = "dd.MM.yyyy HH:mm:ss";
    private static readonly string[] EditableDateFormats = [DisplayDateFormat, "dd.MM.yyyy HH:mm"];
    private readonly PurchaseDto original;
    private readonly string originalDateText;

    internal PurchaseEditor(PurchaseDto purchase, IReadOnlyList<string>? allowedCurrencies = null)
    {
        original = purchase;
        InitializeComponent();
        PurchaseName.Text = purchase.Name;
        RetailPlace.Text = purchase.RetailPlace ?? "";
        RetailPlaceAddress.Text = purchase.RetailPlaceAddress ?? "";
        originalDateText = purchase.AddedOn?.ToString(DisplayDateFormat, CultureInfo.InvariantCulture) ?? "";
        OperationDate.Text = originalDateText;
        if (allowedCurrencies is not null)
        {
            var options = allowedCurrencies.Append(purchase.Currency)
                .Distinct(StringComparer.Ordinal)
                .Select(code => new CurrencyOption(code, code ?? "Не указана"))
                .ToArray();
            PurchaseCurrency.ItemsSource = options;
            PurchaseCurrency.SelectedItem = options.First(option => option.Code == purchase.Currency);
            CurrencyField.Visibility = Visibility.Visible;
        }
    }

    internal PurchaseWriteDto? Result { get; private set; }

    internal ValidatePopupDialog Validate()
    {
        var name = PurchaseName.Text == original.Name ? original.Name : PurchaseName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 400)
            return new(false, "Укажите название покупки длиной до 400 символов.");

        var place = OptionalText(RetailPlace.Text, original.RetailPlace);
        var address = OptionalText(RetailPlaceAddress.Text, original.RetailPlaceAddress);
        if (place is { Length: > 400 } || address is { Length: > 400 })
            return new(false, "Место покупки и адрес могут содержать до 400 символов каждое.");

        var dateText = OperationDate.Text.Trim();
        if (!DateTime.TryParseExact(dateText, EditableDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || date == default)
            return new(false, "Введите дату и время в формате ДД.ММ.ГГГГ ЧЧ:ММ.");

        if (original.AddedOn is { } originalDate && dateText == originalDateText)
            date = originalDate;
        var currency = (PurchaseCurrency.SelectedItem as CurrencyOption)?.Code;
        Result = new(name, place, address, DateTime.SpecifyKind(date, DateTimeKind.Unspecified))
        {
            Currency = currency == original.Currency ? null : currency
        };
        return new(true, null);
    }

    private static string? OptionalText(string text, string? original)
    {
        if (text == (original ?? ""))
            return original;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private sealed record CurrencyOption(string? Code, string Label);
}
