using System.IO;
using System.Windows;
using AdamCodexHub.Core.Interfaces;
using AdamCodexHub.Infrastructure.Paths;
using Microsoft.Win32;

namespace AdamCodexHub.App.Services;

public interface IUserDialogService
{
    bool Confirm(string title, string message, string actionLabel);

    void ShowModelTest(
        string providerName,
        string providerId,
        string modelName,
        string modelId);

    /// <summary>
    /// Opens a file picker for a provider logo, validates the extension and size,
    /// and copies the image into the logos folder keyed by provider id. Returns the
    /// saved relative file name, or null when the user cancels.
    /// </summary>
    string? PickProviderLogo(string providerId);
}

public sealed class UserDialogService : IUserDialogService
{
    private const long MaxLogoBytes = 512 * 1024; // 512 KB
    private static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg" };

    private readonly ICompatibilityService _compatibility;
    private readonly AppPaths _paths;

    public UserDialogService(ICompatibilityService compatibility, AppPaths paths)
    {
        _compatibility = compatibility;
        _paths = paths;
    }

    public bool Confirm(string title, string message, string actionLabel)
    {
        var result = MessageBox.Show(
            $"{message}\n\nConfirm action: {actionLabel}",
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        return result == MessageBoxResult.OK;
    }

    public void ShowModelTest(
        string providerName,
        string providerId,
        string modelName,
        string modelId)
    {
        var window = new ModelTestWindow(
            _compatibility,
            providerName,
            providerId,
            modelName,
            modelId)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    public string? PickProviderLogo(string providerId)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose provider logo",
            Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            MessageBox.Show(
                "Only PNG, JPG and JPEG images are supported.",
                "Unsupported image type",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        var info = new FileInfo(dialog.FileName);
        if (info.Length > MaxLogoBytes)
        {
            MessageBox.Show(
                "The selected image is too large. Choose an image under 512 KB.",
                "Image too large",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        var safeId = SanitizeFileName(providerId);
        var target = Path.Combine(_paths.Logos, $"{safeId}{extension}");

        // Remove any previous logo for this provider with a different extension.
        foreach (var existing in Directory.EnumerateFiles(_paths.Logos, $"{safeId}.*"))
        {
            try
            {
                File.Delete(existing);
            }
            catch (IOException)
            {
            }
        }

        File.Copy(dialog.FileName, target, overwrite: true);
        return Path.GetFileName(target);
    }

    private static string SanitizeFileName(string providerId)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string(providerId
            .Where(c => !invalid.Contains(c))
            .ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "provider" : clean;
    }
}
