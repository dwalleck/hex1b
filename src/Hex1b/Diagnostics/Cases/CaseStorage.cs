using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Creates a case's owner-only directory and files. An existing root must already be owner-only: it is
/// refused, never repaired, when it is a link, not a directory, or accessible to group or others.
/// </summary>
internal static class CaseStorage
{
    internal const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The default root, <c>~/.hex1b/cases</c>.</summary>
    internal static string DefaultRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(home, ".hex1b", "cases");
    }

    /// <summary>Checks the root without changing anything; returns why it is refused, or null.</summary>
    internal static string? CheckRoot(string root)
    {
        var info = new DirectoryInfo(root);
        if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            return $"The case directory root is a link: '{root}'.";
        if (File.Exists(root))
            return $"The case directory root is not a directory: '{root}'.";
        if (info.Exists && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(root) & ~DirectoryMode) != 0)
            return $"The case directory root is accessible to group or others: '{root}'.";
        return null;
    }

    /// <summary>Creates the root if absent and the case's own directory, both owner-only and verified.</summary>
    internal static string CreateCaseDirectory(string root, string caseId)
    {
        if (CheckRoot(root) is { } refused)
            throw new UnauthorizedAccessException(refused);
        CreateOwnerOnlyDirectory(root);
        var path = Path.Combine(root, caseId);
        if (Path.Exists(path))
            throw new IOException($"The case directory already exists: '{path}'.");
        CreateOwnerOnlyDirectory(path);
        return path;
    }

    /// <summary>Creates a new owner-only file, failing if it exists.</summary>
    internal static FileStream CreateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = FileMode;
        var stream = new FileStream(path, options);
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != FileMode)
        {
            stream.Dispose();
            throw new IOException($"Could not restrict case file '{path}'.");
        }
        return stream;
    }

    private static void CreateOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateWindowsDirectory(path);
            return;
        }

        var existed = Directory.Exists(path);
        Directory.CreateDirectory(path, DirectoryMode);
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
            throw new IOException($"The case directory is a link: '{path}'.");
        // A directory that existed keeps its own mode (P4); only an owner-only one is accepted.
        if (File.GetUnixFileMode(path) != DirectoryMode)
            throw new UnauthorizedAccessException(existed
                ? $"The case directory is accessible to group or others: '{path}'."
                : $"Could not restrict case directory '{path}'.");
    }

    // Mirrors WindowsPtySocketPaths' owner-only DACL (not shared: that helper repairs an existing
    // directory, while a case refuses one it did not create owner-only).
    [SupportedOSPlatform("windows")]
    private static void CreateWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            var rules = directory.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            if (rules.Count != 1 || rules[0] is not FileSystemAccessRule only || !only.IdentityReference.Equals(user))
                throw new UnauthorizedAccessException($"The case directory is accessible to others: '{path}'.");
            return;
        }
        directory.Create(security);
    }
}
