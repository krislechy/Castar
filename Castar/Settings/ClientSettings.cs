using System;
using System.ComponentModel;
using System.Diagnostics;
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
    private string settingsPath = Path.Combine(DataDirectory, "Castar.Client.xml");

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

    internal static ClientSettings Load() => Load(DataDirectory, AppContext.BaseDirectory);

    internal static ClientSettings Load(string dataDirectory, string installationDirectory)
    {
        var path = Path.Combine(dataDirectory, "Castar.Client.xml");
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            var settings = (ClientSettings)Serializer.Deserialize(stream)!;
            settings.settingsPath = path;
            return settings;
        }

        var migrated = new ClientSettings { settingsPath = path };
        foreach (var directory in new[] { installationDirectory, dataDirectory })
        {
            var legacyFile = Path.Combine(directory, "Castar.Settings.xml");
            if (!File.Exists(legacyFile))
                continue;
            try
            {
                var systems = XDocument.Load(legacyFile).Root?.Element("Systems");
                if (systems is null)
                    continue;
                var tray = ReadLegacyFlag(systems, nameof(IsHideInTray));
                var autorun = ReadLegacyFlag(systems, nameof(IsAutoRun));
                if (tray is null && autorun is null)
                    continue;
                migrated.IsHideInTray = tray.GetValueOrDefault();
                migrated.IsAutoRun = autorun.GetValueOrDefault();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Xml.XmlException or FormatException)
            {
                continue;
            }

            try
            {
                migrated.Save(overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another instance has created a client file. Use its settings.
                return Load(dataDirectory, installationDirectory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Castar could not persist migrated client settings.");
            }
            break;
        }
        return migrated;
    }

    private static bool? ReadLegacyFlag(XElement systems, string name) =>
        ((string?)systems.Element(name))?.Trim() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null
        };

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

    internal void Save(bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Open(temporary, FileMode.CreateNew))
                Serializer.Serialize(stream, this);
            File.Move(temporary, settingsPath, overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static readonly XmlSerializer Serializer = new(typeof(ClientSettings));

    private void Set(ref bool field, bool value, [CallerMemberName] string? property = null)
    {
        if (field == value)
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
