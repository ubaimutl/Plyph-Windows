using Plyph.Core;
using Xunit;

namespace Plyph.Core.Tests;

public sealed class StorageModeTests
{
    [Fact]
    public void Resolve_Detects_Portable_Mode_From_Cli_Flag_ExecutableName_Or_Marker_File()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"plyph-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. Default without marker -> Installed
            var (mode1, _) = StorageModeResolver.Resolve(Array.Empty<string>(), tempDir, "Plyph");
            Assert.Equal(StorageMode.Installed, mode1);

            // 2. CLI flag --portable -> Portable
            var (mode2, dir2) = StorageModeResolver.Resolve(new[] { "--portable" }, tempDir, "Plyph");
            Assert.Equal(StorageMode.Portable, mode2);
            Assert.Equal(Path.Combine(tempDir, "data"), dir2);

            // 3. Executable named Plyph-Portable -> Portable
            var (mode3, _) = StorageModeResolver.Resolve(Array.Empty<string>(), tempDir, "Plyph-Portable");
            Assert.Equal(StorageMode.Portable, mode3);

            // 4. Marker file portable.marker -> Portable
            var markerFile = Path.Combine(tempDir, StorageModeResolver.PortableMarkerFileName);
            File.WriteAllText(markerFile, "");
            var (mode4, dir4) = StorageModeResolver.Resolve(Array.Empty<string>(), tempDir, "Plyph");
            Assert.Equal(StorageMode.Portable, mode4);
            Assert.Equal(Path.Combine(tempDir, "data"), dir4);

            // 5. Organized Portable subfolder marker -> data folder in parent directory
            var appSubDir = Path.Combine(tempDir, "app");
            Directory.CreateDirectory(appSubDir);
            File.WriteAllText(Path.Combine(appSubDir, StorageModeResolver.PortableParentDataMarkerFileName), "");
            var (mode5, dir5) = StorageModeResolver.Resolve(Array.Empty<string>(), appSubDir, "Plyph-Portable");
            Assert.Equal(StorageMode.Portable, mode5);
            Assert.Equal(Path.Combine(tempDir, "data"), dir5);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Portable_Settings_Persist_Locally_And_Reload_Cleanly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"plyph-persist-{Guid.NewGuid():N}");
        try
        {
            var s1 = new PlyphSettings(StorageMode.Portable, tempDir, persistToDisk: true);
            s1.SetString("provider", "gemini");
            s1.SetDouble("floating-toolbar-scale", 1.5);
            s1.SetStrv("correct-shortcut", new[] { "Ctrl+Alt+C" });

            var s2 = new PlyphSettings(StorageMode.Portable, tempDir, persistToDisk: true);
            Assert.Equal("gemini", s2.GetString("provider"));
            Assert.Equal(1.5, s2.GetDouble("floating-toolbar-scale"));
            Assert.Equal(new[] { "Ctrl+Alt+C" }, s2.GetStrv("correct-shortcut"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Toolbar_Defaults_And_Preferences_Persist_Correctly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"plyph-toolbar-prefs-{Guid.NewGuid():N}");
        try
        {
            var s1 = new PlyphSettings(StorageMode.Portable, tempDir, persistToDisk: true);
            Assert.Equal("button", s1.GetString("floating-toolbar-layout"));
            Assert.Equal("horizontal", s1.GetString("floating-toolbar-orientation"));
            Assert.Equal("selection", s1.GetString("floating-toolbar-position"));
            Assert.False(s1.GetBoolean("toolbar-accent-icon"));
            Assert.False(s1.GetBoolean("result-popup-dismiss-on-outside-click"));

            s1.SetString("floating-toolbar-position", "cursor");
            s1.SetString("floating-toolbar-orientation", "vertical");
            s1.SetBoolean("toolbar-accent-icon", true);
            s1.SetBoolean("result-popup-dismiss-on-outside-click", true);

            var s2 = new PlyphSettings(StorageMode.Portable, tempDir, persistToDisk: true);
            Assert.Equal("cursor", s2.GetString("floating-toolbar-position"));
            Assert.Equal("vertical", s2.GetString("floating-toolbar-orientation"));
            Assert.True(s2.GetBoolean("toolbar-accent-icon"));
            Assert.True(s2.GetBoolean("result-popup-dismiss-on-outside-click"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
