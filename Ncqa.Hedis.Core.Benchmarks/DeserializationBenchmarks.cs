using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Hl7.Fhir.Serialization;

namespace Ncqa.Hedis.Core.Benchmarks;

/// <summary>
/// Benchmarks comparing deserialization performance between:
/// - Ncqa.Hedis.Core lightweight DTOs with System.Text.Json
/// - Firely SDK POCOs with FhirJsonParser
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class DeserializationBenchmarks
{
    private string _patientJson = null!;
    private string _observationJson = null!;
    private string _claimJson = null!;
    private string[] _patientJsonBatch = null!;

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

        // Sample Patient JSON (HEDIS-compatible)
        _patientJson = """
        {
            "resourceType": "Patient",
            "id": "patient-001",
            "meta": {
                "profile": ["https://ncqa.org/fhir/StructureDefinition/hedis-core-patient"],
                "lastUpdated": "2024-01-15T10:30:00Z"
            },
            "identifier": [
                {
                    "system": "http://example.org/memberid",
                    "value": "MEM123456"
                },
                {
                    "system": "http://example.org/mrn",
                    "value": "MRN789012"
                }
            ],
            "name": [
                {
                    "use": "official",
                    "family": "Smith",
                    "given": ["John", "Michael"]
                }
            ],
            "gender": "male",
            "birthDate": "1985-03-15",
            "address": [
                {
                    "use": "home",
                    "line": ["123 Main Street", "Apt 4B"],
                    "city": "Boston",
                    "state": "MA",
                    "postalCode": "02101",
                    "country": "USA"
                }
            ],
            "telecom": [
                {
                    "system": "phone",
                    "value": "555-123-4567",
                    "use": "home"
                },
                {
                    "system": "email",
                    "value": "john.smith@example.com"
                }
            ]
        }
        """;

        // Sample Observation JSON (Lab Result)
        _observationJson = """
        {
            "resourceType": "Observation",
            "id": "obs-001",
            "meta": {
                "profile": ["https://ncqa.org/fhir/StructureDefinition/hedis-core-laboratory-result-observation"]
            },
            "status": "final",
            "category": [
                {
                    "coding": [
                        {
                            "system": "http://terminology.hl7.org/CodeSystem/observation-category",
                            "code": "laboratory",
                            "display": "Laboratory"
                        }
                    ]
                }
            ],
            "code": {
                "coding": [
                    {
                        "system": "http://loinc.org",
                        "code": "4548-4",
                        "display": "Hemoglobin A1c/Hemoglobin.total in Blood"
                    }
                ],
                "text": "HbA1c"
            },
            "subject": {
                "reference": "Patient/patient-001"
            },
            "effectiveDateTime": "2024-01-10T08:30:00Z",
            "valueQuantity": {
                "value": 6.5,
                "unit": "%",
                "system": "http://unitsofmeasure.org",
                "code": "%"
            },
            "performer": [
                {
                    "reference": "Organization/lab-001"
                }
            ]
        }
        """;

        // Sample Claim JSON (Inpatient Institutional)
        _claimJson = """
        {
            "resourceType": "Claim",
            "id": "claim-001",
            "meta": {
                "profile": ["https://ncqa.org/fhir/StructureDefinition/hedis-core-claim-inpatient-institutional"]
            },
            "status": "active",
            "type": {
                "coding": [
                    {
                        "system": "http://terminology.hl7.org/CodeSystem/claim-type",
                        "code": "institutional"
                    }
                ]
            },
            "subType": {
                "coding": [
                    {
                        "system": "http://hl7.org/fhir/us/carin-bb/CodeSystem/C4BBInstitutionalClaimSubType",
                        "code": "inpatient"
                    }
                ]
            },
            "use": "claim",
            "patient": {
                "reference": "Patient/patient-001"
            },
            "billablePeriod": {
                "start": "2024-01-05",
                "end": "2024-01-10"
            },
            "created": "2024-01-15",
            "provider": {
                "reference": "Organization/provider-001"
            },
            "priority": {
                "coding": [
                    {
                        "system": "http://terminology.hl7.org/CodeSystem/processpriority",
                        "code": "normal"
                    }
                ]
            },
            "diagnosis": [
                {
                    "sequence": 1,
                    "diagnosisCodeableConcept": {
                        "coding": [
                            {
                                "system": "http://hl7.org/fhir/sid/icd-10-cm",
                                "code": "E11.9",
                                "display": "Type 2 diabetes mellitus without complications"
                            }
                        ]
                    },
                    "type": [
                        {
                            "coding": [
                                {
                                    "system": "http://terminology.hl7.org/CodeSystem/ex-diagnosistype",
                                    "code": "principal"
                                }
                            ]
                        }
                    ]
                }
            ],
            "insurance": [
                {
                    "sequence": 1,
                    "focal": true,
                    "coverage": {
                        "reference": "Coverage/coverage-001"
                    }
                }
            ],
            "item": [
                {
                    "sequence": 1,
                    "productOrService": {
                        "coding": [
                            {
                                "system": "http://www.ama-assn.org/go/cpt",
                                "code": "99213"
                            }
                        ]
                    },
                    "servicedDate": "2024-01-05",
                    "quantity": {
                        "value": 1
                    }
                }
            ]
        }
        """;

        // Create batch of patients for parallel testing
        _patientJsonBatch = Enumerable.Range(1, 1000)
            .Select(i => _patientJson.Replace("patient-001", $"patient-{i:D4}"))
            .ToArray();
    }

    // ============================================
    // Single Patient Deserialization
    // ============================================

    [Benchmark(Description = "Patient - Hedis DTO (System.Text.Json)")]
    public Patient? DeserializePatient_HedisDto()
    {
        return JsonSerializer.Deserialize<Patient>(_patientJson, _jsonOptions);
    }

    [Benchmark(Description = "Patient - Firely POCO (FhirJsonParser)", Baseline = true)]
    public Hl7.Fhir.Model.Patient? DeserializePatient_FirelyPoco()
    {
        return _fhirParser.Parse<Hl7.Fhir.Model.Patient>(_patientJson);
    }

    // ============================================
    // Single Observation Deserialization
    // ============================================

    [Benchmark(Description = "Observation - Hedis DTO (System.Text.Json)")]
    public Observation? DeserializeObservation_HedisDto()
    {
        return JsonSerializer.Deserialize<Observation>(_observationJson, _jsonOptions);
    }

    [Benchmark(Description = "Observation - Firely POCO (FhirJsonParser)")]
    public Hl7.Fhir.Model.Observation? DeserializeObservation_FirelyPoco()
    {
        return _fhirParser.Parse<Hl7.Fhir.Model.Observation>(_observationJson);
    }

    // ============================================
    // Single Claim Deserialization
    // ============================================

    [Benchmark(Description = "Claim - Hedis DTO (System.Text.Json)")]
    public Claim? DeserializeClaim_HedisDto()
    {
        return JsonSerializer.Deserialize<Claim>(_claimJson, _jsonOptions);
    }

    [Benchmark(Description = "Claim - Firely POCO (FhirJsonParser)")]
    public Hl7.Fhir.Model.Claim? DeserializeClaim_FirelyPoco()
    {
        return _fhirParser.Parse<Hl7.Fhir.Model.Claim>(_claimJson);
    }

    // ============================================
    // Batch Patient Deserialization (1000 patients)
    // ============================================

    [Benchmark(Description = "1000 Patients - Hedis DTO Sequential")]
    public List<Patient?> DeserializePatientBatch_HedisDto_Sequential()
    {
        var results = new List<Patient?>(_patientJsonBatch.Length);
        foreach (var json in _patientJsonBatch)
        {
            results.Add(JsonSerializer.Deserialize<Patient>(json, _jsonOptions));
        }
        return results;
    }

    [Benchmark(Description = "1000 Patients - Firely POCO Sequential")]
    public List<Hl7.Fhir.Model.Patient?> DeserializePatientBatch_FirelyPoco_Sequential()
    {
        var results = new List<Hl7.Fhir.Model.Patient?>(_patientJsonBatch.Length);
        foreach (var json in _patientJsonBatch)
        {
            results.Add(_fhirParser.Parse<Hl7.Fhir.Model.Patient>(json));
        }
        return results;
    }

    // ============================================
    // Parallel Batch Deserialization (1000 patients)
    // ============================================

    [Benchmark(Description = "1000 Patients - Hedis DTO Parallel")]
    public Patient?[] DeserializePatientBatch_HedisDto_Parallel()
    {
        return _patientJsonBatch
            .AsParallel()
            .Select(json => JsonSerializer.Deserialize<Patient>(json, _jsonOptions))
            .ToArray();
    }

    [Benchmark(Description = "1000 Patients - Firely POCO Parallel")]
    public Hl7.Fhir.Model.Patient?[] DeserializePatientBatch_FirelyPoco_Parallel()
    {
        // Note: FhirJsonParser may have thread-safety considerations
        return _patientJsonBatch
            .AsParallel()
            .Select(json =>
            {
                // Create new parser per thread for thread safety
                var parser = new FhirJsonParser(new ParserSettings { PermissiveParsing = true });
                return parser.Parse<Hl7.Fhir.Model.Patient>(json);
            })
            .ToArray();
    }
}
