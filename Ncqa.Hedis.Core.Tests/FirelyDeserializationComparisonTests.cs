using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Xunit;
using FhirBundle = Hl7.Fhir.Model.Bundle;
using SystemTask = System.Threading.Tasks.Task;

namespace Ncqa.Hedis.Core.Tests;

/// <summary>
/// Compares throughput of the generated HEDIS DTOs (System.Text.Json, MustSupport-only fields)
/// against the Firely R4 POCO deserializer (JsonSerializer.ForFhir / FhirJsonParser).
/// Both tests run the same 10k-bundle gzip file with the same producer/consumer pattern.
/// </summary>
public class FirelyDeserializationComparisonTests
{
    private const string GzipFile = @"C:\Development\TestData\input_0-10000.ndjson.gz";
    private const string NdjsonFile = @"C:\Development\TestData\input_0-10000.ndjson";

    // ForFhir() options are expensive to build — create once and reuse.
    private static readonly JsonSerializerOptions FirelyOptions =
        new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector).UsingMode(DeserializerModes.Ostrich);

    [Fact]
    public async System.Threading.Tasks.Task Firely_CanDeserializeLargeBundles()
    {
        var timer = Stopwatch.StartNew();
        var channel = Channel.CreateBounded<string>(capacity: 1000);
        int count = 0;

        var producer = SystemTask.Run(async () =>
        {
            using var fileStream = new FileStream(GzipFile, FileMode.Open, FileAccess.Read,
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

        var consumer = Parallel.ForEachAsync(
            channel.Reader.ReadAllAsync(),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (line, _) =>
            {
                JsonSerializer.Deserialize<Hl7.Fhir.Model.Bundle>(line, FirelyOptions);
                Interlocked.Increment(ref count);
                return ValueTask.CompletedTask;
            });

        await SystemTask.WhenAll(producer, consumer);
        timer.Stop();
        Console.WriteLine($"[Firely STJ]  Processed {count} bundles in {timer.Elapsed.TotalSeconds:F2}s  ({count / timer.Elapsed.TotalSeconds:F0} bundles/sec)");
    }

    [Fact]
    public async System.Threading.Tasks.Task HedisDtos_CanDeserializeLargeBundles()
    {
        var timer = Stopwatch.StartNew();
        var channel = Channel.CreateBounded<string>(capacity: 1000);
        int count = 0;

        var hedisOptions = new JsonSerializerOptions();

        var producer = SystemTask.Run(async () =>
        {
            using var fileStream = new FileStream(GzipFile, FileMode.Open, FileAccess.Read,
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

        var consumer = Parallel.ForEachAsync(
            channel.Reader.ReadAllAsync(),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (line, _) =>
            {
                var model = JsonSerializer.Deserialize<Ncqa.Hedis.Core.Bundle>(line, hedisOptions);
                Interlocked.Increment(ref count);
                return ValueTask.CompletedTask;
            });

        await SystemTask.WhenAll(producer, consumer);
        timer.Stop();
        Console.WriteLine($"[HEDIS DTOs] Processed {count} bundles in {timer.Elapsed.TotalSeconds:F2}s  ({count / timer.Elapsed.TotalSeconds:F0} bundles/sec)");
    }

    [Fact]
    public async System.Threading.Tasks.Task Firely_CanDeserializeLargeBundlesUncompressed()
    {
        var timer = Stopwatch.StartNew();
        var channel = Channel.CreateBounded<string>(capacity: 1000);
        int count = 0;

        var producer = SystemTask.Run(async () =>
        {
            using var fileStream = new FileStream(NdjsonFile, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 1 << 17);
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
                JsonSerializer.Deserialize<FhirBundle>(line, FirelyOptions);
                Interlocked.Increment(ref count);
                return ValueTask.CompletedTask;
            });

        await SystemTask.WhenAll(producer, consumer);
        timer.Stop();
        Console.WriteLine($"[Firely STJ uncompressed]  Processed {count} bundles in {timer.Elapsed.TotalSeconds:F2}s  ({count / timer.Elapsed.TotalSeconds:F0} bundles/sec)");
    }
}
