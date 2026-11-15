using System.Runtime.InteropServices;

namespace LanPod.Services;

internal static class NotificationShortcut
{
    internal static void RemoveLegacy(string shortcutPath, string appUserModelId) =>
        Marshal.ThrowExceptionForHR(LanRemoveLegacyNotificationShortcut(shortcutPath, appUserModelId));

    [DllImport("LanPod.TrayRuntime.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int LanRemoveLegacyNotificationShortcut(string shortcutPath, string appUserModelId);
}
