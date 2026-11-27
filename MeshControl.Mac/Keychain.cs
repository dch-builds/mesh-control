using System.Runtime.InteropServices;
using System.Text;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Secrets (mot de passe, cookie de second facteur) dans le Trousseau macOS —
/// l'équivalent de DPAPI dans la version Windows.
///
/// Prototype : passe par l'API « generic password » historique de
/// Security.framework, simple à appeler sans liaison Objective-C. Elle est
/// dépréciée au profit de SecItemAdd / SecItemCopyMatching, vers lesquelles il
/// faudra migrer avant diffusion (elles exigent des dictionnaires CoreFoundation).
///
/// Sous Windows (développement, ou future version unique), les secrets sont
/// chiffrés par DPAPI pour la session de l'utilisateur, comme dans la version
/// WPF : voir <see cref="WindowsSecrets"/>.
/// </summary>
public static class Keychain
{
    private const string Service = "Mesh Control";

    public static bool IsAvailable => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    public static string? Read(string account)
    {
        if (OperatingSystem.IsWindows()) return WindowsSecrets.Read(account);
        if (!IsAvailable) return null;
        try
        {
            var service = Encoding.UTF8.GetBytes(Service);
            var acct = Encoding.UTF8.GetBytes(account);
            int status = Native.SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)service.Length, service, (uint)acct.Length, acct,
                out uint length, out IntPtr data, out IntPtr item);
            if (status != 0) return null;
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                Native.SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero) Native.CFRelease(item);
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Lecture du Trousseau impossible : {ex.Message}", "Trousseau");
            return null;
        }
    }

    public static void Write(string account, string secret)
    {
        if (OperatingSystem.IsWindows()) { WindowsSecrets.Write(account, secret); return; }
        if (!IsAvailable) return;
        try
        {
            var service = Encoding.UTF8.GetBytes(Service);
            var acct = Encoding.UTF8.GetBytes(account);
            var data = Encoding.UTF8.GetBytes(secret);

            int status = Native.SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)service.Length, service, (uint)acct.Length, acct,
                out uint oldLength, out IntPtr oldData, out IntPtr item);
            if (status == 0)
            {
                if (oldData != IntPtr.Zero) Native.SecKeychainItemFreeContent(IntPtr.Zero, oldData);
                try
                {
                    status = Native.SecKeychainItemModifyAttributesAndData(
                        item, IntPtr.Zero, (uint)data.Length, data);
                }
                finally
                {
                    if (item != IntPtr.Zero) Native.CFRelease(item);
                }
            }
            else
            {
                status = Native.SecKeychainAddGenericPassword(
                    IntPtr.Zero, (uint)service.Length, service, (uint)acct.Length, acct,
                    (uint)data.Length, data, out IntPtr created);
                if (created != IntPtr.Zero) Native.CFRelease(created);
            }

            if (status != 0) Log.Warning($"Écriture dans le Trousseau refusée (code {status}).", "Trousseau");
        }
        catch (Exception ex)
        {
            Log.Warning($"Écriture dans le Trousseau impossible : {ex.Message}", "Trousseau");
        }
    }

    public static void Delete(string account)
    {
        if (OperatingSystem.IsWindows()) { WindowsSecrets.Write(account, null); return; }
        if (!IsAvailable) return;
        try
        {
            var service = Encoding.UTF8.GetBytes(Service);
            var acct = Encoding.UTF8.GetBytes(account);
            int status = Native.SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)service.Length, service, (uint)acct.Length, acct,
                out _, out IntPtr data, out IntPtr item);
            if (status != 0) return;
            if (data != IntPtr.Zero) Native.SecKeychainItemFreeContent(IntPtr.Zero, data);
            try { Native.SecKeychainItemDelete(item); }
            finally { if (item != IntPtr.Zero) Native.CFRelease(item); }
        }
        catch (Exception ex)
        {
            Log.Warning($"Suppression dans le Trousseau impossible : {ex.Message}", "Trousseau");
        }
    }

    private static class Native
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(Security)]
        public static extern int SecKeychainFindGenericPassword(
            IntPtr keychainOrArray,
            uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName,
            out uint passwordLength, out IntPtr passwordData,
            out IntPtr itemRef);

        [DllImport(Security)]
        public static extern int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName,
            uint passwordLength, byte[] passwordData,
            out IntPtr itemRef);

        [DllImport(Security)]
        public static extern int SecKeychainItemModifyAttributesAndData(
            IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

        [DllImport(Security)]
        public static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(Security)]
        public static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundation)]
        public static extern void CFRelease(IntPtr cf);
    }

    /// <summary>
    /// Secrets chiffrés par DPAPI (portée utilisateur), dans un fichier à part
    /// des réglages : déchiffrables seulement par la session Windows qui les a
    /// écrits.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static class WindowsSecrets
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mesh Control", "secrets-mac.json");

        private static Dictionary<string, string> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(FilePath)) ?? new();
            }
            catch { }
            return new();
        }

        public static string? Read(string account)
        {
            try
            {
                if (!Load().TryGetValue(account, out var protectedValue)) return null;
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(protectedValue), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }

        public static void Write(string account, string? secret)
        {
            try
            {
                var all = Load();
                if (secret is null) all.Remove(account);
                else
                    all[account] = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                        Encoding.UTF8.GetBytes(secret), null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser));
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(all));
            }
            catch (Exception ex)
            {
                Log.Warning($"Enregistrement du secret impossible : {ex.Message}", "Trousseau");
            }
        }
    }
}
