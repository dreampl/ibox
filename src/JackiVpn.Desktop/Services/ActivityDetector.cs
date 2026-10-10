using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using JackiVpn.Application.Connection;
using JackiVpn.Domain.Connection;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Detects games and streams locally (nothing leaves the machine) so the connection can stop doing optional
/// work while they run. Signals, checked every 5 s while connected:
/// <list type="bullet">
/// <item>Gaming: the foreground app is a known game or lives in a game library (Steam, Epic, Riot, EA, Ubisoft,
/// GOG, Xbox), or Windows reports an exclusive full-screen Direct3D app that is not a browser or player.</item>
/// <item>Streaming: a broadcasting app (OBS, Streamlabs, XSplit, Twitch Studio) is running, or a browser/video
/// player is in front showing full-screen video or pulling a sustained stream (≥ 1.5 Mbit/s).</item>
/// </list>
/// Changes are debounced (2 readings to enter, 30 s of silence to leave) so the profile never flaps.
/// </summary>
public sealed partial class ActivityDetector : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private const long StreamBytesPerSecond = 190 * 1024;
    private const int EnterReadings = 2;
    private const int LeaveReadings = 6;

    private static readonly string[] GameLibraries =
    [
        @"\steamapps\common\", @"\epic games\", @"\riot games\", @"\ea games\", @"\electronic arts\",
        @"\ubisoft game launcher\games\", @"\gog galaxy\games\", @"\xboxgames\", @"\rockstar games\", @"\battlestate games\",
    ];

    private static readonly HashSet<string> GameExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "valorant-win64-shipping.exe", "cs2.exe", "csgo.exe", "dota2.exe", "fortniteclient-win64-shipping.exe", "r5apex.exe", "r5apex_dx12.exe",
        "league of legends.exe", "overwatch.exe", "gta5.exe", "gta5_enhanced.exe", "rocketleague.exe", "tslgame.exe", "cod.exe",
        "robloxplayerbeta.exe", "minecraft.windows.exe", "eldenring.exe", "destiny2.exe", "rainbowsix.exe", "rainbowsix_vulkan.exe",
        "deadlock.exe", "marvel-win64-shipping.exe", "pubg.exe", "wow.exe", "hearthstone.exe", "fc25.exe", "fc26.exe", "eafc25.exe", "eafc26.exe",
    };

    private static readonly HashSet<string> BroadcastApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64", "obs32", "obs", "Streamlabs OBS", "Streamlabs Desktop", "XSplit.Core", "TwitchStudio",
    };

    private static readonly HashSet<string> VideoApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "browser.exe",
        "vlc.exe", "mpc-hc64.exe", "mpc-be64.exe", "potplayermini64.exe", "mpv.exe", "wmplayer.exe", "video.ui.exe", "stremio.exe", "kodi.exe", "plex.exe",
    };

    private readonly ConnectionOrchestrator _orchestrator;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<bool> _streamTicks = new();
    private ActivityProfile _current = ActivityProfile.Normal;
    private ActivityProfile _candidate = ActivityProfile.Normal;
    private int _candidateReadings;
    private int _silentReadings;

    public ActivityDetector(ConnectionOrchestrator orchestrator, ILogger logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>The game in front (path) and its process family (parent and children: launcher, crash handler…).</summary>
    public Action<string?, IReadOnlyList<string>>? GameChanged { get; init; }

    private string? _reportedGame;

    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var status = _orchestrator.Status;
                    var detected = status.State == ConnectionState.Connected ? Detect(status) : ActivityProfile.Normal;
                    var settled = Settle(detected);
                    if (settled != _current)
                    {
                        _current = settled;
                        _orchestrator.ReportActivity(settled);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogDebug("Activity detection failed: {Error}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private ActivityProfile Detect(ConnectionStatus status)
    {
        var sustained = SustainedStream(status.Traffic?.DownloadBytesPerSecond ?? 0);
        var foreground = ForegroundProcessPath();
        var fileName = foreground is null ? null : Path.GetFileName(foreground);
        var state = SHQueryUserNotificationState(out var s) == 0 ? s : 0;
        var isVideoApp = fileName is not null && VideoApps.Contains(fileName);

        if (foreground is not null && IsGame(foreground, fileName!))
        {
            ReportGame(foreground);
            return ActivityProfile.Gaming;
        }

        if (state == QunsRunningD3DFullScreen && !isVideoApp)
        {
            ReportGame(foreground);
            return ActivityProfile.Gaming;
        }

        if (IsBroadcasting() || (isVideoApp && (state == QunsBusy || sustained)))
        {
            return ActivityProfile.Streaming;
        }

        if (_reportedGame is not null && _silentReadings >= LeaveReadings - 1)
        {
            _reportedGame = null;
            GameChanged?.Invoke(null, []);
        }

        return ActivityProfile.Normal;
    }

    /// <summary>Enter a profile after 2 consistent readings; fall back to Normal after 30 s without signals.</summary>
    private ActivityProfile Settle(ActivityProfile detected)
    {
        if (detected == ActivityProfile.Normal)
        {
            _candidateReadings = 0;
            return ++_silentReadings >= LeaveReadings ? ActivityProfile.Normal : _current;
        }

        _silentReadings = 0;
        if (detected == _current)
        {
            return _current;
        }

        _candidateReadings = detected == _candidate ? _candidateReadings + 1 : 1;
        _candidate = detected;
        return _candidateReadings >= EnterReadings ? detected : _current;
    }

    private bool SustainedStream(long downloadBytesPerSecond)
    {
        _streamTicks.Enqueue(downloadBytesPerSecond >= StreamBytesPerSecond);
        while (_streamTicks.Count > 6)
        {
            _streamTicks.Dequeue();
        }

        return _streamTicks.Count(t => t) >= 4;
    }

    internal static bool IsGame(string path, string fileName) =>
        GameExecutables.Contains(fileName) || GameLibraries.Any(l => path.Contains(l, StringComparison.OrdinalIgnoreCase)) || Domain.Usage.TrafficCatalog.IsGame(path);

    private void ReportGame(string? path)
    {
        if (path is null || string.Equals(path, _reportedGame, StringComparison.OrdinalIgnoreCase) || GameChanged is null)
        {
            return;
        }

        _reportedGame = path;
        GameChanged(path, GameFamily(path));
    }

    /// <summary>Names of the game's parent and child processes (a launcher that started it, helpers it started).</summary>
    private static IReadOnlyList<string> GameFamily(string gamePath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = CreateToolhelp32Snapshot(0x2, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return [];
        }

        try
        {
            var entries = new List<(uint Pid, uint Parent, string Exe)>();
            var e = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32FirstW(snapshot, ref e))
            {
                do
                {
                    entries.Add((e.th32ProcessID, e.th32ParentProcessID, e.szExeFile));
                    e.dwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
                }
                while (Process32NextW(snapshot, ref e));
            }

            var game = Path.GetFileName(gamePath);
            foreach (var g in entries.Where(x => string.Equals(x.Exe, game, StringComparison.OrdinalIgnoreCase)))
            {
                if (entries.FirstOrDefault(x => x.Pid == g.Parent) is { Exe: { Length: > 0 } parent } && !parent.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(parent.ToLowerInvariant());
                }

                foreach (var child in entries.Where(x => x.Parent == g.Pid))
                {
                    names.Add(child.Exe.ToLowerInvariant());
                }
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return names.Take(32).ToList();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    private static bool IsBroadcasting()
    {
        foreach (var name in BroadcastApps)
        {
            var processes = Process.GetProcessesByName(name);
            var running = processes.Length > 0;
            foreach (var p in processes)
            {
                p.Dispose();
            }

            if (running)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ForegroundProcessPath()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int QunsBusy = 2;
    private const int QunsRunningD3DFullScreen = 3;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] buffer, ref uint size);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
