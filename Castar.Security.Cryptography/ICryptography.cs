using System.Security.Cryptography;
using System.Text;

namespace Castar.Security.Cryptography
{
    public interface ICryptography
    {
        string? Encrypt(string plainText);
        string? Decrypt(string encryptedText);
    }
}