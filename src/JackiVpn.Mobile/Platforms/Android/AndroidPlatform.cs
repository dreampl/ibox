using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Net;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Notifications;
using JackiVpn.Cores.Hosting;
using JackiVpn.Security;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>
/// Which network the phone is on, for per-network memory of working servers: <c>cell:&lt;MCC-MNC&gt;</c> on mobile
/// data (e.g. 43211 = MCI), <c>wifi</c>, or <c>other</c>. No permission needed; the VPN's own network is ignored.
/// </summary>
public static class AndroidNetworkKey
{
    public static string Current(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager cm)
            {
                return "other";
            }

            var cellular = false;
#pragma warning disable CA1422 // GetAllNetworks: still the way to see the underlying network while a VPN is up
            foreach (var network in cm.GetAllNetworks())
#pragma warning restore CA1422
            {
                if (cm.GetNetworkCapabilities(network) is not { } caps || caps.HasTransport(TransportType.Vpn) || !caps.HasCapability(NetCapability.Internet))
                {
                    continue;
                }

                if (caps.HasTransport(TransportType.Wifi) || caps.HasTransport(TransportType.Ethernet))
                {
                    return "wifi";
                }

                cellular |= caps.HasTransport(TransportType.Cellular);
            }

            if (cellular && context.GetSystemService(Context.TelephonyService) is global::Android.Telephony.TelephonyManager tm && tm.NetworkOperator is { Length: >= 5 } op)
            {
                return "cell:" + op;
            }

            return cellular ? "cell" : "other";
        }
        catch (Java.Lang.Exception)
        {
            return "other";
        }
    }
}

/// <summary>
/// Secrets at rest (subscription URLs, credentials) are sealed with AES-256-GCM using a key that lives in the
/// Android Keystore (hardware-backed where available) and can never be exported. The purpose string is bound
/// as associated data, so ciphertext from one column cannot be replayed into another.
/// </summary>
public sealed class AndroidKeystoreSecretProtector : ISecretProtector
{
    private const string Alias = "jackivpn-secrets-v1";
    private const byte FormatVersion = 1;
    private readonly Lazy<IKey> _key = new(GetOrCreateKey);

    public byte[] Protect(string plaintext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, _key.Value);
        cipher.UpdateAAD(Encoding.UTF8.GetBytes("JackiVpn.v1:" + purpose));
        var data = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var ct = cipher.DoFinal(data)!;
            var iv = cipher.GetIV()!;
            var result = new byte[2 + iv.Length + ct.Length];
            result[0] = FormatVersion;
            result[1] = (byte)iv.Length;
            iv.CopyTo(result, 2);
            ct.CopyTo(result, 2 + iv.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public string Unprotect(byte[] protectedData, string purpose)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        if (protectedData.Length < 3 || protectedData[0] != FormatVersion || protectedData[1] is < 12 or > 16 || protectedData.Length < 2 + protectedData[1] + 16)
        {
            throw new CryptographicException("Unsupported protected data format.");
        }

        var ivLength = protectedData[1];
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(Javax.Crypto.CipherMode.DecryptMode, _key.Value, new GCMParameterSpec(128, protectedData, 2, ivLength));
        cipher.UpdateAAD(Encoding.UTF8.GetBytes("JackiVpn.v1:" + purpose));
        var plain = cipher.DoFinal(protectedData, 2 + ivLength, protectedData.Length - 2 - ivLength)!;
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static IKey GetOrCreateKey()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.GetKey(Alias, null) is IKey existing)
        {
            return existing;
        }

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .SetRandomizedEncryptionRequired(true)!
            .Build());
        return generator.GenerateKey()!;
    }
}

/// <summary>
/// The core is part of the signed APK and extracted by Android into the app's native library directory, which is
/// system-owned and read-only to the app (the real protection). Its SHA-256 is recorded after each install or
/// update so the shared <see cref="CoreBinaryCatalog"/> check still runs before every launch and refuses a file
/// that changed after it was hashed.
/// </summary>
public static class AndroidCores
{
    public const string SingBoxVersion = "1.14.2";
    public const string XrayVersion = "26.3.27";

    public static CoreBinaryCatalog CreateCatalog(Context context, string dataDir)
    {
        ArgumentNullException.ThrowIfNull(context);
        var libDir = context.ApplicationInfo!.NativeLibraryDir!;
        var manifestPath = Path.Combine(dataDir, "cores-manifest.json");
        var singBox = new FileInfo(Path.Combine(libDir, CoreBinaryCatalog.ExecutableName(Domain.Connection.CoreKind.SingBox)));
        var xray = new FileInfo(Path.Combine(libDir, CoreBinaryCatalog.ExecutableName(Domain.Connection.CoreKind.Xray)));
        var stampPath = manifestPath + ".stamp";
        // Re-hash after every install/update (the extracted files get a new size/timestamp).
        static string Stamp(FileInfo f) => f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "missing";
        var stamp = Stamp(singBox) + "|" + Stamp(xray);
        if (singBox.Exists && (!File.Exists(manifestPath) || !File.Exists(stampPath) || File.ReadAllText(stampPath) != stamp))
        {
            var cores = new Dictionary<string, CoreManifestEntry>
            {
                ["sing-box"] = new(SingBoxVersion, "GPL-3.0-or-later", "https://github.com/SagerNet/sing-box", [Entry(singBox)]),
            };
            if (xray.Exists)
            {
                cores["xray"] = new(XrayVersion, "MPL-2.0", "https://github.com/XTLS/Xray-core", [Entry(xray)]);
            }

            SecureFileSystem.WriteAllTextAtomic(manifestPath, JsonSerializer.Serialize(new CoreManifest(1, cores)));
            SecureFileSystem.WriteAllTextAtomic(stampPath, stamp);
        }

        return new CoreBinaryCatalog(libDir, manifestPath);
    }

    private static CoreManifestFile Entry(FileInfo binary)
    {
        using var fs = binary.OpenRead();
        return new CoreManifestFile(binary.Name, Convert.ToHexStringLower(SHA256.HashData(fs)), binary.Length);
    }
}
/// <summary>Reports default-network changes (Wi-Fi ↔ mobile data, loss, return) to the orchestrator.</summary>
public sealed class AndroidNetworkMonitor : ConnectivityManager.NetworkCallback
{
    private readonly ConnectionOrchestrator _orchestrator;
    private readonly ConnectivityManager _manager;
    private Network? _current;
    private Timer? _debounce;

    public AndroidNetworkMonitor(Context context, ConnectionOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(context);
        _orchestrator = orchestrator;
        _manager = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService)!;
    }

    public void Start() => _manager.RegisterDefaultNetworkCallback(this);

    public override void OnAvailable(Network network)
    {
        var changed = _current is not null && !_current.Equals(network);
        _current = network;
        if (changed)
        {
            Schedule(false, "network changed");
        }
    }

    public override void OnLost(Network network)
    {
        if (network.Equals(_current))
        {
            _current = null;
            Schedule(true, "network lost");
        }
    }

    private void Schedule(bool lost, string reason)
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => _orchestrator.NotifyNetworkChanged(lost, reason), null, TimeSpan.FromSeconds(1.5), Timeout.InfiniteTimeSpan);
    }
}

/// <summary>Events that matter while the app may be in the background become Android notifications.</summary>
public sealed class AndroidNotificationSink(Context context) : INotificationSink
{
    private const string ChannelId = "events";
    private int _id = 100;

    public void Show(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // The ongoing (black) VPN notification already shows connected/disconnected; a second one would only
        // make Android group and collapse them.
        if (notification.Category is Domain.Settings.NotificationCategory.Connected or Domain.Settings.NotificationCategory.Disconnected)
        {
            return;
        }

        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager is null || !manager.AreNotificationsEnabled())
        {
            return;
        }

        if (manager.GetNotificationChannel(ChannelId) is null)
        {
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Connection events", NotificationImportance.Default));
        }

        var n = new Notification.Builder(context, ChannelId)
            .SetContentTitle(notification.Title)!
            .SetContentText(notification.Message)!
            .SetSmallIcon(Resource.Drawable.ic_stat_jacki)!
            .SetAutoCancel(true)!
            .Build()!;
        manager.Notify(Interlocked.Increment(ref _id), n);
    }
}

/// <summary>Android requires the user's consent before an app may create a VPN.</summary>
public static class VpnPermission
{
    public const int RequestCode = 4242;
    private static TaskCompletionSource<bool>? _pending;

    public static Task<bool> RequestAsync(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var intent = VpnService.Prepare(activity);
        if (intent is null)
        {
            return Task.FromResult(true);
        }

        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.StartActivityForResult(intent, RequestCode);
        return _pending.Task;
    }

    public static void OnResult(int requestCode, Result resultCode)
    {
        if (requestCode == RequestCode)
        {
            _pending?.TrySetResult(resultCode == Result.Ok);
            _pending = null;
        }
    }
}
