using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Castar.DAL.Migrations;

/// <inheritdoc />
public partial class _20261121180251_DefaultBudgetCategories : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        InsertDefaults(migrationBuilder, "purchase", firstId: 1,
        [
            new("Продукты", "shopping-cart", "Супермаркеты", "рынки", "доставка продуктов"),
            new("Кафе и рестораны", "utensils", "Рестораны", "кафе", "фастфуд", "доставка еды"),
            new("Жильё", "house", "Аренда", "коммунальные услуги", "ремонт", "мебель"),
            new("Транспорт", "car-front", "Общественный транспорт", "такси", "топливо", "парковка", "обслуживание авто"),
            new("Здоровье", "heart-pulse", "Аптеки", "врачи", "стоматология", "анализы"),
            new("Одежда и обувь", "shirt", "Одежда", "обувь", "аксессуары"),
            new("Связь и интернет", "smartphone", "Мобильная связь", "домашний интернет"),
            new("Техника", "monitor", "Электроника", "компьютеры", "аксессуары"),
            new("Подписки", "repeat", "Программы", "облачные сервисы", "стриминг"),
            new("Развлечения", "gamepad-2", "Игры", "кино", "концерты", "мероприятия"),
            new("Путешествия", "plane", "Билеты", "гостиницы", "экскурсии"),
            new("Спорт", "dumbbell", "Спортзал", "инвентарь", "тренировки"),
            new("Образование", "graduation-cap", "Курсы", "книги", "обучение"),
            new("Покупки", "shopping-bag", "Товары для дома", "подарки", "прочие товары"),
            new("Личный уход", "scissors", "Парикмахерская", "косметика", "гигиена"),
            new("Налоги и сборы", "landmark", "Налоги", "госпошлины", "штрафы"),
            new("Финансовые расходы", "wallet", "Комиссии", "проценты по кредитам", "страховки"),
            new("Питомцы", "paw-print", "Корм", "ветеринар", "аксессуары"),
            new("Подарки и помощь", "gift", "Подарки", "благотворительность", "поддержка близких"),
            new("Прочее", "ellipsis", "Неопределённые расходы")
        ]);
        InsertDefaults(migrationBuilder, "income", firstId: 21,
        [
            new("Зарплата", "briefcase-business", "Основная зарплата", "премии", "бонусы"),
            new("Подработка", "laptop", "Фриланс", "консультации", "разовые проекты"),
            new("Арендный доход", "building-2", "Недвижимость", "другое имущество"),
            new("Инвестиции", "chart-no-axes-combined", "Дивиденды", "проценты", "прибыль от продажи активов"),
            new("Продажи", "store", "Продажа личных вещей", "товаров"),
            new("Возвраты", "rotate-ccw", "Возвраты покупок", "компенсации", "кешбэк"),
            new("Подарки", "gift", "Денежные подарки", "помощь"),
            new("Государственные выплаты", "landmark", "Пособия", "субсидии", "налоговые возвраты"),
            new("Прочие доходы", "ellipsis", "Неопределённые поступления")
        ]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Categories can be edited and referenced after the upgrade. Retain them on schema rollback.
    }

    private static void InsertDefaults(MigrationBuilder migrationBuilder, string kind, int firstId, SeedCategory[] categories)
    {
        var rows = categories.Select((category, index) =>
        {
            var id = "42BD59FF-F203-440E-9CCC-" + (firstId + index).ToString("D12", CultureInfo.InvariantCulture);
            var normalizedName = category.Name.Trim().Normalize().ToUpperInvariant();
            return $"({Quote(id)}, {Quote(kind)}, {Quote(category.Name)}, {Quote(normalizedName)}, " +
                $"{Quote(category.IconKey)}, {Quote(JsonSerializer.Serialize(category.Tags))}, CURRENT_TIMESTAMP)";
        });
        migrationBuilder.Sql($"""
            INSERT INTO "BudgetCategories" ("Id", "Kind", "Name", "NormalizedName", "IconKey", "Tags", "CreatedOn")
            VALUES {string.Join(",\n", rows)}
            ON CONFLICT DO NOTHING;
            """);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private sealed record SeedCategory(string Name, string IconKey, params string[] Tags);
}
