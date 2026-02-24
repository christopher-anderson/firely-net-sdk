using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Hl7.Fhir.Serialization;

namespace Ncqa.Hedis.Core.Benchmarks;

/// <summary>
/// Benchmarks comparing FHIR Bundle deserialization performance between:
/// - Ncqa.Hedis.Core lightweight DTOs with System.Text.Json
/// - Firely SDK POCOs with FhirJsonParser
///
/// Uses a real HEDIS patient bundle (~31KB, 21 resources) from the Demo data.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class BundleBenchmarks
{
    private string _bundleJson = null!;
    private JsonSerializerOptions _jsonOptions = null!;
    private FhirJsonParser _fhirParser = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Configure System.Text.Json options
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // Configure Firely parser
        _fhirParser = new FhirJsonParser(new ParserSettings
        {
            PermissiveParsing = true
        });

        // Load the real HEDIS patient bundle from the firely-cql-sdk Demo folder
        // This is a ~31KB bundle with 21 resources (Patient, Claims, EOB, Coverage, etc.)
        var bundlePath = Path.Combine(
            Environment.GetEnvironmentVariable("FIRELY_CQL_SDK_PATH")
            ?? @"C:\Development\Projects\firely-cql-sdk",
            "Demo", "CLI", "Data", "95085.json");

        if (File.Exists(bundlePath))
        {
            _bundleJson = File.ReadAllText(bundlePath);
        }
        else
        {
            // Fallback: use embedded sample bundle if file not found
            _bundleJson = CreateSampleBundle();
        }
    }

    [Benchmark(Description = "Bundle - Hedis DTO (System.Text.Json)")]
    public Bundle? DeserializeBundle_HedisDto()
    {
        return JsonSerializer.Deserialize<Bundle>(_bundleJson, _jsonOptions);
    }

    [Benchmark(Description = "Bundle - Firely POCO (FhirJsonParser)", Baseline = true)]
    public Hl7.Fhir.Model.Bundle? DeserializeBundle_FirelyPoco()
    {
        return _fhirParser.Parse<Hl7.Fhir.Model.Bundle>(_bundleJson);
    }

    /// <summary>
    /// Creates a sample bundle for fallback testing if the real file isn't found.
    /// </summary>
    private static string CreateSampleBundle()
    {
        return """
        {
            "resourceType": "Bundle",
            "id": "sample-bundle",
            "type": "collection",
            "entry": [
                {
                    "fullUrl": "http://example.org/Patient/001",
                    "resource": {
                        "resourceType": "Patient",
                        "id": "001",
                        "identifier": [
                            {
                                "system": "http://example.org/memberid",
                                "value": "MEM001"
                            }
                        ],
                        "name": [
                            {
                                "family": "Smith",
                                "given": ["John"]
                            }
                        ],
                        "gender": "male",
                        "birthDate": "1985-03-15"
                    }
                },
                {
                    "fullUrl": "http://example.org/Coverage/001",
                    "resource": {
                        "resourceType": "Coverage",
                        "id": "001",
                        "status": "active",
                        "beneficiary": {
                            "reference": "Patient/001"
                        },
                        "period": {
                            "start": "2024-01-01",
                            "end": "2024-12-31"
                        }
                    }
                },
                {
                    "fullUrl": "http://example.org/Claim/001",
                    "resource": {
                        "resourceType": "Claim",
                        "id": "001",
                        "status": "active",
                        "type": {
                            "coding": [
                                {
                                    "system": "http://terminology.hl7.org/CodeSystem/claim-type",
                                    "code": "institutional"
                                }
                            ]
                        },
                        "use": "claim",
                        "patient": {
                            "reference": "Patient/001"
                        },
                        "created": "2024-01-15",
                        "provider": {
                            "reference": "Organization/001"
                        },
                        "priority": {
                            "coding": [
                                {
                                    "code": "normal"
                                }
                            ]
                        },
                        "insurance": [
                            {
                                "sequence": 1,
                                "focal": true,
                                "coverage": {
                                    "reference": "Coverage/001"
                                }
                            }
                        ]
                    }
                }
            ]
        }
        """;
    }
}
