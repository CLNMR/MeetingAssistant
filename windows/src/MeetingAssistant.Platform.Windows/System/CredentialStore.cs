using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using MeetingAssistant.Core.Platform;

namespace MeetingAssistant.Platform.Windows.SystemServices;

/// <see cref="ISecretStore"/> on the Windows Credential Manager (spec 02-assist-core, "API key
/// storage"; Mac: Keychain service "MeetingAssistant"). Generic credential, target
/// `MeetingAssistant/<account>` (e.g. `MeetingAssistant/aya-api-key`), user name = account,
/// secret as UTF-16, persisted for the local machine (not roamed). Saving null/empty deletes it.
public sealed class CredentialStore : ISecretStore
{
    const int CRED_TYPE_GENERIC = 1;
    const int CRED_PERSIST_LOCAL_MACHINE = 2;
    const int ERROR_NOT_FOUND = 1168;

    public static string Target(string account) => "MeetingAssistant/" + account;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public uint LastWrittenLow, LastWrittenHigh;   // FILETIME
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredRead(string target, int type, int flags, out nint credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(nint buffer);

    public string? Load(string account)
    {
        if (!CredRead(Target(account), CRED_TYPE_GENERIC, 0, out var p))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(err);
        }
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            if (c.CredentialBlob == 0 || c.CredentialBlobSize == 0) return null;
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(p); }
    }

    public void Save(string account, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (!CredDelete(Target(account), CRED_TYPE_GENERIC, 0))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ERROR_NOT_FOUND) throw new Win32Exception(err);
            }
            return;
        }
        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(account),
                Comment = $"MeetingAssistant ({account})",
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = account,
            };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);   // do not leave the secret in freed memory
            Marshal.FreeHGlobal(blob);
        }
    }
}
