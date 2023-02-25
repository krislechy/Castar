using System.Security.Cryptography;
using System.Text;

namespace Castar.Security.Cryptography
{
    public sealed class Cryptography : BaseCryptography, ICryptography
    {
        public Cryptography(string password, string vector) : base(password, vector)
        {
        }
        public string? Encrypt(string plainText)
        {
            if (String.IsNullOrEmpty(plainText))
                return null;
            using (Aes aes = CreateAES256())
            {
                byte[] encrypted = EncryptStringToBytes_Aes(plainText, aes.Key, aes.IV);
                var encryptedText = Convert.ToBase64String(encrypted);
                return encryptedText;
            }
        }
        public string? Decrypt(string encryptedText)
        {
            if (String.IsNullOrEmpty(encryptedText))
                return null;
            using (Aes aes = CreateAES256())
            {
                try
                {
                    var stringFromByte = Convert.FromBase64String(encryptedText);
                    return DecryptStringFromBytes_Aes(stringFromByte, aes.Key, aes.IV);
                }
                catch (FormatException) { return null; }
            }
        }
    }
}