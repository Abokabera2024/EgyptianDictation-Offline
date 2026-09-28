using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using EgyptianDictation.Contracts;
using Microsoft.Win32;

namespace EgyptianDictation.Setup;

public partial class MainWindow : Window
{
    private const string AddInGuid = "{A90C9D0B-14F7-43BF-BEF9-6B2C85D1F50E}";
    private const string ProgId = "EgyptianDictation.WordAddIn";

    public MainWindow()
    {
        InitializeComponent();
        AttributionText.Text = ProductAttribution.FullNotice;
        using var terms = Assembly.GetExecutingAssembly().GetManifestResourceStream("EgyptianDictation.terms-ar.txt")
            ?? throw new InvalidOperationException("شروط الاستخدام غير موجودة في المثبّت.");
        using var reader = new StreamReader(terms, Encoding.UTF8);
        TermsText.Text = reader.ReadToEnd();
    }

    private void AcceptTerms_Changed(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = AcceptTerms.IsChecked == true;
        Status.Text = AcceptTerms.IsChecked == true ? "جاهز للتثبيت" : "وافق على الشروط للمتابعة";
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (AcceptTerms.IsChecked != true)
            return;
        InstallButton.IsEnabled = false;
        AcceptTerms.IsEnabled = false;
        Progress.IsIndeterminate = true;
        Status.Text = "جارٍ نسخ ملفات التشغيل والتحقق من النموذج...";
        try
        {
            var installRoot = GetInstallRoot();
            var restartRequired = await Task.Run(() => Install(installRoot, acceptedTerms: true));
            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            Status.Text = restartRequired
                ? "اكتمل التثبيت. أعد تشغيل Windows قبل استخدام إضافة Word."
                : "اكتمل التثبيت. ستظهر علامة «الإملاء دون إنترنت» عند فتح Word.";
            MessageBox.Show(this, restartRequired
                    ? "تم تثبيت البرنامج والمكتبات. أعد تشغيل Windows قبل استخدام إضافة Word."
                    : "تم تثبيت البرنامج وإضافة Word بنجاح.", "اكتمل التثبيت",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Progress.IsIndeterminate = false;
            Status.Text = "فشل التثبيت";
            MessageBox.Show(this, ex.Message, "خطأ في التثبيت", MessageBoxButton.OK, MessageBoxImage.Error);
            InstallButton.IsEnabled = true;
            AcceptTerms.IsEnabled = true;
        }
    }

    internal static string GetInstallRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "EgyptianDictation");

    internal static bool Install(string installRoot, bool acceptedTerms)
    {
        if (!acceptedTerms)
            throw new InvalidOperationException("يجب الموافقة على شروط الاستخدام قبل التثبيت.");
        PrerequisiteInstaller.VerifySupportedPlatform();
        EnsureNoRunningApplication(installRoot);
        Directory.CreateDirectory(installRoot);
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("EgyptianDictation.payload.zip")
            ?? throw new InvalidOperationException("ملف التثبيت الداخلي غير موجود.");
        ZipFile.ExtractToDirectory(payload, installRoot, overwriteFiles: true);
        var restartRequired = PrerequisiteInstaller.EnsureInstalled(installRoot);
        InstallModelIfIncluded(installRoot);

        var addInPath = Path.Combine(installRoot, "addin", "EgyptianDictation.WordAddIn.dll");
        if (!File.Exists(addInPath))
            throw new FileNotFoundException("ملف إضافة Word غير موجود في الحزمة.", addInPath);
        CleanupLegacyUserRegistration();
        RegisterManagedComAddIn(addInPath);
        RegisterUninstallEntry(installRoot);
        CreateStartMenuShortcut(Path.Combine(installRoot, "app", "EgyptianDictation.Host.exe"));
        return restartRequired;
    }

    internal static string VerifyEmbeddedPayload()
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("EgyptianDictation.payload.zip")
            ?? throw new InvalidOperationException("ملف التثبيت الداخلي غير موجود.");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
        var prerequisites = PrerequisiteInstaller.VerifyArchive(archive);
        var entry = archive.GetEntry($"models/{OfflineModel.FileName}");
        if (entry is null)
            return $"App-only installer; embedded payload bytes: {payload.Length}.{Environment.NewLine}{prerequisites}";

        using var stream = entry.Open();
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (entry.Length != OfflineModel.FileSize || !hash.Equals(OfflineModel.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Embedded model is corrupt. Size={entry.Length}; SHA256={hash}; " +
                $"ExpectedSize={OfflineModel.FileSize}; ExpectedSHA256={OfflineModel.Sha256}.");

        return $"OK{Environment.NewLine}{prerequisites}{Environment.NewLine}PayloadBytes={payload.Length}{Environment.NewLine}" +
               $"ModelBytes={entry.Length}{Environment.NewLine}ModelSHA256={hash}";
    }

    private static void InstallModelIfIncluded(string installRoot)
    {
        var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EgyptianDictation");
        var modelDirectory = Path.Combine(programData, "Models");
        var destination = Path.Combine(modelDirectory, OfflineModel.FileName);
        var included = Path.Combine(installRoot, "models", OfflineModel.FileName);
        Directory.CreateDirectory(modelDirectory);
        if (File.Exists(included))
        {
            ValidateModel(included);
            if (!File.Exists(destination) || !HasExpectedModelHash(destination))
            {
                File.Move(included, destination, overwrite: true);
            }
            else
            {
                File.Delete(included);
            }
        }
        if (!File.Exists(destination) || !HasExpectedModelHash(destination))
            throw new InvalidDataException(
                "نموذج الإملاء غير موجود أو تالف. استخدم حزمة التثبيت الكاملة بدل حزمة تحديث التطبيق فقط.");
        var settings = new
        {
            modelPath = destination,
            language = "ar",
            threads = 0,
            backend = "auto",
            vadPreRollMs = 250,
            vadMinimumSpeechMs = 240,
            vadEndSilenceMs = 450,
            vadBoundarySilenceMs = 160,
            targetUtteranceSeconds = 4,
            maxUtteranceSeconds = 12
        };
        File.WriteAllText(Path.Combine(programData, "engine-settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ValidateModel(string path)
    {
        var info = new FileInfo(path);
        var actualHash = ComputeSha256(path);
        if (info.Length != OfflineModel.FileSize || !actualHash.Equals(OfflineModel.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "ملف نموذج Cohere Q5_K_M المستخرج غير مكتمل أو تالف. " +
                $"الحجم الفعلي: {info.Length:N0} بايت (المتوقع: {OfflineModel.FileSize:N0}). " +
                $"البصمة الفعلية: {actualHash}. أعد نسخ ملف المثبت الكامل وتحقق من بصمته قبل تشغيله.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool HasExpectedModelHash(string path)
    {
        try
        {
            return ComputeSha256(path).Equals(OfflineModel.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void EnsureNoRunningApplication(string installRoot)
    {
        using var word = Process.GetProcessesByName("WINWORD").FirstOrDefault();
        if (word is not null)
            throw new InvalidOperationException("أغلق Microsoft Word واحفظ مستنداتك قبل التثبيت، ثم شغّل المثبت مرة أخرى.");

        var prefix = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var name in new[] { "EgyptianDictation.Host", "ArabicSTTWorker" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch { path = null; }
                    if (path?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                        throw new InvalidOperationException(
                            "أغلق برنامج الإملاء ومحرك الصوت القديمين قبل التحديث، ثم شغّل المثبت مرة أخرى.");
                }
            }
        }
    }

    private static void RegisterManagedComAddIn(string assemblyPath)
    {
        RegisterWithRegAsm(assemblyPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"), "win64");
        RegisterWithRegAsm(assemblyPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework\v4.0.30319\RegAsm.exe"), "win32");

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var office = machine.CreateSubKey($@"Software\Microsoft\Office\Word\Addins\{ProgId}")!;
            office.SetValue("FriendlyName", "الإملاء العربي والمصري دون إنترنت");
            office.SetValue("Description", "إملاء محلي بالكامل داخل Microsoft Word");
            office.SetValue("LoadBehavior", 3, RegistryValueKind.DWord);
            office.SetValue("CommandLineSafe", 1, RegistryValueKind.DWord);
        }
    }

    private static void CleanupLegacyUserRegistration()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            user.DeleteSubKeyTree($@"Software\Microsoft\Office\Word\Addins\{ProgId}", throwOnMissingSubKey: false);
            using var classes = user.OpenSubKey(@"Software\Classes", writable: true);
            classes?.DeleteSubKeyTree($@"CLSID\{AddInGuid}", throwOnMissingSubKey: false);
            classes?.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        }

        using var disabledItems = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Office\16.0\Word\Resiliency\DisabledItems", writable: true);
        if (disabledItems is null)
            return;
        foreach (var valueName in disabledItems.GetValueNames())
        {
            if (disabledItems.GetValue(valueName) is not byte[] value)
                continue;
            var text = Encoding.Unicode.GetString(value);
            if (text.Contains(ProgId, StringComparison.OrdinalIgnoreCase) ||
                text.Contains("EgyptianDictation.WordAddIn.dll", StringComparison.OrdinalIgnoreCase))
                disabledItems.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private static void RegisterWithRegAsm(string assemblyPath, string regAsmPath, string typeLibrarySuffix)
    {
        if (!File.Exists(regAsmPath))
            throw new FileNotFoundException("تعذر العثور على أداة تسجيل .NET Framework.", regAsmPath);
        var typeLibrary = Path.Combine(Path.GetDirectoryName(assemblyPath)!, $"EgyptianDictation.WordAddIn.{typeLibrarySuffix}.tlb");
        var start = new ProcessStartInfo
        {
            FileName = regAsmPath,
            Arguments = $"\"{assemblyPath}\" /codebase /tlb:\"{typeLibrary}\" /silent",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("تعذر تشغيل أداة تسجيل إضافة Word.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"فشل تسجيل إضافة Word ({typeLibrarySuffix}) برمز {process.ExitCode}.");
    }

    private static void RegisterUninstallEntry(string installRoot)
    {
        using var key = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\EgyptianDictation")!;
        key.SetValue("DisplayName", "الإملاء العربي والمصري دون إنترنت");
        key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.3.0");
        key.SetValue("Publisher", ProductAttribution.Owner);
        key.SetValue("InstallLocation", installRoot);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
    }

    private static void CreateStartMenuShortcut(string executable)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        Directory.CreateDirectory(directory);
        var shortcutPath = Path.Combine(directory, "Egyptian Dictation Offline.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = executable;
        shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
        shortcut.Description = "الإملاء العربي والمصري دون إنترنت";
        shortcut.Save();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
