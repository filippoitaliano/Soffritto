using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop;

/// <summary>
/// Machine resources for the header box: system disk space and physical RAM, read again
/// every <see cref="Interval"/>.
/// </summary>
public sealed class SystemLoad : ObservableObject
{
    private const double Gigabyte = 1024d * 1024 * 1024;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private static readonly Brush Calm = Frozen(0x6D, 0xB8, 0x5B);
    private static readonly Brush Busy = Frozen(0xE8, 0xA3, 0x3D);
    private static readonly Brush Full = Frozen(0xE0, 0x53, 0x3D);

    private double diskPercent;
    private double ramPercent;
    private string diskText = "…";
    private string ramText = "…";
    private string toolTip = "Machine resources: first reading in progress";

    public static SystemLoad Current { get; } = new();

    /// <summary>The system disk, without a trailing backslash: "C:".</summary>
    public string DiskLabel { get; } = SystemDrive.TrimEnd('\\');

    public double DiskPercent { get => diskPercent; private set => SetProperty(ref diskPercent, value); }

    public double RamPercent { get => ramPercent; private set => SetProperty(ref ramPercent, value); }

    public string DiskText { get => diskText; private set => SetProperty(ref diskText, value); }

    public string RamText { get => ramText; private set => SetProperty(ref ramText, value); }

    public Brush DiskBrush => BrushOf(diskPercent);

    public Brush RamBrush => BrushOf(ramPercent);

    public string ToolTip { get => toolTip; private set => SetProperty(ref toolTip, value); }

    private static string SystemDrive => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            (double Total, double Free)? disk = null;
            try
            {
                var drive = new DriveInfo(SystemDrive);
                disk = (drive.TotalSize / Gigabyte, drive.AvailableFreeSpace / Gigabyte);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }

            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            var hasMemory = GlobalMemoryStatusEx(ref memory);

            UiThread.Run(() => Apply(disk, hasMemory ? memory : null));

            try
            {
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Apply((double Total, double Free)? disk, MemoryStatus? memory)
    {
        var lines = new List<string>();

        if (disk is { Total: > 0 } space)
        {
            DiskPercent = 100.0 * (space.Total - space.Free) / space.Total;
            DiskText = $"{space.Free:0} GB free";
            OnPropertyChanged(nameof(DiskBrush));
            lines.Add($"Disk {DiskLabel} {space.Total - space.Free:0.0} GB used, {space.Free:0.0} GB free of {space.Total:0.0} GB ({DiskPercent:0}% used)");
        }

        if (memory is { TotalPhysical: > 0 } status)
        {
            var total = status.TotalPhysical / Gigabyte;
            var free = status.AvailablePhysical / Gigabyte;
            RamPercent = 100.0 * (total - free) / total;
            RamText = $"{total - free:0.0}/{total:0} GB";
            OnPropertyChanged(nameof(RamBrush));
            lines.Add($"RAM: {total - free:0.0} GB used, {free:0.0} GB free of {total:0.0} GB ({RamPercent:0}% used)");
        }

        lines.Add($"Refreshes every {Interval.TotalSeconds:0} seconds · last reading {DateTime.Now:HH:mm:ss}");
        ToolTip = string.Join(Environment.NewLine, lines);
    }

    private static Brush BrushOf(double percent) => percent >= 90 ? Full : percent >= 70 ? Busy : Calm;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
