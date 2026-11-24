using Castar.Api;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Castar.Compontents;

public sealed class BudgetCategoryIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        BudgetCategoryIcons.ForKey(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

internal static class BudgetCategoryIcons
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["briefcase-business"] = "Работа", ["laptop"] = "Фриланс", ["building-2"] = "Компания",
        ["chart-no-axes-combined"] = "Инвестиции", ["store"] = "Бизнес", ["rotate-ccw"] = "Возврат",
        ["gift"] = "Подарок", ["landmark"] = "Банк", ["ellipsis"] = "Другое", ["star"] = "Премия",
        ["house"] = "Дом и аренда", ["percent"] = "Проценты", ["banknote"] = "Наличные", ["coins"] = "Монеты",
        ["hand-coins"] = "Вознаграждение", ["badge-dollar-sign"] = "Выплата", ["credit-card"] = "Карта",
        ["piggy-bank"] = "Накопления", ["receipt-text"] = "Чек и налоги", ["users"] = "Совместный доход",
        ["shopping-cart"] = "Продукты", ["utensils"] = "Ресторан", ["car-front"] = "Автомобиль",
        ["heart-pulse"] = "Здоровье", ["shirt"] = "Одежда", ["smartphone"] = "Связь", ["monitor"] = "Электроника",
        ["repeat"] = "Подписки", ["gamepad-2"] = "Игры", ["plane"] = "Путешествия", ["dumbbell"] = "Спорт",
        ["graduation-cap"] = "Образование", ["shopping-bag"] = "Покупки", ["scissors"] = "Красота",
        ["wallet"] = "Кошелёк", ["paw-print"] = "Питомцы", ["coffee"] = "Кофе", ["bus"] = "Транспорт",
        ["fuel"] = "Топливо", ["zap"] = "Коммунальные услуги", ["pill"] = "Аптека", ["cross"] = "Медицинский крест",
        ["baby"] = "Дети",
        ["shield-check"] = "Страхование", ["package"] = "Доставка", ["wine"] = "Алкоголь",
        ["archive"] = "Архив", ["arrow-left-right"] = "Перевод", ["banknote-arrow-up"] = "Поступление наличных",
        ["badge-check"] = "Подтверждённая выплата", ["chart-candlestick"] = "Торговля активами", ["circle-percent"] = "Кешбэк",
        ["file-check"] = "Выплата по договору", ["handshake"] = "Партнёрство", ["key-round"] = "Недвижимость",
        ["medal"] = "Награда", ["package-check"] = "Доход от продаж", ["rocket"] = "Свой проект",
        ["trending-up"] = "Рост капитала", ["vault"] = "Вклад", ["wallet-cards"] = "Личные поступления",
        ["calendar-check"] = "Регулярное поступление", ["ice-cream-cone"] = "Мороженое", ["candy"] = "Сладости и жвачка",
        ["cookie"] = "Печенье и снеки", ["can"] = "Напитки", ["bike"] = "Велосипед",
        ["book-open"] = "Книги", ["car-taxi-front"] = "Такси", ["cat"] = "Кошки",
        ["clapperboard"] = "Кино", ["flower-2"] = "Цветы", ["guitar"] = "Музыка",
        ["hammer"] = "Ремонт", ["hand-heart"] = "Благотворительность", ["plug"] = "Электричество",
        ["ticket"] = "Билеты", ["washing-machine"] = "Бытовая техника", ["wrench"] = "Обслуживание"
    };

    internal static IReadOnlyList<Choice> ForKind(string kind) => BudgetCategoryIconKeys.ForKind(kind)
        .Select(key => new Choice(key, Labels[key], ReadGeometry(key))).ToArray();

    internal static Geometry ForKey(string? key) =>
        ReadGeometry(BudgetCategoryIconKeys.Canonicalize(key) ?? "tag");

    private static Geometry ReadGeometry(string key) => (Geometry)Application.Current.FindResource($"Lucide.{key}");

    internal sealed record Choice(string Key, string Label, Geometry Shape);
}
