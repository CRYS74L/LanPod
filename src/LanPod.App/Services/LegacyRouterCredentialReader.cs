using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LanPod.Services;

// Used only when importing an older installation into the portable Data folder.
internal static class LegacyRouterCredentialReader
{
    internal static string? ReadPassword(string routerAddress)
    {
        foreach (var prefix in new[] { "LanPod", "LanDeviceMonitor" })
        {
            var target = $"{prefix}.Router.{routerAddress.Trim().ToLowerInvariant()}";
            if (!CredRead(target, 1, 0, out var pointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1168) continue;
                throw new Win32Exception(error);
            }

            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
                return credential.CredentialBlob == IntPtr.Zero ? null
                    : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / sizeof(char));
            }
            finally { CredFree(pointer); }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
