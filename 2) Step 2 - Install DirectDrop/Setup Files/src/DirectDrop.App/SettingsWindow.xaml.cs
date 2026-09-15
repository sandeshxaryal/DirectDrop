using System.Diagnostics;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using DirectDrop.App.Services;

namespace DirectDrop.App;

public partial class SettingsWindow : Window
{
    private readonly DirectDropSettingsService _service;
    private readonly DirectDropSettings _settings;
    private readonly bool _firstRun;

    public SettingsWindow(DirectDropSettingsService service, DirectDropSettings settings, bool firstRun = false)
    {
        InitializeComponent();
        _service = service;
        _settings = settings;
        _firstRun = firstRun;
        LoadFields();

        if (_firstRun)
        {
            Title = "Welcome to DirectDrop";
            TitleText.Text = "Choose your download location";
            SubtitleText.Text = "Before DirectDrop starts, choose where files received from your phone should be saved.";
            ResetButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Use default";
            SaveButton.Content = "Use this folder";
            ApplyHintText.Text = "You can change this later from Settings.";
        }
    }

    private void LoadFields()
    {
        UploadFolderTextBox.Text = _settings.UploadDestination;
    }

    private void BrowseUploadButton_Click(object sender, RoutedEventArgs e) => BrowseInto(UploadFolderTextBox);

    private static void BrowseInto(System.Windows.Controls.TextBox target)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(target.Text) ? target.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) target.Text = dialog.SelectedPath;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        UploadFolderTextBox.Text = DirectDropSettingsService.DefaultUploadDestination;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_firstRun)
        {
            _settings.UploadDestination = DirectDropSettingsService.DefaultUploadDestination;
            _settings.HasCompletedFirstRunSetup = true;
            _service.Save(_settings);
            DialogResult = true;
            return;
        }
        DialogResult = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UploadFolderTextBox.Text))
        {
            System.Windows.MessageBox.Show(this, "Choose a valid folder location.", "DirectDrop", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.UploadDestination = UploadFolderTextBox.Text.Trim();
        _settings.HasCompletedFirstRunSetup = true;

        if (!_service.Save(_settings))
        {
            System.Windows.MessageBox.Show(this, "DirectDrop couldn't save these settings.", "DirectDrop", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = true;
    }
    private void GitHubButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/sandeshxaryal/directdrop",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Could not open GitHub.\n\n{ex.Message}", "DirectDrop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}
