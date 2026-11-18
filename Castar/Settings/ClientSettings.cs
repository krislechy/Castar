using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Xml.Serialization;
using Castar.Currency;

namespace Castar.Settings;

public enum NotificationMode { Windows, Castar }

public enum NotificationCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public sealed record DisplayTimeZoneOption(string Id, string Label);

public sealed class ClientSettings : INotifyPropertyChanged
{
    public const string DefaultServerUrl = "http://127.0.0.1:5080";
    private static readonly string InstalledDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Castar");
    internal static bool IsLocalPreview { get; } =
        Environment.GetCommandLineArgs().Contains("--local-preview", StringComparer.Ordinal) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Castar-Preview.cmd"));
    public static string DataDirectory { get; } = IsLocalPreview
        ? Path.Combine(InstalledDataDirectory, "Preview") : InstalledDataDirectory;

    private bool hideInTray;
    private bool autoRun;
    private NotificationMode notificationMode;
    private NotificationCorner notificationCorner = NotificationCorner.BottomRight;
    private int maxVisibleNotifications = 3;
    private string displayCurrency = "";
    private string displayTimeZoneId = "";
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

    public NotificationMode NotificationMode
    {
        get => notificationMode;
        set => Set(ref notificationMode, value);
    }

    public NotificationCorner NotificationCorner
    {
        get => notificationCorner;
        set => Set(ref notificationCorner, value);
    }

    public int MaxVisibleNotifications
    {
        get => maxVisibleNotifications;
        set => Set(ref maxVisibleNotifications, Math.Clamp(value, 1, 5));
    }

    public string DisplayCurrency
    {
        get => displayCurrency;
        set => Set(ref displayCurrency, HistoricalExchangeRates.IsCurrencyCode(value) ? value : "");
    }

    public string DisplayTimeZoneId
    {
        get => displayTimeZoneId;
        set => Set(ref displayTimeZoneId, !string.IsNullOrEmpty(value) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(value, out _) ? value : "");
    }

    [XmlIgnore]
    internal TimeZoneInfo DisplayTimeZone => TimeZoneInfo.TryFindSystemTimeZoneById(displayTimeZoneId, out var zone)
        ? zone : TimeZoneInfo.Local;

    [XmlIgnore]
    public IReadOnlyList<DisplayTimeZoneOption> DisplayTimeZones { get; } =
        [new("", "Системный"), .. TimeZoneInfo.GetSystemTimeZones().Select(item => new DisplayTimeZoneOption(item.Id, item.DisplayName))];

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
        if (IsLocalPreview)
        {
            var installed = Path.Combine(InstalledDataDirectory, "Castar.Client.xml");
            var preview = Path.Combine(DataDirectory, "Castar.Client.xml");
            if (File.Exists(installed) && !File.Exists(preview))
            {
                try
                {
                    using var stream = File.OpenRead(installed);
                    var copy = (ClientSettings)Serializer.Deserialize(stream)!;
                    copy.settingsPath = preview;
                    copy.Save(overwrite: false);
                }
                catch (FileNotFoundException) { }
                catch (IOException) when (File.Exists(preview)) { }
            }
        }
        return Load(DataDirectory, AppContext.BaseDirectory);
    }

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
        var encrypted = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser));
        StoreConnection(server.AbsoluteUri, encrypted);
    }

    internal void Disconnect() => StoreConnection(ServerUrl, null);

    private void StoreConnection(string address, string? protectedKey)
    {
        var previousUrl = ServerUrl;
        var previousKey = ProtectedApiKey;
        ServerUrl = address;
        ProtectedApiKey = protectedKey;
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

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
