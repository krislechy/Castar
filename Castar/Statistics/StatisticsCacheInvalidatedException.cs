using System;

namespace Castar.Statistics;

/// <summary>An obsolete chart request superseded by a financial or connection change.</summary>
internal sealed class StatisticsCacheInvalidatedException()
    : Exception("Статистика изменилась во время загрузки.")
{
}
