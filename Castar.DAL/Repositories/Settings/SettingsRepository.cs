using Castar.Domain.Models.SettingsModel;
using Castar.Extensions;
using Castar.Security.Cryptography;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Castar.DAL.Repositories.Settings
{

    public class SettingsRepository : ISettingsRepository
    {
        private readonly XmlSerializer xmlSerializer;
        private readonly string pathToSettings;
        private readonly ICryptography cryptography;
        public SettingsRepository(string pathToSettings, ICryptography cryptography)
        {
            this.cryptography = cryptography;
            this.pathToSettings = pathToSettings;
            xmlSerializer = new XmlSerializer(typeof(SettingsModel));
        }

        public void Save(SettingsModel settings)
        {
            var temporaryPath = pathToSettings + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pathToSettings))!);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew))
                    xmlSerializer.Serialize(stream, EncryptValues(settings, true));
                File.Move(temporaryPath, pathToSettings, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        public SettingsModel Load()
        {
            if (!File.Exists(pathToSettings))
                return new SettingsModel();

            using var stream = File.OpenRead(pathToSettings);
            return stream.Length == 0
                ? new SettingsModel()
                : EncryptValues((SettingsModel)xmlSerializer.Deserialize(stream)!, false);
        }

        public IEnumerable<PropertyInfo> FindEncryptionProperties()
        {
            var properties= typeof(SettingsModel).GetProperties();
            foreach (var property in properties)
            {
                var mustEncrypted = property.PropertyType.GetProperties()
                    .Where(x => x.GetCustomAttributes(typeof(EncryptionAttribute)).Any());
                if (mustEncrypted != null && mustEncrypted.Any())
                    foreach (var prop in mustEncrypted)
                        yield return prop;
            }
        }

        public SettingsModel EncryptValues(SettingsModel settings, bool IsEncrypt)
        {
            var copied_settings = settings.Copy();
            foreach (var propertyEncrypt in FindEncryptionProperties())
            {
                foreach (var property in typeof(SettingsModel).GetProperties())
                {
                    if (propertyEncrypt.DeclaringType != property.PropertyType) continue;
                    foreach (var subProperty in property.PropertyType.GetProperties())
                    {
                        if (subProperty.Name == propertyEncrypt.Name)
                        {
                            var valuesProperties = property.GetValue(copied_settings);
                            var valueString = (string?)propertyEncrypt.GetValue(valuesProperties, null);
                            if (valueString != null)
                            {
                                var encryptedValue = IsEncrypt ? cryptography.Encrypt(valueString) : cryptography.Decrypt(valueString);
                                propertyEncrypt.SetValue(valuesProperties, encryptedValue);
                            }
                        }
                    }
                }
            }
            return copied_settings;
        }
    }
}
