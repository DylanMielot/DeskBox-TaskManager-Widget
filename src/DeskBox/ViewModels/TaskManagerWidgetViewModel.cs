using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

/// <summary>
/// View model for the Task Manager widget. Enumerates running processes and
/// displays them grouped by executable name with CPU, memory, disk and network
/// columns. CPU and memory come from <see cref="Process"/>; disk I/O is read
/// through the PDH counter API (best effort, degrades to zero when
/// unavailable); per-process network is deferred to Phase 2 (ETW).
/// </summary>
public sealed partial class TaskManagerWidgetViewModel : ObservableObject, IDisposable
{
    private const string RefreshIntervalMetadataKey = "TaskManager.RefreshIntervalSeconds";
    private const double DefaultRefreshIntervalSeconds = 2d;
    private const double MinRefreshIntervalSeconds = 1d;
    private const double MaxRefreshIntervalSeconds = 10d;

    private readonly WidgetConfig _config;
    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly DispatcherQueueTimer? _refreshTimer;
    private readonly Stopwatch _refreshStopwatch = Stopwatch.StartNew();

    private readonly Dictionary<int, TimeSpan> _previousCpuTimeByProcessId = [];
    private TimeSpan _lastCollectUtc;
    private IntPtr _pdhQuery;
    private IntPtr _pdhDiskCounter;
    private bool _pdhPrimed;
    private bool _isDisposed;
    private bool _isWindowVisible;
    private bool _isWindowRevealCompleted;

    private string _sortColumn = "Memory";
    private bool _sortDescending = true;

    [ObservableProperty]
    private ObservableCollection<TaskManagerProcessRow> _rows = [];

    [ObservableProperty]
    private string _capsuleSummary = string.Empty;

    [ObservableProperty]
    private double _aggregateCpuPercent;

    [ObservableProperty]
    private double _aggregateMemoryPercent;

    [ObservableProperty]
    private double _aggregateDiskPercent;

    [ObservableProperty]
    private bool _isCompactCollapsed;

    public TaskManagerWidgetViewModel(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        DispatcherQueue? dispatcherQueue = null)
    {
        _config = config;
        _localizationService = localizationService;
        _settingsService = settingsService;
        _dispatcherQueue = dispatcherQueue ?? TryGetCurrentDispatcherQueue();

        RefreshIntervalSeconds = ReadRefreshInterval();

        if (_dispatcherQueue is not null)
        {
            _refreshTimer = _dispatcherQueue.CreateTimer();
            _refreshTimer.Tick += RefreshTimer_Tick;
        }

        InitializePdh();
    }

    /// <summary>Refresh interval in seconds (1-10), persisted via WidgetConfig.Metadata.</summary>
    public double RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        private set => SetProperty(ref _refreshIntervalSeconds, value);
    }

    private double _refreshIntervalSeconds = DefaultRefreshIntervalSeconds;

    public string SortColumn
    {
        get => _sortColumn;
        set
        {
            if (SetProperty(ref _sortColumn, value))
            {
                ApplySort();
                OnSortColumnChanged();
            }
        }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            if (SetProperty(ref _sortDescending, value))
            {
                ApplySort();
                OnSortDirectionChanged();
            }
        }
    }

    // ─── Computed properties replacing XAML converters ───
    // WinUI 2.5.1 XAML compiler cannot resolve local converter types in
    // UserControl.Resources (WMC0060), so we expose computed properties
    // that the XAML binds to directly.

    public Visibility CompactVisibility => IsCompactCollapsed ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExpandedVisibility => IsCompactCollapsed ? Visibility.Collapsed : Visibility.Visible;

    public string CpuSortGlyph => SortColumn == "CPU" ? (SortDescending ? "▼" : "▲") : string.Empty;
    public string MemorySortGlyph => SortColumn == "Memory" ? (SortDescending ? "▼" : "▲") : string.Empty;
    public string DiskSortGlyph => SortColumn == "Disk" ? (SortDescending ? "▼" : "▲") : string.Empty;
    public string NetworkSortGlyph => SortColumn == "Network" ? (SortDescending ? "▼" : "▲") : string.Empty;

    public Visibility CpuSortIndicatorVisibility => SortColumn == "CPU" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MemorySortIndicatorVisibility => SortColumn == "Memory" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DiskSortIndicatorVisibility => SortColumn == "Disk" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NetworkSortIndicatorVisibility => SortColumn == "Network" ? Visibility.Visible : Visibility.Collapsed;

    public string CpuHeaderText => $"CPU {AggregateCpuPercent:0.#}%";
    public string MemoryHeaderText => $"Memory {AggregateMemoryPercent:0.#}%";
    public string DiskHeaderText => $"Disk {AggregateDiskPercent:0.#}%";
    public string NetworkHeaderText => "Network";

    private void OnSortColumnChanged()
    {
        OnPropertyChanged(nameof(CpuSortGlyph));
        OnPropertyChanged(nameof(MemorySortGlyph));
        OnPropertyChanged(nameof(DiskSortGlyph));
        OnPropertyChanged(nameof(NetworkSortGlyph));
        OnPropertyChanged(nameof(CpuSortIndicatorVisibility));
        OnPropertyChanged(nameof(MemorySortIndicatorVisibility));
        OnPropertyChanged(nameof(DiskSortIndicatorVisibility));
        OnPropertyChanged(nameof(NetworkSortIndicatorVisibility));
    }

    private void OnSortDirectionChanged()
    {
        OnPropertyChanged(nameof(CpuSortGlyph));
        OnPropertyChanged(nameof(MemorySortGlyph));
        OnPropertyChanged(nameof(DiskSortGlyph));
        OnPropertyChanged(nameof(NetworkSortGlyph));
    }

    public void ApplyAppearance()
    {
        // No appearance overrides specific to Task Manager today; hook kept for
        // parity with the other widget view models.
    }

    public void OnActivated()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isWindowRevealCompleted)
        {
            _ = RefreshAsync();
        }
        StartTimer();
    }

    public void OnDeactivated()
    {
        StopTimer();
    }

    /// <summary>
    /// Called when the host window becomes visible or hidden. Stops the refresh
    /// timer while hidden to keep idle CPU under 1%, restarts it when visible.
    /// </summary>
    public void OnWindowVisibilityChanged(bool visible)
    {
        if (_isDisposed)
        {
            return;
        }

        _isWindowVisible = visible;
        if (!visible)
        {
            _isWindowRevealCompleted = false;
            StopTimer();
        }
        else
        {
            StartTimer();
        }
    }

    public void OnWindowRevealCompleted()
    {
        if (_isDisposed || !_isWindowVisible || _isWindowRevealCompleted)
        {
            return;
        }

        _isWindowRevealCompleted = true;
        _ = RefreshAsync();
    }

    public void OnCompactStateChanged(bool collapsed)
    {
        if (_isDisposed || _isCompactCollapsed == collapsed)
        {
            return;
        }

        IsCompactCollapsed = collapsed;
        OnPropertyChanged(nameof(CompactVisibility));
        OnPropertyChanged(nameof(ExpandedVisibility));
        if (collapsed)
        {
            StopTimer();
        }
        else
        {
            StartTimer();
            _ = RefreshAsync();
        }
    }

    private void StartTimer()
    {
        if (_isDisposed || _refreshTimer is null || _isCompactCollapsed)
        {
            return;
        }

        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(MinRefreshIntervalSeconds, RefreshIntervalSeconds));
        _refreshTimer.Start();
    }

    private void StopTimer()
    {
        if (_refreshTimer is not null)
        {
            _refreshTimer.Stop();
        }
    }

    private void RefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed || !_isWindowVisible || _isCompactCollapsed)
        {
            return;
        }

        _ = RefreshAsync();
    }

    /// <summary>
    /// Enumerates processes, builds grouped rows, and replaces the displayed
    /// collection on the UI thread. Process enumeration runs on the thread pool
    /// so the UI thread is never stalled.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            var snapshots = await Task.Run(CollectProcessSnapshots);
            if (_isDisposed)
            {
                return;
            }

            var rows = BuildRows(snapshots);
            if (_isDisposed)
            {
                return;
            }

            if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
            {
                _dispatcherQueue.TryEnqueue(() => Rows = rows);
            }
            else
            {
                Rows = rows;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[TaskManagerWidgetViewModel] Refresh failed: {ex}");
        }
    }

    private List<ProcessSnapshot> CollectProcessSnapshots()
    {
        var snapshots = new List<ProcessSnapshot>();
        var now = _refreshStopwatch.Elapsed;
        double elapsedSeconds = _previousCpuTimeByProcessId.Count == 0
            ? 0d
            : Math.Max(now.TotalSeconds - _lastCollectUtc.TotalSeconds, 0.001);
        _lastCollectUtc = now;

        foreach (Process? process in Process.GetProcesses())
        {
            try
            {
                string name = process.ProcessName;
                if (string.Equals(name, "Idle", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TimeSpan cpuTime = process.TotalProcessorTime;
                long privateBytes = process.PrivateMemorySize64;

                string? executablePath = null;
                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch
                {
                    // Access denied or 32-bit/64-bit mismatch — icon unavailable.
                }

                _previousCpuTimeByProcessId.TryGetValue(process.Id, out TimeSpan previousCpuTime);
                _previousCpuTimeByProcessId[process.Id] = cpuTime;

                double cpuPercent = 0d;
                if (previousCpuTime > TimeSpan.Zero && elapsedSeconds > 0)
                {
                    double deltaMs = (cpuTime - previousCpuTime).TotalMilliseconds;
                    cpuPercent = deltaMs / elapsedSeconds / 1000d / Environment.ProcessorCount * 100d;
                    if (cpuPercent < 0) cpuPercent = 0;
                    if (cpuPercent > 100 * Environment.ProcessorCount)
                    {
                        cpuPercent = 100 * Environment.ProcessorCount;
                    }
                }

                snapshots.Add(new ProcessSnapshot(
                    process.Id,
                    name,
                    executablePath,
                    cpuTime,
                    privateBytes,
                    cpuPercent));
            }
            catch
            {
                // Access denied, exited, or otherwise unreadable process — skip.
                continue;
            }
            finally
            {
                process.Dispose();
            }
        }

        return snapshots;
    }

    private ObservableCollection<TaskManagerProcessRow> BuildRows(List<ProcessSnapshot> snapshots)
    {
        var diskByName = ReadDiskBytesPerSecondByProcessName();

        var groups = snapshots
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessGroup(
                g.Key,
                g.Count(),
                g.Sum(s => s.CpuPercent),
                g.Sum(s => s.PrivateBytes),
                diskByName.TryGetValue(g.Key, out double value) ? value : 0d,
                g.First().ProcessId,
                g.First().ExecutablePath))
            .ToList();

        var ordered = ApplySortTo(groups, _sortColumn, _sortDescending);

        double maxCpu = ordered.Count > 0 ? ordered.Max(g => g.CpuPercent) : 0;
        double maxMemory = ordered.Count > 0 ? ordered.Max(g => (double)g.MemoryBytes) : 1;
        double maxDisk = ordered.Count > 0 ? ordered.Max(g => g.DiskBytesPerSecond) : 0;

        double aggregateCpu = ordered.Sum(g => g.CpuPercent);
        long aggregateMemory = ordered.Sum(g => g.MemoryBytes);
        double aggregateDisk = ordered.Sum(g => g.DiskBytesPerSecond);

        var rows = ordered.Select(g => new TaskManagerProcessRow(
            g.RepresentativeId,
            g.Name,
            g.InstanceCount,
            g.CpuPercent,
            g.MemoryBytes,
            g.DiskBytesPerSecond,
            maxCpu,
            maxMemory,
            maxDisk,
            g.ExecutablePath is not null ? new Uri(g.ExecutablePath) : null)).ToList();

        UpdateCapsule(aggregateCpu, aggregateMemory, aggregateDisk, maxCpu, maxMemory, maxDisk);

        return new ObservableCollection<TaskManagerProcessRow>(rows);
    }

    private static List<ProcessGroup> ApplySortTo(List<ProcessGroup> groups, string sortColumn, bool sortDescending)
    {
        return sortColumn switch
        {
            "Name" => sortDescending
                ? groups.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "CPU" => sortDescending
                ? groups.OrderByDescending(g => g.CpuPercent).ToList()
                : groups.OrderBy(g => g.CpuPercent).ToList(),
            "Memory" => sortDescending
                ? groups.OrderByDescending(g => g.MemoryBytes).ToList()
                : groups.OrderBy(g => g.MemoryBytes).ToList(),
            "Disk" => sortDescending
                ? groups.OrderByDescending(g => g.DiskBytesPerSecond).ToList()
                : groups.OrderBy(g => g.DiskBytesPerSecond).ToList(),
            _ => groups
        };
    }

    private void ApplySort()
    {
        IEnumerable<TaskManagerProcessRow> sorted = _sortColumn switch
        {
            "Name" => _sortDescending
                ? _rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase)
                : _rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            "CPU" => _sortDescending
                ? _rows.OrderByDescending(r => r.CpuPercent)
                : _rows.OrderBy(r => r.CpuPercent),
            "Memory" => _sortDescending
                ? _rows.OrderByDescending(r => r.MemoryBytes)
                : _rows.OrderBy(r => r.MemoryBytes),
            "Disk" => _sortDescending
                ? _rows.OrderByDescending(r => r.DiskBytesPerSecond)
                : _rows.OrderBy(r => r.DiskBytesPerSecond),
            "Network" => _sortDescending
                ? _rows.OrderByDescending(r => r.NetworkBytesPerSecond)
                : _rows.OrderBy(r => r.NetworkBytesPerSecond),
            _ => _rows
        };

        Rows = new ObservableCollection<TaskManagerProcessRow>(sorted);
    }

    private void UpdateCapsule(double aggregateCpu, long aggregateMemoryBytes, double aggregateDiskBytesPerSecond,
        double maxCpu, double maxMemory, double maxDisk)
    {
        AggregateCpuPercent = aggregateCpu > 100 * Environment.ProcessorCount
            ? 100 * Environment.ProcessorCount
            : aggregateCpu;

        double totalPhysicalMemory = ReadTotalPhysicalMemoryBytes();
        double availablePhysicalMemory = ReadAvailablePhysicalMemoryBytes();
        double memoryInUseBytes = totalPhysicalMemory > availablePhysicalMemory
            ? totalPhysicalMemory - availablePhysicalMemory
            : 0;
        AggregateMemoryPercent = totalPhysicalMemory > 0
            ? Math.Clamp(memoryInUseBytes / totalPhysicalMemory * 100d, 0, 100)
            : 0;

        AggregateDiskPercent = maxDisk > 0
            ? Math.Clamp(aggregateDiskBytesPerSecond / maxDisk * 100d, 0, 100)
            : 0;

        CapsuleSummary = _localizationService.Format(
            "TaskManager.CapsuleSummary",
            Math.Round(AggregateCpuPercent),
            Math.Round(AggregateMemoryPercent),
            Math.Round(AggregateDiskPercent));

        OnPropertyChanged(nameof(CpuHeaderText));
        OnPropertyChanged(nameof(MemoryHeaderText));
        OnPropertyChanged(nameof(DiskHeaderText));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopTimer();
        if (_refreshTimer is not null)
        {
            _refreshTimer.Tick -= RefreshTimer_Tick;
            _refreshTimer.Stop();
        }

        ClosePdh();
        Rows = [];
        OnPropertyChanged(nameof(Rows));
    }

    // ─── Refresh interval persistence ───

    private double ReadRefreshInterval()
    {
        if (_config.Metadata is not null &&
            _config.Metadata.TryGetValue(RefreshIntervalMetadataKey, out string? raw) &&
            double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
        {
            return Math.Clamp(value, MinRefreshIntervalSeconds, MaxRefreshIntervalSeconds);
        }

        return DefaultRefreshIntervalSeconds;
    }

    internal static string NormalizeRefreshInterval(double value) =>
        Math.Clamp(value, MinRefreshIntervalSeconds, MaxRefreshIntervalSeconds).ToString("0.##",
            CultureInfo.InvariantCulture);

    // ─── PDH disk counters ───

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint PdhOpenQuery(IntPtr dataSource, ulong counterTypeId, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint PdhAddCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, out IntPtr bufferSize,
        out IntPtr itemCount, IntPtr itemArray);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(out MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong ExtendedVirtual;
    }

    private const uint PdhFmtDouble = 0x00000100;

    private void InitializePdh()
    {
        try
        {
            if (PdhOpenQuery(IntPtr.Zero, 0, out IntPtr query) != 0)
            {
                return;
            }

            _pdhQuery = query;
            if (PdhAddCounter(query, @"\Process(*)\IO Data Bytes/sec", IntPtr.Zero, out IntPtr counter) != 0)
            {
                ClosePdh();
                return;
            }

            _pdhDiskCounter = counter;
            // Prime the rate counter: first collect establishes the baseline.
            PdhCollectQueryData(query);
            _pdhPrimed = true;
        }
        catch
        {
            ClosePdh();
        }
    }

    private void ClosePdh()
    {
        if (_pdhQuery != IntPtr.Zero)
        {
            PdhCloseQuery(_pdhQuery);
            _pdhQuery = IntPtr.Zero;
        }

        _pdhDiskCounter = IntPtr.Zero;
        _pdhPrimed = false;
    }

    private Dictionary<string, double> ReadDiskBytesPerSecondByProcessName()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_pdhDiskCounter == IntPtr.Zero || !_pdhPrimed)
        {
            return result;
        }

        try
        {
            if (PdhCollectQueryData(_pdhQuery) != 0)
            {
                return result;
            }

            IntPtr bufferSize = IntPtr.Zero;
            IntPtr itemCount = IntPtr.Zero;
            uint status = PdhGetFormattedCounterArray(_pdhDiskCounter, PdhFmtDouble, out bufferSize, out itemCount, IntPtr.Zero);
            if (status != 0 && status != 0x800007D2u) // PDH_MORE_DATA
            {
                return result;
            }

            if (bufferSize == IntPtr.Zero || itemCount == IntPtr.Zero || (long)itemCount <= 0)
            {
                return result;
            }

            IntPtr itemArray = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = PdhGetFormattedCounterArray(_pdhDiskCounter, PdhFmtDouble, out bufferSize, out itemCount, itemArray);
                if (status != 0)
                {
                    return result;
                }

                long count = (long)itemCount;
                // PDH_FMT_COUNTERVALUE_ITEM: InstanceName(ptr), CStatus(uint),
                // pad(4 bytes), DoubleValue(double).
                int itemSize = IntPtr.Size + 8 + 8;
                for (long i = 0; i < count; i++)
                {
                    IntPtr itemPtr = itemArray + (int)(i * itemSize);
                    IntPtr instanceNamePtr = Marshal.ReadIntPtr(itemPtr);
                    uint statusField = (uint)Marshal.ReadInt32(itemPtr, IntPtr.Size);
                    byte[] valueBytes = new byte[8];
                    Marshal.Copy(itemPtr + IntPtr.Size + 8, valueBytes, 0, 8);
                    double value = BitConverter.ToDouble(valueBytes, 0);
                    if (statusField != 0 || instanceNamePtr == IntPtr.Zero)
                    {
                        continue;
                    }

                    string instance = Marshal.PtrToStringUni(instanceNamePtr) ?? string.Empty;
                    if (string.IsNullOrEmpty(instance) || string.Equals(instance, "_Total", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int hash = instance.IndexOf('#');
                    string baseName = hash >= 0 ? instance[..hash] : instance;

                    result.TryGetValue(baseName, out double existing);
                    result[baseName] = existing + value;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(itemArray);
            }
        }
        catch
        {
            // PDH is best effort — disk falls back to zero for all rows.
        }

        return result;
    }

    private static double ReadTotalPhysicalMemoryBytes()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(out status))
            {
                return (double)status.TotalPhysical;
            }
        }
        catch
        {
            // Best effort.
        }

        return 0;
    }

    private static double ReadAvailablePhysicalMemoryBytes()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(out status))
            {
                return (double)status.AvailablePhysical;
            }
        }
        catch
        {
            // Best effort.
        }

        return 0;
    }

    private static DispatcherQueue? TryGetCurrentDispatcherQueue()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}

public sealed class ProcessSnapshot
{
    public int ProcessId { get; }
    public string Name { get; }
    public string? ExecutablePath { get; }
    public TimeSpan CpuTime { get; }
    public long PrivateBytes { get; }
    public double CpuPercent { get; }

    public ProcessSnapshot(int processId, string name, string? executablePath, TimeSpan cpuTime, long privateBytes, double cpuPercent)
    {
        ProcessId = processId;
        Name = name;
        ExecutablePath = executablePath;
        CpuTime = cpuTime;
        PrivateBytes = privateBytes;
        CpuPercent = cpuPercent;
    }
}

public sealed class TaskManagerProcessRow
{
    public int RepresentativeProcessId { get; }
    public string Name { get; }
    public int InstanceCount { get; }
    public double CpuPercent { get; }
    public long MemoryBytes { get; }
    public double DiskBytesPerSecond { get; }
    public double NetworkBytesPerSecond { get; } // Phase 2: per-process network via ETW.
    public Uri? IconUri { get; } // Executable icon, best effort.

    public string CpuText => $"{CpuPercent:0.#}%";
    public string MemoryText => MemoryBytes >= 1024L * 1024 * 1024
        ? $"{MemoryBytes / (1024.0 * 1024 * 1024):0.#} GB"
        : $"{MemoryBytes / (1024.0 * 1024):0.#} MB";
    public string DiskText => DiskBytesPerSecond >= 1024 * 1024
        ? $"{DiskBytesPerSecond / (1024 * 1024):0.#} MB/s"
        : $"{DiskBytesPerSecond:0.#} B/s";
    public string NetworkText => "n/a"; // Phase 2.

    public string InstanceCountText => InstanceCount == 1 ? "1 process" : $"{InstanceCount} processes";

    public double CpuIntensity { get; }
    public double MemoryIntensity { get; }
    public double DiskIntensity { get; }
    public double NetworkIntensity => 0;

    public TaskManagerProcessRow(
        int representativeProcessId,
        string name,
        int instanceCount,
        double cpuPercent,
        long memoryBytes,
        double diskBytesPerSecond,
        double maxCpu,
        double maxMemory,
        double maxDisk,
        Uri? iconUri = null)
    {
        RepresentativeProcessId = representativeProcessId;
        Name = name;
        InstanceCount = instanceCount;
        CpuPercent = cpuPercent;
        MemoryBytes = memoryBytes;
        DiskBytesPerSecond = diskBytesPerSecond;
        IconUri = iconUri;

        CpuIntensity = maxCpu > 0 ? Math.Clamp(cpuPercent / maxCpu, 0d, 1d) : 0;
        MemoryIntensity = maxMemory > 0 ? Math.Clamp((double)memoryBytes / maxMemory, 0d, 1d) : 0;
        DiskIntensity = maxDisk > 0 ? Math.Clamp(diskBytesPerSecond / maxDisk, 0d, 1d) : 0;
    }
}

internal sealed record ProcessGroup(
    string Name,
    int InstanceCount,
    double CpuPercent,
    long MemoryBytes,
    double DiskBytesPerSecond,
    int RepresentativeId,
    string? ExecutablePath);
