using System.Text;
using System.Text.Json;
using HardwareLive.Core;

namespace HardwareLive.Tests;

public sealed class LayoutStoreTests
{
    [Fact]
    public void CustomLayoutRoundTripsExactWidgetReferencesAndSchemaExtensions()
    {
        using var directory = new TestDirectory();
        var expected = new Layout(
            "workstation",
            "Workstation",
            [
                new LayoutWidget("tile", "S", new LayoutReference("cpu.clock.effective.core", "/cpu/0/clock/1", "CPU")),
                new LayoutWidget("tile", "S", new LayoutReference("fan.system", "/lpc/0/fan/0", "Board")),
                new LayoutWidget("tile", "S", new LayoutReference("fan.system", "/lpc/0/fan/1", "Board")),
                new LayoutWidget("tile", "S", new LayoutReference(null, "/lpc/0/temp/9", "Board")),
            ],
            Focus: "cpu",
            Sort: null);

        var store = new FileLayoutStore(directory.Path);
        Assert.Equal(LayoutWriteResult.Success, store.Create(expected));

        var reloaded = new FileLayoutStore(directory.Path);
        var actual = Assert.Single(reloaded.List(), item => !item.Builtin).Layout;

        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    [Fact]
    public void InterruptedCommitLeavesPreviousFileAndInMemoryStateIntact()
    {
        using var directory = new TestDirectory();
        var commits = 0;
        var store = new FileLayoutStore(directory.Path, beforeCommit: () =>
        {
            commits++;
            if (commits == 2)
            {
                throw new IOException("simulated crash before replace");
            }
        });

        Assert.Equal(LayoutWriteResult.Success, store.Create(EmptyLayout("first")));
        var before = File.ReadAllText(System.IO.Path.Combine(directory.Path, "layouts.json"));

        Assert.Throws<IOException>(() => store.Create(EmptyLayout("second")));

        Assert.Equal(before, File.ReadAllText(System.IO.Path.Combine(directory.Path, "layouts.json")));
        Assert.DoesNotContain(store.List(), item => item.Layout.Id == "second");
        Assert.DoesNotContain(new FileLayoutStore(directory.Path).List(), item => item.Layout.Id == "second");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptOrOversizeFileIsQuarantinedAndRaisesRecoveryWarning(bool oversize)
    {
        using var directory = new TestDirectory();
        var path = System.IO.Path.Combine(directory.Path, "layouts.json");
        if (oversize)
        {
            File.WriteAllBytes(path, new byte[FileLayoutStore.MaximumFileSize + 1]);
        }
        else
        {
            File.WriteAllText(path, "{not-json", Encoding.UTF8);
        }

        var store = new FileLayoutStore(directory.Path);

        Assert.True(store.SavedLayoutsCouldNotBeRead);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(directory.Path, "layouts.corrupt-*.json"));
        Assert.DoesNotContain(store.List(), item => !item.Builtin);
    }

    [Fact]
    public void NonIntegralDocumentVersionIsQuarantinedInsteadOfEscapingTheLoader()
    {
        using var directory = new TestDirectory();
        var path = System.IO.Path.Combine(directory.Path, "layouts.json");
        File.WriteAllText(path, """{"version":1.5,"activePresetId":null,"layouts":[]}""");

        var store = new FileLayoutStore(directory.Path);

        Assert.True(store.SavedLayoutsCouldNotBeRead);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(directory.Path, "layouts.corrupt-*.json"));
    }

    [Fact]
    public void SuccessfulReplacementKeepsOneBackupOfThePreviousFile()
    {
        using var directory = new TestDirectory();
        var store = new FileLayoutStore(directory.Path);
        Assert.Equal(LayoutWriteResult.Success, store.Create(EmptyLayout("first")));
        var first = File.ReadAllText(System.IO.Path.Combine(directory.Path, "layouts.json"));

        Assert.Equal(LayoutWriteResult.Success, store.Create(EmptyLayout("second")));

        var backupPath = System.IO.Path.Combine(directory.Path, "layouts.json.bak");
        Assert.True(File.Exists(backupPath));
        Assert.Equal(first, File.ReadAllText(backupPath));

        var second = File.ReadAllText(System.IO.Path.Combine(directory.Path, "layouts.json"));
        Assert.Equal(LayoutWriteResult.Success, store.Create(EmptyLayout("third")));
        Assert.Single(Directory.GetFiles(directory.Path, "layouts.json.bak"));
        Assert.Equal(second, File.ReadAllText(backupPath));
    }

    [Fact]
    public async Task ConcurrentWritesAreSerializedAndProduceValidCompleteJson()
    {
        using var directory = new TestDirectory();
        var store = new FileLayoutStore(directory.Path);

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            Task.Run(() => store.Create(EmptyLayout($"layout-{index}")))));

        Assert.All(results, result => Assert.Equal(LayoutWriteResult.Success, result));
        using var json = JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(directory.Path, "layouts.json")));
        Assert.Equal(40, json.RootElement.GetProperty("layouts").GetArrayLength());
        Assert.Equal(40, new FileLayoutStore(directory.Path).List().Count(item => !item.Builtin));
    }

    [Fact]
    public void StoreEnforcesTwoHundredCustomLayoutCapWithoutChangingTheFile()
    {
        using var directory = new TestDirectory();
        var store = new FileLayoutStore(directory.Path);
        var batch = Enumerable.Range(0, 200).Select(index => EmptyLayout($"layout-{index}")).ToArray();

        var imported = store.Import(batch);
        var before = File.ReadAllText(System.IO.Path.Combine(directory.Path, "layouts.json"));

        Assert.True(imported.Success);
        Assert.Equal(LayoutWriteResult.LimitExceeded, store.Create(EmptyLayout("one-too-many")));
        Assert.Equal(before, File.ReadAllText(System.IO.Path.Combine(directory.Path, "layouts.json")));
    }

    [Fact]
    public void BuiltInsAreFirstAndCurrentLayoutMigratesToMyLayout()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            System.IO.Path.Combine(directory.Path, "layouts.json"),
            """{"version":1,"activePresetId":"current","temperatureUnit":null,"layouts":[{"id":"current","name":"Overview","widgets":[]}]}""");

        var store = new FileLayoutStore(directory.Path);
        var listed = store.List();

        Assert.Equal(
            ["builtin-overview", "builtin-cpu", "builtin-gpu", "builtin-gaming", "builtin-thermals", "builtin-cooling", "builtin-storage"],
            listed.Take(7).Select(item => item.Layout.Id));
        Assert.All(listed.Take(7), item => Assert.True(item.Builtin));
        var migrated = Assert.Single(listed, item => item.Layout.Id == "current");
        Assert.False(migrated.Builtin);
        Assert.Equal("My layout", migrated.Layout.Name);
        Assert.Equal("current", store.GetSettings().ActivePresetId);
    }

    [Fact]
    public void BuiltInPresetsUseOnlyPortableRoleReferencesAndExposeFocusAndSort()
    {
        Assert.Equal(
            ["Overview", "CPU", "GPU", "3D Gaming", "Thermals", "Cooling", "Storage"],
            BuiltInLayouts.All.Select(layout => layout.Name));
        Assert.Equal("cpu", BuiltInLayouts.All.Single(layout => layout.Id == "builtin-cpu").Focus);
        Assert.Equal("gpu", BuiltInLayouts.All.Single(layout => layout.Id == "builtin-gpu").Focus);
        Assert.Equal("gaming", BuiltInLayouts.All.Single(layout => layout.Id == "builtin-gaming").Focus);
        Assert.Equal("headroom", BuiltInLayouts.All.Single(layout => layout.Id == "builtin-thermals").Sort);

        var references = BuiltInLayouts.All
            .SelectMany(layout => layout.Widgets)
            .SelectMany(widget => widget.Series ?? (widget.Ref is null ? [] : [widget.Ref]));
        Assert.All(references, reference =>
        {
            Assert.NotNull(reference.Role);
            Assert.Null(reference.Id);
            Assert.Null(reference.Hw);
        });
    }

    private static Layout EmptyLayout(string id) => new(id, id, []);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hardware-live-layout-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
