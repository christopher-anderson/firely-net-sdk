using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ProfileCodeGenerator;

/// <summary>
/// Batch generator specifically for NCQA HEDIS Core profiles.
/// Generates lightweight DTOs from differential-based profiles using a union approach:
/// - One class per FHIR resource type (not per profile)
/// - Shared backbone components across resources
/// - Immutable records with init setters
/// </summary>
public class HedisProfileBatchGenerator
{
    private readonly string _profilesPath;
    private readonly string _outputPath;
    private readonly string _namespace;
    private readonly bool _mustSupportOnly;

    // Tracks all backbone elements found across all profiles, grouped by resource type
    private readonly Dictionary<string, Dictionary<string, BackboneInfo>> _backbonesByResource = new();

    // Tracks all fields per resource type (union of all profiles)
    private readonly Dictionary<string, Dictionary<string, FieldInfo>> _fieldsByResource = new();

    // Maps resource type to list of profile URLs
    private readonly Dictionary<string, List<string>> _profilesByResource = new();

    // Index of all available StructureDefinition profiles from dependency packages (URL -> file path)
    private readonly Dictionary<string, string> _profileIndex = new(StringComparer.OrdinalIgnoreCase);

    // Tracks profile URLs that have already been analyzed (to prevent re-processing)
    private readonly HashSet<string> _processedProfileUrls = new(StringComparer.OrdinalIgnoreCase);

    // Tracks dependency URLs that have been enqueued to avoid duplicates
    private readonly HashSet<string> _enqueuedDependencyUrls = new(StringComparer.OrdinalIgnoreCase);

    // Queue of dependency profile URLs pending analysis
    private readonly Queue<string> _pendingDependencyUrls = new();

    // Cache of StructureDefinitions extracted from R4 spec XML bundles (URL -> XElement)
    private readonly Dictionary<string, XElement> _profileXmlCache = new(StringComparer.OrdinalIgnoreCase);

    // Root directory containing all profile packages (parent of _profilesPath)
    private readonly string _allProfilesRootPath;

    // Abstract FHIR base types and data types that are never standalone Bundle entries
    private static readonly HashSet<string> _nonFhirResourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Resource", "DomainResource", "Element", "BackboneElement", "Extension", "Quantity"
    };

    // Structural resource types that bypass MustSupport filtering — their fields are
    // always required regardless of profile constraints (e.g. Bundle.Entry.Resource).
    private static readonly HashSet<string> _structuralResourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bundle"
    };

    public HedisProfileBatchGenerator(string profilesPath, string outputPath, string @namespace, bool mustSupportOnly = false)
    {
        _profilesPath = profilesPath;
        _outputPath = outputPath;
        _namespace = @namespace;
        _mustSupportOnly = mustSupportOnly;
        _allProfilesRootPath = Path.GetDirectoryName(Path.GetFullPath(profilesPath)) ?? profilesPath;
    }

    /// <summary>
    /// Generate DTOs from all HEDIS core profiles using the union approach.
    /// </summary>
    public async Task GenerateAllAsync()
    {
        Console.WriteLine($"Reading profiles from: {_profilesPath}");
        Console.WriteLine($"Output path: {_outputPath}");
        Console.WriteLine();

        // Find all hedis-core-*.json files (StructureDefinition profiles)
        var profileFiles = Directory.GetFiles(_profilesPath, "hedis-core-*.json")
            .Where(f => !Path.GetFileName(f).StartsWith("hedis-core-meta")) // Skip Meta profile (it's a data type)
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        Console.WriteLine($"Found {profileFiles.Count} profile definitions:");
        foreach (var file in profileFiles)
        {
            Console.WriteLine($"  - {Path.GetFileName(file)}");
        }
        Console.WriteLine();

        // Create output directories and clean previously generated .cs files
        // so stale files from prior runs don't cause duplicate type errors.
        Directory.CreateDirectory(_outputPath);
        var resourcesDir = Path.Combine(_outputPath, "Resources");
        var componentsDir = Path.Combine(_outputPath, "Components");
        Directory.CreateDirectory(resourcesDir);
        Directory.CreateDirectory(componentsDir);
        foreach (var stale in Directory.GetFiles(resourcesDir, "*.cs").Concat(Directory.GetFiles(componentsDir, "*.cs")))
        {
            File.Delete(stale);
        }

        // Build profile index from all available dependency packages
        await BuildProfileIndexAsync();

        // Pre-seed structural resource types so they are resolved from the R4 spec
        // even if no HEDIS profile directly references them.
        foreach (var structuralType in _structuralResourceTypes)
        {
            EnqueueDependencyUrl($"http://hl7.org/fhir/StructureDefinition/{structuralType}");
        }

        // Phase 1: Read all HEDIS core profiles and collect fields/backbones
        Console.WriteLine("Phase 1: Analyzing all HEDIS core profiles...");
        foreach (var profileFile in profileFiles)
        {
            try
            {
                await AnalyzeProfileAsync(profileFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error analyzing {Path.GetFileName(profileFile)}: {ex.Message}");
            }
        }

        // Resolve and analyze all transitively dependent profiles
        await ResolveDependenciesAsync();

        // Phase 2: Identify shared backbone components
        Console.WriteLine("\nPhase 2: Identifying shared components...");
        var sharedComponents = IdentifySharedComponents();
        Console.WriteLine($"  Found {sharedComponents.Count} shared component types");

        // Phase 3: Generate CommonTypes.cs
        Console.WriteLine("\nPhase 3: Generating CommonTypes.cs...");
        await GenerateCommonTypesAsync();

        // Phase 4: Generate shared components
        Console.WriteLine("\nPhase 4: Generating shared components...");
        await GenerateSharedComponentsAsync(sharedComponents);

        // Phase 5: Generate resource files
        Console.WriteLine("\nPhase 5: Generating resource files...");
        var generatedResources = new List<string>();
        foreach (var resourceType in _fieldsByResource.Keys.Where(t => !_nonFhirResourceTypes.Contains(t)).OrderBy(k => k))
        {
            var fileName = await GenerateResourceFileAsync(resourceType, sharedComponents);
            if (fileName != null)
            {
                generatedResources.Add(fileName);
            }
        }

        // Phase 6: Generate HedisResourceConverter (keeps the type registry in sync automatically)
        await GenerateHedisResourceConverterAsync();

        Console.WriteLine();
        Console.WriteLine($"Generated files summary:");
        Console.WriteLine($"  - CommonTypes.cs");
        Console.WriteLine($"  - Components/SharedComponents.cs");
        Console.WriteLine($"  - HedisResourceConverter.cs");
        foreach (var resource in generatedResources)
        {
            Console.WriteLine($"  - Resources/{resource}");
        }
        Console.WriteLine();
        Console.WriteLine($"Total: {generatedResources.Count + 2} files for {_profilesByResource.Values.Sum(l => l.Count)} profiles");
    }

    private async Task AnalyzeProfileAsync(string profilePath)
    {
        var json = await File.ReadAllTextAsync(profilePath);
        AnalyzeProfileFromJson(json);
    }

    private void AnalyzeProfileFromJson(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify this is a StructureDefinition
        if (!root.TryGetProperty("resourceType", out var resourceType) ||
            resourceType.GetString() != "StructureDefinition")
        {
            return;
        }

        var profileName = root.GetProperty("name").GetString() ?? "Unknown";
        var fhirType = root.GetProperty("type").GetString() ?? "Resource";
        var profileUrl = root.GetProperty("url").GetString() ?? "";

        Console.WriteLine($"  Analyzing: {profileName} ({fhirType})");

        // Track profile URL by resource type
        if (!_profilesByResource.ContainsKey(fhirType))
        {
            _profilesByResource[fhirType] = new List<string>();
        }
        _profilesByResource[fhirType].Add(profileUrl);
        _processedProfileUrls.Add(profileUrl);

        // Enqueue base definition for transitive dependency resolution
        if (root.TryGetProperty("baseDefinition", out var baseDefProp))
        {
            EnqueueDependencyUrl(baseDefProp.GetString());
        }

        // Get differential elements
        if (!root.TryGetProperty("differential", out var differential) ||
            !differential.TryGetProperty("element", out var elements))
        {
            return;
        }

        // Initialize resource tracking if needed
        if (!_fieldsByResource.ContainsKey(fhirType))
        {
            _fieldsByResource[fhirType] = new Dictionary<string, FieldInfo>();
        }
        if (!_backbonesByResource.ContainsKey(fhirType))
        {
            _backbonesByResource[fhirType] = new Dictionary<string, BackboneInfo>();
        }

        var resourceFields = _fieldsByResource[fhirType];
        var resourceBackbones = _backbonesByResource[fhirType];

        // First pass: identify all backbone elements
        foreach (var element in elements.EnumerateArray())
        {
            var path = element.GetProperty("path").GetString() ?? "";
            var parts = path.Split('.');

            // Skip root element and deeply nested elements
            if (parts.Length < 2) continue;

            // Identify backbone elements (depth 2, e.g., Claim.careTeam)
            if (parts.Length == 2)
            {
                var propName = NormalizePropertyName(parts[1]);
                if (propName == "extension" || propName == "modifierExtension") continue;

                // Check if this element has a known type - if so, it's NOT a backbone
                var elementType = GetElementType(element);
                if (IsKnownComplexType(elementType))
                {
                    continue; // Skip - this is a known complex type, not a backbone
                }

                // Also check by property name - FHIR has standard properties that are always known types
                if (IsKnownPropertyName(propName))
                {
                    continue; // Skip - this is a known property with a standard type
                }

                // Check if this is a backbone element by looking for child elements
                var childPath = path + ".";
                var hasChildren = elements.EnumerateArray()
                    .Any(e => (e.GetProperty("path").GetString() ?? "").StartsWith(childPath) &&
                             !(e.GetProperty("path").GetString() ?? "").Contains("[x]"));

                if (hasChildren)
                {
                    if (!resourceBackbones.ContainsKey(propName))
                    {
                        resourceBackbones[propName] = new BackboneInfo
                        {
                            Name = propName,
                            Fields = new Dictionary<string, FieldInfo>()
                        };
                    }
                }
            }
        }

        // Second pass: collect all fields
        foreach (var element in elements.EnumerateArray())
        {
            var path = element.GetProperty("path").GetString() ?? "";
            var parts = path.Split('.');

            if (parts.Length < 2) continue;

            // Collect referenced type profiles for dependency resolution
            if (element.TryGetProperty("type", out var elementTypes))
            {
                foreach (var typeEntry in elementTypes.EnumerateArray())
                {
                    if (typeEntry.TryGetProperty("profile", out var profileArr))
                    {
                        foreach (var profileEntry in profileArr.EnumerateArray())
                        {
                            EnqueueDependencyUrl(profileEntry.GetString());
                        }
                    }
                }
            }

            var propName = NormalizePropertyName(parts[1]);
            if (propName == "extension" || propName == "modifierExtension") continue;

            // Top-level property
            if (parts.Length == 2)
            {
                var field = ExtractFieldInfo(element, propName);

                // Check if it's a backbone
                if (resourceBackbones.ContainsKey(propName))
                {
                    field.IsBackbone = true;
                    field.BackboneName = propName;
                }

                // Union: add or merge field
                if (!resourceFields.ContainsKey(propName))
                {
                    resourceFields[propName] = field;
                }
                else
                {
                    // Merge: take less restrictive cardinality; MustSupport is true if any profile marks it so
                    var existing = resourceFields[propName];
                    if (field.Min < existing.Min) existing.Min = field.Min;
                    if (!existing.IsCollection && field.IsCollection) existing.IsCollection = true;
                    if (field.MustSupport) existing.MustSupport = true;
                }
            }
            // Backbone child property
            else if (parts.Length == 3 && !parts[2].Contains(":"))
            {
                var parentProp = NormalizePropertyName(parts[1]);
                var childProp = NormalizePropertyName(parts[2]);

                if (resourceBackbones.TryGetValue(parentProp, out var backbone))
                {
                    var field = ExtractFieldInfo(element, childProp);
                    if (!backbone.Fields.ContainsKey(childProp))
                    {
                        backbone.Fields[childProp] = field;
                    }
                    else if (field.MustSupport)
                    {
                        backbone.Fields[childProp].MustSupport = true;
                    }
                }
            }
        }
    }

    private async Task BuildProfileIndexAsync()
    {
        if (!Directory.Exists(_allProfilesRootPath))
        {
            Console.WriteLine($"Warning: Profile packages directory not found: {_allProfilesRootPath}");
            Console.WriteLine("Dependency resolution will be skipped.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"Building profile dependency index from: {_allProfilesRootPath}");

        var normalizedHedisPath = Path.GetFullPath(_profilesPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var jsonFiles = Directory.GetFiles(_allProfilesRootPath, "*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetFullPath(f).StartsWith(normalizedHedisPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"  Scanning {jsonFiles.Count} JSON files in dependency packages...");

        int indexed = 0;
        foreach (var file in jsonFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("resourceType", out var rtProp) ||
                    rtProp.GetString() != "StructureDefinition")
                    continue;

                if (!root.TryGetProperty("url", out var urlProp))
                    continue;

                var url = urlProp.GetString();
                if (!string.IsNullOrEmpty(url))
                {
                    _profileIndex[url] = file;
                    indexed++;
                }
            }
            catch
            {
                // Skip files that cannot be parsed
            }
        }

        Console.WriteLine($"  Indexed {indexed} StructureDefinitions from dependency packages");

        await IndexXmlSpecificationBundlesAsync();
    }

    private async Task ResolveDependenciesAsync()
    {
        if (_pendingDependencyUrls.Count == 0)
        {
            Console.WriteLine("No resolvable external dependencies found.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("Resolving dependent profile definitions...");
        int resolved = 0;
        int notFound = 0;

        while (_pendingDependencyUrls.Count > 0)
        {
            var url = _pendingDependencyUrls.Dequeue();

            if (_processedProfileUrls.Contains(url))
                continue;

            if (_profileIndex.TryGetValue(url, out var filePath))
            {
                try
                {
                    await AnalyzeProfileAsync(filePath);
                    resolved++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Error resolving dependency {url}: {ex.Message}");
                }
            }
            else if (_profileXmlCache.TryGetValue(url, out var sdXmlElement))
            {
                try
                {
                    var json = ConvertFhirXmlSdToMinimalJson(sdXmlElement);
                    AnalyzeProfileFromJson(json);
                    resolved++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Error resolving XML dependency {url}: {ex.Message}");
                }
            }
            else
            {
                notFound++;
            }
        }

        Console.WriteLine($"  Resolved {resolved} dependent profiles ({notFound} URLs not available locally)");
        Console.WriteLine();
    }

    private void EnqueueDependencyUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (_processedProfileUrls.Contains(url)) return;
        if (_enqueuedDependencyUrls.Add(url))
        {
            _pendingDependencyUrls.Enqueue(url);
        }
    }

    private async Task IndexXmlSpecificationBundlesAsync()
    {
        var r4SpecPath = Path.Combine(_allProfilesRootPath, "r4-specification");
        if (!Directory.Exists(r4SpecPath))
        {
            Console.WriteLine();
            return;
        }

        // Only the FHIR bundle XML files contain StructureDefinitions
        var xmlBundleFiles = Directory.GetFiles(r4SpecPath, "*.xml")
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.StartsWith("profiles-") || name == "extension-definitions";
            })
            .ToList();

        if (xmlBundleFiles.Count == 0)
        {
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"  Indexing R4 base specification from {xmlBundleFiles.Count} XML bundles...");
        var fhirNs = XNamespace.Get("http://hl7.org/fhir");
        int totalIndexed = 0;

        foreach (var xmlFile in xmlBundleFiles)
        {
            try
            {
                await using var stream = File.OpenRead(xmlFile);
                var xdoc = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
                int fileCount = 0;

                foreach (var sd in xdoc.Descendants(fhirNs + "StructureDefinition"))
                {
                    var url = sd.Element(fhirNs + "url")?.Attribute("value")?.Value;
                    if (string.IsNullOrEmpty(url)) continue;

                    _profileXmlCache[url] = sd;
                    fileCount++;
                }

                Console.WriteLine($"    {Path.GetFileName(xmlFile)}: {fileCount} StructureDefinitions");
                totalIndexed += fileCount;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Warning: could not parse {Path.GetFileName(xmlFile)}: {ex.Message}");
            }
        }

        Console.WriteLine($"  Indexed {totalIndexed} base R4 StructureDefinitions from XML bundles");
        Console.WriteLine();
    }

    /// <summary>
    /// Converts a FHIR XML StructureDefinition element to the minimal JSON subset
    /// expected by <see cref="AnalyzeProfileFromJson"/>.
    /// FHIR XML uses <c>value</c> attributes for primitives, e.g. &lt;url value="..."/&gt;.
    /// </summary>
    private static string ConvertFhirXmlSdToMinimalJson(XElement sd)
    {
        var ns = sd.Name.Namespace;

        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartObject();
        writer.WriteString("resourceType", "StructureDefinition");
        writer.WriteString("url", sd.Element(ns + "url")?.Attribute("value")?.Value ?? "");
        writer.WriteString("name", sd.Element(ns + "name")?.Attribute("value")?.Value ?? "");
        writer.WriteString("type", sd.Element(ns + "type")?.Attribute("value")?.Value ?? "");

        var baseDefinition = sd.Element(ns + "baseDefinition")?.Attribute("value")?.Value;
        if (!string.IsNullOrEmpty(baseDefinition))
            writer.WriteString("baseDefinition", baseDefinition);

        var differential = sd.Element(ns + "differential");
        if (differential != null)
        {
            writer.WritePropertyName("differential");
            writer.WriteStartObject();
            writer.WritePropertyName("element");
            writer.WriteStartArray();

            foreach (var element in differential.Elements(ns + "element"))
            {
                writer.WriteStartObject();

                var path = element.Element(ns + "path")?.Attribute("value")?.Value;
                if (path != null) writer.WriteString("path", path);

                var shortDesc = element.Element(ns + "short")?.Attribute("value")?.Value;
                if (shortDesc != null) writer.WriteString("short", shortDesc);

                if (int.TryParse(element.Element(ns + "min")?.Attribute("value")?.Value, out var minVal))
                    writer.WriteNumber("min", minVal);

                var maxStr = element.Element(ns + "max")?.Attribute("value")?.Value;
                if (maxStr != null) writer.WriteString("max", maxStr);

                var mustSupportStr = element.Element(ns + "mustSupport")?.Attribute("value")?.Value;
                if (mustSupportStr == "true") writer.WriteBoolean("mustSupport", true);

                // Element-level <type> contains child <code> and optional <profile> elements
                var typeElements = element.Elements(ns + "type").ToList();
                if (typeElements.Count > 0)
                {
                    writer.WritePropertyName("type");
                    writer.WriteStartArray();
                    foreach (var typeEl in typeElements)
                    {
                        writer.WriteStartObject();

                        var code = typeEl.Element(ns + "code")?.Attribute("value")?.Value;
                        if (code != null) writer.WriteString("code", code);

                        var profileUrls = typeEl.Elements(ns + "profile")
                            .Select(p => p.Attribute("value")?.Value)
                            .Where(p => p != null)
                            .ToList();

                        if (profileUrls.Count > 0)
                        {
                            writer.WritePropertyName("profile");
                            writer.WriteStartArray();
                            foreach (var profileUrl in profileUrls)
                                writer.WriteStringValue(profileUrl);
                            writer.WriteEndArray();
                        }

                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private FieldInfo ExtractFieldInfo(JsonElement element, string propName)
    {
        var field = new FieldInfo
        {
            Name = propName,
            JsonName = propName.EndsWith("[x]") ? propName[..^3] : propName,
            PascalName = ToPascalCase(propName.EndsWith("[x]") ? propName[..^3] : propName),
            CSharpType = "object",
            IsCollection = false,
            Min = 0,
            IsChoiceType = propName.EndsWith("[x]")
        };

        // Get documentation
        if (element.TryGetProperty("short", out var shortProp))
            field.Description = shortProp.GetString() ?? "";
        else if (element.TryGetProperty("definition", out var defProp))
            field.Description = defProp.GetString() ?? "";

        // Get type info
        if (element.TryGetProperty("type", out var types) && types.GetArrayLength() > 0)
        {
            var firstType = types[0];
            if (firstType.TryGetProperty("code", out var code))
            {
                field.CSharpType = MapFhirTypeToCSharp(code.GetString() ?? "object");
                field.FhirType = code.GetString() ?? "object";
            }
        }
        else
        {
            // No explicit type - infer from property name for known standard properties
            var inferredType = InferTypeFromPropertyName(propName);
            if (inferredType != null)
            {
                field.CSharpType = inferredType;
                field.FhirType = inferredType;
            }
        }

        // Get cardinality
        if (element.TryGetProperty("max", out var max))
        {
            var maxVal = max.GetString() ?? "1";
            field.IsCollection = maxVal != "1" && maxVal != "0";
        }
        else
        {
            // No max specified - infer from property name for known repeating elements
            field.IsCollection = IsTypicallyCollection(propName);
        }

        if (element.TryGetProperty("min", out var min))
        {
            field.Min = min.GetInt32();
        }

        if (element.TryGetProperty("mustSupport", out var mustSupport))
        {
            field.MustSupport = mustSupport.GetBoolean();
        }

        return field;
    }

    private static bool IsTypicallyCollection(string propName)
    {
        // Properties that are typically collections (max=*) in FHIR
        return propName.ToLowerInvariant() switch
        {
            // Backbone elements that are typically arrays
            "careteam" => true,
            "diagnosis" => true,
            "procedure" => true,
            "supportinginfo" => true,
            "item" => true,
            "insurance" => true,
            "total" => true,
            "benefitbalance" => true,
            "processnote" => true,
            "adjudication" => true,
            "component" => true,
            "referencerange" => true,
            "performer" => true,
            "participant" => true,
            "location" => true,
            "classhistory" => true,
            "statushistory" => true,
            "stage" => true,
            "evidence" => true,
            "costtobenefit" or "costtobeneficiary" => true,
            "contact" => true,
            "communication" => true,
            "link" => true,
            "reaction" => true,
            "protocolapplied" => true,
            "education" => true,
            "focaldevice" => true,
            "hoursofoperation" => true,
            "qualification" => true,
            "availabletime" => true,
            "notavailable" => true,
            "content" => true,
            "relatesto" => true,
            "ingredient" => true,
            // Standard repeating complex types
            "identifier" => true,
            "address" => true,
            "name" => true,
            "telecom" => true,
            "category" => true,
            "note" => true,
            "reasoncode" => true,
            "reasonreference" => true,
            "basedon" => true,
            "partof" => true,
            "focus" => true,
            "author" => true,
            "result" => true,
            _ => false
        };
    }

    private Dictionary<string, ComponentInfo> IdentifySharedComponents()
    {
        var sharedComponents = new Dictionary<string, ComponentInfo>();

        // Standard shared components that appear across multiple resources
        var standardComponents = new Dictionary<string, string[]>
        {
            ["CareTeam"] = new[] { "sequence", "provider", "responsible", "role", "qualification" },
            ["Diagnosis"] = new[] { "sequence", "diagnosis", "type", "onAdmission", "packageCode" },
            ["Procedure"] = new[] { "sequence", "type", "date", "procedure", "udi" },
            ["SupportingInfo"] = new[] { "sequence", "category", "code", "timing", "value", "reason" },
            ["Item"] = new[] { "sequence", "careTeamSequence", "diagnosisSequence", "procedureSequence",
                              "informationSequence", "revenue", "category", "productOrService",
                              "modifier", "programCode", "serviced", "location", "quantity",
                              "unitPrice", "factor", "net", "udi", "bodySite", "subSite",
                              "encounter", "noteNumber", "adjudication", "detail" },
        };

        // Analyze actual backbone usage across resources
        foreach (var (resourceType, backbones) in _backbonesByResource)
        {
            foreach (var (backboneName, backbone) in backbones)
            {
                var componentName = ToPascalCase(backboneName) + "Component";

                // Check if this matches a standard component
                if (standardComponents.TryGetValue(ToPascalCase(backboneName), out var standardFields))
                {
                    // Use standard shared component
                    if (!sharedComponents.ContainsKey(componentName))
                    {
                        sharedComponents[componentName] = new ComponentInfo
                        {
                            Name = componentName,
                            Fields = new Dictionary<string, FieldInfo>(),
                            UsedByResources = new List<string>()
                        };
                    }
                    sharedComponents[componentName].UsedByResources.Add(resourceType);

                    // Union the fields
                    foreach (var (fieldName, field) in backbone.Fields)
                    {
                        if (!sharedComponents[componentName].Fields.ContainsKey(fieldName))
                        {
                            sharedComponents[componentName].Fields[fieldName] = field;
                        }
                    }
                }
            }
        }

        return sharedComponents;
    }

    private async Task GenerateCommonTypesAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Common FHIR data types for HEDIS Core profiles.");
        sb.AppendLine($"// Generated at: {DateTime.UtcNow:O}");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_namespace};");
        sb.AppendLine();

        // Coding
        GenerateRecord(sb, "Coding", "A reference to a code defined by a terminology system.", new[]
        {
            ("string?", "System", "system"),
            ("string?", "Version", "version"),
            ("string?", "Code", "code"),
            ("string?", "Display", "display"),
            ("bool?", "UserSelected", "userSelected")
        });

        // CodeableConcept
        GenerateRecord(sb, "CodeableConcept", "Concept - reference to a terminology or just text.", new[]
        {
            ("List<Coding>?", "Coding", "coding"),
            ("string?", "Text", "text")
        });

        // Identifier
        GenerateRecord(sb, "Identifier", "An identifier intended for computation.", new[]
        {
            ("string?", "Use", "use"),
            ("CodeableConcept?", "Type", "type"),
            ("string?", "System", "system"),
            ("string?", "Value", "value"),
            ("Period?", "Period", "period"),
            ("ResourceReference?", "Assigner", "assigner")
        });

        // Period
        GenerateRecord(sb, "Period", "Time range defined by start and end date/time.", new[]
        {
            ("string?", "Start", "start"),
            ("string?", "End", "end")
        });

        // ResourceReference
        GenerateRecord(sb, "ResourceReference", "A reference from one resource to another.", new[]
        {
            ("string?", "Reference", "reference"),
            ("string?", "Type", "type"),
            ("Identifier?", "Identifier", "identifier"),
            ("string?", "Display", "display")
        });

        // Quantity
        GenerateRecord(sb, "Quantity", "A measured or measurable amount.", new[]
        {
            ("decimal?", "Value", "value"),
            ("string?", "Comparator", "comparator"),
            ("string?", "Unit", "unit"),
            ("string?", "System", "system"),
            ("string?", "Code", "code")
        });

        // Money
        GenerateRecord(sb, "Money", "An amount of economic utility in some recognized currency.", new[]
        {
            ("decimal?", "Value", "value"),
            ("string?", "Currency", "currency")
        });

        // Meta
        GenerateRecord(sb, "Meta", "Metadata about a resource.", new[]
        {
            ("string?", "VersionId", "versionId"),
            ("string?", "LastUpdated", "lastUpdated"),
            ("string?", "Source", "source"),
            ("List<string>?", "Profile", "profile"),
            ("List<Coding>?", "Security", "security"),
            ("List<Coding>?", "Tag", "tag")
        });

        // HumanName
        GenerateRecord(sb, "HumanName", "A name of a human with text, parts and usage information.", new[]
        {
            ("string?", "Use", "use"),
            ("string?", "Text", "text"),
            ("string?", "Family", "family"),
            ("List<string>?", "Given", "given"),
            ("List<string>?", "Prefix", "prefix"),
            ("List<string>?", "Suffix", "suffix"),
            ("Period?", "Period", "period")
        });

        // Address
        GenerateRecord(sb, "Address", "An address expressed using postal conventions.", new[]
        {
            ("string?", "Use", "use"),
            ("string?", "Type", "type"),
            ("string?", "Text", "text"),
            ("List<string>?", "Line", "line"),
            ("string?", "City", "city"),
            ("string?", "District", "district"),
            ("string?", "State", "state"),
            ("string?", "PostalCode", "postalCode"),
            ("string?", "Country", "country"),
            ("Period?", "Period", "period")
        });

        // ContactPoint
        GenerateRecord(sb, "ContactPoint", "Details for all kinds of technology-mediated contact points.", new[]
        {
            ("string?", "System", "system"),
            ("string?", "Value", "value"),
            ("string?", "Use", "use"),
            ("int?", "Rank", "rank"),
            ("Period?", "Period", "period")
        });

        // Attachment
        GenerateRecord(sb, "Attachment", "Content in a format defined elsewhere.", new[]
        {
            ("string?", "ContentType", "contentType"),
            ("string?", "Language", "language"),
            ("string?", "Data", "data"),
            ("string?", "Url", "url"),
            ("long?", "Size", "size"),
            ("string?", "Hash", "hash"),
            ("string?", "Title", "title"),
            ("string?", "Creation", "creation")
        });

        // Range
        GenerateRecord(sb, "Range", "Set of values bounded by low and high.", new[]
        {
            ("Quantity?", "Low", "low"),
            ("Quantity?", "High", "high")
        });

        // Ratio
        GenerateRecord(sb, "Ratio", "A ratio of two Quantity values.", new[]
        {
            ("Quantity?", "Numerator", "numerator"),
            ("Quantity?", "Denominator", "denominator")
        });

        // Annotation
        GenerateRecord(sb, "Annotation", "Text with attribution.", new[]
        {
            ("ResourceReference?", "AuthorReference", "authorReference"),
            ("string?", "AuthorString", "authorString"),
            ("string?", "Time", "time"),
            ("string?", "Text", "text")
        });

        // Narrative
        GenerateRecord(sb, "Narrative", "A human-readable formatted text, including images.", new[]
        {
            ("string?", "Status", "status"),
            ("string?", "Div", "div")
        });

        // Extension
        GenerateRecord(sb, "Extension", "Optional Extensions Element.", new[]
        {
            ("string?", "Url", "url"),
            ("string?", "ValueString", "valueString"),
            ("bool?", "ValueBoolean", "valueBoolean"),
            ("int?", "ValueInteger", "valueInteger"),
            ("decimal?", "ValueDecimal", "valueDecimal"),
            ("string?", "ValueCode", "valueCode"),
            ("string?", "ValueDate", "valueDate"),
            ("string?", "ValueDateTime", "valueDateTime"),
            ("Coding?", "ValueCoding", "valueCoding"),
            ("CodeableConcept?", "ValueCodeableConcept", "valueCodeableConcept"),
            ("ResourceReference?", "ValueReference", "valueReference"),
            ("Period?", "ValuePeriod", "valuePeriod"),
            ("Quantity?", "ValueQuantity", "valueQuantity"),
            ("List<Extension>?", "Extension_", "extension")
        });

        // Duration (specialization of Quantity)
        GenerateRecord(sb, "Duration", "A length of time.", new[]
        {
            ("decimal?", "Value", "value"),
            ("string?", "Comparator", "comparator"),
            ("string?", "Unit", "unit"),
            ("string?", "System", "system"),
            ("string?", "Code", "code")
        });

        // Age (specialization of Quantity)
        GenerateRecord(sb, "Age", "A duration of time during which an organism has existed.", new[]
        {
            ("decimal?", "Value", "value"),
            ("string?", "Comparator", "comparator"),
            ("string?", "Unit", "unit"),
            ("string?", "System", "system"),
            ("string?", "Code", "code")
        });

        // Timing
        sb.AppendLine("/// <summary>A timing schedule that specifies an event that may occur multiple times.</summary>");
        sb.AppendLine("public sealed record Timing");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"event\")] public List<string>? Event { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"repeat\")] public TimingRepeat? Repeat { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"code\")] public CodeableConcept? Code { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // TimingRepeat
        GenerateRecord(sb, "TimingRepeat", "Timing repeat component.", new[]
        {
            ("Duration?", "BoundsDuration", "boundsDuration"),
            ("Period?", "BoundsPeriod", "boundsPeriod"),
            ("int?", "Count", "count"),
            ("decimal?", "Duration_", "duration"),
            ("string?", "DurationUnit", "durationUnit"),
            ("int?", "Frequency", "frequency"),
            ("decimal?", "Period_", "period"),
            ("string?", "PeriodUnit", "periodUnit"),
            ("List<string>?", "DayOfWeek", "dayOfWeek"),
            ("List<string>?", "TimeOfDay", "timeOfDay")
        });

        // Dosage
        sb.AppendLine("/// <summary>How the medication is/was taken or should be taken.</summary>");
        sb.AppendLine("public sealed record Dosage");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"sequence\")] public int? Sequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"text\")] public string? Text { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"timing\")] public Timing? Timing { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"asNeededBoolean\")] public bool? AsNeededBoolean { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"asNeededCodeableConcept\")] public CodeableConcept? AsNeededCodeableConcept { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"site\")] public CodeableConcept? Site { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"route\")] public CodeableConcept? Route { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"method\")] public CodeableConcept? Method { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"doseAndRate\")] public List<DoseAndRate>? DoseAndRate { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // DoseAndRate
        GenerateRecord(sb, "DoseAndRate", "Amount of medication per dose.", new[]
        {
            ("CodeableConcept?", "Type", "type"),
            ("Range?", "DoseRange", "doseRange"),
            ("Quantity?", "DoseQuantity", "doseQuantity"),
            ("Ratio?", "RateRatio", "rateRatio"),
            ("Range?", "RateRange", "rateRange"),
            ("Quantity?", "RateQuantity", "rateQuantity")
        });

        // Signature
        GenerateRecord(sb, "Signature", "A Signature - XML DigSig, JWT, Graphical image of signature, etc.", new[]
        {
            ("List<Coding>?", "Type", "type"),
            ("string?", "When", "when"),
            ("ResourceReference?", "Who", "who"),
            ("ResourceReference?", "OnBehalfOf", "onBehalfOf"),
            ("string?", "TargetFormat", "targetFormat"),
            ("string?", "SigFormat", "sigFormat"),
            ("string?", "Data", "data")
        });

        // SampledData
        GenerateRecord(sb, "SampledData", "A series of measurements taken by a device.", new[]
        {
            ("Quantity?", "Origin", "origin"),
            ("decimal?", "Period_", "period"),
            ("decimal?", "Factor", "factor"),
            ("decimal?", "LowerLimit", "lowerLimit"),
            ("decimal?", "UpperLimit", "upperLimit"),
            ("int?", "Dimensions", "dimensions"),
            ("string?", "Data", "data")
        });

        var outputFile = Path.Combine(_outputPath, "CommonTypes.cs");
        await File.WriteAllTextAsync(outputFile, sb.ToString());
    }

    private void GenerateRecord(StringBuilder sb, string name, string description, (string type, string name, string json)[] properties)
    {
        sb.AppendLine($"/// <summary>{EscapeXml(description)}</summary>");
        sb.AppendLine($"public sealed record {name}");
        sb.AppendLine("{");
        foreach (var (type, propName, jsonName) in properties)
        {
            sb.AppendLine($"    [JsonPropertyName(\"{jsonName}\")] public {type} {propName} {{ get; init; }}");
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private async Task GenerateSharedComponentsAsync(Dictionary<string, ComponentInfo> sharedComponents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Shared backbone components for HEDIS Core resources.");
        sb.AppendLine($"// Generated at: {DateTime.UtcNow:O}");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_namespace};");
        sb.AppendLine();

        // CareTeamComponent - used by Claim, ExplanationOfBenefit
        sb.AppendLine("/// <summary>Care team member who provided services.</summary>");
        sb.AppendLine("public sealed record CareTeamComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"sequence\")] public int? Sequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"provider\")] public ResourceReference? Provider { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"responsible\")] public bool? Responsible { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"role\")] public CodeableConcept? Role { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"qualification\")] public CodeableConcept? Qualification { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // DiagnosisComponent - used by Claim, ExplanationOfBenefit
        sb.AppendLine("/// <summary>Diagnosis information.</summary>");
        sb.AppendLine("public sealed record DiagnosisComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"sequence\")] public int? Sequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"diagnosisCodeableConcept\")] public CodeableConcept? DiagnosisCodeableConcept { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"diagnosisReference\")] public ResourceReference? DiagnosisReference { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public List<CodeableConcept>? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"onAdmission\")] public CodeableConcept? OnAdmission { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"packageCode\")] public CodeableConcept? PackageCode { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ProcedureComponent - used by Claim, ExplanationOfBenefit
        sb.AppendLine("/// <summary>Procedure information.</summary>");
        sb.AppendLine("public sealed record ProcedureComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"sequence\")] public int? Sequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public List<CodeableConcept>? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"date\")] public string? Date { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"procedureCodeableConcept\")] public CodeableConcept? ProcedureCodeableConcept { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"procedureReference\")] public ResourceReference? ProcedureReference { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"udi\")] public List<ResourceReference>? Udi { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // SupportingInfoComponent - used by Claim, ExplanationOfBenefit
        sb.AppendLine("/// <summary>Supporting information.</summary>");
        sb.AppendLine("public sealed record SupportingInfoComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"sequence\")] public int? Sequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"category\")] public CodeableConcept? Category { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"code\")] public CodeableConcept? Code { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"timingDate\")] public string? TimingDate { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"timingPeriod\")] public Period? TimingPeriod { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueBoolean\")] public bool? ValueBoolean { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueString\")] public string? ValueString { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueQuantity\")] public Quantity? ValueQuantity { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueAttachment\")] public Attachment? ValueAttachment { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueReference\")] public ResourceReference? ValueReference { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"reason\")] public CodeableConcept? Reason { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ItemComponent - used by Claim, ExplanationOfBenefit (comprehensive)
        sb.AppendLine("/// <summary>Line item for claim or explanation of benefit.</summary>");
        sb.AppendLine("public sealed record ItemComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"sequence\")] public int? Sequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"careTeamSequence\")] public List<int>? CareTeamSequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"diagnosisSequence\")] public List<int>? DiagnosisSequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"procedureSequence\")] public List<int>? ProcedureSequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"informationSequence\")] public List<int>? InformationSequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"revenue\")] public CodeableConcept? Revenue { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"category\")] public CodeableConcept? Category { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"productOrService\")] public CodeableConcept? ProductOrService { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"modifier\")] public List<CodeableConcept>? Modifier { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"programCode\")] public List<CodeableConcept>? ProgramCode { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"servicedDate\")] public string? ServicedDate { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"servicedPeriod\")] public Period? ServicedPeriod { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"locationCodeableConcept\")] public CodeableConcept? LocationCodeableConcept { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"locationAddress\")] public Address? LocationAddress { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"locationReference\")] public ResourceReference? LocationReference { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"quantity\")] public Quantity? Quantity { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"unitPrice\")] public Money? UnitPrice { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"factor\")] public decimal? Factor { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"net\")] public Money? Net { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"udi\")] public List<ResourceReference>? Udi { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"bodySite\")] public CodeableConcept? BodySite { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"subSite\")] public List<CodeableConcept>? SubSite { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"encounter\")] public List<ResourceReference>? Encounter { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"noteNumber\")] public List<int>? NoteNumber { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"adjudication\")] public List<AdjudicationComponent>? Adjudication { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"detail\")] public List<ItemDetailComponent>? Detail { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // AdjudicationComponent - used by ExplanationOfBenefit
        sb.AppendLine("/// <summary>Adjudication details.</summary>");
        sb.AppendLine("public sealed record AdjudicationComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"category\")] public CodeableConcept? Category { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"reason\")] public CodeableConcept? Reason { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"amount\")] public Money? Amount { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"value\")] public decimal? Value { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ItemDetailComponent - used in ItemComponent.Detail
        sb.AppendLine("/// <summary>Item detail component.</summary>");
        sb.AppendLine("public sealed record ItemDetailComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"sequence\")] public int? Sequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"revenue\")] public CodeableConcept? Revenue { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"category\")] public CodeableConcept? Category { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"productOrService\")] public CodeableConcept? ProductOrService { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"modifier\")] public List<CodeableConcept>? Modifier { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"programCode\")] public List<CodeableConcept>? ProgramCode { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"quantity\")] public Quantity? Quantity { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"unitPrice\")] public Money? UnitPrice { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"factor\")] public decimal? Factor { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"net\")] public Money? Net { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"udi\")] public List<ResourceReference>? Udi { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"noteNumber\")] public List<int>? NoteNumber { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"adjudication\")] public List<AdjudicationComponent>? Adjudication { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"subDetail\")] public List<ItemSubDetailComponent>? SubDetail { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ItemSubDetailComponent
        sb.AppendLine("/// <summary>Item sub-detail component.</summary>");
        sb.AppendLine("public sealed record ItemSubDetailComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"sequence\")] public int? Sequence { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"revenue\")] public CodeableConcept? Revenue { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"category\")] public CodeableConcept? Category { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"productOrService\")] public CodeableConcept? ProductOrService { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"modifier\")] public List<CodeableConcept>? Modifier { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"programCode\")] public List<CodeableConcept>? ProgramCode { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"quantity\")] public Quantity? Quantity { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"unitPrice\")] public Money? UnitPrice { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"factor\")] public decimal? Factor { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"net\")] public Money? Net { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"udi\")] public List<ResourceReference>? Udi { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"noteNumber\")] public List<int>? NoteNumber { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"adjudication\")] public List<AdjudicationComponent>? Adjudication { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // TotalComponent - used by ExplanationOfBenefit
        sb.AppendLine("/// <summary>Total amount for the category.</summary>");
        sb.AppendLine("public sealed record TotalComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"category\")] public CodeableConcept? Category { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"amount\")] public Money? Amount { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // PaymentComponent - used by ExplanationOfBenefit
        sb.AppendLine("/// <summary>Payment details.</summary>");
        sb.AppendLine("public sealed record PaymentComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public CodeableConcept? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"adjustment\")] public Money? Adjustment { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"adjustmentReason\")] public CodeableConcept? AdjustmentReason { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"date\")] public string? Date { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"amount\")] public Money? Amount { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"identifier\")] public Identifier? Identifier { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // InsuranceComponent - used by Claim, ExplanationOfBenefit
        sb.AppendLine("/// <summary>Insurance information.</summary>");
        sb.AppendLine("public sealed record InsuranceComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"focal\")] public bool? Focal { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"coverage\")] public ResourceReference? Coverage { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"preAuthRef\")] public List<string>? PreAuthRef { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // AccidentComponent - used by Claim, ExplanationOfBenefit
        sb.AppendLine("/// <summary>Accident information.</summary>");
        sb.AppendLine("public sealed record AccidentComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"date\")] public string? Date { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public CodeableConcept? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"locationAddress\")] public Address? LocationAddress { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"locationReference\")] public ResourceReference? LocationReference { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // RelatedClaimComponent - used by Claim, ExplanationOfBenefit
        sb.AppendLine("/// <summary>Related claim information.</summary>");
        sb.AppendLine("public sealed record RelatedClaimComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"claim\")] public ResourceReference? Claim { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"relationship\")] public CodeableConcept? Relationship { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"reference\")] public Identifier? Reference { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // PayeeComponent - used by Claim, ExplanationOfBenefit
        sb.AppendLine("/// <summary>Payee information.</summary>");
        sb.AppendLine("public sealed record PayeeComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public CodeableConcept? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"party\")] public ResourceReference? Party { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // BenefitBalanceComponent - used by ExplanationOfBenefit
        sb.AppendLine("/// <summary>Benefit balance information.</summary>");
        sb.AppendLine("public sealed record BenefitBalanceComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"category\")] public CodeableConcept? Category { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"excluded\")] public bool? Excluded { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"name\")] public string? Name { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"description\")] public string? Description { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"network\")] public CodeableConcept? Network { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"unit\")] public CodeableConcept? Unit { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"term\")] public CodeableConcept? Term { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"financial\")] public List<BenefitComponent>? Financial { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // BenefitComponent - used in BenefitBalanceComponent
        sb.AppendLine("/// <summary>Benefit information.</summary>");
        sb.AppendLine("public sealed record BenefitComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public CodeableConcept? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"allowedUnsignedInt\")] public uint? AllowedUnsignedInt { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"allowedString\")] public string? AllowedString { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"allowedMoney\")] public Money? AllowedMoney { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"usedUnsignedInt\")] public uint? UsedUnsignedInt { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"usedMoney\")] public Money? UsedMoney { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // NoteComponent - used by ExplanationOfBenefit
        sb.AppendLine("/// <summary>Note information.</summary>");
        sb.AppendLine("public sealed record NoteComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"number\")] public int? Number { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public string? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"text\")] public string? Text { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"language\")] public CodeableConcept? Language { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ObservationComponent - used by Observation
        sb.AppendLine("/// <summary>Component results for an observation.</summary>");
        sb.AppendLine("public sealed record ObservationComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"code\")] public CodeableConcept? Code { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueQuantity\")] public Quantity? ValueQuantity { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueCodeableConcept\")] public CodeableConcept? ValueCodeableConcept { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueString\")] public string? ValueString { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueBoolean\")] public bool? ValueBoolean { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueInteger\")] public int? ValueInteger { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueRange\")] public Range? ValueRange { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueRatio\")] public Ratio? ValueRatio { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueSampledData\")] public SampledData? ValueSampledData { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueTime\")] public string? ValueTime { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueDateTime\")] public string? ValueDateTime { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valuePeriod\")] public Period? ValuePeriod { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"dataAbsentReason\")] public CodeableConcept? DataAbsentReason { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"interpretation\")] public List<CodeableConcept>? Interpretation { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"referenceRange\")] public List<ReferenceRangeComponent>? ReferenceRange { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ReferenceRangeComponent - used by Observation
        sb.AppendLine("/// <summary>Reference range for an observation.</summary>");
        sb.AppendLine("public sealed record ReferenceRangeComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"low\")] public Quantity? Low { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"high\")] public Quantity? High { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public CodeableConcept? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"appliesTo\")] public List<CodeableConcept>? AppliesTo { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"age\")] public Range? Age { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"text\")] public string? Text { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // PerformerComponent - used by multiple resources
        sb.AppendLine("/// <summary>Performer information.</summary>");
        sb.AppendLine("public sealed record PerformerComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"function\")] public CodeableConcept? Function { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"actor\")] public ResourceReference? Actor { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ParticipantComponent - used by Encounter
        sb.AppendLine("/// <summary>Participant in an encounter.</summary>");
        sb.AppendLine("public sealed record ParticipantComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public List<CodeableConcept>? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public Period? Period { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"individual\")] public ResourceReference? Individual { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // LocationComponent - used by Encounter
        sb.AppendLine("/// <summary>Location during an encounter.</summary>");
        sb.AppendLine("public sealed record LocationComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"location\")] public ResourceReference? Location { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"status\")] public string? Status { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"physicalType\")] public CodeableConcept? PhysicalType { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public Period? Period { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // HospitalizationComponent - used by Encounter
        sb.AppendLine("/// <summary>Hospitalization details.</summary>");
        sb.AppendLine("public sealed record HospitalizationComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"preAdmissionIdentifier\")] public Identifier? PreAdmissionIdentifier { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"origin\")] public ResourceReference? Origin { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"admitSource\")] public CodeableConcept? AdmitSource { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"reAdmission\")] public CodeableConcept? ReAdmission { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"dietPreference\")] public List<CodeableConcept>? DietPreference { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"specialCourtesy\")] public List<CodeableConcept>? SpecialCourtesy { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"specialArrangement\")] public List<CodeableConcept>? SpecialArrangement { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"destination\")] public ResourceReference? Destination { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"dischargeDisposition\")] public CodeableConcept? DischargeDisposition { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ClassHistoryComponent - used by Encounter
        sb.AppendLine("/// <summary>Class history for an encounter.</summary>");
        sb.AppendLine("public sealed record ClassHistoryComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"class\")] public Coding? Class { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public Period? Period { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // StatusHistoryComponent - used by Encounter
        sb.AppendLine("/// <summary>Status history for an encounter.</summary>");
        sb.AppendLine("public sealed record StatusHistoryComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"status\")] public string? Status { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public Period? Period { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // EncounterDiagnosisComponent - used by Encounter
        sb.AppendLine("/// <summary>Diagnosis information for an encounter.</summary>");
        sb.AppendLine("public sealed record EncounterDiagnosisComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"condition\")] public ResourceReference? Condition { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"use\")] public CodeableConcept? Use { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"rank\")] public int? Rank { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // StageComponent - used by Condition
        sb.AppendLine("/// <summary>Stage information for a condition.</summary>");
        sb.AppendLine("public sealed record StageComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"summary\")] public CodeableConcept? Summary { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"assessment\")] public List<ResourceReference>? Assessment { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public CodeableConcept? Type { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // EvidenceComponent - used by Condition
        sb.AppendLine("/// <summary>Evidence for a condition.</summary>");
        sb.AppendLine("public sealed record EvidenceComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"code\")] public List<CodeableConcept>? Code { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"detail\")] public List<ResourceReference>? Detail { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // CoverageClassComponent - used by Coverage
        sb.AppendLine("/// <summary>Coverage class information.</summary>");
        sb.AppendLine("public sealed record CoverageClassComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public CodeableConcept? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"value\")] public string? Value { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"name\")] public string? Name { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // CostToBeneficiaryComponent - used by Coverage
        sb.AppendLine("/// <summary>Cost to beneficiary information.</summary>");
        sb.AppendLine("public sealed record CostToBeneficiaryComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public CodeableConcept? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueQuantity\")] public Quantity? ValueQuantity { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueMoney\")] public Money? ValueMoney { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"exception\")] public List<ExceptionComponent>? Exception { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ExceptionComponent - used in CostToBeneficiaryComponent
        sb.AppendLine("/// <summary>Exception information.</summary>");
        sb.AppendLine("public sealed record ExceptionComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public CodeableConcept? Type { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public Period? Period { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ContactComponent - used by Patient
        sb.AppendLine("/// <summary>Contact party for a patient.</summary>");
        sb.AppendLine("public sealed record ContactComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"relationship\")] public List<CodeableConcept>? Relationship { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"name\")] public HumanName? Name { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"telecom\")] public List<ContactPoint>? Telecom { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"address\")] public Address? Address { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"gender\")] public string? Gender { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"organization\")] public ResourceReference? Organization { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public Period? Period { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // CommunicationComponent - used by Patient
        sb.AppendLine("/// <summary>Communication preferences.</summary>");
        sb.AppendLine("public sealed record CommunicationComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"language\")] public CodeableConcept? Language { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"preferred\")] public bool? Preferred { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // LinkComponent - used by Patient
        sb.AppendLine("/// <summary>Link to another patient resource.</summary>");
        sb.AppendLine("public sealed record LinkComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"other\")] public ResourceReference? Other { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"type\")] public string? Type { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ReactionComponent - used by Immunization
        sb.AppendLine("/// <summary>Reaction information for an immunization.</summary>");
        sb.AppendLine("public sealed record ReactionComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"date\")] public string? Date { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"detail\")] public ResourceReference? Detail { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"reported\")] public bool? Reported { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ProtocolAppliedComponent - used by Immunization
        sb.AppendLine("/// <summary>Protocol applied for an immunization.</summary>");
        sb.AppendLine("public sealed record ProtocolAppliedComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"series\")] public string? Series { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"authority\")] public ResourceReference? Authority { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"targetDisease\")] public List<CodeableConcept>? TargetDisease { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"doseNumberPositiveInt\")] public uint? DoseNumberPositiveInt { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"doseNumberString\")] public string? DoseNumberString { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"seriesDosesPositiveInt\")] public uint? SeriesDosesPositiveInt { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"seriesDosesString\")] public string? SeriesDosesString { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // EducationComponent - used by Immunization
        sb.AppendLine("/// <summary>Educational material presented to patient.</summary>");
        sb.AppendLine("public sealed record EducationComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"documentType\")] public string? DocumentType { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"reference\")] public string? Reference { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"publicationDate\")] public string? PublicationDate { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"presentationDate\")] public string? PresentationDate { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // QuestionnaireItemComponent - used by QuestionnaireResponse
        sb.AppendLine("/// <summary>Item in a questionnaire response.</summary>");
        sb.AppendLine("public sealed record QuestionnaireItemComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"linkId\")] public string? LinkId { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"definition\")] public string? Definition { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"text\")] public string? Text { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"answer\")] public List<QuestionnaireAnswerComponent>? Answer { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"item\")] public List<QuestionnaireItemComponent>? Item { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // QuestionnaireAnswerComponent - used in QuestionnaireItemComponent
        sb.AppendLine("/// <summary>Answer to a questionnaire item.</summary>");
        sb.AppendLine("public sealed record QuestionnaireAnswerComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"valueBoolean\")] public bool? ValueBoolean { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueDecimal\")] public decimal? ValueDecimal { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueInteger\")] public int? ValueInteger { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueDate\")] public string? ValueDate { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueDateTime\")] public string? ValueDateTime { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueTime\")] public string? ValueTime { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueString\")] public string? ValueString { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueUri\")] public string? ValueUri { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueAttachment\")] public Attachment? ValueAttachment { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueCoding\")] public Coding? ValueCoding { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueQuantity\")] public Quantity? ValueQuantity { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"valueReference\")] public ResourceReference? ValueReference { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"item\")] public List<QuestionnaireItemComponent>? Item { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // FocalDeviceComponent - used by Procedure
        sb.AppendLine("/// <summary>Focal device for a procedure.</summary>");
        sb.AppendLine("public sealed record FocalDeviceComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"action\")] public CodeableConcept? Action { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"manipulated\")] public ResourceReference? Manipulated { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // PositionComponent - used by Location
        sb.AppendLine("/// <summary>Geographic position of a location.</summary>");
        sb.AppendLine("public sealed record PositionComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"longitude\")] public decimal? Longitude { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"latitude\")] public decimal? Latitude { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"altitude\")] public decimal? Altitude { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // HoursOfOperationComponent - used by Location
        sb.AppendLine("/// <summary>Hours of operation for a location.</summary>");
        sb.AppendLine("public sealed record HoursOfOperationComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"daysOfWeek\")] public List<string>? DaysOfWeek { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"allDay\")] public bool? AllDay { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"openingTime\")] public string? OpeningTime { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"closingTime\")] public string? ClosingTime { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // QualificationComponent - used by Practitioner
        sb.AppendLine("/// <summary>Qualification for a practitioner.</summary>");
        sb.AppendLine("public sealed record QualificationComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"identifier\")] public List<Identifier>? Identifier { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"code\")] public CodeableConcept? Code { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public Period? Period { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"issuer\")] public ResourceReference? Issuer { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // AvailableTimeComponent - used by PractitionerRole
        sb.AppendLine("/// <summary>Available time for a practitioner role.</summary>");
        sb.AppendLine("public sealed record AvailableTimeComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"daysOfWeek\")] public List<string>? DaysOfWeek { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"allDay\")] public bool? AllDay { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"availableStartTime\")] public string? AvailableStartTime { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"availableEndTime\")] public string? AvailableEndTime { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // NotAvailableComponent - used by PractitionerRole
        sb.AppendLine("/// <summary>Not available time for a practitioner role.</summary>");
        sb.AppendLine("public sealed record NotAvailableComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"description\")] public string? Description { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"during\")] public Period? During { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ContentComponent - used by DocumentReference
        sb.AppendLine("/// <summary>Document content information.</summary>");
        sb.AppendLine("public sealed record ContentComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"attachment\")] public Attachment? Attachment { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"format\")] public Coding? Format { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ContextComponent - used by DocumentReference
        sb.AppendLine("/// <summary>Clinical context for a document.</summary>");
        sb.AppendLine("public sealed record ContextComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"encounter\")] public List<ResourceReference>? Encounter { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"event\")] public List<CodeableConcept>? Event { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"period\")] public Period? Period { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"facilityType\")] public CodeableConcept? FacilityType { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"practiceSetting\")] public CodeableConcept? PracticeSetting { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"sourcePatientInfo\")] public ResourceReference? SourcePatientInfo { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"related\")] public List<ResourceReference>? Related { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // RelatesToComponent - used by DocumentReference
        sb.AppendLine("/// <summary>Document relationship information.</summary>");
        sb.AppendLine("public sealed record RelatesToComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"code\")] public string? Code { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"target\")] public ResourceReference? Target { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // IngredientComponent - used by Medication
        sb.AppendLine("/// <summary>Ingredient information for a medication.</summary>");
        sb.AppendLine("public sealed record IngredientComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"itemCodeableConcept\")] public CodeableConcept? ItemCodeableConcept { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"itemReference\")] public ResourceReference? ItemReference { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"isActive\")] public bool? IsActive { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"strength\")] public Ratio? Strength { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // BatchComponent - used by Medication
        sb.AppendLine("/// <summary>Batch information for a medication.</summary>");
        sb.AppendLine("public sealed record BatchComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"lotNumber\")] public string? LotNumber { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"expirationDate\")] public string? ExpirationDate { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // BundleEntryComponent - used by Bundle
        sb.AppendLine("/// <summary>Entry in a Bundle — contains a resource and optional request/response metadata.</summary>");
        sb.AppendLine("public sealed record BundleEntryComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"fullUrl\")] public string? FullUrl { get; init; }");
        sb.AppendLine("    [JsonConverter(typeof(HedisResourceConverter))]");
        sb.AppendLine("    [JsonPropertyName(\"resource\")] public object? Resource { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"search\")] public BundleSearchComponent? Search { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"request\")] public BundleRequestComponent? Request { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"response\")] public BundleResponseComponent? Response { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"link\")] public List<BundleLinkComponent>? Link { get; init; }");
        sb.AppendLine("    /// <summary>Returns the resource as <typeparamref name=\"T\"/>, or null if it is a different type.</summary>");
        sb.AppendLine("    public T? GetResource<T>() where T : class => Resource as T;");
        sb.AppendLine("}");
        sb.AppendLine();

        // BundleLinkComponent - used by Bundle and BundleEntryComponent
        sb.AppendLine("/// <summary>Links related to this Bundle.</summary>");
        sb.AppendLine("public sealed record BundleLinkComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"relation\")] public string? Relation { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"url\")] public string? Url { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // BundleSearchComponent - used by BundleEntryComponent
        sb.AppendLine("/// <summary>Search-related information for a Bundle entry.</summary>");
        sb.AppendLine("public sealed record BundleSearchComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"mode\")] public string? Mode { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"score\")] public decimal? Score { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // BundleRequestComponent - used by BundleEntryComponent
        sb.AppendLine("/// <summary>Transaction/batch request details for a Bundle entry.</summary>");
        sb.AppendLine("public sealed record BundleRequestComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"method\")] public string? Method { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"url\")] public string? Url { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"ifNoneMatch\")] public string? IfNoneMatch { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"ifModifiedSince\")] public string? IfModifiedSince { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"ifMatch\")] public string? IfMatch { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"ifNoneExist\")] public string? IfNoneExist { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        // BundleResponseComponent - used by BundleEntryComponent
        sb.AppendLine("/// <summary>Transaction/batch response details for a Bundle entry.</summary>");
        sb.AppendLine("public sealed record BundleResponseComponent");
        sb.AppendLine("{");
        sb.AppendLine("    [JsonPropertyName(\"status\")] public string? Status { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"location\")] public string? Location { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"etag\")] public string? Etag { get; init; }");
        sb.AppendLine("    [JsonPropertyName(\"lastModified\")] public string? LastModified { get; init; }");
        sb.AppendLine("}");
        sb.AppendLine();

        var outputFile = Path.Combine(_outputPath, "Components", "SharedComponents.cs");
        await File.WriteAllTextAsync(outputFile, sb.ToString());
    }

    private async Task<string?> GenerateResourceFileAsync(string resourceType, Dictionary<string, ComponentInfo> sharedComponents)
    {
        if (!_fieldsByResource.TryGetValue(resourceType, out var fields))
        {
            return null;
        }

        var sb = new StringBuilder();
        var profileUrls = _profilesByResource.GetValueOrDefault(resourceType, new List<string>());

        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"// HEDIS Core {resourceType} - Union of {profileUrls.Count} profile(s).");
        foreach (var url in profileUrls)
        {
            sb.AppendLine($"//   - {url}");
        }
        sb.AppendLine($"// Generated at: {DateTime.UtcNow:O}");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_namespace};");
        sb.AppendLine();

        // Generate the resource record
        sb.AppendLine($"/// <summary>");
        sb.AppendLine($"/// HEDIS Core {resourceType}.");
        if (profileUrls.Count > 1)
        {
            sb.AppendLine($"/// <para>Union of {profileUrls.Count} HEDIS profiles for this resource type.</para>");
        }
        sb.AppendLine($"/// </summary>");
        sb.AppendLine($"public sealed record {resourceType}");
        sb.AppendLine("{");

        // Standard base fields always included: resourceType, id, meta, extension.
        // Extension is always kept because HEDIS profiles use extensions for key data
        // (race, ethnicity, coverage flags, etc.) — removing it would break access to
        // all HEDIS-specific extension slices even when they are MustSupport.
        // The remaining base fields (implicitRules, language, text, contained,
        // modifierExtension) are omitted in MustSupport-only mode.
        sb.AppendLine($"    /// <summary>The FHIR resource type.</summary>");
        sb.AppendLine($"    [JsonPropertyName(\"resourceType\")] public string ResourceType {{ get; init; }} = \"{resourceType}\";");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>Logical id of this artifact.</summary>");
        sb.AppendLine($"    [JsonPropertyName(\"id\")] public string? Id {{ get; init; }}");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>Metadata about the resource.</summary>");
        sb.AppendLine($"    [JsonPropertyName(\"meta\")] public Meta? Meta {{ get; init; }}");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>Additional content defined by implementations.</summary>");
        sb.AppendLine($"    [JsonPropertyName(\"extension\")] public List<Extension>? Extension {{ get; init; }}");
        sb.AppendLine();

        var generatedFields = new HashSet<string> { "id", "meta", "implicitRules", "language", "text", "contained", "extension", "modifierExtension" };

        if (!_mustSupportOnly)
        {
            sb.AppendLine($"    /// <summary>A set of rules under which this content was created.</summary>");
            sb.AppendLine($"    [JsonPropertyName(\"implicitRules\")] public string? ImplicitRules {{ get; init; }}");
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Language of the resource content.</summary>");
            sb.AppendLine($"    [JsonPropertyName(\"language\")] public string? Language {{ get; init; }}");
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Text summary of the resource.</summary>");
            sb.AppendLine($"    [JsonPropertyName(\"text\")] public Narrative? Text {{ get; init; }}");
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Contained, inline Resources.</summary>");
            sb.AppendLine($"    [JsonConverter(typeof(HedisResourceListConverter))]");
            sb.AppendLine($"    [JsonPropertyName(\"contained\")] public List<object>? Contained {{ get; init; }}");
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Extensions that cannot be ignored.</summary>");
            sb.AppendLine($"    [JsonPropertyName(\"modifierExtension\")] public List<Extension>? ModifierExtension {{ get; init; }}");
            sb.AppendLine();
        }

        // Generate profile-specific fields.
        // Structural types (e.g. Bundle) bypass MustSupport filtering — all their fields
        // are required for the system to function regardless of profile constraints.
        bool applyMustSupportFilter = _mustSupportOnly && !_structuralResourceTypes.Contains(resourceType);

        foreach (var (fieldName, field) in fields.OrderBy(f => f.Key))
        {
            if (generatedFields.Contains(fieldName)) continue;
            generatedFields.Add(fieldName);

            if (applyMustSupportFilter && !field.MustSupport) continue;

            GenerateField(sb, field, resourceType, sharedComponents);
        }

        sb.AppendLine("}");

        var fileName = $"{resourceType}.cs";
        var outputFile = Path.Combine(_outputPath, "Resources", fileName);
        await File.WriteAllTextAsync(outputFile, sb.ToString());

        return fileName;
    }

    private async Task GenerateHedisResourceConverterAsync()
    {
        // Only include proper FHIR resource types (exclude abstract base types and data types)
        var resourceTypes = _fieldsByResource.Keys
            .Where(t => !_nonFhirResourceTypes.Contains(t))
            .OrderBy(t => t)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Polymorphic JSON converters for HEDIS FHIR resources.");
        sb.AppendLine($"// Generated at: {DateTime.UtcNow:O}");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_namespace};");
        sb.AppendLine();

        // ── HedisResourceConverter ──────────────────────────────────────────────
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Polymorphic JSON converter for a single FHIR resource.");
        sb.AppendLine("/// Reads the <c>resourceType</c> discriminator and deserializes to the");
        sb.AppendLine("/// matching HEDIS DTO. Unknown types are preserved as <see cref=\"JsonElement\"/>.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public sealed class HedisResourceConverter : JsonConverter<object>");
        sb.AppendLine("{");
        sb.AppendLine("    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (reader.TokenType == JsonTokenType.Null)");
        sb.AppendLine("            return null;");
        sb.AppendLine();
        sb.AppendLine("        // Copy the reader (Utf8JsonReader is a value type) to scan for resourceType");
        sb.AppendLine("        // without consuming the original. This avoids building a JsonDocument DOM");
        sb.AppendLine("        // (one full allocation per resource entry) and lets us deserialize with a");
        sb.AppendLine("        // single forward pass via JsonSerializer.Deserialize<T>(ref reader, options).");
        sb.AppendLine("        var scanReader = reader;");
        sb.AppendLine("        var resourceType = ScanForResourceType(ref scanReader);");
        sb.AppendLine();
        sb.AppendLine("        return resourceType switch");
        sb.AppendLine("        {");

        int maxLen = resourceTypes.Count > 0 ? resourceTypes.Max(t => t.Length) : 0;
        foreach (var rt in resourceTypes)
        {
            var pad = new string(' ', maxLen - rt.Length + 1);
            sb.AppendLine($"            \"{rt}\"{pad}=> JsonSerializer.Deserialize<{rt}>(ref reader, options),");
        }
        sb.AppendLine($"            {new string(' ', maxLen + 1)}_ => JsonSerializer.Deserialize<JsonElement>(ref reader, options)");

        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Scans a copy of the reader for the top-level <c>resourceType</c> property");
        sb.AppendLine("    /// without advancing the original reader. Only examines depth-1 tokens so");
        sb.AppendLine("    /// nested objects are skipped efficiently.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    private static string? ScanForResourceType(ref Utf8JsonReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("        int startDepth = reader.CurrentDepth;");
        sb.AppendLine("        while (reader.Read())");
        sb.AppendLine("        {");
        sb.AppendLine("            if (reader.CurrentDepth == startDepth + 1 &&");
        sb.AppendLine("                reader.TokenType == JsonTokenType.PropertyName &&");
        sb.AppendLine("                reader.ValueTextEquals(\"resourceType\"u8))");
        sb.AppendLine("            {");
        sb.AppendLine("                return reader.Read() ? reader.GetString() : null;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)");
        sb.AppendLine("        => JsonSerializer.Serialize(writer, value, value.GetType(), options);");
        sb.AppendLine("}");
        sb.AppendLine();

        // ── HedisResourceListConverter ──────────────────────────────────────────
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Polymorphic JSON converter for a <see cref=\"List{T}\"/> of FHIR resources.");
        sb.AppendLine("/// Used on the <c>contained</c> property of domain resources.");
        sb.AppendLine("/// Delegates each element to <see cref=\"HedisResourceConverter\"/>.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public sealed class HedisResourceListConverter : JsonConverter<List<object>>");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly HedisResourceConverter ItemConverter = new();");
        sb.AppendLine();
        sb.AppendLine("    public override List<object>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (reader.TokenType == JsonTokenType.Null) return null;");
        sb.AppendLine("        if (reader.TokenType != JsonTokenType.StartArray)");
        sb.AppendLine("            throw new JsonException($\"Expected StartArray, got {reader.TokenType}\");");
        sb.AppendLine();
        sb.AppendLine("        var list = new List<object>();");
        sb.AppendLine("        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)");
        sb.AppendLine("        {");
        sb.AppendLine("            var item = ItemConverter.Read(ref reader, typeof(object), options);");
        sb.AppendLine("            if (item != null) list.Add(item);");
        sb.AppendLine("        }");
        sb.AppendLine("        return list;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public override void Write(Utf8JsonWriter writer, List<object> value, JsonSerializerOptions options)");
        sb.AppendLine("    {");
        sb.AppendLine("        writer.WriteStartArray();");
        sb.AppendLine("        foreach (var item in value)");
        sb.AppendLine("            ItemConverter.Write(writer, item, options);");
        sb.AppendLine("        writer.WriteEndArray();");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        var outputFile = Path.Combine(_outputPath, "HedisResourceConverter.cs");
        await File.WriteAllTextAsync(outputFile, sb.ToString());
        Console.WriteLine("  Generated: HedisResourceConverter.cs");
    }

    private void GenerateField(StringBuilder sb, FieldInfo field, string resourceType, Dictionary<string, ComponentInfo> sharedComponents)
    {
        // Add documentation
        if (!string.IsNullOrEmpty(field.Description))
        {
            sb.AppendLine($"    /// <summary>{EscapeXml(field.Description)}</summary>");
        }

        // Determine type
        string csharpType;
        if (field.IsBackbone)
        {
            csharpType = GetComponentType(field.BackboneName, resourceType, sharedComponents);
        }
        else
        {
            csharpType = field.CSharpType;
        }

        // Handle collections
        string finalType;
        if (field.IsCollection)
        {
            finalType = $"List<{csharpType}>?";
        }
        else if (IsValueType(csharpType))
        {
            finalType = $"{csharpType}?";
        }
        else
        {
            finalType = $"{csharpType}?";
        }

        sb.AppendLine($"    [JsonPropertyName(\"{field.JsonName}\")] public {finalType} {field.PascalName} {{ get; init; }}");
        sb.AppendLine();
    }

    private string GetComponentType(string backboneName, string resourceType, Dictionary<string, ComponentInfo> sharedComponents)
    {
        var componentName = ToPascalCase(backboneName) + "Component";

        // Check for shared components
        if (sharedComponents.ContainsKey(componentName))
        {
            return componentName;
        }

        // Use resource-specific naming for non-shared components
        return MapBackboneToComponent(backboneName, resourceType);
    }

    private static string MapBackboneToComponent(string backboneName, string resourceType)
    {
        // Map common backbone names to their shared component types
        return backboneName.ToLowerInvariant() switch
        {
            "careteam" => "CareTeamComponent",
            "diagnosis" => resourceType == "Encounter" ? "EncounterDiagnosisComponent" : "DiagnosisComponent",
            "procedure" => resourceType == "Procedure" ? "object" : "ProcedureComponent",
            "supportinginfo" => "SupportingInfoComponent",
            "item" => "ItemComponent",
            "total" => "TotalComponent",
            "payment" => "PaymentComponent",
            "insurance" => "InsuranceComponent",
            "accident" => "AccidentComponent",
            "related" => "RelatedClaimComponent",
            "payee" => "PayeeComponent",
            "benefitbalance" => "BenefitBalanceComponent",
            "processNote" or "processnote" => "NoteComponent",
            "component" => "ObservationComponent",
            "referencerange" => "ReferenceRangeComponent",
            "performer" => "PerformerComponent",
            "participant" => "ParticipantComponent",
            "location" => "LocationComponent",
            "hospitalization" => "HospitalizationComponent",
            "classhistory" => "ClassHistoryComponent",
            "statushistory" => "StatusHistoryComponent",
            "stage" => "StageComponent",
            "evidence" => "EvidenceComponent",
            "class" => "CoverageClassComponent",
            "costtobenefit" or "costtobeneficiary" => "CostToBeneficiaryComponent",
            "contact" => "ContactComponent",
            "communication" => "CommunicationComponent",
            "link" when resourceType == "Bundle" => "BundleLinkComponent",
            "entry" when resourceType == "Bundle" => "BundleEntryComponent",
            "link" => "LinkComponent",
            "reaction" => "ReactionComponent",
            "protocolapplied" => "ProtocolAppliedComponent",
            "education" => "EducationComponent",
            "focaldevice" => "FocalDeviceComponent",
            "position" => "PositionComponent",
            "hoursofoperation" => "HoursOfOperationComponent",
            "qualification" => "QualificationComponent",
            "availabletime" => "AvailableTimeComponent",
            "notavailable" => "NotAvailableComponent",
            "content" => "ContentComponent",
            "context" => "ContextComponent",
            "relatesto" => "RelatesToComponent",
            "ingredient" => "IngredientComponent",
            "batch" => "BatchComponent",
            _ => ToPascalCase(backboneName) + "Component"
        };
    }

    private static string NormalizePropertyName(string propName)
    {
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

        return propName;
    }

    private static string? GetElementType(JsonElement element)
    {
        if (element.TryGetProperty("type", out var types) && types.GetArrayLength() > 0)
        {
            var firstType = types[0];
            if (firstType.TryGetProperty("code", out var code))
            {
                return code.GetString();
            }
        }
        return null;
    }

    private static bool IsKnownComplexType(string? fhirType)
    {
        if (string.IsNullOrEmpty(fhirType)) return false;

        // Known FHIR complex types that should NOT be treated as backbones
        return fhirType switch
        {
            // Data types with structure that profiles might constrain
            "HumanName" => true,
            "Address" => true,
            "ContactPoint" => true,
            "Identifier" => true,
            "CodeableConcept" => true,
            "Coding" => true,
            "Quantity" => true,
            "Period" => true,
            "Reference" => true,
            "Attachment" => true,
            "Range" => true,
            "Ratio" => true,
            "Annotation" => true,
            "Signature" => true,
            "Meta" => true,
            "Narrative" => true,
            "Extension" => true,
            "Timing" => true,
            "Duration" => true,
            "Age" => true,
            "Money" => true,
            "Dosage" => true,
            "SimpleQuantity" => true,
            "SampledData" => true,
            // Primitive types (should not have children, but just in case)
            "boolean" => true,
            "integer" => true,
            "string" => true,
            "decimal" => true,
            "uri" => true,
            "url" => true,
            "canonical" => true,
            "base64Binary" => true,
            "instant" => true,
            "date" => true,
            "dateTime" => true,
            "time" => true,
            "code" => true,
            "oid" => true,
            "id" => true,
            "markdown" => true,
            "unsignedInt" => true,
            "positiveInt" => true,
            "uuid" => true,
            "xhtml" => true,
            _ when fhirType.StartsWith("Reference(") => true,
            _ => false
        };
    }

    private static bool IsKnownPropertyName(string propName)
    {
        // Property names that are known to be standard FHIR types (not BackboneElements)
        // These properties exist on many resources with their standard type
        return propName.ToLowerInvariant() switch
        {
            // Complex types that appear on resources
            "address" => true,
            "identifier" => true,
            "name" => true,
            "telecom" => true,
            "meta" => true,
            "period" => true,
            "code" => true,
            "category" => true,
            "type" => true,
            "subtype" => true,
            "bodySite" or "bodysite" => true,
            "method" => true,
            "subject" => true,
            "patient" => true,
            "encounter" => true,
            "author" => true,
            "performer" => true,  // Note: performer CAN be a backbone on some resources - need to check
            "asserter" => true,
            "recorder" => true,
            "informationSource" or "informationsource" => true,
            "basedOn" or "basedon" => true,
            "partOf" or "partof" => true,
            "statusReason" or "statusreason" => true,
            "reasonCode" or "reasoncode" => true,
            "reasonReference" or "reasonreference" => true,
            "note" => true,
            "interpretation" => true,
            "dataAbsentReason" or "dataabsentreason" => true,
            "value" => true,  // When it appears as a backbone, it's usually valueX choice type
            "effective" => true, // effectiveDateTime, effectivePeriod, etc.
            "issued" => true,
            "provider" => true,
            "insurer" => true,
            "enterer" => true,
            "referral" => true,
            "facility" => true,
            "prescription" => true,
            "originalPrescription" or "originalprescription" => true,
            "destination" => true,
            "receiver" => true,
            "billablePeriod" or "billableperiod" => true,  // This is a Period
            _ => false
        };
    }

    private static string? InferTypeFromPropertyName(string propName)
    {
        // Infer the FHIR type from property name for standard FHIR properties
        // These are properties that have a known standard type across all FHIR resources
        return propName.ToLowerInvariant() switch
        {
            // Patient / Person properties
            "address" => "Address",
            "identifier" => "Identifier",
            "name" => "HumanName",
            "telecom" => "ContactPoint",

            // Metadata
            "meta" => "Meta",

            // Common coded properties
            "code" => "CodeableConcept",
            "category" => "CodeableConcept",
            "type" => "CodeableConcept",
            "subtype" => "CodeableConcept",

            // Common references
            "subject" => "ResourceReference",
            "patient" => "ResourceReference",
            "encounter" => "ResourceReference",
            "author" => "ResourceReference",
            "asserter" => "ResourceReference",
            "recorder" => "ResourceReference",
            "provider" => "ResourceReference",
            "insurer" => "ResourceReference",
            "enterer" => "ResourceReference",
            "referral" => "ResourceReference",
            "facility" => "ResourceReference",
            "prescription" => "ResourceReference",
            "originalprescription" => "ResourceReference",
            "destination" => "ResourceReference",
            "receiver" => "ResourceReference",
            "basedon" => "ResourceReference",
            "partof" => "ResourceReference",
            "informationsource" => "ResourceReference",
            "focus" => "ResourceReference",

            // Period properties
            "period" => "Period",
            "billableperiod" => "Period",

            // Other standard types
            "bodysite" => "CodeableConcept",
            "method" => "CodeableConcept",
            "statusreason" => "CodeableConcept",
            "reasoncode" => "CodeableConcept",
            "reasonreference" => "ResourceReference",
            "note" => "Annotation",
            "interpretation" => "CodeableConcept",
            "dataabsentreason" => "CodeableConcept",

            // Coded encounter class (Encounter.class is a single Coding in FHIR R4)
            "class" => "Coding",

            // Primitive properties (when profiles constrain child elements)
            "birthdate" => "string",  // date type
            "gender" => "string",     // code type
            "status" => "string",     // code type
            "issued" => "string",     // instant/dateTime type

            _ => null
        };
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
            "HumanName" => "HumanName",
            "Address" => "Address",
            "ContactPoint" => "ContactPoint",
            "Identifier" => "Identifier",
            "CodeableConcept" => "CodeableConcept",
            "Coding" => "Coding",
            "Quantity" => "Quantity",
            "Period" => "Period",
            "Reference" => "ResourceReference",
            "Attachment" => "Attachment",
            "Range" => "Range",
            "Ratio" => "Ratio",
            "Annotation" => "Annotation",
            "Signature" => "Signature",
            "Meta" => "Meta",
            "Narrative" => "Narrative",
            "Extension" => "Extension",
            "Timing" => "Timing",
            "Duration" => "Duration",
            "Age" => "Age",
            "Money" => "Money",
            "Dosage" => "Dosage",
            "SimpleQuantity" => "Quantity",
            "SampledData" => "SampledData",
            "BackboneElement" => "object",
            "Element" => "object",
            _ when fhirType.StartsWith("Reference(") => "ResourceReference",
            _ => "object"
        };
    }

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var sb = new StringBuilder();
        var capitalizeNext = true;

        foreach (var c in input)
        {
            if (c == '-' || c == '_' || c == '[' || c == ']')
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
        return csharpType is "bool" or "int" or "long" or "decimal" or "uint";
    }

    private static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}

// Helper classes
internal class FieldInfo
{
    public string Name { get; set; } = "";
    public string JsonName { get; set; } = "";
    public string PascalName { get; set; } = "";
    public string CSharpType { get; set; } = "object";
    public string? FhirType { get; set; }
    public string Description { get; set; } = "";
    public bool IsCollection { get; set; }
    public int Min { get; set; }
    public bool MustSupport { get; set; }
    public bool IsChoiceType { get; set; }
    public bool IsBackbone { get; set; }
    public string BackboneName { get; set; } = "";
}

internal class BackboneInfo
{
    public string Name { get; set; } = "";
    public Dictionary<string, FieldInfo> Fields { get; set; } = new();
}

internal class ComponentInfo
{
    public string Name { get; set; } = "";
    public Dictionary<string, FieldInfo> Fields { get; set; } = new();
    public List<string> UsedByResources { get; set; } = new();
}
