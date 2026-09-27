using HardwareLive.Core.Fps;

namespace HardwareLive.Tests.Fps;

public sealed class PresentMonCsvParserTests
{
    private const string RealHeader =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing," +
        "PresentMode,CPUStartQPCTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait," +
        "DisplayLatency,DisplayedTime,AnimationError,AnimationTime,MsFlipDelay,AllInputToPhotonLatency," +
        "ClickToPhotonLatency";

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "presentmon", fileName);

    [Fact]
    public void RealFixtureHeaderParsesAndEveryRowParses()
    {
        var lines = File.ReadAllLines(FixturePath("presentmon-2.6.0-v2-raw-stdout.csv"));
        Assert.True(PresentMonCsvParser.TryParseHeader(lines[0], out var columns));

        var parsedCount = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrEmpty(lines[i]))
            {
                continue;
            }

            Assert.True(PresentMonCsvParser.TryParseRow(lines[i], columns, out var row), $"Row {i} failed to parse: {lines[i]}");
            Assert.NotNull(row);
            parsedCount++;
        }

        Assert.True(parsedCount > 0);
    }

    [Fact]
    public void HeaderColumnsAreFoundByNameNotIndex()
    {
        // Shuffled column order relative to the real header -- if the parser ever indexed
        // positionally instead of by name, this would silently read the wrong field.
        const string shuffledHeader = "FrameTime,ProcessID,Application,CPUStartQPCTime,PresentMode";
        Assert.True(PresentMonCsvParser.TryParseHeader(shuffledHeader, out var columns));

        const string row = "12.5,4242,notepad.exe,999000.25,Hardware: Legacy Flip";
        Assert.True(PresentMonCsvParser.TryParseRow(row, columns, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal("notepad.exe", parsed!.Application);
        Assert.Equal(4242, parsed.ProcessId);
        Assert.Equal(999000.25, parsed.CpuStartQpcTimeMs, precision: 6);
        Assert.Equal(12.5, parsed.FrameTimeMs);
        Assert.Equal("Hardware: Legacy Flip", parsed.PresentMode);
    }

    [Theory]
    [InlineData("Application,ProcessID,CPUStartQPCTime")] // missing FrameTime
    [InlineData("Application,FrameTime,CPUStartQPCTime")] // missing ProcessID
    [InlineData("Application,ProcessID,FrameTime")] // missing CPUStartQPCTime
    [InlineData("")]
    public void HeaderMissingARequiredColumnIsRejected(string header)
    {
        Assert.False(PresentMonCsvParser.TryParseHeader(header, out _));
    }

    [Fact]
    public void NaAndEmptyFrameTimeParseAsNull()
    {
        Assert.True(PresentMonCsvParser.TryParseHeader(RealHeader, out var columns));

        const string naRow =
            "game.exe,100,0x0,DXGI,0,0,0,Hardware: Legacy Flip,1000.0,NA,1,1,1,1,1,1,1,NA,NA,1,NA,NA,NA";
        Assert.True(PresentMonCsvParser.TryParseRow(naRow, columns, out var naParsed));
        Assert.Null(naParsed!.FrameTimeMs);

        const string emptyRow =
            "game.exe,100,0x0,DXGI,0,0,0,Hardware: Legacy Flip,1000.0,,1,1,1,1,1,1,1,NA,NA,1,NA,NA,NA";
        Assert.True(PresentMonCsvParser.TryParseRow(emptyRow, columns, out var emptyParsed));
        Assert.Null(emptyParsed!.FrameTimeMs);
    }

    [Fact]
    public void OversizeLineIsDropped()
    {
        Assert.True(PresentMonCsvParser.TryParseHeader(RealHeader, out var columns));
        var huge = new string('a', PresentMonCsvParser.MaxLineLength + 1);
        Assert.False(PresentMonCsvParser.TryParseRow(huge, columns, out var row));
        Assert.Null(row);
    }

    [Fact]
    public void MalformedRowWithTooFewFieldsIsDropped()
    {
        Assert.True(PresentMonCsvParser.TryParseHeader(RealHeader, out var columns));
        Assert.False(PresentMonCsvParser.TryParseRow("game.exe,100", columns, out var row));
        Assert.Null(row);
    }

    [Fact]
    public void NonNumericProcessIdIsDropped()
    {
        Assert.True(PresentMonCsvParser.TryParseHeader(RealHeader, out var columns));
        const string row =
            "game.exe,NOTANUMBER,0x0,DXGI,0,0,0,Hardware: Legacy Flip,1000.0,10.0,1,1,1,1,1,1,1,NA,NA,1,NA,NA,NA";
        Assert.False(PresentMonCsvParser.TryParseRow(row, columns, out var parsed));
        Assert.Null(parsed);
    }
}
