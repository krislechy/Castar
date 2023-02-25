using System.Security.Cryptography;
using System.Text;

namespace Castar.Services.Encryption
{
    public class BaseE
    public sealed class EncryptionService
    {
        public EncryptionService()
        {
        }
        public void ddd(string plainText)
        {
            string encryptedText = "";
            string pass = "1234567890123456";
            string vector = "1234567890123456";
            using (Aes aes = Aes.Create())
            {
                byte[] encrypted = EncryptStringToBytes_Aes(plainText, Encoding.UTF8.GetBytes(pass), Encoding.UTF8.GetBytes(vector));
                encryptedText = Convert.ToBase64String(encrypted);

            }
            using (Aes aes = Aes.Create())
            {
                var stringFromByte = Convert.FromBase64String(encryptedText);
                var ee = DecryptStringFromBytes_Aes(stringFromByte, Encoding.UTF8.GetBytes(pass), Encoding.UTF8.GetBytes(vector));
            }
        }
        public string Encrypt(string plainText)
        {
            using (Aes aes = CreateAES256())
            {
                byte[] encrypted = EncryptStringToBytes_Aes(plainText, aes.Key, aes.IV);
                //var encryptedText = Encoding.UTF8.GetString(encrypted);
                var encryptedText = Convert.ToBase64String(encrypted);
                return encryptedText;
            }
        }
        public string Decrypt(string encryptedText)
        {
            using (Aes aes = CreateAES256())
            {
                //var stringFromByte = Encoding.UTF8.GetBytes(encryptedText);
                var stringFromByte = Convert.FromBase64String(encryptedText);
                return DecryptStringFromBytes_Aes(stringFromByte, aes.Key, aes.IV);
            }
        }
        #region Helper
        private Aes CreateAES256()
        {
            using var aes = Aes.Create();
            aes.KeySize = 128;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            //aes.BlockSize = 128;
            //aes.IV = new byte[16];
            //aes.Key = new byte[16];
            return aes;
        }
        private byte[] EncryptStringToBytes_Aes(string plainText, byte[] Key, byte[] IV)
        {
            // Check arguments.
            if (plainText == null || plainText.Length <= 0)
                throw new ArgumentNullException("plainText");
            if (Key == null || Key.Length <= 0)
                throw new ArgumentNullException("Key");
            if (IV == null || IV.Length <= 0)
                throw new ArgumentNullException("IV");
            byte[] encrypted;

            // Create an Aes object
            // with the specified key and IV.
            using (Aes aesAlg = Aes.Create())
            {
                aesAlg.Key = Key;
                aesAlg.IV = IV;
                // Create an encryptor to perform the stream transform.
                ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);

                // Create the streams used for encryption.
                using (MemoryStream msEncrypt = new MemoryStream())
                {
                    using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    {
                        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
                        {
                            //Write all data to the stream.
                            swEncrypt.Write(plainText);
                        }
                        encrypted = msEncrypt.ToArray();
                    }
                }
            }

            // Return the encrypted bytes from the memory stream.
            return encrypted;
        }
        private string DecryptStringFromBytes_Aes(byte[] cipherText, byte[] Key, byte[] IV)
        {
            // Check arguments.
            if (cipherText == null || cipherText.Length <= 0)
                throw new ArgumentNullException("cipherText");
            if (Key == null || Key.Length <= 0)
                throw new ArgumentNullException("Key");
            if (IV == null || IV.Length <= 0)
                throw new ArgumentNullException("IV");

            // Declare the string used to hold
            // the decrypted text.
            string plaintext = null;

            // Create an Aes object
            // with the specified key and IV.
            using (Aes aesAlg = Aes.Create())
            {
                aesAlg.Key = Key;
                aesAlg.IV = IV;
                // Create a decryptor to perform the stream transform.
                ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);
                // Create the streams used for decryption.
                using (MemoryStream msDecrypt = new MemoryStream(cipherText))
                {
                    using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                    {
                        using (StreamReader srDecrypt = new StreamReader(csDecrypt))
                        {

                            // Read the decrypted bytes from the decrypting stream
                            // and place them in a string.
                            plaintext = srDecrypt.ReadToEnd();
                        }
                    }
                }
            }

            return plaintext;
        }
        #endregion
    }
}