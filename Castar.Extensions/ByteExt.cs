using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Extensions
{
    public static class ByteExt
    {
        public static byte[] AddByteToArray(this byte[] bArray, byte newByte)
        {
            byte[] newArray = new byte[bArray.Length + 1];
            bArray.CopyTo(newArray, 1);
            newArray[0] = newByte;
            return newArray;
        }
        public static byte[] RemoveByteToArray(this byte[] bArray)
        {
            return bArray.SkipLast(1).ToArray();
        }
    }
}
