using System.Windows.Controls;

namespace ItchioDownloader.Views
{
    public partial class ItchioDownloaderSettingsView : UserControl
    {
        public ItchioDownloaderSettingsView()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => SyncApiKeyBox();
        }

        private void SyncApiKeyBox()
        {
            // PasswordBox.Password is not a dependency property, so it cannot be bound.
            var model = DataContext as ItchioDownloaderSettingsViewModel;
            ApiKeyBox.Password = model?.Settings?.ApiKey ?? string.Empty;
        }

        private void ApiKeyBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            var model = DataContext as ItchioDownloaderSettingsViewModel;
            if (model?.Settings != null)
            {
                model.Settings.ApiKey = ApiKeyBox.Password;
            }
        }
    }
}
