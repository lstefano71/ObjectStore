using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

var config = DefaultConfig.Instance
    .AddJob(Job.ShortRun.WithWarmupCount(3).WithIterationCount(10));

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
