using DeskBox.Contracts;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;

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
        set => DataContext = value;
    }

    private void NameHeader_Click(object sender, RoutedEventArgs e) => SetSort("Name");
    private void CpuHeader_Click(object sender, RoutedEventArgs e) => SetSort("CPU");
    private void MemoryHeader_Click(object sender, RoutedEventArgs e) => SetSort("Memory");
    private void DiskHeader_Click(object sender, RoutedEventArgs e) => SetSort("Disk");
    private void NetworkHeader_Click(object sender, RoutedEventArgs e) => SetSort("Network");

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

