using Android.App;
using Android.Runtime;
using JackiVpn.Mobile.Services;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Mobile;

[Application]
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    /// <summary>Written when the app crashes; offered for sharing on the next start (redacted, never sent automatically).</summary>
    public static string CrashFile => Path.Combine(Android.App.Application.Context.FilesDir!.AbsolutePath, "last-crash.txt");

    public override void OnCreate()
    {
        // Last line of defence: a crash ends the process and with it the VPN. A managed exception that reaches the
        // UI thread (e.g. from an async command) is logged (redacted) and the app keeps running; the connection
        // state machine is unaffected because it never runs on these callbacks.
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            if (e.Exception is OutOfMemoryException)
            {
                return;
            }

            MobileServices.Instance?.Logger.LogError(e.Exception, "Unhandled UI exception: {Message}", SecretRedactor.Redact(e.Exception.Message));
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            MobileServices.Instance?.Logger.LogWarning("Unobserved task exception: {Message}", SecretRedactor.Redact(e.Exception.GetBaseException().Message));
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash("managed", e.ExceptionObject?.ToString());

        // Java-side crashes (services, receivers, widgets) are recorded too, then handed to Android as usual.
        var previous = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
        Java.Lang.Thread.DefaultUncaughtExceptionHandler = new CrashHandler(previous);
        base.OnCreate();
    }

    internal static void WriteCrash(string kind, string? details)
    {
        try
        {
            var text = $"IBOX {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString}) crash ({kind}) at {DateTimeOffset.UtcNow:u}\n" +
                       $"Device: {Android.OS.Build.Manufacturer} {Android.OS.Build.Model}, Android {Android.OS.Build.VERSION.Release} (API {(int)Android.OS.Build.VERSION.SdkInt}), ABI {Android.OS.Build.SupportedAbis?.FirstOrDefault()}\n\n" +
                       SecretRedactor.Redact(details ?? "(no details)");
            File.WriteAllText(CrashFile, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    private sealed class CrashHandler(Java.Lang.Thread.IUncaughtExceptionHandler? previous) : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
    {
        public void UncaughtException(Java.Lang.Thread t, Java.Lang.Throwable e)
        {
            WriteCrash("java:" + t.Name, Android.Util.Log.GetStackTraceString(e));
            previous?.UncaughtException(t, e);
        }
    }
}
