using System.Net;
using System.Text.Json;

namespace HardwareLive.Tests;

public sealed class NotesApiTests
{
    [Fact]
    public async Task NotesReturnsNoContentWhenTheFileIsAbsent()
    {
        var directory = CreateEmptyNotesDirectory();
        await using var host = await ServerTestHost.StartAsync(notesDirectory: directory);

        using var response = await host.Client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task NotesReturnsAValidFileWithControlCharactersStripped()
    {
        var directory = CreateEmptyNotesDirectory();
        WriteNotes(directory, """
            { "at": "2026-09-27 12:00", "ts": 1758974400, "source": "Claude\r\u0000 Code",
              "lines": ["All good\r", "Second\u0000 line"] }
            """);
        await using var host = await ServerTestHost.StartAsync(notesDirectory: directory);

        using var response = await host.Client.GetAsync("/api/notes");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Claude Code", json.RootElement.GetProperty("source").GetString());
        var lines = json.RootElement.GetProperty("lines").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["All good", "Second line"], lines);
        Assert.True(json.RootElement.GetProperty("ageMinutes").GetDouble() >= 0);
    }

    [Fact]
    public async Task NotesOmitsSourceWhenAbsentFromTheFile()
    {
        var directory = CreateEmptyNotesDirectory();
        WriteNotes(directory, """{ "at": "now", "ts": 1758974400, "lines": ["hi"] }""");
        await using var host = await ServerTestHost.StartAsync(notesDirectory: directory);

        using var response = await host.Client.GetAsync("/api/notes");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("source").ValueKind);
    }

    public static IEnumerable<object[]> InvalidNotesFiles()
    {
        yield return ["malformed JSON", "{"];
        yield return ["non-object", "[]"];
        yield return ["missing at", """{"ts":1,"lines":[]}"""];
        yield return ["missing ts", """{"at":"now","lines":[]}"""];
        yield return ["non-numeric ts", """{"at":"now","ts":"1","lines":[]}"""];
        yield return ["missing lines", """{"at":"now","ts":1}"""];
        yield return ["non-array lines", """{"at":"now","ts":1,"lines":{}}"""];
        yield return ["too many lines", $$"""{"at":"now","ts":1,"lines":[{{string.Join(',', Enumerable.Repeat("\"x\"", 21))}}]}"""];
        yield return ["line too long", $$"""{"at":"now","ts":1,"lines":["{{new string('x', 501)}}"]}"""];
        yield return ["unexpected property", """{"at":"now","ts":1,"lines":[],"extra":true}"""];
        yield return ["empty at", """{"at":"","ts":1,"lines":[]}"""];
        yield return ["non-string line", """{"at":"now","ts":1,"lines":[1]}"""];
    }

    [Theory]
    [MemberData(nameof(InvalidNotesFiles))]
    public async Task InvalidNotesFilesAreTreatedAsAbsent(string _, string content)
    {
        var directory = CreateEmptyNotesDirectory();
        WriteNotes(directory, content);
        await using var host = await ServerTestHost.StartAsync(notesDirectory: directory);

        using var response = await host.Client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task OversizeNotesFileIsTreatedAsAbsent()
    {
        var directory = CreateEmptyNotesDirectory();
        var oversizeLine = new string('x', 500);
        var lines = string.Join(',', Enumerable.Repeat($"\"{oversizeLine}\"", 20));
        WriteNotes(directory, $$"""{"at":"now","ts":1,"lines":[{{lines}}],"pad":"{{new string('y', 20 * 1024)}}"}""");
        await using var host = await ServerTestHost.StartAsync(notesDirectory: directory);

        using var response = await host.Client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static string CreateEmptyNotesDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hl-notes-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteNotes(string directory, string content) =>
        File.WriteAllText(Path.Combine(directory, "notes.json"), content);
}
