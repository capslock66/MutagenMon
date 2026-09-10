using MutagenMon.Core.Sessions;
using Xunit;

namespace MutagenMon.Core.Tests;

public class SessionCommandLineParserTests
{
    [Fact]
    public void ParsesNameAlphaBetaFromAMinimalLine()
    {
        var model = SessionCommandLineParser.Parse("mutagen sync create C:/Photos server:/home/photos --name=photos-sync");

        Assert.Equal("photos-sync", model.Name);
        Assert.Equal("C:/Photos", model.Alpha);
        Assert.Equal("server:/home/photos", model.Beta);
        Assert.Equal(SyncMode.TwoWaySafe, model.Mode);
        Assert.Empty(model.UnknownFlags);
    }

    [Fact]
    public void ParsesFlagsInterspersedBeforePositionalsLikeExistingFixtures()
    {
        var model = SessionCommandLineParser.Parse("mutagen sync create --name=photos-sync -m two-way-safe C:/Photos server:/home/photos");

        Assert.Equal("photos-sync", model.Name);
        Assert.Equal("C:/Photos", model.Alpha);
        Assert.Equal("server:/home/photos", model.Beta);
    }

    [Fact]
    public void RenderOmitsModeWhenDefault()
    {
        var model = new SessionCommandLine { Name = "s", Alpha = "A", Beta = "B" };

        var line = SessionCommandLineParser.Render(model);

        Assert.DoesNotContain("-m ", line);
        Assert.Equal("mutagen sync create A B --name=s", line);
    }

    [Fact]
    public void RenderEmitsModeWhenNotDefault()
    {
        var model = new SessionCommandLine { Name = "s", Alpha = "A", Beta = "B", Mode = SyncMode.OneWayReplica };

        var line = SessionCommandLineParser.Render(model);

        Assert.Contains("-m one-way-replica", line);
    }

    [Fact]
    public void ParsesBothShortAndLongModeFlagsButRendersOnlyTheShortForm()
    {
        var model = SessionCommandLineParser.Parse("mutagen sync create A B --name=s --mode=one-way-safe");
        Assert.Equal(SyncMode.OneWaySafe, model.Mode);

        var rendered = SessionCommandLineParser.Render(model);
        Assert.Equal("mutagen sync create A B --name=s -m one-way-safe", rendered);

        var reparsed = SessionCommandLineParser.Parse(rendered);
        Assert.Equal(SyncMode.OneWaySafe, reparsed.Mode);
    }

    [Fact]
    public void ParsesSyncModeAsASynonymOfModeAndNormalizesToTheShortFormOnSave()
    {
        var model = SessionCommandLineParser.Parse("mutagen sync create A B --name=s --sync-mode=two-way-resolved");

        Assert.Equal(SyncMode.TwoWayResolved, model.Mode);
        Assert.Empty(model.UnknownFlags);

        var rendered = SessionCommandLineParser.Render(model);
        Assert.Equal("mutagen sync create A B --name=s -m two-way-resolved", rendered);
    }

    [Fact]
    public void ParsesRepeatedIgnoreFlagsAndRendersOnePerLine()
    {
        var model = SessionCommandLineParser.Parse("mutagen sync create A B --name=s -i node_modules/ -i *.tmp");

        Assert.Equal(new[] { "node_modules/", "*.tmp" }, model.Ignores);

        var rendered = SessionCommandLineParser.Render(model);
        Assert.Contains("-i node_modules/", rendered);
        Assert.Contains("-i *.tmp", rendered);
    }

    [Fact]
    public void SplitsCommaSeparatedIgnoreFlagIntoSeparatePatterns()
    {
        var model = SessionCommandLineParser.Parse("mutagen sync create A B --name=s --ignore=node_modules/,*.tmp");

        Assert.Equal(new[] { "node_modules/", "*.tmp" }, model.Ignores);
    }

    [Fact]
    public void QuotesIgnorePatternsContainingWhitespaceOnRender()
    {
        var model = new SessionCommandLine { Name = "s", Alpha = "A", Beta = "B", Ignores = { "some pattern" } };

        var line = SessionCommandLineParser.Render(model);

        Assert.Contains("-i \"some pattern\"", line);
    }

    [Fact]
    public void ParsesQuotedTokensAsSingleValues()
    {
        var model = SessionCommandLineParser.Parse("mutagen sync create \"C:/Program Files/A\" B --name=s -i \"some pattern\"");

        Assert.Equal("C:/Program Files/A", model.Alpha);
        Assert.Equal(new[] { "some pattern" }, model.Ignores);
    }

    [Theory]
    [InlineData(true, "--ignore-vcs")]
    [InlineData(false, "--no-ignore-vcs")]
    public void RoundTripsIgnoreVcsTriState(bool value, string expectedFlag)
    {
        var model = new SessionCommandLine { Name = "s", Alpha = "A", Beta = "B", IgnoreVcs = value };

        var line = SessionCommandLineParser.Render(model);
        Assert.Contains(expectedFlag, line);

        var reparsed = SessionCommandLineParser.Parse(line);
        Assert.Equal(value, reparsed.IgnoreVcs);
    }

    [Fact]
    public void IgnoreVcsOmitsBothFlagsWhenNull()
    {
        var model = new SessionCommandLine { Name = "s", Alpha = "A", Beta = "B" };

        var line = SessionCommandLineParser.Render(model);

        Assert.DoesNotContain("ignore-vcs", line);
    }

    [Fact]
    public void RoundTripsPermissionsZoneIncludingPerSideOverrides()
    {
        var model = new SessionCommandLine
        {
            Name = "s", Alpha = "A", Beta = "B",
            PermissionsMode = PermissionsMode.Manual,
            Owner = new PerSideText(Session: "bob", Alpha: "alice"),
            Group = new PerSideText(Beta: "staff"),
            FileMode = new PerSideText(Session: "0644"),
            DirectoryMode = new PerSideText(Alpha: "0750", Beta: "0750"),
        };

        var line = SessionCommandLineParser.Render(model);
        var reparsed = SessionCommandLineParser.Parse(line);

        Assert.Equal(PermissionsMode.Manual, reparsed.PermissionsMode);
        Assert.Equal("bob", reparsed.Owner.Session);
        Assert.Equal("alice", reparsed.Owner.Alpha);
        Assert.Null(reparsed.Owner.Beta);
        Assert.Equal("staff", reparsed.Group.Beta);
        Assert.Equal("0644", reparsed.FileMode.Session);
        Assert.Equal("0750", reparsed.DirectoryMode.Alpha);
        Assert.Equal("0750", reparsed.DirectoryMode.Beta);
    }

    [Fact]
    public void AllowsCombiningSessionAndPerSideValueOnTheSameRowFr215()
    {
        var model = new SessionCommandLine
        {
            Name = "s", Alpha = "A", Beta = "B",
            WatchMode = new PerSide<WatchMode>(Session: Sessions.WatchMode.ForcePoll, Alpha: Sessions.WatchMode.NoWatch),
        };

        var line = SessionCommandLineParser.Render(model);

        Assert.Contains("--watch-mode=force-poll", line);
        Assert.Contains("--watch-mode-alpha=no-watch", line);
    }

    [Fact]
    public void OmitsPerSideEnumValueEqualToDefault()
    {
        var model = new SessionCommandLine
        {
            Name = "s", Alpha = "A", Beta = "B",
            ProbeMode = new PerSide<ProbeMode>(Alpha: Sessions.ProbeMode.Probe),
        };

        var line = SessionCommandLineParser.Render(model);

        Assert.DoesNotContain("probe-mode", line);
    }

    [Fact]
    public void RoundTripsWatchingAndLimitsZones()
    {
        var model = new SessionCommandLine
        {
            Name = "s", Alpha = "A", Beta = "B",
            WatchMode = new PerSide<WatchMode>(Session: Sessions.WatchMode.ForcePoll),
            WatchPollingInterval = new PerSide<int>(Alpha: 30),
            MaxStagingFileSize = "1000 MB",
            MaxEntryCount = 500000,
        };

        var line = SessionCommandLineParser.Render(model);
        var reparsed = SessionCommandLineParser.Parse(line);

        Assert.Equal(Sessions.WatchMode.ForcePoll, reparsed.WatchMode.Session);
        Assert.Equal(30, reparsed.WatchPollingInterval.Alpha);
        Assert.Equal("1000 MB", reparsed.MaxStagingFileSize);
        Assert.Equal(500000, reparsed.MaxEntryCount);
    }

    [Fact]
    public void PreservesUnrecognizedFlagWithValueVerbatimByDefault()
    {
        var model = SessionCommandLineParser.Parse("mutagen sync create A B --name=s --hash=xxh128");

        Assert.Single(model.UnknownFlags);
        Assert.Equal("--hash=xxh128", model.UnknownFlags[0].Text);
        Assert.True(model.UnknownFlags[0].Keep);

        var line = SessionCommandLineParser.Render(model);
        Assert.Contains("--hash=xxh128", line);
    }

    [Fact]
    public void PreservesUnrecognizedFlagAndItsSpaceSeparatedValue()
    {
        var model = SessionCommandLineParser.Parse("mutagen sync create A B --name=s --hash xxh128");

        Assert.Single(model.UnknownFlags);
        Assert.Equal("--hash xxh128", model.UnknownFlags[0].Text);
    }

    [Fact]
    public void UncheckedUnknownFlagIsDroppedOnRender()
    {
        var model = SessionCommandLineParser.Parse("mutagen sync create A B --name=s --hash=xxh128");
        model.UnknownFlags[0] = model.UnknownFlags[0] with { Keep = false };

        var line = SessionCommandLineParser.Render(model);

        Assert.DoesNotContain("--hash", line);
    }

    [Fact]
    public void UnrecognizedFlagWithBadEnumValueFallsBackToUnknownInsteadOfThrowing()
    {
        var model = SessionCommandLineParser.Parse("mutagen sync create A B --name=s --mode=some-future-mode");

        Assert.Equal(SyncMode.TwoWaySafe, model.Mode);
        Assert.Single(model.UnknownFlags);
        Assert.Equal("--mode=some-future-mode", model.UnknownFlags[0].Text);
    }

    [Fact]
    public void FullRoundTripOfEveryZoneIsStable()
    {
        var model = new SessionCommandLine
        {
            Name = "full", Alpha = "C:/A", Beta = "server:/b",
            Mode = SyncMode.TwoWayResolved,
            Ignores = { "node_modules/", "*.tmp" },
            IgnoreVcs = true,
            IgnoreSyntaxDocker = true,
            PermissionsMode = PermissionsMode.Manual,
            Owner = new PerSideText(Session: "bob"),
            Group = new PerSideText(Beta: "staff"),
            FileMode = new PerSideText(Session: "0644"),
            DirectoryMode = new PerSideText(Session: "0755"),
            SymlinkMode = SymlinkMode.PosixRaw,
            WatchMode = new PerSide<WatchMode>(Session: Sessions.WatchMode.ForcePoll),
            WatchPollingInterval = new PerSide<int>(Session: 30),
            ProbeMode = new PerSide<ProbeMode>(Alpha: Sessions.ProbeMode.Assume),
            ScanMode = new PerSide<ScanMode>(Beta: Sessions.ScanMode.Full),
            StageMode = new PerSide<StageMode>(Session: Sessions.StageMode.Neighboring),
            MaxStagingFileSize = "1000 MB",
            MaxEntryCount = 42,
        };

        var line = SessionCommandLineParser.Render(model);
        var reparsed = SessionCommandLineParser.Parse(line);
        var rerendered = SessionCommandLineParser.Render(reparsed);

        Assert.Equal(line, rerendered);
        Assert.Empty(reparsed.UnknownFlags);
    }
}
