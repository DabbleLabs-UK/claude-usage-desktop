using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClaudeUsage.Services;

// Reads Claude Code's OAuth credential from the Windows Credential Manager. Claude Code 2.1.x moved
// OAuth storage OFF disk (~/.claude/.credentials.json) into the OS keystore via keytar, which on
// Windows is the classic Credential Manager (CredWriteW/CredReadW, CRED_TYPE_GENERIC). keytar stores
// the secret under a target of the form "<service>/<account>"; Claude Code stores the SAME JSON it
// used to write to the file: { "claudeAiOauth": { "accessToken", "refreshToken", "expiresAt", ... } }.
//
// We match by BLOB CONTENT, not target name: the keytar service/account naming has varied across
// versions and may not contain "claude" at all (so filtering by target name misses it). Every
// generic credential's blob is decoded and we return the first that parses as the Claude OAuth
// JSON. The blob's text encoding has also varied between keytar builds (UTF-8 vs UTF-16LE), so we
// try both and keep whichever parses.
//
// Windows-only; returns null on any other OS, on a P/Invoke error, or when no matching entry exists
// (the caller then falls back to the on-disk file).
internal static class WindowsCredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const int  CRED_ENUMERATE_ALL_CREDENTIALS = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint   Flags;
        public uint   Type;
        public IntPtr TargetName;          // LPWSTR
        public IntPtr Comment;             // LPWSTR
        public long   LastWritten;         // FILETIME (8 bytes)
        public uint   CredentialBlobSize;  // bytes
        public IntPtr CredentialBlob;      // LPBYTE
        public uint   Persist;
        public uint   AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;         // LPWSTR
        public IntPtr UserName;            // LPWSTR
    }

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string? filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    // Returns the raw credential JSON string for Claude Code's keystore entry (and the target name
    // it was found under, for diagnostics), or null if none.
    public static string? TryReadClaudeCredentialJson(out string? matchedTarget)
    {
        matchedTarget = null;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // Enumerate ALL credentials (filter must be null when the ALL flag is set) and match by
            // blob content in managed code -- the native filter only does target-name prefix
            // matching, which can't find an entry whose name doesn't contain "claude".
            if (!CredEnumerate(null, CRED_ENUMERATE_ALL_CREDENTIALS, out var count, out var pCreds))
                return null;
            try
            {
                var ptrSize = IntPtr.Size;
                for (int i = 0; i < count; i++)
                {
                    var entryPtr = Marshal.ReadIntPtr(pCreds, i * ptrSize);
                    if (entryPtr == IntPtr.Zero) continue;
                    var cred = Marshal.PtrToStructure<CREDENTIAL>(entryPtr);
                    if (cred.Type != CRED_TYPE_GENERIC) continue;

                    // DecodeBlob returns non-null only when the blob is Claude's OAuth JSON, so this
                    // identifies the entry by content regardless of its target name.
                    var json = DecodeBlob(cred.CredentialBlob, cred.CredentialBlobSize);
                    if (json is not null)
                    {
                        matchedTarget = cred.TargetName != IntPtr.Zero
                            ? Marshal.PtrToStringUni(cred.TargetName)
                            : null;
                        return json;
                    }
                }
            }
            finally { CredFree(pCreds); }
        }
        catch
        {
            // Any marshalling / P-Invoke failure -> treat as "not found" and let the caller fall back.
        }
        return null;
    }

    // Decodes the credential blob to text, returning it only if it parses as Claude's OAuth JSON.
    // keytar builds have written the password as either UTF-8 or UTF-16LE, so we try both.
    private static string? DecodeBlob(IntPtr blob, uint size)
    {
        if (blob == IntPtr.Zero || size == 0) return null;
        var bytes = new byte[size];
        Marshal.Copy(blob, bytes, 0, (int)size);

        var utf16 = Encoding.Unicode.GetString(bytes);
        if (LooksLikeClaudeOauth(utf16)) return utf16;

        var utf8 = Encoding.UTF8.GetString(bytes);
        if (LooksLikeClaudeOauth(utf8)) return utf8;

        return null;
    }

    private static bool LooksLikeClaudeOauth(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.IndexOf("claudeAiOauth", StringComparison.Ordinal) < 0)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.TryGetProperty("accessToken", out var token)
                && token.ValueKind == JsonValueKind.String;
        }
        catch { return false; }
    }
}
