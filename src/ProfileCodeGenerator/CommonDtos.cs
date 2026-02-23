using System.Text.Json.Serialization;

namespace ProfileCodeGenerator;

/// <summary>
/// Common lightweight DTOs for FHIR data types.
/// These are optimized for fast deserialization with System.Text.Json.
/// </summary>

public sealed class HumanNameDto
{
    [JsonPropertyName("use")]
    public string? Use { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("given")]
    public List<string>? Given { get; set; }

    [JsonPropertyName("prefix")]
    public List<string>? Prefix { get; set; }

    [JsonPropertyName("suffix")]
    public List<string>? Suffix { get; set; }

    [JsonPropertyName("period")]
    public PeriodDto? Period { get; set; }
}

public sealed class AddressDto
{
    [JsonPropertyName("use")]
    public string? Use { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("line")]
    public List<string>? Line { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("district")]
    public string? District { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("postalCode")]
    public string? PostalCode { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("period")]
    public PeriodDto? Period { get; set; }
}

public sealed class ContactPointDto
{
    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("use")]
    public string? Use { get; set; }

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    [JsonPropertyName("period")]
    public PeriodDto? Period { get; set; }
}

public sealed class IdentifierDto
{
    [JsonPropertyName("use")]
    public string? Use { get; set; }

    [JsonPropertyName("type")]
    public CodeableConceptDto? Type { get; set; }

    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("period")]
    public PeriodDto? Period { get; set; }

    [JsonPropertyName("assigner")]
    public ReferenceDto? Assigner { get; set; }
}

public sealed class CodeableConceptDto
{
    [JsonPropertyName("coding")]
    public List<CodingDto>? Coding { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public sealed class CodingDto
{
    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("display")]
    public string? Display { get; set; }

    [JsonPropertyName("userSelected")]
    public bool? UserSelected { get; set; }
}

public sealed class QuantityDto
{
    [JsonPropertyName("value")]
    public decimal? Value { get; set; }

    [JsonPropertyName("comparator")]
    public string? Comparator { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

public sealed class PeriodDto
{
    [JsonPropertyName("start")]
    public string? Start { get; set; }

    [JsonPropertyName("end")]
    public string? End { get; set; }
}

public sealed class ReferenceDto
{
    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("identifier")]
    public IdentifierDto? Identifier { get; set; }

    [JsonPropertyName("display")]
    public string? Display { get; set; }
}

public sealed class AttachmentDto
{
    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("creation")]
    public string? Creation { get; set; }
}

public sealed class RangeDto
{
    [JsonPropertyName("low")]
    public QuantityDto? Low { get; set; }

    [JsonPropertyName("high")]
    public QuantityDto? High { get; set; }
}

public sealed class RatioDto
{
    [JsonPropertyName("numerator")]
    public QuantityDto? Numerator { get; set; }

    [JsonPropertyName("denominator")]
    public QuantityDto? Denominator { get; set; }
}

public sealed class AnnotationDto
{
    [JsonPropertyName("authorReference")]
    public ReferenceDto? AuthorReference { get; set; }

    [JsonPropertyName("authorString")]
    public string? AuthorString { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public sealed class MetaDto
{
    [JsonPropertyName("versionId")]
    public string? VersionId { get; set; }

    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("profile")]
    public List<string>? Profile { get; set; }

    [JsonPropertyName("security")]
    public List<CodingDto>? Security { get; set; }

    [JsonPropertyName("tag")]
    public List<CodingDto>? Tag { get; set; }
}

public sealed class NarrativeDto
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("div")]
    public string? Div { get; set; }
}

public sealed class ExtensionDto
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("valueString")]
    public string? ValueString { get; set; }

    [JsonPropertyName("valueBoolean")]
    public bool? ValueBoolean { get; set; }

    [JsonPropertyName("valueInteger")]
    public int? ValueInteger { get; set; }

    [JsonPropertyName("valueDecimal")]
    public decimal? ValueDecimal { get; set; }

    [JsonPropertyName("valueCode")]
    public string? ValueCode { get; set; }

    [JsonPropertyName("valueCoding")]
    public CodingDto? ValueCoding { get; set; }

    [JsonPropertyName("valueCodeableConcept")]
    public CodeableConceptDto? ValueCodeableConcept { get; set; }

    [JsonPropertyName("valueReference")]
    public ReferenceDto? ValueReference { get; set; }

    [JsonPropertyName("extension")]
    public List<ExtensionDto>? Extension { get; set; }
}

public sealed class SignatureDto
{
    [JsonPropertyName("type")]
    public List<CodingDto>? Type { get; set; }

    [JsonPropertyName("when")]
    public string? When { get; set; }

    [JsonPropertyName("who")]
    public ReferenceDto? Who { get; set; }

    [JsonPropertyName("onBehalfOf")]
    public ReferenceDto? OnBehalfOf { get; set; }

    [JsonPropertyName("targetFormat")]
    public string? TargetFormat { get; set; }

    [JsonPropertyName("sigFormat")]
    public string? SigFormat { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}
