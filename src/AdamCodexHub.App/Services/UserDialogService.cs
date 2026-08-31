using System.Windows;

namespace AdamCodexHub.App.Services;

public interface IUserDialogService
{
    bool Confirm(string title, string message, string actionLabel);
}

public sealed class UserDialogService : IUserDialogService
{
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
}
