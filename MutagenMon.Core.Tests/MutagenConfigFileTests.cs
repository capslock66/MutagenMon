using System.Text;
using MutagenMon.Core.Configuration;
using Xunit;

namespace MutagenMon.Core.Tests;

public class MutagenConfigFileTests
{
    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"mutagenmon-test-{Guid.NewGuid():N}.mutagen.yml");

    [Fact]
    public void LoadReportsFileDoesNotExistAndReturnsEmptyContent()
    {
        var path = NewTempPath();

        var result = MutagenConfigFile.Load(path);

        Assert.False(result.FileExisted);
        Assert.Equal("", result.Content);
    }

    [Fact]
    public void SaveCreatesTheFileWhenItDidNotExistYet()
    {
        var path = NewTempPath();
        try
        {
            MutagenConfigFile.Save(path, "synchronization:\n  defaults:\n    ignoreVCSDirectories: true\n", Encoding.UTF8);

            Assert.True(File.Exists(path));
            var reloaded = MutagenConfigFile.Load(path);
            Assert.True(reloaded.FileExisted);
            Assert.Contains("ignoreVCSDirectories", reloaded.Content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAndSaveRoundTripPreserveUtf8ContentWithNoBom()
    {
        var path = NewTempPath();
        try
        {
            var noBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(path, "a: é\n", noBom);

            var loaded = MutagenConfigFile.Load(path);
            Assert.Equal("a: é\n", loaded.Content);

            MutagenConfigFile.Save(path, "a: é\nb: 1\n", loaded.Encoding);

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAndSaveRoundTripPreserveUtf8Bom()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, "a: 1\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var loaded = MutagenConfigFile.Load(path);
            Assert.Equal("a: 1\n", loaded.Content);

            MutagenConfigFile.Save(path, "a: 2\n", loaded.Encoding);

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
