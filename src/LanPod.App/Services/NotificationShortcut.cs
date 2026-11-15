using System.IO;
using System.Runtime.InteropServices;

namespace LanPod.Services;

internal static class NotificationShortcut
{
    internal static void Ensure(
        string shortcutPath,
        string executablePath,
        string appUserModelId,
        Guid activatorClsid)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        Marshal.ThrowExceptionForHR(LanEnsureNotificationShortcut(
            shortcutPath,
            executablePath,
            Path.GetDirectoryName(executablePath)!,
            appUserModelId,
            in activatorClsid));
    }

    [DllImport("LanPod.TrayRuntime.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int LanEnsureNotificationShortcut(
        string shortcutPath,
        string executablePath,
        string workingDirectory,
        string appUserModelId,
        in Guid activatorClsid);
}
