using System.Windows;
using System.Windows.Controls;

namespace AdamCodexHub.App.Mvvm;

public static class PasswordBoxAssistant
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxAssistant),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(bool),
            typeof(PasswordBoxAssistant),
            new PropertyMetadata(false, OnBindPasswordChanged));

    private static readonly DependencyProperty UpdatingPasswordProperty =
        DependencyProperty.RegisterAttached(
            "UpdatingPassword",
            typeof(bool),
            typeof(PasswordBoxAssistant));

    public static string GetBoundPassword(DependencyObject element) =>
        (string)element.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject element, string value) =>
        element.SetValue(BoundPasswordProperty, value);

    public static bool GetBindPassword(DependencyObject element) =>
        (bool)element.GetValue(BindPasswordProperty);

    public static void SetBindPassword(DependencyObject element, bool value) =>
        element.SetValue(BindPasswordProperty, value);

    private static void OnBoundPasswordChanged(
        DependencyObject element,
        DependencyPropertyChangedEventArgs args)
    {
        if (element is not PasswordBox passwordBox ||
            (bool)passwordBox.GetValue(UpdatingPasswordProperty))
        {
            return;
        }

        passwordBox.Password = args.NewValue as string ?? string.Empty;
    }

    private static void OnBindPasswordChanged(
        DependencyObject element,
        DependencyPropertyChangedEventArgs args)
    {
        if (element is not PasswordBox passwordBox)
        {
            return;
        }

        if ((bool)args.OldValue)
        {
            passwordBox.PasswordChanged -= OnPasswordChanged;
        }

        if ((bool)args.NewValue)
        {
            passwordBox.PasswordChanged += OnPasswordChanged;
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs args)
    {
        var passwordBox = (PasswordBox)sender;
        passwordBox.SetValue(UpdatingPasswordProperty, true);
        SetBoundPassword(passwordBox, passwordBox.Password);
        passwordBox.SetValue(UpdatingPasswordProperty, false);
    }
}
