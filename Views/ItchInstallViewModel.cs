using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ItchioDownloader.Butler;
using ItchioDownloader.Services;
using Playnite.SDK;

namespace ItchioDownloader.Views
{
    /// <summary>One row in the file picker.</summary>
    public class UploadChoice
    {
        public ItchUpload Upload { get; set; }
        public bool Compatible { get; set; }

        public string Label => Upload.Label;

        public string Detail
        {
            get
            {
                var parts = new List<string>();

                if (Upload.Size > 0)
                {
                    parts.Add(ItchInstallViewModel.FormatBytes(Upload.Size));
                }

                var platforms = Platforms();
                if (!string.IsNullOrEmpty(platforms))
                {
                    parts.Add(platforms);
                }

                if (!string.IsNullOrEmpty(Upload.ChannelName))
                {
                    parts.Add("channel " + Upload.ChannelName);
                }

                if (Upload.Build != null)
                {
                    parts.Add("v" + Upload.Build.DisplayVersion);
                }

                if (Upload.Demo)
                {
                    parts.Add("demo");
                }

                if (Upload.Preorder)
                {
                    parts.Add("preorder");
                }

                if (!Compatible)
                {
                    parts.Add("not tagged for this system");
                }

                return string.Join(" · ", parts);
            }
        }

        private string Platforms()
        {
            var p = Upload.Platforms;
            if (p == null)
            {
                return null;
            }

            var names = new List<string>();
            if (!string.IsNullOrEmpty(p.Windows)) names.Add("Windows");
            if (!string.IsNullOrEmpty(p.Linux)) names.Add("Linux");
            if (!string.IsNullOrEmpty(p.Osx)) names.Add("macOS");
            return names.Count > 0 ? string.Join("/", names) : null;
        }
    }

    public class ItchInstallViewModel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ItchioDownloaderPlugin plugin;
        private readonly ItchInstallOptions options;
        private readonly Window window;

        private UploadChoice selectedUpload;
        private InstallLocationSummary selectedLocation;
        private bool showIncompatible;
        private bool busy;
        private string downloadSizeText = "—";
        private string installSizeText = "—";
        private string freeSpaceText = "—";
        private string afterInstallText = "—";
        private CancellationTokenSource planCts;

        public string GameTitle => options.Game?.Title ?? "itch.io";

        public ObservableCollection<UploadChoice> Uploads { get; } = new ObservableCollection<UploadChoice>();

        public ObservableCollection<InstallLocationSummary> Locations { get; } =
            new ObservableCollection<InstallLocationSummary>();

        public bool HasIncompatible => options.Incompatible != null && options.Incompatible.Count > 0;

        public bool Confirmed { get; private set; }
        public ItchUpload ChosenUpload => selectedUpload?.Upload;
        public string ChosenLocationId => selectedLocation?.Id;

        public UploadChoice SelectedUpload
        {
            get => selectedUpload;
            set
            {
                SetValue(ref selectedUpload, value);
                RefreshPlan();
            }
        }

        public InstallLocationSummary SelectedLocation
        {
            get => selectedLocation;
            set
            {
                SetValue(ref selectedLocation, value);
                RefreshSpace();
            }
        }

        /// <summary>
        /// itch.io uploads are frequently untagged, so hiding the "incompatible" ones by
        /// default without an escape hatch would make some games uninstallable.
        /// </summary>
        public bool ShowIncompatible
        {
            get => showIncompatible;
            set
            {
                SetValue(ref showIncompatible, value);
                BuildUploadList();
            }
        }

        public bool Busy
        {
            get => busy;
            set => SetValue(ref busy, value);
        }

        public string DownloadSizeText
        {
            get => downloadSizeText;
            set => SetValue(ref downloadSizeText, value);
        }

        public string InstallSizeText
        {
            get => installSizeText;
            set => SetValue(ref installSizeText, value);
        }

        public string FreeSpaceText
        {
            get => freeSpaceText;
            set => SetValue(ref freeSpaceText, value);
        }

        public string AfterInstallText
        {
            get => afterInstallText;
            set => SetValue(ref afterInstallText, value);
        }

        public RelayCommand<object> InstallCommand { get; }
        public RelayCommand<object> CancelCommand { get; }
        public RelayCommand<object> BrowseCommand { get; }

        public ItchInstallViewModel(ItchioDownloaderPlugin plugin, ItchInstallOptions options, Window window)
        {
            this.plugin = plugin;
            this.options = options;
            this.window = window;

            foreach (var location in options.Locations ?? new List<InstallLocationSummary>())
            {
                Locations.Add(location);
            }

            selectedLocation = Locations.FirstOrDefault(l => l.Id == options.DefaultLocationId)
                ?? Locations.FirstOrDefault();

            BuildUploadList();
            RefreshSpace();

            InstallCommand = new RelayCommand<object>(_ => Confirm(), _ => SelectedUpload != null && SelectedLocation != null);
            CancelCommand = new RelayCommand<object>(_ => Close());
            BrowseCommand = new RelayCommand<object>(_ => Browse());
        }

        private void BuildUploadList()
        {
            var previous = selectedUpload?.Upload?.Id;
            Uploads.Clear();

            foreach (var upload in options.Compatible ?? new List<ItchUpload>())
            {
                Uploads.Add(new UploadChoice { Upload = upload, Compatible = true });
            }

            if (ShowIncompatible || Uploads.Count == 0)
            {
                foreach (var upload in options.Incompatible ?? new List<ItchUpload>())
                {
                    Uploads.Add(new UploadChoice { Upload = upload, Compatible = false });
                }
            }

            OnPropertyChanged(nameof(Uploads));

            var restored = previous.HasValue
                ? Uploads.FirstOrDefault(u => u.Upload.Id == previous.Value)
                : null;

            SelectedUpload = restored
                ?? Uploads.FirstOrDefault(u => !u.Upload.Demo && !u.Upload.Preorder)
                ?? Uploads.FirstOrDefault();
        }

        private void RefreshPlan()
        {
            planCts?.Cancel();
            planCts = new CancellationTokenSource();
            var token = planCts.Token;

            var upload = selectedUpload?.Upload;
            if (upload == null)
            {
                DownloadSizeText = "—";
                InstallSizeText = "—";
                RefreshSpace();
                return;
            }

            DownloadSizeText = upload.Size > 0 ? FormatBytes(upload.Size) : "—";
            InstallSizeText = "calculating…";
            Busy = true;

            Task.Run(() =>
            {
                try
                {
                    // Install.PlanUpload is the slow half of planning: it inspects the
                    // archive over the network.
                    var plan = plugin.Installs.PlanUpload(upload.Id);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (plan?.DiskUsage != null)
                        {
                            InstallSizeText = FormatBytes(plan.DiskUsage.FinalDiskUsage);
                            plannedInstallSize = plan.DiskUsage.FinalDiskUsage;
                        }
                        else
                        {
                            InstallSizeText = string.IsNullOrEmpty(plan?.ErrorMessage) ? "unknown" : plan.ErrorMessage;
                            plannedInstallSize = 0;
                        }

                        if (plan?.Upload != null && plan.Upload.Size > 0)
                        {
                            DownloadSizeText = FormatBytes(plan.Upload.Size);
                        }

                        RefreshSpace();
                    });
                }
                catch (Exception e)
                {
                    logger.Warn(e, "Install.PlanUpload failed.");
                    if (!token.IsCancellationRequested)
                    {
                        Application.Current.Dispatcher.Invoke(() => InstallSizeText = "unknown");
                    }
                }
                finally
                {
                    if (!token.IsCancellationRequested)
                    {
                        Application.Current.Dispatcher.Invoke(() => Busy = false);
                    }
                }
            });
        }

        private long plannedInstallSize;

        private void RefreshSpace()
        {
            var path = selectedLocation?.Path;
            if (string.IsNullOrEmpty(path))
            {
                FreeSpaceText = "—";
                AfterInstallText = "—";
                return;
            }

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)));
                var free = drive.AvailableFreeSpace;
                FreeSpaceText = FormatBytes(free);
                AfterInstallText = plannedInstallSize > 0 ? FormatBytes(free - plannedInstallSize) : "—";
            }
            catch (Exception e)
            {
                logger.Warn(e, "Could not read free space for " + path);
                FreeSpaceText = "—";
                AfterInstallText = "—";
            }
        }

        private void Browse()
        {
            var path = plugin.PlayniteApi.Dialogs.SelectFolder();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                var added = plugin.Installs.AddInstallLocation(path);
                if (added == null)
                {
                    return;
                }

                var existing = Locations.FirstOrDefault(l => l.Id == added.Id);
                if (existing == null)
                {
                    Locations.Add(added);
                    existing = added;
                }

                SelectedLocation = existing;
            }
            catch (Exception e)
            {
                logger.Error(e, "Could not add an install location.");
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(e.Message, "itch.io");
            }
        }

        private void Confirm()
        {
            Confirmed = true;
            Close();
        }

        private void Close()
        {
            planCts?.Cancel();
            window?.Close();
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0)
            {
                return "—";
            }

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
        }
    }
}
