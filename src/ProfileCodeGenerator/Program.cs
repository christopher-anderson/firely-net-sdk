using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Task = System.Threading.Tasks.Task;

namespace ProfileCodeGenerator;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("FHIR Profile Code Generator");
        Console.WriteLine("===========================");
        Console.WriteLine();

        if (args.Length == 0)
        {
            PrintUsage();

            // Demo: Generate from the base Patient StructureDefinition
            Console.WriteLine("Running demo with US Core Patient profile simulation...");
            Console.WriteLine();

            DemoGeneration();
            return;
        }

        // Check for HEDIS batch mode
        if (args[0] == "--hedis" || args[0] == "-h")
        {
            await RunHedisBatchGeneration(args);
            return;
        }

        var profilePath = args[0];
        var outputPath = args.Length > 1 ? args[1] : null;
        var options = ParseOptions(args);

        await GenerateFromFile(profilePath, outputPath, options);
    }

    static async Task RunHedisBatchGeneration(string[] args)
    {
        // Default paths
        var profilesPath = args.Length > 1 ? args[1] : @"NCQA_Profiles\ncqa.hedis.core";
        var outputPath = args.Length > 2 ? args[2] : @"..\..\..\..\Ncqa.Hedis.Core.2025";
        var @namespace = "Ncqa.Hedis.Core._2025";

        // Parse namespace option
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--namespace" && i + 1 < args.Length)
            {
                @namespace = args[++i];
            }
        }

        Console.WriteLine("HEDIS Core Profile Batch Generation");
        Console.WriteLine("====================================");
        Console.WriteLine();

        var generator = new HedisProfileBatchGenerator(profilesPath, outputPath, @namespace);
        await generator.GenerateAllAsync();
    }

    static void PrintUsage()
    {
        Console.WriteLine("Usage: ProfileCodeGenerator <profile.json> [output.cs] [options]");
        Console.WriteLine();
        Console.WriteLine("Single Profile Mode:");
        Console.WriteLine("  --namespace <ns>      Set the namespace (default: Generated.Fhir)");
        Console.WriteLine("  --class <name>        Override the class name");
        Console.WriteLine("  --must-support        Only include MustSupport elements");
        Console.WriteLine("  --constrained         Only include constrained elements");
        Console.WriteLine("  --include-extensions  Include extension elements");
        Console.WriteLine("  --include-metadata    Include meta, text, contained elements");
        Console.WriteLine();
        Console.WriteLine("HEDIS Batch Mode:");
        Console.WriteLine("  --hedis [profiles-path] [output-path] [--namespace <ns>]");
        Console.WriteLine("  -h      Same as --hedis");
        Console.WriteLine();
        Console.WriteLine("  Generates DTOs from all HEDIS Core profiles.");
        Console.WriteLine("  Default profiles-path: NCQA_Profiles\\ncqa.hedis.core");
        Console.WriteLine("  Default output-path:   ..\\..\\..\\..\\Ncqa.Hedis.Core.2025");
        Console.WriteLine("  Default namespace:     Ncqa.Hedis.Core._2025");
        Console.WriteLine();
    }

    static GeneratorOptions ParseOptions(string[] args)
    {
        var options = new GeneratorOptions();

        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--namespace" when i + 1 < args.Length:
                    options.Namespace = args[++i];
                    break;
                case "--class" when i + 1 < args.Length:
                    options.ClassNameOverride = args[++i];
                    break;
                case "--must-support":
                    options.MustSupportOnly = true;
                    break;
                case "--constrained":
                    options.ConstrainedOnly = true;
                    break;
                case "--include-extensions":
                    options.IncludeExtensions = true;
                    break;
                case "--include-metadata":
                    options.IncludeMetadata = true;
                    break;
            }
        }

        return options;
    }

    static async Task GenerateFromFile(string profilePath, string? outputPath, GeneratorOptions options)
    {
        Console.WriteLine($"Reading profile from: {profilePath}");

        var json = await File.ReadAllTextAsync(profilePath);
        var parser = new FhirJsonParser();
        var profile = parser.Parse<StructureDefinition>(json);

        Console.WriteLine($"Profile: {profile.Name} ({profile.Url})");
        Console.WriteLine($"Base: {profile.BaseDefinition}");
        Console.WriteLine($"Type: {profile.Type}");
        Console.WriteLine();

        var generator = new FhirProfileCodeGenerator(options);
        var code = generator.Generate(profile);

        if (outputPath != null)
        {
            await File.WriteAllTextAsync(outputPath, code);
            Console.WriteLine($"Generated code written to: {outputPath}");
        }
        else
        {
            Console.WriteLine("Generated Code:");
            Console.WriteLine("================");
            Console.WriteLine(code);
        }
    }

    static void DemoGeneration()
    {
        // Create a sample profile that mimics US Core Patient requirements
        var profile = new StructureDefinition
        {
            Url = "http://example.org/fhir/StructureDefinition/MyPatient",
            Name = "MyPatient",
            Type = "Patient",
            Status = PublicationStatus.Active,
            Kind = StructureDefinition.StructureDefinitionKind.Resource,
            Abstract = false,
            BaseDefinition = "http://hl7.org/fhir/StructureDefinition/Patient",
            Derivation = StructureDefinition.TypeDerivationRule.Constraint,
            Snapshot = new StructureDefinition.SnapshotComponent
            {
                Element = new List<ElementDefinition>
                {
                    new() { Path = "Patient", Min = 0, Max = "*" },
                    new() { Path = "Patient.id", Min = 0, Max = "1", Type = new() { new() { Code = "id" } } },
                    new() { Path = "Patient.identifier", Min = 1, Max = "*", MustSupport = true,
                        Type = new() { new() { Code = "Identifier" } }, Short = "Patient identifiers (required)" },
                    new() { Path = "Patient.active", Min = 0, Max = "1",
                        Type = new() { new() { Code = "boolean" } }, Short = "Whether patient is active" },
                    new() { Path = "Patient.name", Min = 1, Max = "*", MustSupport = true,
                        Type = new() { new() { Code = "HumanName" } }, Short = "Patient name (required)" },
                    new() { Path = "Patient.telecom", Min = 0, Max = "*",
                        Type = new() { new() { Code = "ContactPoint" } }, Short = "Contact details" },
                    new() { Path = "Patient.gender", Min = 1, Max = "1", MustSupport = true,
                        Type = new() { new() { Code = "code" } }, Short = "male | female | other | unknown (required)" },
                    new() { Path = "Patient.birthDate", Min = 1, Max = "1", MustSupport = true,
                        Type = new() { new() { Code = "date" } }, Short = "Date of birth (required)" },
                    new() { Path = "Patient.address", Min = 0, Max = "*", MustSupport = true,
                        Type = new() { new() { Code = "Address" } }, Short = "Patient addresses" },
                    new() { Path = "Patient.maritalStatus", Min = 0, Max = "1",
                        Type = new() { new() { Code = "CodeableConcept" } }, Short = "Marital status" },
                    new() { Path = "Patient.contact", Min = 0, Max = "*", Short = "Emergency contacts" },
                    new() { Path = "Patient.contact.relationship", Min = 0, Max = "*",
                        Type = new() { new() { Code = "CodeableConcept" } }, Short = "Relationship to patient" },
                    new() { Path = "Patient.contact.name", Min = 0, Max = "1",
                        Type = new() { new() { Code = "HumanName" } }, Short = "Contact name" },
                    new() { Path = "Patient.contact.telecom", Min = 0, Max = "*",
                        Type = new() { new() { Code = "ContactPoint" } }, Short = "Contact details" },
                    new() { Path = "Patient.communication", Min = 0, Max = "*", Short = "Language preferences" },
                    new() { Path = "Patient.communication.language", Min = 1, Max = "1", MustSupport = true,
                        Type = new() { new() { Code = "CodeableConcept" } }, Short = "Language code" },
                    new() { Path = "Patient.communication.preferred", Min = 0, Max = "1",
                        Type = new() { new() { Code = "boolean" } }, Short = "Is preferred language" },
                    new() { Path = "Patient.generalPractitioner", Min = 0, Max = "*",
                        Type = new() { new() { Code = "Reference", TargetProfile = new[] { "http://hl7.org/fhir/StructureDefinition/Practitioner" } } },
                        Short = "Primary care providers" },
                    new() { Path = "Patient.managingOrganization", Min = 0, Max = "1",
                        Type = new() { new() { Code = "Reference" } }, Short = "Managing organization" },
                }
            }
        };

        // Generate with MustSupport filter
        Console.WriteLine("=== Generated DTO (MustSupport elements only) ===");
        Console.WriteLine();

        var options = new GeneratorOptions
        {
            Namespace = "MyApp.Fhir.Models",
            MustSupportOnly = true
        };

        var generator = new FhirProfileCodeGenerator(options);
        var mustSupportCode = generator.Generate(profile);
        Console.WriteLine(mustSupportCode);

        // Generate with all elements
        Console.WriteLine();
        Console.WriteLine("=== Generated DTO (all elements) ===");
        Console.WriteLine();

        options = new GeneratorOptions
        {
            Namespace = "MyApp.Fhir.Models",
            MustSupportOnly = false
        };

        generator = new FhirProfileCodeGenerator(options);
        var allElementsCode = generator.Generate(profile);
        Console.WriteLine(allElementsCode);

        // Show example usage
        Console.WriteLine();
        Console.WriteLine("=== Example Usage ===");
        Console.WriteLine(@"
// Fast parallel deserialization with generated DTOs:

var jsonStrings = GetPatientJsonStrings(); // Your FHIR data

var patients = jsonStrings
    .AsParallel()
    .WithDegreeOfParallelism(Environment.ProcessorCount)
    .Select(json => JsonSerializer.Deserialize<MyPatient>(json))
    .ToList();

// Process only the fields you need - no wasted memory or CPU cycles!
foreach (var patient in patients)
{
    Console.WriteLine($""Patient: {patient.Name?.FirstOrDefault()?.Family}"");
    Console.WriteLine($""DOB: {patient.BirthDate}"");
    Console.WriteLine($""Gender: {patient.Gender}"");
}
");
    }
}
