using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace EgyptianDictation.Setup;

internal static class PrerequisiteInstaller
{
    private const string ManifestResource = "EgyptianDictation.prerequisites.json";
    private const string VisualCppRegistryPath = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64";
    private const string FrameworkRegistryPath = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full";

    internal static void VerifySupportedPlatform()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) ||
            RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(
                "تتطلب هذه النسخة Windows 10 أو 11 بنظام ومعالج x64. الأجهزة x86 وARM وWindows الأقدم غير مدعومة.");
    }

    internal static bool EnsureInstalled(string installRoot)
    {
        VerifySupportedPlatform();
        var manifest = LoadManifest();
        var directory = Path.Combine(installRoot, "prerequisites");
        var restartRequired = false;

        var requiredVisualCpp = Version.Parse(manifest.VisualCppX64.MinimumVersion);
        if (GetInstalledVisualCppVersion() is not { } installedVisualCpp || installedVisualCpp < requiredVisualCpp)
        {
            var executable = VerifyFile(directory, manifest.VisualCppX64);
            var exitCode = Run(executable, "/install /quiet /norestart");
            restartRequired |= IsRestartRequired(exitCode);
            var afterInstall = GetInstalledVisualCppVersion();
            if (!IsSuccessful(exitCode) || afterInstall is null || afterInstall < requiredVisualCpp)
                throw new InvalidOperationException(
                    $"تعذر تثبيت Microsoft Visual C++ x64 (رمز {exitCode}). أعد التشغيل إن طُلب ثم شغّل المثبت مرة أخرى.");
        }

        if (GetInstalledFrameworkRelease() < manifest.NetFramework48.MinimumRelease)
        {
            var executable = VerifyFile(directory, manifest.NetFramework48);
            var exitCode = Run(executable, "/q /norestart /ChainingPackage EgyptianDictation");
            restartRequired |= IsRestartRequired(exitCode);
            if (!IsSuccessful(exitCode) || GetInstalledFrameworkRelease() < manifest.NetFramework48.MinimumRelease)
                throw new InvalidOperationException(
                    $"تعذر تجهيز .NET Framework 4.8 لإضافة Word (رمز {exitCode}). أعد التشغيل ثم شغّل المثبت مرة أخرى.");
        }

        return restartRequired;
    }

    internal static string VerifyArchive(ZipArchive archive)
    {
        var manifest = LoadManifest();
        VerifyArchiveFile(archive, manifest.VisualCppX64);
        VerifyArchiveFile(archive, manifest.NetFramework48);
        return $"VC++ x64={manifest.VisualCppX64.MinimumVersion}; .NET Framework 4.8=embedded and verified.";
    }

    private static PackageManifest LoadManifest()
    {
        using var stream = typeof(PrerequisiteInstaller).Assembly.GetManifestResourceStream(ManifestResource)
            ?? throw new InvalidDataException("بيان المكتبات المطلوبة غير موجود داخل المثبت.");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest?.VisualCppX64 is null || manifest.NetFramework48 is null)
            throw new InvalidDataException("بيان المكتبات المطلوبة غير صالح.");
        return manifest;
    }

    private static string VerifyFile(string directory, PrerequisitePackage package)
    {
        var path = Path.Combine(directory, package.FileName);
        if (!File.Exists(path) || !HashMatches(File.OpenRead(path), package.Sha256))
            throw new InvalidDataException($"ملف المكتبة المرفقة غير موجود أو تالف: {package.FileName}");
        return path;
    }

    private static void VerifyArchiveFile(ZipArchive archive, PrerequisitePackage package)
    {
        var entry = archive.GetEntry($"prerequisites/{package.FileName}")
            ?? throw new InvalidDataException($"ملف المكتبة غير موجود داخل المثبت: {package.FileName}");
        if (!HashMatches(entry.Open(), package.Sha256))
            throw new InvalidDataException($"ملف المكتبة تالف داخل المثبت: {package.FileName}");
    }

    private static bool HashMatches(Stream stream, string expected)
    {
        using (stream)
            return Convert.ToHexString(SHA256.HashData(stream))
                .Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static Version? GetInstalledVisualCppVersion()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(VisualCppRegistryPath);
        if (key?.GetValue("Installed") is not int installed || installed != 1)
            return null;
        var text = key.GetValue("Version")?.ToString()?.TrimStart('v', 'V');
        return Version.TryParse(text, out var version) ? version : null;
    }

    private static int GetInstalledFrameworkRelease()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(FrameworkRegistryPath);
        return key?.GetValue("Release") is int release ? release : 0;
    }

    private static int Run(string executable, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"تعذر تشغيل المثبت المرفق: {Path.GetFileName(executable)}");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static bool IsSuccessful(int exitCode) => exitCode is 0 or 3010 or 1641;
    private static bool IsRestartRequired(int exitCode) => exitCode is 3010 or 1641;

    private sealed class PackageManifest
    {
        public PrerequisitePackage VisualCppX64 { get; set; } = null!;
        public PrerequisitePackage NetFramework48 { get; set; } = null!;
    }

    private sealed class PrerequisitePackage
    {
        public string FileName { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string MinimumVersion { get; set; } = string.Empty;
        public int MinimumRelease { get; set; }
    }
}
