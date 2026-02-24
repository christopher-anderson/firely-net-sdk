using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Ncqa.Hedis.Core.Benchmarks;

// Run benchmarks
var config = DefaultConfig.Instance
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);

// Run bundle benchmark using real HEDIS patient data
BenchmarkRunner.Run<BundleBenchmarks>(config);
