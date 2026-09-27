using HardwareLive.Core.Analysis;
using HardwareLive.Core.Classification;
using HardwareLive.Protocol;
using HardwareLive.Tests.Classification;

namespace HardwareLive.Tests.Analysis;

public sealed class SensorLabelBuilderTests
{
    [Fact]
    public void NullFrameProducesNoLabels()
    {
        var labels = SensorLabelBuilder.Build(null, ClassificationResult.Empty);

        Assert.Empty(labels);
    }

    [Fact]
    public void TheTwoGpuCoreSensorsGetDifferentTitles()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var classification = SensorClassifier.Classify(frame);
        var labels = SensorLabelBuilder.Build(frame, classification);

        var clockId = classification.Roles.Single(r => r.Role == Roles.GpuClockCore).SensorId;
        var loadId = classification.Roles.Single(r => r.Role == Roles.GpuLoadCore).SensorId;

        Assert.Equal("GPU clock", labels[clockId].Title);
        Assert.Equal("GPU load", labels[loadId].Title);
        Assert.NotEqual(labels[clockId].Title, labels[loadId].Title);
    }

    [Fact]
    public void EachNvmeDriveGetsItsModelInTheSubtitle()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var classification = SensorClassifier.Classify(frame);
        var labels = SensorLabelBuilder.Build(frame, classification);

        var storageTemps = classification.Roles.Where(r => r.Role == Roles.StorageTemp).ToList();
        Assert.True(storageTemps.Count >= 2, "Expected the real fixture to have at least two storage.temp sensors.");

        foreach (var role in storageTemps)
        {
            var label = labels[role.SensorId];
            Assert.Equal("Drive temperature", label.Title);
            var hardware = frame.Hardware.Single(h => h.Id == role.HardwareId);
            Assert.Equal(hardware.Name.Trim(), label.Subtitle);
        }

        // Every drive's subtitle must be distinct (that's the whole point of using the model).
        var subtitles = storageTemps.Select(r => labels[r.SensorId].Subtitle).ToList();
        Assert.Equal(subtitles.Count, subtitles.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DimmTempsAreDisambiguatedBySensorNameNotHardwareName()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var classification = SensorClassifier.Classify(frame);
        var labels = SensorLabelBuilder.Build(frame, classification);

        var dimmTemps = classification.Roles.Where(r => r.Role == Roles.DimmTemp).ToList();
        Assert.True(dimmTemps.Count >= 2, "Expected the real fixture to have at least two dimm.temp sensors.");

        foreach (var role in dimmTemps)
        {
            var label = labels[role.SensorId];
            Assert.Equal("RAM temperature", label.Title);
            Assert.StartsWith("DIMM #", label.Subtitle, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheGarbledDimmHardwareNameIsSanitizedWhenUsedAsAnUnclassifiedSensorSubtitle()
    {
        // docs/SPEC.md: one real DIMM's hardware name has embedded CR/NUL/SUB bytes. The
        // "DIMM #3" temperature sensor itself is clean and disambiguates on its own (previous
        // test), but sibling sensors on the SAME hardware entry with no role of their own
        // (e.g. "Temperature Sensor Resolution", a limit-adjacent metadata sensor) fall back
        // to the hardware name as their subtitle -- and that name is the garbled one.
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var classification = SensorClassifier.Classify(frame);
        var labels = SensorLabelBuilder.Build(frame, classification);

        var garbledHardware = frame.Hardware.Single(h => h.Id == "/memory/dimm/3");
        Assert.Contains('\r', garbledHardware.Name);
        Assert.Contains('\0', garbledHardware.Name);

        var unclassifiedOnThatHardware = frame.Sensors
            .Where(s => s.HardwareId == "/memory/dimm/3" && !classification.Roles.Any(r => r.SensorId == s.Id))
            .ToList();
        Assert.NotEmpty(unclassifiedOnThatHardware);

        foreach (var sensor in unclassifiedOnThatHardware)
        {
            var label = labels[sensor.Id];
            Assert.NotNull(label.Subtitle);
            Assert.DoesNotContain('\r', label.Subtitle);
            Assert.DoesNotContain('\0', label.Subtitle);
            Assert.DoesNotContain('\u001a', label.Subtitle);
        }
    }

    [Fact]
    public void AnUnclassifiedSensorUsesItsOwnSanitizedNameAsTheTitle()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var classification = SensorClassifier.Classify(frame);
        var labels = SensorLabelBuilder.Build(frame, classification);

        var resolutionSensor = frame.Sensors.Single(s =>
            s.HardwareId == "/memory/dimm/1" && s.Name == "Temperature Sensor Resolution");

        Assert.Equal("Temperature Sensor Resolution", labels[resolutionSensor.Id].Title);
    }

    [Fact]
    public void OneNvmeAndOneSataDriveStillGetTheirModelsNotTheirGenericSensorNames()
    {
        // A regression for a PC with exactly one NVMe and one SATA drive: their storage.temp
        // sensor names ("Composite Temperature" vs "Temperature") happen to be unique from
        // each other, but neither one identifies the drive -- the subtitle must still be the
        // drive model, not the sensor name (docs/SPEC.md "arbitrary PC").
        var frame = new SensorFrame(
            SensorFrame.CurrentVersion,
            1,
            1000,
            PawnIoInstalled: true,
            Elevated: true,
            LhmVersion: "0.9.6.0",
            Hardware:
            [
                new HardwareInfo("/nvme/0", "Samsung SSD 990 PRO 2TB", "Storage", null),
                new HardwareInfo("/ssd/0", "Crucial MX500 1TB", "Storage", null),
            ],
            Sensors:
            [
                new SensorReading("/nvme/0/temperature/0", "/nvme/0", "Composite Temperature", "Temperature", 40f, null, null),
                new SensorReading("/ssd/0/temperature/0", "/ssd/0", "Temperature", "Temperature", 35f, null, null),
            ]);

        var classification = SensorClassifier.Classify(frame);
        var labels = SensorLabelBuilder.Build(frame, classification);

        var nvmeRole = classification.Roles.Single(r => r.SensorId == "/nvme/0/temperature/0");
        var ssdRole = classification.Roles.Single(r => r.SensorId == "/ssd/0/temperature/0");
        Assert.Equal(Roles.StorageTemp, nvmeRole.Role);
        Assert.Equal(Roles.StorageTemp, ssdRole.Role);

        Assert.Equal("Samsung SSD 990 PRO 2TB", labels[nvmeRole.SensorId].Subtitle);
        Assert.Equal("Crucial MX500 1TB", labels[ssdRole.SensorId].Subtitle);
    }

    [Fact]
    public void SingleInstanceRolesGetNoSubtitle()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var classification = SensorClassifier.Classify(frame);
        var labels = SensorLabelBuilder.Build(frame, classification);

        var cpuControlId = classification.Roles.Single(r => r.Role == Roles.CpuTempControl).SensorId;

        Assert.Null(labels[cpuControlId].Subtitle);
    }
}
