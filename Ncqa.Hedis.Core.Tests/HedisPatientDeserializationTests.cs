using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using FluentAssertions;
using Ncqa.Hedis.Core;
using Xunit;

namespace Ncqa.Hedis.Core.Tests;

public class HedisPatientDeserializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>
    /// Deserializes the test file as a Bundle and returns the first entry resource cast to Patient.
    /// All patient-level tests use this helper because the test data is now a Bundle wrapper.
    /// </summary>
    private static Patient GetPatientFromBundle(string json)
    {
        var bundle = JsonSerializer.Deserialize<Bundle>(json, JsonOptions);
        return bundle!.Entry![0].GetResource<Patient>()
            ?? throw new InvalidOperationException("First bundle entry is not a Patient.");
    }

    [Fact]
    public void CanDeserializeHedisPatient()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var patient = GetPatientFromBundle(json);

        // Assert
        patient.Should().NotBeNull();
        patient.ResourceType.Should().Be("Patient");
        patient.Id.Should().Be("hedis-core-patient-01");
    }

    [Fact]
    public void DeserializedPatient_HasCorrectMetadata()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var patient = GetPatientFromBundle(json);

        // Assert
        patient.Meta.Should().NotBeNull();
        patient.Meta!.Profile.Should().Contain("https://ncqa.org/fhir/StructureDefinition/hedis-core-patient");
        patient.Meta.Tag.Should().HaveCount(1);
        patient.Meta.Tag![0].Code.Should().Be("administrative");
        patient.Meta.Tag[0].System.Should().Be("https://ncqa.org/fhir/CodeSystem/hedis-data-source");
    }

    [Fact]
    public void DeserializedPatient_HasCorrectDemographics()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var patient = GetPatientFromBundle(json);

        // Assert
        patient.Gender.Should().Be("female");
        patient.BirthDate.Should().Be("1987-02-20");
        // Note: Deceased is a choice type (deceasedBoolean/deceasedDateTime) in FHIR
        // The model uses "deceased" but the test data has "deceasedDateTime"
        // This is a known limitation - choice types would need special handling
    }

    [Fact]
    public void DeserializedPatient_HasNames()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var patient = GetPatientFromBundle(json);

        // Assert - Name is an array
        patient.Name.Should().NotBeNull();
        patient.Name.Should().HaveCount(2);
        patient.Name![0].Family.Should().Be("Shaw");
        patient.Name[0].Given.Should().Contain("Amy");
        patient.Name[1].Family.Should().Be("Baxter");
    }

    [Fact]
    public void DeserializedPatient_HasAddresses()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var patient = GetPatientFromBundle(json);

        // Assert - Address is an array
        patient.Address.Should().NotBeNull();
        patient.Address.Should().HaveCount(2);
        patient.Address![0].City.Should().Be("Mounds");
        patient.Address[0].State.Should().Be("OK");
        patient.Address[1].PostalCode.Should().Be("74048");
    }

    [Fact]
    public void DeserializedPatient_HasTelecom()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var patient = GetPatientFromBundle(json);

        // Assert - Telecom is an array
        patient.Telecom.Should().NotBeNull();
        patient.Telecom.Should().HaveCount(2);
        patient.Telecom![0].System.Should().Be("phone");
        patient.Telecom[0].Value.Should().Be("555-555-5555");
        patient.Telecom[1].System.Should().Be("email");
        patient.Telecom[1].Value.Should().Be("amy.shaw@example.com");
    }

    [Fact]
    public void DeserializedPatient_HasCommunication()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var patient = GetPatientFromBundle(json);

        // Assert - Communication is an array
        patient.Communication.Should().NotBeNull();
        patient.Communication.Should().HaveCount(1);
        patient.Communication![0].Language.Should().NotBeNull();
        patient.Communication[0].Language!.Text.Should().Be("Nederlands");
        patient.Communication[0].Preferred.Should().BeTrue();
    }

    [Fact]
    public void CanDeserializeWithSystemTextJson_NoFhirDependency()
    {
        // This test demonstrates that the generated DTOs work with pure System.Text.Json
        // without requiring any Hl7.Fhir.* dependencies

        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var patient = GetPatientFromBundle(json);
        stopwatch.Stop();

        // Assert
        patient.Should().NotBeNull();

        // Output performance info (visible in test output)
        Console.WriteLine($"Deserialization took: {stopwatch.ElapsedMilliseconds}ms ({stopwatch.ElapsedTicks} ticks)");
    }

    [Fact]
    public void DeserializedPatient_HasIdentifiers()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var patient = GetPatientFromBundle(json);

        // Assert - Identifier is an array
        patient.Identifier.Should().NotBeNull();
        patient.Identifier.Should().HaveCount(1);
        patient.Identifier![0].Type.Should().NotBeNull();
        patient.Identifier[0].Type!.Coding.Should().HaveCount(1);
        patient.Identifier[0].Type!.Coding![0].Code.Should().Be("MB");
    }

    [Fact]
    public void DeserializedPatient_HasExtensions()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var patient = GetPatientFromBundle(json);

        // Assert - Extension is an array with HEDIS-specific extensions
        patient.Extension.Should().NotBeNull();
        patient.Extension!.Count.Should().BeGreaterThan(5);

        // Check for race extension
        var raceExtension = patient.Extension.FirstOrDefault(e =>
            e.Url == "http://hl7.org/fhir/us/core/StructureDefinition/us-core-race");
        raceExtension.Should().NotBeNull();

        // Check for ethnicity extension
        var ethnicityExtension = patient.Extension.FirstOrDefault(e =>
            e.Url == "http://hl7.org/fhir/us/core/StructureDefinition/us-core-ethnicity");
        ethnicityExtension.Should().NotBeNull();

        // Check for birthsex extension
        var birthsexExtension = patient.Extension.FirstOrDefault(e =>
            e.Url == "http://hl7.org/fhir/us/core/StructureDefinition/us-core-birthsex");
        birthsexExtension.Should().NotBeNull();
        birthsexExtension!.ValueCode.Should().Be("F");
    }

    [Fact]
    public void BundleEntry_ResourceIsDeserializedAsPatient()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var bundle = JsonSerializer.Deserialize<Bundle>(json, JsonOptions);

        // Assert - bundle structure
        bundle.Should().NotBeNull();
        bundle!.ResourceType.Should().Be("Bundle");
        bundle.Entry.Should().NotBeNull().And.HaveCount(1);

        // Assert - entry resource is a fully-typed Patient, not a JsonElement
        var resource = bundle.Entry![0].Resource;
        resource.Should().BeOfType<Patient>();

        // Assert - patient fields are populated
        var patient = bundle.Entry[0].GetResource<Patient>();
        patient.Should().NotBeNull();
        patient!.ResourceType.Should().Be("Patient");
        patient.Id.Should().Be("hedis-core-patient-01");
        patient.Gender.Should().Be("female");
    }

    [Fact]
    public void CanRoundTripPatient()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");
        var original = JsonSerializer.Deserialize<Bundle>(json, JsonOptions);

        // Act - Serialize back to JSON
        var serialized = JsonSerializer.Serialize(original, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        // Assert - Can deserialize again and the entry resource is still a typed Patient
        var roundTripped = JsonSerializer.Deserialize<Bundle>(serialized, JsonOptions);
        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(original!.Id);
        roundTripped.ResourceType.Should().Be(original.ResourceType);
        roundTripped.Entry![0].GetResource<Patient>()!.Id.Should().Be(
            original.Entry![0].GetResource<Patient>()!.Id);
    }

    [Fact]
    public async Task CanUnzipLargeBundles()
    {
        var timer = Stopwatch.StartNew();
        var file = @"C:\Development\TestData\input_0-10000.ndjson.gz";
        var channel = Channel.CreateBounded<string>(capacity: 1000);
        int count = 0;

        // Producer: read lines sequentially from the gzip stream.
        // 1<<17 (128 KB) FileStream buffer reduces IO syscall overhead vs the default 4 KB.
        var producer = Task.Run(async () =>
        {
            using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 1 << 17);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using var streamReader = new StreamReader(gzipStream, bufferSize: 1 << 17);
            while (!streamReader.EndOfStream)
            {
                var line = await streamReader.ReadLineAsync();
                if (line != null)
                    await channel.Writer.WriteAsync(line);
            }
            channel.Writer.Complete();
        });

        // Consumer: deserialize lines in parallel across all available cores.
        var consumer = Parallel.ForEachAsync(
            channel.Reader.ReadAllAsync(),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (line, _) =>
            {
                JsonSerializer.Deserialize<Bundle>(line, JsonOptions);
                Interlocked.Increment(ref count);
                return ValueTask.CompletedTask;
            });

        await Task.WhenAll(producer, consumer);
        timer.Stop();
        Console.WriteLine($"Processed {count} bundles in {timer.Elapsed.TotalSeconds:F2}s  ({count / timer.Elapsed.TotalSeconds:F0} bundles/sec)");
    }

    [Fact]
    public async Task CanReadLargeBundlesUncompressed()
    {
        var timer = Stopwatch.StartNew();
        var file = @"C:\Development\TestData\input_0-10000.ndjson";
        var channel = Channel.CreateBounded<string>(capacity: 1000);
        int count = 0;

        var producer = Task.Run(async () =>
        {
            using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 17);
            using var streamReader = new StreamReader(fileStream, bufferSize: 1 << 17);
            while (!streamReader.EndOfStream)
            {
                var line = await streamReader.ReadLineAsync();
                if (line != null)
                    await channel.Writer.WriteAsync(line);
            }
            channel.Writer.Complete();
        });

        var consumer = Parallel.ForEachAsync(
            channel.Reader.ReadAllAsync(),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (line, _) =>
            {
                JsonSerializer.Deserialize<Bundle>(line, JsonOptions);
                Interlocked.Increment(ref count);
                return ValueTask.CompletedTask;
            });

        await Task.WhenAll(producer, consumer);
        timer.Stop();
        Console.WriteLine($"Processed {count} bundles in {timer.Elapsed.TotalSeconds:F2}s  ({count / timer.Elapsed.TotalSeconds:F0} bundles/sec)");
    }
}

