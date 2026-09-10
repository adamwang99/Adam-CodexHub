using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AdamCodexHub.App.Services;
using AdamCodexHub.Core.Domain;

namespace AdamCodexHub.App.Converters;

/// <summary>
/// Shared rendering rules for the model callability badges. Every picker (tray, Home dropdowns,
/// provider page model list) colours a model through these helpers, so one model can never look
/// green in one place and red in another.
/// </summary>
public static class CallabilityVisuals
{
    public static string BrushKey(Callability callability) => callability switch
    {
        Callability.Callable => "BrushStatusCallable",
        Callability.Slow => "BrushStatusSlow",
        Callability.Skip => "BrushStatusSkip",
        _ => "BrushStatusUnknown"
    };

    /// <summary>Concrete brush for a callability value, resolved against the ACTIVE theme
    /// dictionary (DynamicResource is not available from C#; the theme swap repaints every
    /// badge through the Revision notification).</summary>
    public static Brush Brush(Callability callability) =>
        Application.Current?.TryFindResource(BrushKey(callability)) as Brush ?? Brushes.Gray;

    /// <summary>Provider id / model id for a bound item. Items in every model picker are
    /// <see cref="ModelDescriptor"/> values, so the row itself carries the lookup key.</summary>
    public static (string? ProviderId, string? ModelId) Keys(object? item) => item switch
    {
        ModelDescriptor model => (model.ProviderId, model.RemoteId),
        _ => (null, null)
    };

    public static Callability Resolve(object? item)
    {
        var (providerId, modelId) = Keys(item);
        return ModelStatusState.Current.GetCallability(providerId, modelId);
    }

    /// <summary>
    /// The selection order every model picker uses: callable (green) first, then slow (amber),
    /// unknown (grey) and skip (red), with an ordinal-ignore-case name order inside each group.
    /// Applied to the Home selector, the per-card selector, the tray submenu and the provider
    /// page's model list so a model never appears in a different position in two places.
    /// </summary>
    public static List<ModelDescriptor> OrderForSelection(IEnumerable<ModelDescriptor> models) =>
        models
            .OrderBy(m => ModelCallability.SortOrder(
                ModelStatusState.Current.GetCallability(m.ProviderId, m.RemoteId)))
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.RemoteId, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// Callability -> badge brush. MultiBinding: [0] the model item, [1] ModelStatusState.Revision
/// (binding to the counter is what makes a badge re-render when the auto-ping refreshes the model).
/// </summary>
public sealed class CallabilityBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        CallabilityVisuals.Brush(CallabilityVisuals.Resolve(values is { Length: > 0 } ? values[0] : null));

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Callability -> localized tooltip, e.g. "Kiểm tra lúc 14:52 · byte đầu 37,7s".
/// MultiBinding: [0] the model item, [1] ModelStatusState.Revision.
/// </summary>
public sealed class CallabilityTooltipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var item = values is { Length: > 0 } ? values[0] : null;
        var (providerId, modelId) = CallabilityVisuals.Keys(item);
        if (modelId is null)
        {
            return string.Empty;
        }

        return ModelStatusState.Current.DescribeTooltip(providerId, modelId);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Groups the model list by callability. Implemented as a <see cref="GroupDescription"/> rather
/// than a property path because callability is computed from the shared status state (it is not a
/// property of <c>ModelDescriptor</c>). Verified with a headless spike: the groups come out in the
/// order the items first appear, and <see cref="ICollectionView.Refresh"/> re-assigns an item to
/// another group when its status changed.
/// </summary>
public sealed class CallabilityGroupDescription : GroupDescription
{
    public override object GroupNameFromItem(object item, int level, CultureInfo culture) =>
        L10n.T(ModelStatusState.LabelKey(CallabilityVisuals.Resolve(item)));
}

/// <summary>Callability -> whether the model should be offered at all (Skip is hidden unless the
/// "show all models" preference is on). Used by the tray submenu, which builds its items in code.</summary>
public static class CallabilityFilter
{
    public static bool IsVisible(Callability callability, bool showAllModels) =>
        showAllModels || callability != Callability.Skip;
}
