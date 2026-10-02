using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StickyNotes.Infrastructure;

namespace StickyNotes.Tests;

[TestClass]
public class PortableModeAndPathTests
{
    private string _testDir = null!;
    private string? _originalOverride;
    private string? _originalBaseDir;
    private string? _originalEnvVar;

    [TestInitialize]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"StickyNotes_PathTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        _originalOverride = AppPaths.DataDirOverride;
        _originalBaseDir = AppPaths.AppBaseDirectory;
        _originalEnvVar = Environment.GetEnvironmentVariable(AppPaths.DataDirEnvVarName);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // 恢复所有全局重定向与环境变量，确保其他测试套件隔离不受影响
        AppPaths.DataDirOverride = _originalOverride;
        AppPaths.AppBaseDirectory = _originalBaseDir!;
        Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVarName, _originalEnvVar);

        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [TestMethod]
    public void WhenPortableIniAbsent_ShouldUseRoamingDataDirectory()
    {
        AppPaths.DataDirOverride = null;
        AppPaths.AppBaseDirectory = _testDir;
        Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVarName, null);

        Assert.IsFalse(File.Exists(AppPaths.PortableFlagPath));
        Assert.IsFalse(AppPaths.IsPortableMode);
        Assert.AreEqual(AppPaths.RoamingDataDirectory, AppPaths.DataDirectory);
        Assert.AreEqual("标准漫游模式 (%LOCALAPPDATA%)", AppPaths.DeploymentModeDescription);
    }

    [TestMethod]
    public void WhenPortableIniPresent_ShouldActivatePortableModeAndUseAppData()
    {
        AppPaths.DataDirOverride = null;
        AppPaths.AppBaseDirectory = _testDir;
        Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVarName, null);

        // 创建 portable.ini 标记文件
        File.WriteAllText(AppPaths.PortableFlagPath, "# portable mode");

        Assert.IsTrue(File.Exists(AppPaths.PortableFlagPath));
        Assert.IsTrue(AppPaths.IsPortableMode);

        var expectedAppData = Path.Combine(_testDir, "app_data");
        Assert.AreEqual(expectedAppData, AppPaths.DataDirectory);
        Assert.AreEqual("便携绿化模式 (app_data)", AppPaths.DeploymentModeDescription);

        // 验证拓扑派生路径
        Assert.AreEqual(Path.Combine(expectedAppData, "notes.db"), AppPaths.DatabasePath);
        Assert.AreEqual(Path.Combine(expectedAppData, "settings.json"), AppPaths.SettingsPath);
        Assert.AreEqual(Path.Combine(expectedAppData, "window.json"), AppPaths.WindowConfigPath);

        // 访问子目录并验证物理目录已创建
        Assert.AreEqual(Path.Combine(expectedAppData, "backups"), AppPaths.BackupsDirectory);
        Assert.IsTrue(Directory.Exists(AppPaths.BackupsDirectory));

        Assert.AreEqual(Path.Combine(expectedAppData, "logs"), AppPaths.LogsDirectory);
        Assert.IsTrue(Directory.Exists(AppPaths.LogsDirectory));
    }

    [TestMethod]
    public void EnvironmentVariable_ShouldOverridePortableAndRoaming()
    {
        AppPaths.DataDirOverride = null;
        AppPaths.AppBaseDirectory = _testDir;

        // 即使同级存在 portable.ini
        File.WriteAllText(AppPaths.PortableFlagPath, "# portable");

        var customEnvDir = Path.Combine(_testDir, "custom_env_data");
        Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVarName, customEnvDir);

        Assert.AreEqual(customEnvDir, AppPaths.DataDirectory);
        Assert.AreEqual("环境变量模式 (STICKYNOTES_DATA_DIR)", AppPaths.DeploymentModeDescription);
        Assert.IsTrue(Directory.Exists(customEnvDir));
    }

    [TestMethod]
    public void DataDirOverride_ShouldHaveHighestPriority()
    {
        AppPaths.AppBaseDirectory = _testDir;
        File.WriteAllText(AppPaths.PortableFlagPath, "# portable");

        var customEnvDir = Path.Combine(_testDir, "custom_env_data");
        Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVarName, customEnvDir);

        var explicitOverrideDir = Path.Combine(_testDir, "override_data");
        AppPaths.DataDirOverride = explicitOverrideDir;

        Assert.AreEqual(explicitOverrideDir, AppPaths.DataDirectory);
        Assert.AreEqual("手动覆盖模式 (Override)", AppPaths.DeploymentModeDescription);
        Assert.IsTrue(Directory.Exists(explicitOverrideDir));
    }
}
