using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;

namespace CodexUsageMonitor.Core.Services;

public sealed class CodexExecutableLocator : ICodexExecutableLocator
{
    private const string OverrideVariable = "CODEX_USAGE_MONITOR_CODEX_PATH";
    private readonly Func<string, bool> _publisherVerifier;
    private readonly string _userProfile;
    private readonly string _programFiles;

    public CodexExecutableLocator()
        : this(
            VerifyOpenAiPublisher,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
    {
    }

    internal CodexExecutableLocator(
        Func<string, bool> publisherVerifier,
        string userProfile,
        string programFiles)
    {
        _publisherVerifier = publisherVerifier;
        _userProfile = userProfile;
        _programFiles = programFiles;
    }

    public string? Locate()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var normalized = NormalizeAbsoluteLocalPath(overridePath);
            return normalized is not null && IsTrustedExecutable(normalized) ? normalized : null;
        }

        foreach (var candidate in EnumerateTrustedCandidates())
        {
            var normalized = NormalizeAbsoluteLocalPath(candidate);
            if (normalized is not null && IsTrustedExecutable(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    internal static string? NormalizeAbsoluteLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return new Uri(fullPath).IsUnc ? null : fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return null;
        }
    }

    private bool IsTrustedExecutable(string path)
        => File.Exists(path) && _publisherVerifier(path);

    private IEnumerable<string> EnumerateTrustedCandidates()
    {
        yield return Path.Combine(_userProfile, ".codex", ".sandbox-bin", "codex.exe");
        yield return Path.Combine(_userProfile, ".codex", "plugins", ".plugin-appserver", "codex.exe");

        var windowsApps = Path.Combine(_programFiles, "WindowsApps");
        string[] packages;
        try
        {
            packages = Directory.GetDirectories(windowsApps, "OpenAI.Codex_*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var package in packages.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(package, "app", "resources", "codex.exe");
        }
    }

    private static bool VerifyOpenAiPublisher(string path)
    {
        if (!OperatingSystem.IsWindows() || !VerifyAuthenticode(path))
        {
            return false;
        }

        try
        {
            using var signedCertificate = X509Certificate.CreateFromSignedFile(path);
            using var certificate = new X509Certificate2(signedCertificate);
            return certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)
                .Equals("OpenAI OpCo, LLC", StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool VerifyAuthenticode(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        var trustDataPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData(fileInfoPointer);
            trustDataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);
            var policy = WinTrustActionGenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref policy, trustDataPointer) == 0;
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustData>(trustDataPointer);
                Marshal.FreeCoTaskMem(trustDataPointer);
            }

            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeCoTaskMem(fileInfoPointer);
        }
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern uint WinVerifyTrust(IntPtr window, ref Guid actionId, IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct WinTrustFileInfo(string filePath)
    {
        public readonly uint StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        [MarshalAs(UnmanagedType.LPWStr)]
        public readonly string FilePath = filePath;
        public readonly IntPtr FileHandle = IntPtr.Zero;
        public readonly IntPtr KnownSubject = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct WinTrustData(IntPtr fileInfo)
    {
        public readonly uint StructSize = (uint)Marshal.SizeOf<WinTrustData>();
        public readonly IntPtr PolicyCallbackData = IntPtr.Zero;
        public readonly IntPtr SipClientData = IntPtr.Zero;
        public readonly uint UiChoice = 2; // WTD_UI_NONE
        public readonly uint RevocationChecks = 1; // WTD_REVOKE_WHOLECHAIN
        public readonly uint UnionChoice = 1; // WTD_CHOICE_FILE
        public readonly IntPtr FileInfo = fileInfo;
        public readonly uint StateAction = 0; // WTD_STATEACTION_IGNORE
        public readonly IntPtr StateData = IntPtr.Zero;
        [MarshalAs(UnmanagedType.LPWStr)]
        public readonly string? UrlReference = null;
        public readonly uint ProviderFlags = 0x40; // WTD_REVOCATION_CHECK_CHAIN
        public readonly uint UiContext = 0;
    }
}
