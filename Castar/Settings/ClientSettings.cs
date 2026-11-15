using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace Castar.Settings;

public sealed class ClientSettings : INotifyPropertyChanged
{
    public const string DefaultServerUrl = "http://127.0.0.1:5080";
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Castar");

    private bool hideInTray;
    private bool autoRun;

    public string ServerUrl { get; set; } = DefaultServerUrl;
    public string? ProtectedApiKey { get; set; }

    public bool IsHideInTray
    {
        get => hideInTray;
        set => Set(ref hideInTray, value);
    }

    public bool IsAutoRun
    {
        get => autoRun;
        set => Set(ref autoRun, value);
    }

    [XmlIgnore]
    internal string ApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(ProtectedApiKey))
                return "";
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(ProtectedApiKey), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException)
            {
                return "";
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event EventHandler? ConnectionChanged;

    internal static ClientSettings Load()
    {
        if (File.Exists(SettingsPath))
        {
            using var stream = File.OpenRead(SettingsPath);
            return (ClientSettings)Serializer.Deserialize(stream)!;
        }

        foreach (var directory in new[] { AppContext.BaseDirectory, DataDirectory })
        {
            var legacyFile = Path.Combine(directory, "Castar.Settings.xml");
            if (!File.Exists(legacyFile))
                continue;
            try
            {
                var systems = XDocument.Load(legacyFile).Root?.Element("Systems");
                return new ClientSettings
                {
                    IsHideInTray = (bool?)systems?.Element("IsHideInTray") ?? false,
                    IsAutoRun = (bool?)systems?.Element("IsAutoRun") ?? false
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Xml.XmlException or FormatException)
            {
                continue;
            }
        }
        return new ClientSettings();
    }

    internal void SaveConnection(Uri server, string apiKey)
    {
        var previousUrl = ServerUrl;
        var previousKey = ProtectedApiKey;
        var encrypted = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser));
        ServerUrl = server.AbsoluteUri;
        ProtectedApiKey = encrypted;
        try
        {
            Save();
        }
        catch
        {
            ServerUrl = previousUrl;
            ProtectedApiKey = previousKey;
            throw;
        }
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Open(temporary, FileMode.CreateNew))
                Serializer.Serialize(stream, this);
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string SettingsPath => Path.Combine(DataDirectory, "Castar.Client.xml");
    private static readonly XmlSerializer Serializer = new(typeof(ClientSettings));

    private void Set(ref bool field, bool value, [CallerMemberName] string? property = null)
    {
        if (field == value)
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
