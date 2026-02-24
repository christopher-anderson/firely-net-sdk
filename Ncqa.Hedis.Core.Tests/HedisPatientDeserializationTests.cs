using System.Text.Json;
using FluentAssertions;
using Ncqa.Hedis.Core;
using Xunit;

namespace Ncqa.Hedis.Core.Tests;

public class HedisPatientDeserializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void CanDeserializeHedisPatient()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var patient = JsonSerializer.Deserialize<Patient>(json, JsonOptions);

        // Assert
        patient.Should().NotBeNull();
        patient!.ResourceType.Should().Be("Patient");
        patient.Id.Should().Be("hedis-core-patient-01");
    }

    [Fact]
    public void DeserializedPatient_HasCorrectMetadata()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");

        // Act
        var patient = JsonSerializer.Deserialize<Patient>(json, JsonOptions);

        // Assert
        patient!.Meta.Should().NotBeNull();
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
        var patient = JsonSerializer.Deserialize<Patient>(json, JsonOptions);

        // Assert
        patient!.Gender.Should().Be("female");
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
        var patient = JsonSerializer.Deserialize<Patient>(json, JsonOptions);

        // Assert - Name is an array
        patient!.Name.Should().NotBeNull();
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
        var patient = JsonSerializer.Deserialize<Patient>(json, JsonOptions);

        // Assert - Address is an array
        patient!.Address.Should().NotBeNull();
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
        var patient = JsonSerializer.Deserialize<Patient>(json, JsonOptions);

        // Assert - Telecom is an array
        patient!.Telecom.Should().NotBeNull();
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
        var patient = JsonSerializer.Deserialize<Patient>(json, JsonOptions);

        // Assert - Communication is an array
        patient!.Communication.Should().NotBeNull();
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
        var patient = JsonSerializer.Deserialize<Patient>(json, JsonOptions);
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
        var patient = JsonSerializer.Deserialize<Patient>(json, JsonOptions);

        // Assert - Identifier is an array
        patient!.Identifier.Should().NotBeNull();
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
        var patient = JsonSerializer.Deserialize<Patient>(json, JsonOptions);

        // Assert - Extension is an array with HEDIS-specific extensions
        patient!.Extension.Should().NotBeNull();
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
    public void CanRoundTripPatient()
    {
        // Arrange
        var json = File.ReadAllText("TestData/Patient-hedis-core-patient-01.json");
        var patient = JsonSerializer.Deserialize<Patient>(json, JsonOptions);

        // Act - Serialize back to JSON
        var serialized = JsonSerializer.Serialize(patient, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        // Assert - Can deserialize again
        var roundTripped = JsonSerializer.Deserialize<Patient>(serialized, JsonOptions);
        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(patient!.Id);
        roundTripped.ResourceType.Should().Be(patient.ResourceType);
        roundTripped.Name.Should().HaveCount(patient.Name!.Count);
    }
}
