namespace Castar.Domain.Models.DataBase
{
    public class Income : BaseDbModel, IIncome
    {
        private long? _messageId;

        public User User { get; set; } = null!;
        public Guid UserId { get; set; }
        public long MessageId
        {
            get => _messageId.GetValueOrDefault();
            set => _messageId = value == 0 ? null : value;
        }

        public decimal Sum { get; set; }
        public string Source { get; set; } = string.Empty;
        public DateTime AddedOn { get; set; }
    }
}
