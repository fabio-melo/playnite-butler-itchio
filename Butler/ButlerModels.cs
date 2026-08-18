using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ItchioDownloader.Butler
{
    // Field names mirror butlerd's wire format exactly (see butlerd/types.go and
    // go-itchio/types.go). Only the parts this plugin actually reads are modelled.

    public class ItchUser
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("username")] public string Username { get; set; }
        [JsonProperty("displayName")] public string DisplayName { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("coverUrl")] public string CoverUrl { get; set; }
        [JsonProperty("stillCoverUrl")] public string StillCoverUrl { get; set; }

        public string Name => string.IsNullOrEmpty(DisplayName) ? Username : DisplayName;
    }

    public class ItchGame
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("shortText")] public string ShortText { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("classification")] public string Classification { get; set; }
        [JsonProperty("coverUrl")] public string CoverUrl { get; set; }
        [JsonProperty("stillCoverUrl")] public string StillCoverUrl { get; set; }
        [JsonProperty("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonProperty("publishedAt")] public DateTime? PublishedAt { get; set; }
        [JsonProperty("user")] public ItchUser User { get; set; }
        [JsonProperty("platforms")] public ItchPlatforms Platforms { get; set; }

        /// <summary>Non-animated cover when itch.io has one, otherwise the GIF.</summary>
        public string BestCoverUrl => string.IsNullOrEmpty(StillCoverUrl) ? CoverUrl : StillCoverUrl;
    }

    public class ItchPlatforms
    {
        [JsonProperty("windows")] public string Windows { get; set; }
        [JsonProperty("linux")] public string Linux { get; set; }
        [JsonProperty("osx")] public string Osx { get; set; }
    }

    public class ItchBuild
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("userVersion")] public string UserVersion { get; set; }
        [JsonProperty("version")] public long Version { get; set; }
        [JsonProperty("createdAt")] public DateTime? CreatedAt { get; set; }

        public string DisplayVersion =>
            string.IsNullOrEmpty(UserVersion) ? Version.ToString() : UserVersion;
    }

    public class ItchUpload
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("filename")] public string Filename { get; set; }
        [JsonProperty("displayName")] public string DisplayName { get; set; }
        [JsonProperty("size")] public long Size { get; set; }
        [JsonProperty("channelName")] public string ChannelName { get; set; }
        [JsonProperty("build")] public ItchBuild Build { get; set; }
        [JsonProperty("buildId")] public long BuildId { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("preorder")] public bool Preorder { get; set; }
        [JsonProperty("demo")] public bool Demo { get; set; }
        [JsonProperty("platforms")] public ItchPlatforms Platforms { get; set; }

        public string Label =>
            !string.IsNullOrEmpty(DisplayName) ? DisplayName :
            !string.IsNullOrEmpty(Filename) ? Filename :
            $"upload {Id}";
    }

    public class DownloadKey
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("gameId")] public long GameId { get; set; }
        [JsonProperty("game")] public ItchGame Game { get; set; }
        [JsonProperty("createdAt")] public DateTime? CreatedAt { get; set; }
    }

    public class Profile
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("lastConnected")] public DateTime LastConnected { get; set; }
        [JsonProperty("user")] public ItchUser User { get; set; }
    }

    public class ProfileListResult
    {
        [JsonProperty("profiles")] public List<Profile> Profiles { get; set; }
    }

    public class ProfileResult
    {
        [JsonProperty("profile")] public Profile Profile { get; set; }
    }

    public class CaveStats
    {
        [JsonProperty("installedAt")] public DateTime? InstalledAt { get; set; }
        [JsonProperty("lastTouchedAt")] public DateTime? LastTouchedAt { get; set; }
        [JsonProperty("secondsRun")] public long SecondsRun { get; set; }
    }

    public class CaveInstallInfo
    {
        [JsonProperty("installedSize")] public long InstalledSize { get; set; }
        [JsonProperty("installLocation")] public string InstallLocation { get; set; }
        [JsonProperty("installFolder")] public string InstallFolder { get; set; }
        [JsonProperty("pinned")] public bool Pinned { get; set; }
    }

    public class Cave
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("game")] public ItchGame Game { get; set; }
        [JsonProperty("upload")] public ItchUpload Upload { get; set; }
        [JsonProperty("build")] public ItchBuild Build { get; set; }
        [JsonProperty("stats")] public CaveStats Stats { get; set; }
        [JsonProperty("installInfo")] public CaveInstallInfo InstallInfo { get; set; }
    }

    public class PagedResult<T>
    {
        [JsonProperty("items")] public List<T> Items { get; set; }
        [JsonProperty("nextCursor")] public string NextCursor { get; set; }
        [JsonProperty("stale")] public bool Stale { get; set; }
    }

    public class FetchGameResult
    {
        [JsonProperty("game")] public ItchGame Game { get; set; }
        [JsonProperty("stale")] public bool Stale { get; set; }
    }

    public class InstallLocationSizeInfo
    {
        [JsonProperty("installedSize")] public long InstalledSize { get; set; }
        [JsonProperty("freeSize")] public long FreeSize { get; set; }
        [JsonProperty("totalSize")] public long TotalSize { get; set; }
    }

    public class InstallLocationSummary
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("sizeInfo")] public InstallLocationSizeInfo SizeInfo { get; set; }
    }

    public class InstallLocationsListResult
    {
        [JsonProperty("installLocations")] public List<InstallLocationSummary> InstallLocations { get; set; }
    }

    public class InstallLocationsAddResult
    {
        /// <summary>Null when a location with the same path already existed.</summary>
        [JsonProperty("installLocation")] public InstallLocationSummary InstallLocation { get; set; }
    }

    public class InstallGetUploadsResult
    {
        [JsonProperty("game")] public ItchGame Game { get; set; }
        [JsonProperty("uploads")] public List<ItchUpload> Uploads { get; set; }
        [JsonProperty("incompatibleUploads")] public List<ItchUpload> IncompatibleUploads { get; set; }
    }

    public class DiskUsageInfo
    {
        [JsonProperty("finalDiskUsage")] public long FinalDiskUsage { get; set; }
        [JsonProperty("neededFreeSpace")] public long NeededFreeSpace { get; set; }
        [JsonProperty("accuracy")] public string Accuracy { get; set; }
    }

    public class InstallPlanInfo
    {
        [JsonProperty("upload")] public ItchUpload Upload { get; set; }
        [JsonProperty("build")] public ItchBuild Build { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("diskUsage")] public DiskUsageInfo DiskUsage { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
        [JsonProperty("errorMessage")] public string ErrorMessage { get; set; }
    }

    public class InstallPlanUploadResult
    {
        [JsonProperty("info")] public InstallPlanInfo Info { get; set; }
    }

    public class InstallQueueResult
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
        [JsonProperty("caveId")] public string CaveId { get; set; }
        [JsonProperty("game")] public ItchGame Game { get; set; }
        [JsonProperty("upload")] public ItchUpload Upload { get; set; }
        [JsonProperty("build")] public ItchBuild Build { get; set; }
        [JsonProperty("installFolder")] public string InstallFolder { get; set; }
        [JsonProperty("stagingFolder")] public string StagingFolder { get; set; }
        [JsonProperty("installLocationId")] public string InstallLocationId { get; set; }
    }

    public class InstallPerformResult
    {
        [JsonProperty("caveId")] public string CaveId { get; set; }
    }

    public class InstallCancelResult
    {
        [JsonProperty("didCancel")] public bool DidCancel { get; set; }
    }

    public class ProgressNotification
    {
        /// <summary>Overall progress, 0 to 1.</summary>
        [JsonProperty("progress")] public double Progress { get; set; }
        /// <summary>Seconds remaining.</summary>
        [JsonProperty("eta")] public double Eta { get; set; }
        /// <summary>Bytes per second.</summary>
        [JsonProperty("bps")] public double Bps { get; set; }
    }

    public class TaskStartedNotification
    {
        [JsonProperty("reason")] public string Reason { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("game")] public ItchGame Game { get; set; }
        [JsonProperty("upload")] public ItchUpload Upload { get; set; }
        [JsonProperty("build")] public ItchBuild Build { get; set; }
        [JsonProperty("totalSize")] public long TotalSize { get; set; }
    }

    public class TaskSucceededNotification
    {
        [JsonProperty("type")] public string Type { get; set; }
    }

    public class GameUpdateChoice
    {
        [JsonProperty("upload")] public ItchUpload Upload { get; set; }
        [JsonProperty("build")] public ItchBuild Build { get; set; }
    }

    public class GameUpdate
    {
        [JsonProperty("caveId")] public string CaveId { get; set; }
        [JsonProperty("game")] public ItchGame Game { get; set; }
        [JsonProperty("direct")] public bool Direct { get; set; }
        [JsonProperty("choices")] public List<GameUpdateChoice> Choices { get; set; }
    }

    public class CheckUpdateResult
    {
        [JsonProperty("updates")] public List<GameUpdate> Updates { get; set; }
        [JsonProperty("warnings")] public List<string> Warnings { get; set; }
    }

    // Server-to-client requests during Launch.

    public class ManifestAction
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("icon")] public string Icon { get; set; }
        [JsonProperty("args")] public List<string> Args { get; set; }
        [JsonProperty("platform")] public string Platform { get; set; }

        public string Label => string.IsNullOrEmpty(Name) ? Path : Name;
    }

    public class PickManifestActionParams
    {
        [JsonProperty("actions")] public List<ManifestAction> Actions { get; set; }
    }

    public class ShellLaunchParams
    {
        [JsonProperty("itemPath")] public string ItemPath { get; set; }
    }

    public class UrlLaunchParams
    {
        [JsonProperty("url")] public string Url { get; set; }
    }

    public class HtmlLaunchParams
    {
        [JsonProperty("rootFolder")] public string RootFolder { get; set; }
        [JsonProperty("indexPath")] public string IndexPath { get; set; }
    }

    public class AcceptLicenseParams
    {
        [JsonProperty("text")] public string Text { get; set; }
    }

    /// <summary>butlerd method names, kept in one place so typos surface here.</summary>
    public static class ButlerMethods
    {
        public const string MetaAuthenticate = "Meta.Authenticate";
        public const string MetaShutdown = "Meta.Shutdown";

        public const string ProfileList = "Profile.List";
        public const string ProfileLoginWithApiKey = "Profile.LoginWithAPIKey";
        public const string ProfileUseSavedLogin = "Profile.UseSavedLogin";
        public const string ProfileForget = "Profile.Forget";

        public const string FetchProfileOwnedKeys = "Fetch.ProfileOwnedKeys";
        public const string FetchProfileCollections = "Fetch.ProfileCollections";
        public const string FetchCollectionGames = "Fetch.Collection.Games";
        public const string FetchGame = "Fetch.Game";
        public const string FetchCaves = "Fetch.Caves";
        public const string FetchCave = "Fetch.Cave";

        public const string InstallGetUploads = "Install.GetUploads";
        public const string InstallPlanUpload = "Install.PlanUpload";
        public const string InstallQueue = "Install.Queue";
        public const string InstallPerform = "Install.Perform";
        public const string InstallCancel = "Install.Cancel";
        public const string UninstallPerform = "Uninstall.Perform";

        public const string InstallLocationsList = "Install.Locations.List";
        public const string InstallLocationsAdd = "Install.Locations.Add";
        public const string InstallLocationsScan = "Install.Locations.Scan";
        public const string InstallLocationsScanYield = "Install.Locations.Scan.Yield";
        public const string InstallLocationsScanConfirmImport = "Install.Locations.Scan.ConfirmImport";

        public const string Launch = "Launch";
        public const string CheckUpdate = "CheckUpdate";

        public const string CleanDownloadsSearch = "CleanDownloads.Search";
        public const string CleanDownloadsApply = "CleanDownloads.Apply";

        // Notifications
        public const string Progress = "Progress";
        public const string TaskStarted = "TaskStarted";
        public const string TaskSucceeded = "TaskSucceeded";
        public const string LaunchRunning = "LaunchRunning";
        public const string LaunchExited = "LaunchExited";

        // Server-to-client requests
        public const string PickManifestAction = "PickManifestAction";
        public const string AcceptLicense = "AcceptLicense";
        public const string ShellLaunch = "ShellLaunch";
        public const string UrlLaunch = "URLLaunch";
        public const string HtmlLaunch = "HTMLLaunch";
        public const string PrereqsFailed = "PrereqsFailed";
    }
}
