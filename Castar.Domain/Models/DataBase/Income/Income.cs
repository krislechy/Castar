using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Domain.Models.DataBase
{

    [Table("Incomes")]
    public class Income : BaseDbModel, IIncome
    {
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public User User { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public Guid UserId { get; set; }

        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long MessageId { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public decimal Sum { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public string Source { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public DateTime AddedOn { get; set; }
    }
}
