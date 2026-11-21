using Castar.Api;
using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Castar.Compontents;

public partial class PurchaseEditor : UserControl
{
    private readonly PurchaseDto original;

    internal PurchaseEditor(PurchaseDto purchase, IReadOnlyList<string>? allowedCurrencies = null,
        IReadOnlyList<User>? owners = null, bool manualEntry = false, IReadOnlyList<BudgetCategoryDto>? categories = null)
    {
        original = purchase;
        InitializeComponent();
        PurchaseName.Text = purchase.Name;
        RetailPlace.Text = purchase.RetailPlace ?? "";
        RetailPlaceAddress.Text = purchase.RetailPlaceAddress ?? "";
        OperationDate.SelectedDate = purchase.AddedOn?.Date;
        OperationTime.Text = CastarDatePicker.FormatTime(purchase.AddedOn);
        PurchaseCategory.Load(categories, purchase.CategoryId);
        if (owners is not null)
        {
            OwnerColumn.Width = new GridLength(184);
            PurchaseOwner.ApplyListStyle((Style)FindResource("PurchaseFormOwnerList"));
            PurchaseOwner.Users = owners.OrderBy(user => user.Name).ToArray();
            PurchaseOwner.SelectedUser = owners.FirstOrDefault(user => user.Id == purchase.UserId)
                ?? (owners.Count == 1 ? owners[0] : null);
            PurchaseOwner.Visibility = Visibility.Visible;
        }
        if (allowedCurrencies is not null)
        {
            var options = allowedCurrencies.Append(purchase.Currency)
                .Distinct(StringComparer.Ordinal)
                .Select(code => new CurrencyOption(code, code ?? "Не указана"))
                .ToArray();
            PurchaseCurrency.ItemsSource = options;
            PurchaseCurrency.SelectedItem = options.First(option => option.Code == purchase.Currency);
            CurrencyField.Visibility = Visibility.Visible;
            if (categories is not null)
            {
                CurrencyField.Margin = new Thickness(0, 0, 4, 0);
                PurchaseCategory.Margin = new Thickness(4, 0, 0, 0);
            }
        }
        if (manualEntry)
        {
            PurchaseOwner.IsTabStop = false;
            KeyboardNavigation.SetTabNavigation(PurchaseOwner, KeyboardNavigationMode.None);
            OperationDate.IsTabStop = false;
        }
    }

    internal PurchaseWriteDto? Result { get; private set; }

    internal ValidatePopupDialog Validate()
    {
        var name = PurchaseName.Text == original.Name ? original.Name : PurchaseName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 400)
            return new(false, "Укажите название покупки длиной до 400 символов.");
        var owner = PurchaseOwner.SelectedUser;
        if (PurchaseOwner.Visibility == Visibility.Visible && owner is null)
            return new(false, "Выберите пользователя.");

        var place = OptionalText(RetailPlace.Text, original.RetailPlace);
        var address = OptionalText(RetailPlaceAddress.Text, original.RetailPlaceAddress);
        if (place is { Length: > 400 } || address is { Length: > 400 })
            return new(false, "Место покупки и адрес могут содержать до 400 символов каждое.");

        if (!OperationDate.TryGetDateTime(OperationTime.Text, original.AddedOn, out var date))
            return new(false, "Введите дату и время в формате ДД.ММ.ГГГГ ЧЧ:ММ.");
        var currency = (PurchaseCurrency.SelectedItem as CurrencyOption)?.Code;
        Result = new(name, place, address, DateTime.SpecifyKind(date, DateTimeKind.Unspecified))
        {
            Currency = currency == original.Currency ? null : currency,
            UserId = owner is not null && owner.Id != original.UserId ? owner.Id : null,
            AddedOnUtc = date == original.AddedOn ? original.AddedOnUtc : null,
            Category = PurchaseCategory.GetChange(original.CategoryId)
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
