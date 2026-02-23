using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProfileCodeGenerator;

/// <summary>
/// Batch generator specifically for NCQA HEDIS Core profiles.
/// Generates lightweight DTOs from differential-based profiles.
/// </summary>
public class HedisProfileBatchGenerator
{
    private readonly string _profilesPath;
    private readonly string _outputPath;
    private readonly string _namespace;
    private string _currentClassPrefix = "";

    public HedisProfileBatchGenerator(string profilesPath, string outputPath, string @namespace)
    {
        _profilesPath = profilesPath;
        _outputPath = outputPath;
        _namespace = @namespace;
    }

    /// <summary>
    /// Generate DTOs from all HEDIS core profiles.
    /// </summary>
    public async Task GenerateAllAsync()
    {
        Console.WriteLine($"Reading profiles from: {_profilesPath}");
        Console.WriteLine($"Output path: {_outputPath}");
        Console.WriteLine();

        // Find all hedis-core-*.json files (StructureDefinition profiles)
        var profileFiles = Directory.GetFiles(_profilesPath, "hedis-core-*.json")
            .Where(f => !Path.GetFileName(f).StartsWith("Claim-") &&
                       !Path.GetFileName(f).StartsWith("Coverage-") &&
                       !Path.GetFileName(f).StartsWith("Bundle-") &&
                       !Path.GetFileName(f).StartsWith("Condition-") &&
                       !Path.GetFileName(f).StartsWith("DiagnosticReport-") &&
                       !Path.GetFileName(f).StartsWith("DocumentReference-") &&
                       !Path.GetFileName(f).StartsWith("Encounter-") &&
                       !Path.GetFileName(f).StartsWith("ExplanationOfBenefit-") &&
                       !Path.GetFileName(f).StartsWith("Immunization-") &&
                       !Path.GetFileName(f).StartsWith("Location-") &&
                       !Path.GetFileName(f).StartsWith("Medication-") &&
                       !Path.GetFileName(f).StartsWith("Observation-") &&
                       !Path.GetFileName(f).StartsWith("Organization-") &&
                       !Path.GetFileName(f).StartsWith("Patient-") &&
                       !Path.GetFileName(f).StartsWith("Practitioner-") &&
                       !Path.GetFileName(f).StartsWith("Procedure-") &&
                       !Path.GetFileName(f).StartsWith("QuestionnaireResponse-") &&
                       !Path.GetFileName(f).StartsWith("RelatedPerson-"))
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        Console.WriteLine($"Found {profileFiles.Count} profile definitions:");
        foreach (var file in profileFiles)
        {
            Console.WriteLine($"  - {Path.GetFileName(file)}");
        }
        Console.WriteLine();

        // Create output directory if it doesn't exist
        Directory.CreateDirectory(_outputPath);

        // Generate each profile
        var generatedFiles = new List<string>();
        foreach (var profileFile in profileFiles)
        {
            try
            {
                var result = await GenerateFromProfileAsync(profileFile);
                if (result != null)
                {
                    generatedFiles.Add(result);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing {Path.GetFileName(profileFile)}: {ex.Message}");
            }
        }

        // Generate common DTOs file
        await GenerateCommonDtosAsync();
        generatedFiles.Add("CommonDtos.cs");

        Console.WriteLine();
        Console.WriteLine($"Generated {generatedFiles.Count} files:");
        foreach (var file in generatedFiles)
        {
            Console.WriteLine($"  - {file}");
        }
    }

    private async Task<string?> GenerateFromProfileAsync(string profilePath)
    {
        var json = await File.ReadAllTextAsync(profilePath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify this is a StructureDefinition
        if (!root.TryGetProperty("resourceType", out var resourceType) ||
            resourceType.GetString() != "StructureDefinition")
        {
            return null;
        }

        var profileName = root.GetProperty("name").GetString() ?? "Unknown";
        var profileType = root.GetProperty("type").GetString() ?? "Resource";
        var profileUrl = root.GetProperty("url").GetString() ?? "";

        Console.WriteLine($"Processing: {profileName} ({profileType})");

        // Get differential elements
        if (!root.TryGetProperty("differential", out var differential) ||
            !differential.TryGetProperty("element", out var elements))
        {
            Console.WriteLine($"  Skipping - no differential elements");
            return null;
        }

        // Generate the DTO class
        var code = GenerateDto(profileName, profileType, profileUrl, elements);

        // Write to file
        var fileName = $"{profileName}.cs";
        var outputFile = Path.Combine(_outputPath, fileName);
        await File.WriteAllTextAsync(outputFile, code);

        return fileName;
    }

    private string GenerateDto(string profileName, string profileType, string profileUrl, JsonElement elements)
    {
        var sb = new StringBuilder();

        // Create a short prefix for component classes to avoid conflicts
        var classPrefix = profileName.Replace("HEDISCore", "");

        // File header
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"// Generated from HEDIS Profile: {profileUrl}");
        sb.AppendLine($"// Generated at: {DateTime.UtcNow:O}");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_namespace};");
        sb.AppendLine();

        // Generate summary
        sb.AppendLine($"/// <summary>");
        sb.AppendLine($"/// HEDIS Core {profileType} DTO.");
        sb.AppendLine($"/// <para>Profile: {profileUrl}</para>");
        sb.AppendLine($"/// </summary>");
        sb.AppendLine($"public sealed class {profileName}");
        sb.AppendLine("{");

        // Always include resourceType for FHIR resources
        sb.AppendLine("    /// <summary>The FHIR resource type.</summary>");
        sb.AppendLine("    [JsonPropertyName(\"resourceType\")]");
        sb.AppendLine($"    public string ResourceType {{ get; set; }} = \"{profileType}\";");
        sb.AppendLine();

        // Always include id
        sb.AppendLine("    /// <summary>Logical id of this artifact.</summary>");
        sb.AppendLine("    [JsonPropertyName(\"id\")]");
        sb.AppendLine("    public string? Id { get; set; }");
        sb.AppendLine();

        // Store prefix for later use
        _currentClassPrefix = classPrefix;

        // Track which properties we've already generated
        var generatedProperties = new HashSet<string> { "resourceType", "id" };
        var backboneElements = new Dictionary<string, List<JsonElement>>();

        // First pass: identify backbone elements
        foreach (var element in elements.EnumerateArray())
        {
            var path = element.GetProperty("path").GetString() ?? "";
            var parts = path.Split('.');

            if (parts.Length > 2)
            {
                // This is a nested element (e.g., Patient.contact.name)
                var parentPath = string.Join(".", parts.Take(parts.Length - 1));

                // Skip choice type paths (e.g., value[x].unit)
                if (parentPath.Contains("[x]")) continue;

                if (!backboneElements.ContainsKey(parentPath))
                {
                    backboneElements[parentPath] = new List<JsonElement>();
                }
                backboneElements[parentPath].Add(element);
            }
        }

        // Second pass: generate properties
        foreach (var element in elements.EnumerateArray())
        {
            var path = element.GetProperty("path").GetString() ?? "";
            var parts = path.Split('.');

            // Skip the root element
            if (parts.Length < 2) continue;

            // Only generate top-level properties (depth 1)
            if (parts.Length != 2) continue;

            var propName = parts[1];

            // Handle choice types (e.g., "value[x]" -> "value")
            if (propName.EndsWith("[x]"))
            {
                propName = propName[..^3];
            }

            // Handle slices (e.g., "identifier:memberid")
            if (propName.Contains(':'))
            {
                propName = propName.Split(':')[0];
            }

            // Skip extensions for now (handled separately)
            if (propName == "extension" || propName == "modifierExtension") continue;

            // Skip if already generated
            if (generatedProperties.Contains(propName)) continue;
            generatedProperties.Add(propName);

            // Skip backbone elements that are choice types - don't generate nested classes for them
            var isBackbone = backboneElements.ContainsKey(path) && !path.EndsWith("[x]");
            GenerateProperty(sb, element, propName, isBackbone);
        }

        sb.AppendLine("}");

        // Generate nested component classes with unique names
        foreach (var (parentPath, childElements) in backboneElements)
        {
            var parts = parentPath.Split('.');
            if (parts.Length == 2)
            {
                var propPart = parts[1];
                // Skip choice types
                if (propPart.EndsWith("[x]")) continue;

                // Prefix with class name to avoid conflicts
                var componentName = _currentClassPrefix + ToPascalCase(propPart) + "Component";
                GenerateComponentClass(sb, componentName, parentPath, childElements);
            }
        }

        return sb.ToString();
    }

    private void GenerateProperty(StringBuilder sb, JsonElement element, string propName, bool isBackbone)
    {
        var jsonName = propName;
        var pascalName = ToPascalCase(propName);

        // Handle choice types
        if (propName.EndsWith("[x]"))
        {
            propName = propName[..^3];
            jsonName = propName;
            pascalName = ToPascalCase(propName);
        }

        // Get documentation
        var shortDesc = "";
        var definition = "";
        if (element.TryGetProperty("short", out var shortProp))
            shortDesc = shortProp.GetString() ?? "";
        if (element.TryGetProperty("definition", out var defProp))
            definition = defProp.GetString() ?? "";

        var description = !string.IsNullOrEmpty(shortDesc) ? shortDesc : definition;
        if (!string.IsNullOrEmpty(description))
        {
            sb.AppendLine($"    /// <summary>{EscapeXml(description)}</summary>");
        }

        // Get type info
        var csharpType = "object";
        var isCollection = false;
        var isRequired = false;

        if (element.TryGetProperty("type", out var types) && types.GetArrayLength() > 0)
        {
            var firstType = types[0];
            if (firstType.TryGetProperty("code", out var code))
            {
                csharpType = MapFhirTypeToCSharp(code.GetString() ?? "object");
            }
        }
        else if (isBackbone)
        {
            // Use prefixed component name to avoid conflicts
            csharpType = _currentClassPrefix + pascalName + "Component";
        }

        if (element.TryGetProperty("max", out var max))
        {
            var maxVal = max.GetString() ?? "1";
            isCollection = maxVal != "1" && maxVal != "0";
        }

        if (element.TryGetProperty("min", out var min))
        {
            isRequired = min.GetInt32() > 0;
        }

        // Generate the property
        sb.AppendLine($"    [JsonPropertyName(\"{jsonName}\")]");

        string finalType;
        if (isCollection)
        {
            finalType = $"List<{csharpType}>?";
        }
        else if (IsValueType(csharpType))
        {
            finalType = isRequired ? csharpType : $"{csharpType}?";
        }
        else
        {
            finalType = $"{csharpType}?";
        }

        sb.AppendLine($"    public {finalType} {pascalName} {{ get; set; }}");
        sb.AppendLine();
    }

    private void GenerateComponentClass(StringBuilder sb, string className, string parentPath, List<JsonElement> elements)
    {
        sb.AppendLine();
        sb.AppendLine($"/// <summary>");
        sb.AppendLine($"/// Component class for {parentPath}");
        sb.AppendLine($"/// </summary>");
        sb.AppendLine($"public sealed class {className}");
        sb.AppendLine("{");

        var generatedProps = new HashSet<string>();

        foreach (var element in elements)
        {
            var path = element.GetProperty("path").GetString() ?? "";
            var parts = path.Split('.');
            var propName = parts.Last();

            // Handle choice types
            if (propName.EndsWith("[x]"))
            {
                propName = propName[..^3];
            }

            // Handle slices
            if (propName.Contains(':'))
            {
                propName = propName.Split(':')[0];
            }

            if (generatedProps.Contains(propName)) continue;
            generatedProps.Add(propName);

            GenerateProperty(sb, element, propName, false);
        }

        sb.AppendLine("}");
    }

    private async Task GenerateCommonDtosAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Common FHIR DTOs for HEDIS Core profiles.");
        sb.AppendLine($"// Generated at: {DateTime.UtcNow:O}");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_namespace};");
        sb.AppendLine();

        // HumanName
        sb.AppendLine("/// <summary>A name of a human with text, parts and usage information.</summary>");
        sb.AppendLine("public sealed class HumanNameDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"use\")] public string? Use { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"text\")] public string? Text { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"family\")] public string? Family { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"given\")] public List<string>? Given { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"prefix\")] public List<string>? Prefix { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"suffix\")] public List<string>? Suffix { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public PeriodDto? Period { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Address
        sb.AppendLine("/// <summary>An address expressed using postal conventions.</summary>");
        sb.AppendLine("public sealed class AddressDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"use\")] public string? Use { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public string? Type { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"text\")] public string? Text { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"line\")] public List<string>? Line { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"city\")] public string? City { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"district\")] public string? District { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"state\")] public string? State { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"postalCode\")] public string? PostalCode { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"country\")] public string? Country { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public PeriodDto? Period { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ContactPoint
        sb.AppendLine("/// <summary>Details for all kinds of technology-mediated contact points.</summary>");
        sb.AppendLine("public sealed class ContactPointDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"system\")] public string? System { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"value\")] public string? Value { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"use\")] public string? Use { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"rank\")] public int? Rank { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public PeriodDto? Period { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Identifier
        sb.AppendLine("/// <summary>An identifier intended for computation.</summary>");
        sb.AppendLine("public sealed class IdentifierDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"use\")] public string? Use { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public CodeableConceptDto? Type { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"system\")] public string? System { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"value\")] public string? Value { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public PeriodDto? Period { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"assigner\")] public ReferenceDto? Assigner { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // CodeableConcept
        sb.AppendLine("/// <summary>Concept - reference to a terminology or just text.</summary>");
        sb.AppendLine("public sealed class CodeableConceptDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"coding\")] public List<CodingDto>? Coding { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"text\")] public string? Text { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Coding
        sb.AppendLine("/// <summary>A reference to a code defined by a terminology system.</summary>");
        sb.AppendLine("public sealed class CodingDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"system\")] public string? System { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"version\")] public string? Version { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"code\")] public string? Code { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"display\")] public string? Display { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"userSelected\")] public bool? UserSelected { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Quantity
        sb.AppendLine("/// <summary>A measured or measurable amount.</summary>");
        sb.AppendLine("public sealed class QuantityDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"value\")] public decimal? Value { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"comparator\")] public string? Comparator { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"unit\")] public string? Unit { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"system\")] public string? System { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"code\")] public string? Code { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Period
        sb.AppendLine("/// <summary>Time range defined by start and end date/time.</summary>");
        sb.AppendLine("public sealed class PeriodDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"start\")] public string? Start { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"end\")] public string? End { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Reference
        sb.AppendLine("/// <summary>A reference from one resource to another.</summary>");
        sb.AppendLine("public sealed class ReferenceDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"reference\")] public string? Reference { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public string? Type { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"identifier\")] public IdentifierDto? Identifier { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"display\")] public string? Display { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Meta
        sb.AppendLine("/// <summary>Metadata about a resource.</summary>");
        sb.AppendLine("public sealed class MetaDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"versionId\")] public string? VersionId { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"lastUpdated\")] public string? LastUpdated { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"source\")] public string? Source { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"profile\")] public List<string>? Profile { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"security\")] public List<CodingDto>? Security { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"tag\")] public List<CodingDto>? Tag { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Attachment
        sb.AppendLine("/// <summary>Content in a format defined elsewhere.</summary>");
        sb.AppendLine("public sealed class AttachmentDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"contentType\")] public string? ContentType { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"language\")] public string? Language { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"data\")] public string? Data { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"url\")] public string? Url { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"size\")] public long? Size { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"hash\")] public string? Hash { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"title\")] public string? Title { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"creation\")] public string? Creation { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Narrative
        sb.AppendLine("/// <summary>A human-readable formatted text, including images.</summary>");
        sb.AppendLine("public sealed class NarrativeDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"status\")] public string? Status { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"div\")] public string? Div { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Extension
        sb.AppendLine("/// <summary>Optional Extensions Element.</summary>");
        sb.AppendLine("public sealed class ExtensionDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"url\")] public string? Url { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valueString\")] public string? ValueString { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valueBoolean\")] public bool? ValueBoolean { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valueInteger\")] public int? ValueInteger { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valueDecimal\")] public decimal? ValueDecimal { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valueCode\")] public string? ValueCode { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valueDate\")] public string? ValueDate { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valueDateTime\")] public string? ValueDateTime { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valueCoding\")] public CodingDto? ValueCoding { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valueCodeableConcept\")] public CodeableConceptDto? ValueCodeableConcept { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valueReference\")] public ReferenceDto? ValueReference { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valuePeriod\")] public PeriodDto? ValuePeriod { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"valueQuantity\")] public QuantityDto? ValueQuantity { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"extension\")] public List<ExtensionDto>? Extension { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Range
        sb.AppendLine("/// <summary>Set of values bounded by low and high.</summary>");
        sb.AppendLine("public sealed class RangeDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"low\")] public QuantityDto? Low { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"high\")] public QuantityDto? High { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Ratio
        sb.AppendLine("/// <summary>A ratio of two Quantity values.</summary>");
        sb.AppendLine("public sealed class RatioDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"numerator\")] public QuantityDto? Numerator { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"denominator\")] public QuantityDto? Denominator { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Annotation
        sb.AppendLine("/// <summary>Text with attribution.</summary>");
        sb.AppendLine("public sealed class AnnotationDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"authorReference\")] public ReferenceDto? AuthorReference { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"authorString\")] public string? AuthorString { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"time\")] public string? Time { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"text\")] public string? Text { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Signature
        sb.AppendLine("/// <summary>A Signature - XML DigSig, JWT, Graphical image of signature, etc.</summary>");
        sb.AppendLine("public sealed class SignatureDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public List<CodingDto>? Type { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"when\")] public string? When { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"who\")] public ReferenceDto? Who { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"onBehalfOf\")] public ReferenceDto? OnBehalfOf { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"targetFormat\")] public string? TargetFormat { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"sigFormat\")] public string? SigFormat { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"data\")] public string? Data { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Timing
        sb.AppendLine("/// <summary>A timing schedule that specifies an event that may occur multiple times.</summary>");
        sb.AppendLine("public sealed class TimingDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"event\")] public List<string>? Event { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"repeat\")] public TimingRepeatDto? Repeat { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"code\")] public CodeableConceptDto? Code { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("/// <summary>Timing repeat component.</summary>");
        sb.AppendLine("public sealed class TimingRepeatDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"boundsDuration\")] public DurationDto? BoundsDuration { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"boundsPeriod\")] public PeriodDto? BoundsPeriod { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"count\")] public int? Count { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"duration\")] public decimal? Duration { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"durationUnit\")] public string? DurationUnit { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"frequency\")] public int? Frequency { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public decimal? Period { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"periodUnit\")] public string? PeriodUnit { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"dayOfWeek\")] public List<string>? DayOfWeek { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"timeOfDay\")] public List<string>? TimeOfDay { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Duration
        sb.AppendLine("/// <summary>A length of time.</summary>");
        sb.AppendLine("public sealed class DurationDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"value\")] public decimal? Value { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"comparator\")] public string? Comparator { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"unit\")] public string? Unit { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"system\")] public string? System { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"code\")] public string? Code { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Age
        sb.AppendLine("/// <summary>A duration of time during which an organism has existed.</summary>");
        sb.AppendLine("public sealed class AgeDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"value\")] public decimal? Value { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"comparator\")] public string? Comparator { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"unit\")] public string? Unit { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"system\")] public string? System { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"code\")] public string? Code { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Money
        sb.AppendLine("/// <summary>An amount of economic utility in some recognized currency.</summary>");
        sb.AppendLine("public sealed class MoneyDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"value\")] public decimal? Value { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"currency\")] public string? Currency { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Dosage
        sb.AppendLine("/// <summary>How the medication is/was taken or should be taken.</summary>");
        sb.AppendLine("public sealed class DosageDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"sequence\")] public int? Sequence { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"text\")] public string? Text { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"timing\")] public TimingDto? Timing { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"asNeededBoolean\")] public bool? AsNeededBoolean { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"asNeededCodeableConcept\")] public CodeableConceptDto? AsNeededCodeableConcept { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"site\")] public CodeableConceptDto? Site { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"route\")] public CodeableConceptDto? Route { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"method\")] public CodeableConceptDto? Method { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"doseAndRate\")] public List<DoseAndRateDto>? DoseAndRate { get; set; }");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("/// <summary>Amount of medication per dose.</summary>");
        sb.AppendLine("public sealed class DoseAndRateDto");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public CodeableConceptDto? Type { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"doseRange\")] public RangeDto? DoseRange { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"doseQuantity\")] public QuantityDto? DoseQuantity { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"rateRatio\")] public RatioDto? RateRatio { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"rateRange\")] public RangeDto? RateRange { get; set; }");
        sb.AppendLine("    [JsonPropertyName(\"rateQuantity\")] public QuantityDto? RateQuantity { get; set; }");
        sb.AppendLine("}");

        var outputFile = Path.Combine(_outputPath, "CommonDtos.cs");
        await File.WriteAllTextAsync(outputFile, sb.ToString());
    }

    private static string MapFhirTypeToCSharp(string fhirType)
    {
        return fhirType switch
        {
            "boolean" => "bool",
            "integer" => "int",
            "integer64" => "long",
            "string" => "string",
            "decimal" => "decimal",
            "uri" => "string",
            "url" => "string",
            "canonical" => "string",
            "base64Binary" => "string",
            "instant" => "string",
            "date" => "string",
            "dateTime" => "string",
            "time" => "string",
            "code" => "string",
            "oid" => "string",
            "id" => "string",
            "markdown" => "string",
            "unsignedInt" => "uint",
            "positiveInt" => "uint",
            "uuid" => "string",
            "xhtml" => "string",
            "HumanName" => "HumanNameDto",
            "Address" => "AddressDto",
            "ContactPoint" => "ContactPointDto",
            "Identifier" => "IdentifierDto",
            "CodeableConcept" => "CodeableConceptDto",
            "Coding" => "CodingDto",
            "Quantity" => "QuantityDto",
            "Period" => "PeriodDto",
            "Reference" => "ReferenceDto",
            "Attachment" => "AttachmentDto",
            "Range" => "RangeDto",
            "Ratio" => "RatioDto",
            "Annotation" => "AnnotationDto",
            "Signature" => "SignatureDto",
            "Meta" => "MetaDto",
            "Narrative" => "NarrativeDto",
            "Extension" => "ExtensionDto",
            "Timing" => "TimingDto",
            "Duration" => "DurationDto",
            "Age" => "AgeDto",
            "Money" => "MoneyDto",
            "Dosage" => "DosageDto",
            "SimpleQuantity" => "QuantityDto",
            "BackboneElement" => "object",
            "Element" => "object",
            _ when fhirType.StartsWith("Reference(") => "ReferenceDto",
            _ => "object"
        };
    }

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var result = char.ToUpperInvariant(input[0]) + input[1..];
        var sb = new StringBuilder();
        var capitalizeNext = true;

        foreach (var c in result)
        {
            if (c == '-' || c == '_')
            {
                capitalizeNext = true;
            }
            else if (capitalizeNext)
            {
                sb.Append(char.ToUpperInvariant(c));
                capitalizeNext = false;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static bool IsValueType(string csharpType)
    {
        return csharpType is "bool" or "int" or "long" or "decimal" or "uint" or "DateTimeOffset";
    }

    private static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
