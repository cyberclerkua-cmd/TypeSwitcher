using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using TypeSwitcher.Config;
using TypeSwitcher.Localization;
using Xunit;

namespace TypeSwitcher.Tests;

public sealed class BrandingTests
{
    [Fact]
    public void AssemblyNamespacesAndResourcesUseProductIdentity()
    {
        var assembly = typeof(App).Assembly;
        Assert.Equal("TypeSwitcher", assembly.GetName().Name);
        // Coverage adds its own tracker type at test time; it is not application source.
        Assert.All(assembly.GetTypes().Where(t => t.Namespace != null && !t.Namespace.StartsWith("Microsoft.")
            && !t.Namespace.StartsWith("System.") && !t.Namespace.StartsWith("Coverlet.Core.Instrumentation.")),
            type => Assert.StartsWith("TypeSwitcher", type.Namespace!));
        Assert.Contains("TypeSwitcher.Localization.en.json", assembly.GetManifestResourceNames());
        Assert.Equal("TypeSwitcher — dictionaries", L.Get("TypeSwitcher — подключённые словари", "en"));
        Assert.Equal("TypeSwitcher — підключені словники", L.Get("TypeSwitcher — подключённые словари", "uk"));
    }

    [Fact]
    public void DefaultIconPathsUpgradeWithoutChangingCustomIcons()
    {
        var cfg = new AppConfig();
        cfg.Tray.IconIdle = "assets/icon_blue.png";
        cfg.Tray.IconActive = "assets/icon_red.png";
        cfg.Normalize();
        Assert.Equal("assets/typeswitcher.png", cfg.Tray.IconIdle);
        Assert.Equal("assets/typeswitcher-action.png", cfg.Tray.IconActive);
        cfg.Tray.IconIdle = "custom/personal.png";
        cfg.Normalize();
        Assert.Equal("custom/personal.png", cfg.Tray.IconIdle);
    }

    [Fact]
    public void ApplicationIconContainsAllRequiredWindowsSizes()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TypeSwitcher.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var stream = File.OpenRead(Path.Combine(directory.FullName, "assets", "typeswitcher.ico"));
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        int count = reader.ReadUInt16();
        Assert.Equal(9, count);
        var sizes = new List<int>();
        for (int index = 0; index < count; index++)
        {
            int width = reader.ReadByte(), height = reader.ReadByte();
            sizes.Add(width == 0 ? 256 : width);
            Assert.Equal(width, height);
            reader.ReadByte(); reader.ReadByte();
            Assert.Equal(1, reader.ReadUInt16());
            Assert.Equal(32, reader.ReadUInt16());
            uint length = reader.ReadUInt32(), offset = reader.ReadUInt32();
            Assert.True(length > 8 && offset + length <= stream.Length);
            long next = stream.Position;
            stream.Position = offset;
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, reader.ReadBytes(8));
            stream.Position = next;
        }
        Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, sizes);
    }

    [Fact]
    public void UpgradeInformationUsesExplicitPaths()
    {
        using var temp = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "data"));
        string previous = Path.Combine(temp.Path, "previous", "PreviousApp.exe");
        File.WriteAllText(Path.Combine(temp.Path, "data", UpgradeHistory.FileName), JsonSerializer.Serialize(new { previous_executables = new[] { previous, "relative.exe" } }));
        Assert.Equal(new[] { previous }, UpgradeHistory.PreviousExecutables(temp.Path));
    }

    [Fact]
    public void AutostartMigrationOnlyReplacesMatchingExecutableEntries()
    {
        string subkey = @"Software\TypeSwitcher.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subkey);
            key.SetValue("PreviousApp", "\"C:\\Apps\\PreviousApp.exe\"");
            key.SetValue("Unrelated", "\"C:\\Other\\Tool.exe\"");
            AutostartManager.ApplyToKey(key, true, @"C:\Apps\TypeSwitcher.exe", [@"C:\Apps\PreviousApp.exe"]);
            Assert.Null(key.GetValue("PreviousApp"));
            Assert.Equal("\"C:\\Apps\\TypeSwitcher.exe\"", key.GetValue("TypeSwitcher"));
            Assert.NotNull(key.GetValue("Unrelated"));
            AutostartManager.ApplyToKey(key, false, @"C:\Apps\TypeSwitcher.exe", []);
            Assert.Null(key.GetValue("TypeSwitcher"));
            Assert.NotNull(key.GetValue("Unrelated"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(subkey, throwOnMissingSubKey: false); }
    }
}
