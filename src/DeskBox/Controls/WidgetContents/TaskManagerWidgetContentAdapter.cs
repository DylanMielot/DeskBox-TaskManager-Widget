using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Content adapter for the Task Manager feature widget. Owns the view model
/// and the lazy leaf view; releases both when the widget is disabled.
/// </summary>
public sealed class TaskManagerWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetResponsiveLayoutContent
{
    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;

    public TaskManagerWidgetContentAdapter(WidgetConfig config, LocalizationService localizationService)
        : this(config, localizationService, null)
    {
    }

    public TaskManagerWidgetContentAdapter(WidgetConfig config, LocalizationService localizationService, SettingsService? settingsService)
        : base(config, () => new TaskManagerWidgetContent())
    {
        if (config.WidgetKind != WidgetKind.TaskManager)
        {
            throw new ArgumentException("Task Manager content requires a Task Manager widget config.", nameof(config));
        }

        _localizationService = localizationService;
        _settingsService = settingsService;
        DispatcherQueue? dispatcherQueue = null;
        try
        {
            dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Not on a dispatcher thread; the view model falls back to its own
            // capture and degrades gracefully (timer disabled, direct updates).
        }

        ViewModel = new TaskManagerWidgetViewModel(config, localizationService, settingsService, dispatcherQueue);
    }

    public TaskManagerWidgetViewModel ViewModel { get; }

    public bool IsReadyForReuse => !IsDisposed;

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        if (view is TaskManagerWidgetContent content)
        {
            content.ViewModel = ViewModel;
        }
    }

    public override Task InitializeAsync()
    {
        ViewModel.ApplyAppearance();
        return ViewModel.RefreshAsync();
    }

    public override Task RefreshAsync()
    {
        return ViewModel.RefreshAsync();
    }

    public override void ApplyAppearance()
    {
        ViewModel.ApplyAppearance();
    }

    public override void OnActivated()
    {
        ViewModel.OnActivated();
    }

    public override void OnDeactivated()
    {
        ViewModel.OnDeactivated();
    }

    public override void OnWindowVisibilityChanged(bool visible)
    {
        ViewModel.OnWindowVisibilityChanged(visible);
    }

    public override void OnWindowRevealCompleted()
    {
        ViewModel.OnWindowRevealCompleted();
    }

    public override void OnCompactStateChanged(bool collapsed)
    {
        ViewModel.OnCompactStateChanged(collapsed);
    }

    public void BeginResponsiveLayoutTransition(double targetContentWidth, double targetContentHeight, bool isCollapsing)
    {
        if (MaterializedView is TaskManagerWidgetContent content)
        {
            content.BeginResponsiveLayoutTransition(targetContentWidth, targetContentHeight, isCollapsing);
        }
    }

    public void CompleteResponsiveLayoutTransition(double finalContentWidth, double finalContentHeight)
    {
        if (MaterializedView is TaskManagerWidgetContent content)
        {
            content.CompleteResponsiveLayoutTransition(finalContentWidth, finalContentHeight);
        }
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (MaterializedView is TaskManagerWidgetContent content)
        {
            content.CancelResponsiveLayoutTransition();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (MaterializedView is TaskManagerWidgetContent content)
        {
            content.ViewModel = null;
        }

        ViewModel.Dispose();
    }
}
