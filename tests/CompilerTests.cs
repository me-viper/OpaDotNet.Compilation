﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpaDotNet.Compilation.Abstractions;
using OpaDotNet.Compilation.Cli;
using OpaDotNet.Compilation.Tests.Common;

using Xunit.Abstractions;

namespace OpaDotNet.Compilation.Tests;

public abstract class CompilerTests<T, TOptions>
    where T : IRegoCompiler
    where TOptions : RegoCompilerOptions, new()
{
    protected readonly ILoggerFactory LoggerFactory;

    protected CompilerTests(ITestOutputHelper output)
    {
        LoggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(output) });
    }

    protected abstract T CreateCompiler(TOptions? opts = null, ILoggerFactory? loggerFactory = null);

    [Theory]
    [InlineData("ex/test")]
    [InlineData(null)]
    public async Task CompileFile(string? entrypoint)
    {
        var opts = new TOptions
        {
            Debug = true,
        };

        var eps = string.IsNullOrWhiteSpace(entrypoint) ? null : new[] { entrypoint };
        var compiler = CreateCompiler(opts, LoggerFactory);
        var policy = await compiler.CompileFile(Path.Combine("TestData", "policy.rego"), eps);

        AssertPolicy.IsValid(policy);
    }

    [Fact]
    public async Task Version()
    {
        var compiler = CreateCompiler();
        var v = await compiler.Version();

        Assert.NotNull(v.Version);
        Assert.NotNull(v.GoVersion);
        Assert.NotNull(v.Platform);
    }

    [Fact]
    public async Task FailCompilation()
    {
        var opts = new TOptions();
        var compiler = CreateCompiler(opts, LoggerFactory);
        var ex = await Assert.ThrowsAsync<RegoCompilationException>(() => compiler.CompileSource("bad rego", new[] { "ep" }));

        Assert.Contains("rego_parse_error: package expected", ex.Message);
    }

    [Fact]
    public async Task FailCapabilities()
    {
        var compiler = CreateCompiler(new(), LoggerFactory);

        _ = await Assert.ThrowsAsync<RegoCompilationException>(
            () => compiler.CompileBundle(
                Path.Combine("TestData", "capabilities"),
                new[] { "capabilities/f" },
                Path.Combine("TestData", "capabilities", "capabilities.json")
                )
            );
    }

    [Fact]
    public async Task SetCapabilitiesBundle()
    {
        var opts = new TOptions
        {
            CapabilitiesVersion = "v0.53.1",
        };

        var compiler = CreateCompiler(opts, LoggerFactory);

        await using var policy = await compiler.CompileBundle(
            Path.Combine("TestData", "compile-bundle", "example"),
            new[] { "test1/hello", "test2/hello" }
            );

        Assert.NotNull(policy);
    }

    [Fact]
    public async Task SetCapabilitiesSource()
    {
        var opts = new TOptions
        {
            CapabilitiesVersion = "v0.53.1",
        };

        var compiler = CreateCompiler(opts, LoggerFactory);

        await using var policy = await compiler.CompileSource(
            TestHelpers.SimplePolicySource,
            TestHelpers.SimplePolicyEntrypoints
            );

        Assert.NotNull(policy);
    }

    [Fact]
    public async Task MergeCapabilities()
    {
        var opts = new TOptions
        {
            CapabilitiesVersion = "v0.53.1",
        };

        var compiler = CreateCompiler(opts, LoggerFactory);

        await using var policy = await compiler.CompileBundle(
            Path.Combine("TestData", "capabilities"),
            new[] { "capabilities/f" },
            Path.Combine("TestData", "capabilities", "capabilities.json")
            );

        AssertPolicy.IsValid(policy);
    }
}