using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.Receipts.Models
{
    public sealed class Receipt
    {
        public int dateTime { get; set; }
        public int cashTotalSum { get; set; }
        public int code { get; set; }
        public int creditSum { get; set; }
        public int ecashTotalSum { get; set; }
        public int fiscalDocumentFormatVer { get; set; }
        public int fiscalDocumentNumber { get; set; }
        public string fiscalDriveNumber { get; set; }
        public long fiscalSign { get; set; }
        public string fnsUrl { get; set; }
        public List<Item> items { get; set; }
        public string kktRegId { get; set; }
        public int nds10 { get; set; }
        public int operationType { get; set; }
        public string @operator { get; set; }
        public int prepaidSum { get; set; }
        public int provisionSum { get; set; }
        public int requestNumber { get; set; }
        public string retailPlace { get; set; }
        public string retailPlaceAddress { get; set; }
        public int shiftNumber { get; set; }
        public int taxationType { get; set; }
        public int appliedTaxationType { get; set; }
        public int totalSum { get; set; }
        public decimal totalSumDecimal { get => ((decimal)totalSum / 100); }
        public string user { get; set; }
        public string userInn { get; set; }
    }
}
