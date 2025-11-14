using System;
using System.IO;
using System.Windows;

namespace SharpDownloadManager.UI.Views;

public partial class NewDownloadDialog : Window
{
    public NewDownloadDialog()
    {
        InitializeComponent();
    }

    public string SelectedFileName => FileNameTextBox.Text.Trim();

    public string SelectedFolder => FolderTextBox.Text.Trim();

    public void Initialize(string url, string? suggestedFileName, string defaultFolder)
    {
        UrlTextBox.Text = url;
        FileNameTextBox.Text = string.IsNullOrWhiteSpace(suggestedFileName) ? "download.bin" : suggestedFileName;
        FolderTextBox.Text = string.IsNullOrWhiteSpace(defaultFolder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : defaultFolder;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = SelectedFolder,
            ShowNewFolderButton = true,
            Description = "Select the folder where the file will be saved"
        };

        var result = dialog.ShowDialog();
        if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            FolderTextBox.Text = dialog.SelectedPath;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedFileName))
        {
            MessageBox.Show(
                this,
                "Please provide a file name.",
                "New Download",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedFolder) || !Directory.Exists(SelectedFolder))
        {
            MessageBox.Show(
                this,
                "Please choose a valid target folder.",
                "New Download",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
