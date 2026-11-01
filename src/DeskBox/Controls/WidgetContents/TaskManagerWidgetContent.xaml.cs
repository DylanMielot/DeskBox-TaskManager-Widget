using DeskBox.Contracts;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Task Manager widget content view: a process table with click-to-sort column
/// headers and blue-tinted intensity cells, plus a capsule summary for the
/// collapsed (capsule) presentation.
/// </summary>
public sealed partial class TaskManagerWidgetContent : UserControl, IWidgetResponsiveLayoutContent
{
    public TaskManagerWidgetContent()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Set by the owning adapter after materialization (<see cref="TaskManagerWidgetContentAdapter.OnViewMaterialized"/>)
    /// and cleared on dispose. Drives <x:Bind> against the view model.
    /// </summary>
    public TaskManagerWidgetViewModel ViewModel
    {
        get => (TaskManagerWidgetViewModel)DataContext!;
        set
        {
            if (DataContext is TaskManagerWidgetViewModel oldVm)
            {
                oldVm.Refreshing -= OnVmRefreshing;
                oldVm.Refreshed -= OnVmRefreshed;
            }
            DataContext = value;
            if (value is TaskManagerWidgetViewModel newVm)
            {
                newVm.Refreshing += OnVmRefreshing;
                newVm.Refreshed += OnVmRefreshed;
            }
        }
    }

    private double? _capturedScrollOffset;

    private void OnVmRefreshing(object? sender, EventArgs e)
    {
        // Remember where the user was scrolled before the list is rebuilt so we
        // can put the scrollbar back afterward (keeps the scrollbar steady).
        _capturedScrollOffset = GetVerticalOffset(ProcessList);
    }

    private void OnVmRefreshed(object? sender, EventArgs e)
    {
        if (_capturedScrollOffset is not double offset)
        {
            return;
        }
        _capturedScrollOffset = null;

        // Restore the scroll offset after the ListView has rebuilt its items so
        // the scrollbar stays where the user left it across refreshes.
        var scrollViewer = FindScrollViewer(ProcessList);
        if (scrollViewer is not null)
        {
            scrollViewer.ChangeView(null, offset, null, true);
        }
    }

    private static double GetVerticalOffset(ListView? listView)
    {
        var scrollViewer = FindScrollViewer(listView);
        return scrollViewer is not null ? scrollViewer.VerticalOffset : 0d;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? obj)
    {
        if (obj is null)
        {
            return null;
        }
        if (obj is ScrollViewer scrollViewer)
        {
            return scrollViewer;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(obj, i));
            if (result is not null)
            {
                return result;
            }
        }
        return null;
    }

    private TaskManagerProcessRow? _contextMenuRow;

    private void NameHeader_Click(object sender, RoutedEventArgs e) => SetSort("Name");
    private void CpuHeader_Click(object sender, RoutedEventArgs e) => SetSort("CPU");
    private void MemoryHeader_Click(object sender, RoutedEventArgs e) => SetSort("Memory");
    private void DiskHeader_Click(object sender, RoutedEventArgs e) => SetSort("Disk");

    private void SetSort(string column)
    {
        if (DataContext is not TaskManagerWidgetViewModel vm)
        {
            return;
        }

        if (string.Equals(vm.SortColumn, column, StringComparison.Ordinal))
        {
            vm.SortDescending = !vm.SortDescending;
        }
        else
        {
            vm.SortColumn = column;
            // Numeric columns default to descending; Name defaults to ascending.
            vm.SortDescending = !string.Equals(column, "Name", StringComparison.Ordinal);
        }
    }

    private void ProcessRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not TaskManagerProcessRow row)
        {
            return;
        }

        ViewModel.SelectedRow = row;
        _contextMenuRow = row;

        var flyout = new MenuFlyout();

        var restartItem = new MenuFlyoutItem
        {
            Text = Localized.T("TaskManager.Restart"),
            IsEnabled = ViewModel is { } vm && vm.CanRestart(row),
        };
        restartItem.Click += RestartMenuItem_Click;
        flyout.Items.Add(restartItem);

        var endItem = new MenuFlyoutItem
        {
            Text = Localized.T("TaskManager.EndTask"),
            IsEnabled = ViewModel is { } vm2 && vm2.CanEndTask(row),
        };
        endItem.Click += EndTaskMenuItem_Click;
        flyout.Items.Add(endItem);

        flyout.ShowAt(element, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Right });
        e.Handled = true;
    }

    private async void EndTaskMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = _contextMenuRow;
        _contextMenuRow = null;
        if (row is null)
        {
            return;
        }

        var vm = ViewModel;
        if (vm is null || !vm.CanEndTask(row))
        {
            return;
        }

        bool confirmed = await ConfirmEndTaskAsync(row);
        if (!confirmed)
        {
            return;
        }

        var outcome = vm.EndTask(row, out var message);
        if (outcome == TaskManagerWidgetViewModel.EndTaskOutcome.Success)
        {
            _ = vm.RefreshAsync();
            return;
        }

        await ShowMessageAsync(
            Localized.T("TaskManager.EndTask.Title"),
            message ?? Localized.T("TaskManager.EndTask.ErrorUnknown"));
    }

    private async Task<bool> ConfirmEndTaskAsync(TaskManagerProcessRow row)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = Localized.T("TaskManager.EndTask.Title"),
            Content = new TextBlock
            {
                Text = string.Format(
                    Localized.T("TaskManager.EndTask.Confirm"),
                    row.Name,
                    row.InstanceCount),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 480
            },
            PrimaryButtonText = Localized.T("TaskManager.EndTask.PrimaryButton"),
            CloseButtonText = Localized.T("Common.Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void RestartMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = _contextMenuRow;
        _contextMenuRow = null;
        if (row is null)
        {
            return;
        }

        var vm = ViewModel;
        if (vm is null || !vm.CanRestart(row))
        {
            return;
        }

        bool confirmed = await ConfirmRestartAsync(row);
        if (!confirmed)
        {
            return;
        }

        var outcome = vm.Restart(row, out var message);
        if (outcome == TaskManagerWidgetViewModel.RestartOutcome.Success)
        {
            _ = vm.RefreshAsync();
            return;
        }

        await ShowMessageAsync(
            Localized.T("TaskManager.Restart.Title"),
            message ?? Localized.T("TaskManager.Restart.ErrorUnknown"));
    }

    private async Task<bool> ConfirmRestartAsync(TaskManagerProcessRow row)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = Localized.T("TaskManager.Restart.Title"),
            Content = new TextBlock
            {
                Text = string.Format(
                    Localized.T("TaskManager.Restart.Confirm"),
                    row.Name,
                    row.InstanceCount),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 480
            },
            PrimaryButtonText = Localized.T("TaskManager.Restart.PrimaryButton"),
            CloseButtonText = Localized.T("Common.Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
            CloseButtonText = Localized.T("Common.Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    // IWidgetResponsiveLayoutContent — the task manager surface has no animated
    // layout stages of its own; the host owns capsule/compact transitions.
    public void BeginResponsiveLayoutTransition(double targetContentWidth, double targetContentHeight, bool isCollapsing)
    {
    }

    public void CompleteResponsiveLayoutTransition(double finalContentWidth, double finalContentHeight)
    {
    }

    public void CancelResponsiveLayoutTransition()
    {
    }
}

