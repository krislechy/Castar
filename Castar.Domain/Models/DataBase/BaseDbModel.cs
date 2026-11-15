namespace Castar.Domain.Models.DataBase
{
    public abstract class BaseDbModel
    {
        public Guid Id { get; private set; }
        public DateTime CreatedOn { get; private set; }
        public DateTime? ModifiedOn { get; private set; }

        public T Clone<T>() => (T)MemberwiseClone();
    }
}
