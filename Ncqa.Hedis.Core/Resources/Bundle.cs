// HEDIS Core Bundle type for deserializing FHIR bundles.
// Bundles are not profile-specific but needed for loading patient data.

#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ncqa.Hedis.Core;

/// <summary>
/// FHIR Bundle resource for containing collections of resources.
/// </summary>
public sealed record Bundle
{
    /// <summary>The FHIR resource type.</summary>
    [JsonPropertyName("resourceType")] public string ResourceType { get; init; } = "Bundle";

    /// <summary>Logical id of this artifact.</summary>
    [JsonPropertyName("id")] public string? Id { get; init; }

    /// <summary>Metadata about the resource.</summary>
    [JsonPropertyName("meta")] public Meta? Meta { get; init; }

    /// <summary>Indicates the purpose of this bundle.</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>When the bundle was assembled.</summary>
    [JsonPropertyName("timestamp")] public string? Timestamp { get; init; }

    /// <summary>If search, the total number of matches.</summary>
    [JsonPropertyName("total")] public int? Total { get; init; }

    /// <summary>Links related to this Bundle.</summary>
    [JsonPropertyName("link")] public List<BundleLinkComponent>? Link { get; init; }

    /// <summary>Entry in the bundle - will have a resource or information.</summary>
    [JsonPropertyName("entry")] public List<BundleEntryComponent>? Entry { get; init; }

    /// <summary>Digital Signature.</summary>
    [JsonPropertyName("signature")] public Signature? Signature { get; init; }
}

/// <summary>Links related to the Bundle.</summary>
public sealed record BundleLinkComponent
{
    /// <summary>See http://www.iana.org/assignments/link-relations/link-relations.xhtml#link-relations-1.</summary>
    [JsonPropertyName("relation")] public string? Relation { get; init; }

    /// <summary>Reference details for the link.</summary>
    [JsonPropertyName("url")] public string? Url { get; init; }
}

/// <summary>Entry in the bundle.</summary>
public sealed record BundleEntryComponent
{
    /// <summary>Links related to this entry.</summary>
    [JsonPropertyName("link")] public List<BundleLinkComponent>? Link { get; init; }

    /// <summary>URI for resource (Absolute URL server address or URI for UUID/OID).</summary>
    [JsonPropertyName("fullUrl")] public string? FullUrl { get; init; }

    /// <summary>A resource in the bundle - stored as JsonElement for polymorphic deserialization.</summary>
    [JsonPropertyName("resource")] public JsonElement? Resource { get; init; }

    /// <summary>Search related information.</summary>
    [JsonPropertyName("search")] public BundleSearchComponent? Search { get; init; }

    /// <summary>Additional execution information (transaction/batch/history).</summary>
    [JsonPropertyName("request")] public BundleRequestComponent? Request { get; init; }

    /// <summary>Results of execution (transaction/batch/history).</summary>
    [JsonPropertyName("response")] public BundleResponseComponent? Response { get; init; }
}

/// <summary>Search related information.</summary>
public sealed record BundleSearchComponent
{
    /// <summary>match | include | outcome - why this is in the result set.</summary>
    [JsonPropertyName("mode")] public string? Mode { get; init; }

    /// <summary>Search ranking (between 0 and 1).</summary>
    [JsonPropertyName("score")] public decimal? Score { get; init; }
}

/// <summary>Additional execution information (transaction/batch/history).</summary>
public sealed record BundleRequestComponent
{
    /// <summary>GET | HEAD | POST | PUT | DELETE | PATCH.</summary>
    [JsonPropertyName("method")] public string? Method { get; init; }

    /// <summary>URL for HTTP equivalent of this entry.</summary>
    [JsonPropertyName("url")] public string? Url { get; init; }

    /// <summary>For managing cache currency.</summary>
    [JsonPropertyName("ifNoneMatch")] public string? IfNoneMatch { get; init; }

    /// <summary>For managing cache currency.</summary>
    [JsonPropertyName("ifModifiedSince")] public string? IfModifiedSince { get; init; }

    /// <summary>For managing update contention.</summary>
    [JsonPropertyName("ifMatch")] public string? IfMatch { get; init; }

    /// <summary>For conditional creates.</summary>
    [JsonPropertyName("ifNoneExist")] public string? IfNoneExist { get; init; }
}

/// <summary>Results of execution (transaction/batch/history).</summary>
public sealed record BundleResponseComponent
{
    /// <summary>Status response code (text optional).</summary>
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>The location (if the operation returns a location).</summary>
    [JsonPropertyName("location")] public string? Location { get; init; }

    /// <summary>The Etag for the resource (if relevant).</summary>
    [JsonPropertyName("etag")] public string? Etag { get; init; }

    /// <summary>Server's date time modified.</summary>
    [JsonPropertyName("lastModified")] public string? LastModified { get; init; }

    /// <summary>OperationOutcome with hints and warnings (for batch/transaction).</summary>
    [JsonPropertyName("outcome")] public JsonElement? Outcome { get; init; }
}

// Note: Signature type is defined in CommonTypes.cs
