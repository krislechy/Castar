using System.Collections.ObjectModel;

namespace Castar.Domain.Models.DataBase
{
    public interface IPurchase
    {
        string Name { get; set; }
        long MessageId { get; set; }
        decimal TotalSum { get; set; }
        string? RetailPlace { get; set; }
        string? RetailPlaceAddress { get; set; }
        byte[]? RawData { get; set; }
        ObservableCollection<Item> Items { get; set; }
        DateTime? AddedOn { get; set; }
    }
}
