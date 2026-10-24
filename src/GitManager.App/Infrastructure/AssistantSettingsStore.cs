using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitManager.Agent;

namespace GitManager.App.Infrastructure;

public sealed class AssistantSettingsStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitManager", "assistant.json");
    public AssistantOptions Load()
    {
        try { return JsonSerializer.Deserialize<AssistantOptions>(File.ReadAllText(_path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(AssistantOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public interface IAssistantSecretStore
{
    string? Read(string endpoint);
    void Write(string endpoint, string secret);
    void Delete(string endpoint);
}

public sealed class WindowsAssistantSecretStore : IAssistantSecretStore
{
    private static string Target(string endpoint) => "GitManager/Assistant/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        new AssistantOptions(BaseUrl: endpoint).Endpoint.AbsoluteUri)));
    public string? Read(string endpoint)
    {
        if (!CredRead(Target(endpoint), 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Windows 凭据管理器。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            try { Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }
    public void Write(string endpoint, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > 4096) throw new ArgumentException("API Key 过长。");
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = Target(endpoint), CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = pointer, Persist = 2, UserName = "GitManager" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存到 Windows 凭据管理器。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.FreeHGlobal(pointer);
        }
    }
    public void Delete(string endpoint)
    {
        if (!CredDelete(Target(endpoint), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法删除 Windows 凭据。");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);
}
