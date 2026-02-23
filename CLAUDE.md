# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

This is the Firely .NET SDK - the official SDK for working with HL7 FHIR on .NET. It supports multiple FHIR versions: STU3, R4, R4B, R5, and R6 (experimental).

## Build Commands

```bash
# Restore dependencies
dotnet restore

# Build the solution
dotnet build Hl7.Fhir.sln

# Build for CI (enables additional checks)
dotnet build Hl7.Fhir.sln --configuration Release /p:ContinuousIntegrationBuild=true

# Run all tests
dotnet test Hl7.Fhir.sln

# Run tests for a specific project (e.g., R4)
dotnet test src/Hl7.Fhir.R4.Tests/Hl7.Fhir.R4.Tests.csproj

# Run a single test by filter
dotnet test --filter "FullyQualifiedName~TestMethodName"

# Generate binary compatibility suppression file (when breaking binary compat intentionally)
dotnet pack /p:ApiCompatGenerateSuppressionFile=true
```

## Architecture

### Project Layering

The SDK uses a layered architecture with shared code across FHIR versions:

1. **Hl7.Fhir.Base** - Core functionality shared across all FHIR versions:
   - ElementModel (ITypedElement, ISourceNode) - format-agnostic tree representation
   - FhirPath expression evaluation
   - Serialization infrastructure (JSON/XML)
   - REST client base
   - Base POCO types (Resource, Element, primitives)

2. **Hl7.Fhir.Conformance** - Conformance resources (StructureDefinition, ValueSet, etc.) shared across R4+

3. **Hl7.Fhir.{Version}** (STU3, R4, R4B, R5, R6) - Version-specific libraries:
   - Generated POCO classes for all FHIR resource types
   - ModelInfo with version-specific type metadata
   - Version-specific serializers/parsers

4. **Hl7.Fhir.Specification.Data.{Version}** - Embedded specification.zip containing FHIR profiles and valuesets

### Shared Projects (Shims)

Code that works across multiple FHIR versions uses MSBuild shared projects:

- `Hl7.Fhir.Shims.Base` - Code shared by Base and Conformance
- `Hl7.Fhir.Shims.STU3AndUp` - Code for STU3 and newer (serializers, FhirClient, etc.)
- `Hl7.Fhir.Shims.R4AndUp` - Code for R4 and newer

These are `.shproj` files that compile into each version-specific project.

### Key Abstractions

- **ITypedElement** - Read-only, typed access to FHIR data with schema awareness
- **ISourceNode** - Low-level, untyped access to serialized FHIR data
- **POCO classes** - Generated strongly-typed C# classes for each FHIR resource/type
- **FhirClient** - HTTP client for FHIR REST operations

### Test Structure

Tests use shared projects mirroring the main code structure:
- `Hl7.Fhir.Shared.Tests` - Tests compiled into each version's test project
- `Hl7.Fhir.{Area}.Shared.Tests` - Area-specific shared tests (Serialization, ElementModel, Specification)
- `Hl7.Fhir.{Version}.Tests` - Version-specific test projects

Test frameworks: MSTest (primary), xUnit, FluentAssertions, NSubstitute, Verify.MSTest

## Code Conventions

- Target frameworks: `net8.0` and `netstandard2.1`
- C# language version: 14.0
- Private fields: `_camelCase`
- Constants and static readonly: `ALL_UPPER`
- Interfaces: `IPrefix`
- Warnings treated as errors; CS4014 (un-awaited async) is an error
- Braces on new lines (Allman style)
- Use explicit types over `var`

## Contributing

- Branch from `develop` (Git Flow)
- STU3+ versions are all in the same `develop` branch
- The separate `firely-net-common` repo is deprecated; all code is now here

## CI/CD

- Azure Pipelines (build/azure-pipelines.yml)
- Pre-release packages published to GitHub Packages on every commit to develop
- Tagged releases (v*) publish to NuGet.org
