using System.Collections.ObjectModel;
using System.Linq;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;

namespace DeskBox.Tests;

public sealed class TaskManagerWidgetViewModelTests
{
    private static TaskManagerWidgetViewModel CreateVm()
    {
        return new TaskManagerWidgetViewModel(
            new WidgetConfig(),
            TestServices.CreateLocalizationService(),
            new SettingsService(),
            null);
    }

    private static TaskManagerProcessRow MakeRow(int id, string name, double cpuPercent, long memoryBytes)
    {
        return new TaskManagerProcessRow(
            id,
            name,
            1,
            cpuPercent,
            memoryBytes,
            0,
            100,
            1024L * 1024 * 1024,
            10);
    }

    [Fact]
    public void DefaultSort_IsMemoryDescending()
    {
        var vm = CreateVm();

        Assert.Equal("Memory", vm.SortColumn);
        Assert.True(vm.SortDescending);
        Assert.Equal("▼", vm.MemorySortGlyph);
        Assert.Equal(string.Empty, vm.CpuSortGlyph);
        Assert.Equal(Visibility.Visible, vm.MemorySortIndicatorVisibility);
        Assert.Equal(Visibility.Collapsed, vm.CpuSortIndicatorVisibility);
    }

    [Fact]
    public void SortArrow_TracksColumnAndTogglesDirection()
    {
        var vm = CreateVm();

        vm.SortColumn = "CPU";

        Assert.Equal("CPU", vm.SortColumn);
        Assert.True(vm.SortDescending); // numeric columns default descending
        Assert.Equal("▼", vm.CpuSortGlyph);
        Assert.Equal(string.Empty, vm.MemorySortGlyph);
        Assert.Equal(Visibility.Visible, vm.CpuSortIndicatorVisibility);
        Assert.Equal(Visibility.Collapsed, vm.MemorySortIndicatorVisibility);

        vm.SortDescending = false;
        Assert.Equal("▲", vm.CpuSortGlyph);

        vm.SortColumn = "Name";

        Assert.Equal("Name", vm.SortColumn);
        Assert.False(vm.SortDescending); // Name defaults ascending
        Assert.Equal("▲", vm.NameSortGlyph);
        Assert.Equal(Visibility.Visible, vm.NameSortIndicatorVisibility);
        Assert.Equal(Visibility.Collapsed, vm.CpuSortIndicatorVisibility);
    }

    [Fact]
    public void HeaderText_ShowsGlobalPercent()
    {
        var vm = CreateVm();

        Assert.Equal("CPU 0%", vm.CpuHeaderText);
        Assert.Equal("Memory 0%", vm.MemoryHeaderText);
        Assert.Equal("Disk 0%", vm.DiskHeaderText);
        Assert.Equal("Network", vm.NetworkHeaderText);
    }

    [Fact]
    public void ApplySort_ReordersRowsByCpuDescending()
    {
        var vm = CreateVm();
        vm.Rows = new ObservableCollection<TaskManagerProcessRow>
        {
            MakeRow(1, "aaa", 10, 100),
            MakeRow(2, "bbb", 50, 200),
            MakeRow(3, "ccc", 30, 300),
        };

        vm.SortColumn = "CPU";

        Assert.Equal(new[] { "bbb", "ccc", "aaa" }, vm.Rows.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void ApplySort_ReordersRowsByNameAscending()
    {
        var vm = CreateVm();
        vm.Rows = new ObservableCollection<TaskManagerProcessRow>
        {
            MakeRow(1, "bbb", 10, 100),
            MakeRow(2, "aaa", 20, 200),
            MakeRow(3, "ccc", 30, 300),
        };

        vm.SortDescending = false;
        vm.SortColumn = "Name";

        Assert.Equal(new[] { "aaa", "bbb", "ccc" }, vm.Rows.Select(r => r.Name).ToArray());
    }
}
