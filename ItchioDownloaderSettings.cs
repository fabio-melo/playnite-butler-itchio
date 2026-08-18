using System;
using System.Collections.Generic;
using ItchioDownloader.Butler;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace ItchioDownloader
{
    public class ItchioDownloaderSettings : ObservableObject
    {
        private string apiKey = string.Empty;
        private long profileId;
        private string connectedAs = string.Empty;
        private string installLocationPath = string.Empty;
        private bool importOwnedGames = true;
        private bool importInstalledGames = true;
        private bool onlyGameClassification = true;
        private bool onlyWindowsCompatible;
        private bool adoptItchAppInstalls = true;

        /// <summary>
        /// itch.io API key from https://itch.io/user/settings/api-keys. butlerd keeps
        /// the resulting credentials in its own database; this is only kept so the key
        /// can be re-sent if the database is ever rebuilt.
        /// </summary>
        public string ApiKey
        {
            get => apiKey;
            set => SetValue(ref apiKey, value);
        }

        /// <summary>butlerd profile id, which is also the itch.io user id.</summary>
        public long ProfileId
        {
            get => profileId;
            set => SetValue(ref profileId, value);
        }

        public string ConnectedAs
        {
            get => connectedAs;
            set => SetValue(ref connectedAs, value);
        }

        /// <summary>Where new installs go. Empty means the extension's own games folder.</summary>
        public string InstallLocationPath
        {
            get => installLocationPath;
            set => SetValue(ref installLocationPath, value);
        }

        public bool ImportOwnedGames
        {
            get => importOwnedGames;
            set => SetValue(ref importOwnedGames, value);
        }

        public bool ImportInstalledGames
        {
            get => importInstalledGames;
            set => SetValue(ref importInstalledGames, value);
        }

        /// <summary>itch.io keys also cover tools, comics, soundtracks and assets.</summary>
        public bool OnlyGameClassification
        {
            get => onlyGameClassification;
            set => SetValue(ref onlyGameClassification, value);
        }

        /// <summary>
        /// Filters the import to uploads tagged for Windows. Off by default: plenty of
        /// itch.io uploads carry no platform tag at all and would be dropped.
        /// </summary>
        public bool OnlyWindowsCompatible
        {
            get => onlyWindowsCompatible;
            set => SetValue(ref onlyWindowsCompatible, value);
        }

        /// <summary>
        /// Registers the itch.io app's install folder as a location and scans it, so
        /// games already installed through the app show up as installed here too.
        /// </summary>
        public bool AdoptItchAppInstalls
        {
            get => adoptItchAppInstalls;
            set => SetValue(ref adoptItchAppInstalls, value);
        }

        public bool IsConnected => ProfileId != 0;
    }

    public class ItchioDownloaderSettingsViewModel : ObservableObject, ISettings
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ItchioDownloaderPlugin plugin;
        private ItchioDownloaderSettings editingClone;
        private ItchioDownloaderSettings settings;
        private string status = string.Empty;
        private bool busy;

        public ItchioDownloaderSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public string Status
        {
            get => status;
            set => SetValue(ref status, value);
        }

        public bool Busy
        {
            get => busy;
            set => SetValue(ref busy, value);
        }

        public RelayCommand<object> ConnectCommand { get; }
        public RelayCommand<object> DisconnectCommand { get; }
        public RelayCommand<object> BrowseInstallLocationCommand { get; }
        public RelayCommand<object> MigrateCommand { get; }
        public RelayCommand<object> RevertMigrationCommand { get; }
        public RelayCommand<object> AdoptCommand { get; }

        public ItchioDownloaderSettingsViewModel(ItchioDownloaderPlugin plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<ItchioDownloaderSettings>() ?? new ItchioDownloaderSettings();

            if (Settings.IsConnected)
            {
                Status = $"Connected as {Settings.ConnectedAs}.";
            }

            ConnectCommand = new RelayCommand<object>(_ => Connect(), _ => !Busy);
            DisconnectCommand = new RelayCommand<object>(_ => Disconnect(), _ => !Busy && Settings.IsConnected);
            BrowseInstallLocationCommand = new RelayCommand<object>(_ => BrowseInstallLocation());
            MigrateCommand = new RelayCommand<object>(_ => plugin.MigrateInteractive(revert: false));
            RevertMigrationCommand = new RelayCommand<object>(_ => plugin.MigrateInteractive(revert: true));
            AdoptCommand = new RelayCommand<object>(_ => plugin.AdoptInteractive());
        }

        private void Connect()
        {
            if (string.IsNullOrWhiteSpace(Settings.ApiKey))
            {
                Status = "Paste an API key first.";
                return;
            }

            Busy = true;
            Status = "Starting butler…";

            try
            {
                using (var client = plugin.OpenButler(message => Status = message))
                {
                    var profile = client.LoginWithApiKey(Settings.ApiKey.Trim());
                    if (profile?.User == null)
                    {
                        Status = "butler returned no profile.";
                        return;
                    }

                    Settings.ProfileId = profile.Id;
                    Settings.ConnectedAs = profile.User.Name;
                    Status = $"Connected as {profile.User.Name}.";
                    OnPropertyChanged(nameof(Settings));
                }
            }
            catch (RpcException e)
            {
                logger.Error(e, "itch.io login failed.");
                Status = e.UserMessage;
            }
            catch (Exception e)
            {
                logger.Error(e, "itch.io login failed.");
                Status = e.Message;
            }
            finally
            {
                Busy = false;
            }
        }

        private void Disconnect()
        {
            Busy = true;
            try
            {
                using (var client = plugin.OpenButler())
                {
                    client.Forget(Settings.ProfileId);
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "Profile.Forget failed; clearing local state anyway.");
            }
            finally
            {
                Settings.ProfileId = 0;
                Settings.ConnectedAs = string.Empty;
                Settings.ApiKey = string.Empty;
                Status = "Disconnected.";
                OnPropertyChanged(nameof(Settings));
                Busy = false;
            }
        }

        private void BrowseInstallLocation()
        {
            var path = plugin.PlayniteApi.Dialogs.SelectFolder();
            if (!string.IsNullOrEmpty(path))
            {
                Settings.InstallLocationPath = path;
            }
        }

        public void BeginEdit()
        {
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
            plugin.OnSettingsChanged();
        }

        public bool VerifySettings(out List<string> errors)
        {
            // Saving without an account is fine — the library import simply stays empty
            // until one is connected.
            errors = new List<string>();
            return true;
        }
    }
}
