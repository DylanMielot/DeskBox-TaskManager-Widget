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
    public void DefaultSort_IsCpuDescending()
    {
        var vm = CreateVm();

        Assert.Equal("CPU", vm.SortColumn);
        Assert.True(vm.SortDescending);
        Assert.Equal("▼", vm.CpuSortGlyph);
        Assert.Equal(string.Empty, vm.MemorySortGlyph);
        Assert.Equal(Visibility.Visible, vm.CpuSortIndicatorVisibility);
        Assert.Equal(Visibility.Collapsed, vm.MemorySortIndicatorVisibility);
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

        // Default sort is already CPU-descending; force a column change so
        // ApplySort actually re-runs when switching to CPU.
        vm.SortColumn = "Memory";
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

    [Theory]
    [InlineData("System")]
    [InlineData("Idle")]
    [InlineData("Registry")]
    [InlineData("smss")]
    [InlineData("csrss")]
    [InlineData("wininit")]
    [InlineData("winlogon")]
    [InlineData("services")]
    [InlineData("lsass")]
    [InlineData("dwm")]
    [InlineData("deskbox")]
    public void CanEndTask_ProtectedProcessName_ReturnsFalse(string name)
    {
        var vm = CreateVm();
        var row = MakeRow(1, name, 0, 0);

        Assert.False(vm.CanEndTask(row));
    }

    [Fact]
    public void CanEndTask_NormalProcessName_ReturnsTrue()
    {
        var vm = CreateVm();
        var row = MakeRow(1, "notepad", 0, 0);

        Assert.True(vm.CanEndTask(row));
    }

    [Fact]
    public void CanEndTask_NullRow_ReturnsFalse()
    {
        var vm = CreateVm();

        Assert.False(vm.CanEndTask(null));
    }

    [Fact]
    public void EndTask_NullRow_ReturnsFailedWithMessage()
    {
        var vm = CreateVm();

        var outcome = vm.EndTask(null, out var message);

        Assert.Equal(TaskManagerWidgetViewModel.EndTaskOutcome.Failed, outcome);
        Assert.NotNull(message);
    }

    [Fact]
    public void EndTask_ProtectedProcess_ReturnsProtectedAndNamesIt()
    {
        var vm = CreateVm();
        var row = MakeRow(1, "System", 0, 0);

        var outcome = vm.EndTask(row, out var message);

        Assert.Equal(TaskManagerWidgetViewModel.EndTaskOutcome.Protected, outcome);
        Assert.Contains("System", message!);
    }

    [Fact]
    public void EndTask_NonexistentProcess_ReturnsAlreadyExited()
    {
        var vm = CreateVm();
        var row = MakeRow(1, "__DeskBox_Nonexistent_Process__", 0, 0);

        var outcome = vm.EndTask(row, out var message);

        Assert.Equal(TaskManagerWidgetViewModel.EndTaskOutcome.AlreadyExited, outcome);
        Assert.NotNull(message);
    }

    [Fact]
    public void SelectedRow_DefaultsToNullAndCanBeSet()
    {
        var vm = CreateVm();
        var row = MakeRow(1, "notepad", 0, 0);

        Assert.Null(vm.SelectedRow);
        vm.SelectedRow = row;
        Assert.Same(row, vm.SelectedRow);
    }

    [Theory]
    [InlineData("System")]
    [InlineData("Idle")]
    [InlineData("dwm")]
    [InlineData("deskbox")]
    public void CanRestart_ProtectedProcessName_ReturnsFalse(string name)
    {
        var vm = CreateVm();
        var row = MakeRow(1, name, 0, 0);

        Assert.False(vm.CanRestart(row));
    }

    [Fact]
    public void CanRestart_NormalProcessName_ReturnsTrue()
    {
        var vm = CreateVm();
        var row = MakeRow(1, "notepad", 0, 0);

        Assert.True(vm.CanRestart(row));
    }

    [Fact]
    public void CanRestart_NullRow_ReturnsFalse()
    {
        var vm = CreateVm();

        Assert.False(vm.CanRestart(null));
    }

    [Fact]
    public void Restart_NullRow_ReturnsFailedWithMessage()
    {
        var vm = CreateVm();

        var outcome = vm.Restart(null, out var message);

        Assert.Equal(TaskManagerWidgetViewModel.RestartOutcome.Failed, outcome);
        Assert.NotNull(message);
    }

    [Fact]
    public void Restart_ProtectedProcess_ReturnsProtectedAndNamesIt()
    {
        var vm = CreateVm();
        var row = MakeRow(1, "System", 0, 0);

        var outcome = vm.Restart(row, out var message);

        Assert.Equal(TaskManagerWidgetViewModel.RestartOutcome.Protected, outcome);
        Assert.Contains("System", message!);
    }

    [Fact]
    public void Restart_NonexistentProcess_ReturnsAlreadyExited()
    {
        var vm = CreateVm();
        var row = MakeRow(1, "__DeskBox_Nonexistent_Process__", 0, 0);

        var outcome = vm.Restart(row, out var message);

        Assert.Equal(TaskManagerWidgetViewModel.RestartOutcome.AlreadyExited, outcome);
        Assert.NotNull(message);
    }
}
