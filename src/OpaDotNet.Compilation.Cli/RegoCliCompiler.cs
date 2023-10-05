﻿using System.Diagnostics.CodeAnalysis;

using JetBrains.Annotations;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpaDotNet.Compilation.Abstractions;

namespace OpaDotNet.Compilation.Cli;

/// <summary>
/// Compiles OPA bundle with opa cli tool.
/// </summary>
[PublicAPI]
public class RegoCliCompiler : IRegoCompiler
{
    private static IOptions<RegoCliCompilerOptions> Default { get; } = new OptionsWrapper<RegoCliCompilerOptions>(new());

    private readonly ILogger _logger;

    private readonly IOptions<RegoCliCompilerOptions> _options;

    /// <summary>
    /// Creates new instance of <see cref="RegoCliCompiler"/> class.
    /// </summary>
    /// <param name="options">Compilation options</param>
    /// <param name="logger">Logger instance</param>
    public RegoCliCompiler(
        IOptions<RegoCliCompilerOptions>? options = null,
        ILogger<RegoCliCompiler>? logger = null)
    {
        _options = options ?? Default;
        _logger = logger ?? NullLogger<RegoCliCompiler>.Instance;
    }

    private string CliPath => string.IsNullOrWhiteSpace(_options.Value.OpaToolPath)
        ? "opa"
        : _options.Value.OpaToolPath;

    private static string NormalizePath(string path) => path.Replace("\\", "/");

    /// <inheritdoc />
    public async Task<RegoCompilerVersion> Version(CancellationToken cancellationToken = default)
    {
        var cli = await OpaCliWrapper.Create(CliPath, _logger, cancellationToken).ConfigureAwait(false);
        return cli.VersionInfo;
    }

    /// <inheritdoc />
    public async Task<Stream> Compile(
        string path,
        CompilationParameters parameters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(parameters);

        path = NormalizePath(path);

        using var scope = _logger.BeginScope("Bundle {Path}", path);

        var cli = await OpaCliWrapper.Create(CliPath, _logger, cancellationToken).ConfigureAwait(false);

        var fullPath = Path.GetFullPath(path);

        // bundlePath can be directory or bundle archive.
        var bundleDirectory =
            File.GetAttributes(fullPath).HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath).Directory!;

        var outDir = new DirectoryInfo(_options.Value.OutputPath ?? bundleDirectory.FullName);
        var outputPath = outDir.FullName;
        var outputFileName = Path.Combine(outputPath, $"{Guid.NewGuid()}.tar.gz");

        var capsFile = await WriteCapabilities(cli, parameters, outputPath, cancellationToken).ConfigureAwait(false);

        var sourcePath = fullPath;

        if (!Path.IsPathRooted(path))
            sourcePath = Path.GetRelativePath(AppContext.BaseDirectory, path);

        var args = new OpaCliBuildArgs
        {
            IsBundle = parameters.IsBundle,
            SourcePath = sourcePath,
            OutputFile = outputFileName,
            Entrypoints = parameters.Entrypoints?.ToHashSet(),
            ExtraArguments = _options.Value.ExtraArguments,
            CapabilitiesFile = capsFile?.FullName,
            CapabilitiesVersion = _options.Value.CapabilitiesVersion,
            PruneUnused = _options.Value.PruneUnused,
            Debug = _options.Value.Debug,
        };

        try
        {
            return await Build(cli, args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var doCleanup = capsFile != null && capsFile.Attributes.HasFlag(FileAttributes.Temporary);

            if (doCleanup && !_options.Value.PreserveBuildArtifacts)
                capsFile?.Delete();
        }
    }

    /// <inheritdoc />
    public async Task<Stream> Compile(
        Stream stream,
        CompilationParameters parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(parameters);

        var path = _options.Value.OutputPath ?? AppContext.BaseDirectory;
        var fileName = Guid.NewGuid();
        var sourceFile = new FileInfo(Path.Combine(path, $"{fileName}.tar.gz"));

        try
        {
            var fs = new FileStream(sourceFile.FullName, FileMode.CreateNew);
            await using var _ = fs.ConfigureAwait(false);

            await stream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);

            return await Compile(
                fs.Name,
                parameters,
                cancellationToken
                ).ConfigureAwait(false);
        }
        finally
        {
            if (!_options.Value.PreserveBuildArtifacts)
                sourceFile.Delete();
        }
    }

    private async Task<FileInfo?> WriteCapabilities(
        OpaCliWrapper cli,
        CompilationParameters parameters,
        string outputPath,
        CancellationToken cancellationToken)
    {
        FileInfo? result = null;

        if (string.IsNullOrWhiteSpace(parameters.CapabilitiesFilePath) && parameters.CapabilitiesStream == null)
            return null;

        if (!string.IsNullOrWhiteSpace(parameters.CapabilitiesFilePath))
        {
            result = new FileInfo(parameters.CapabilitiesFilePath);

            if (!result.Exists)
                throw new RegoCompilationException($"Capabilities file {result.FullName} was not found");
        }

        if (parameters.CapabilitiesStream != null)
        {
            var capsFileName = Path.Combine(outputPath, $"{Guid.NewGuid()}.json");
            result = new FileInfo(capsFileName);

            var fs = result.OpenWrite();
            await using var _ = fs.ConfigureAwait(false);

            await parameters.CapabilitiesStream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);

            result.Attributes |= FileAttributes.Temporary;
        }

        if (result == null || string.IsNullOrWhiteSpace(_options.Value.CapabilitiesVersion))
            return result;

        var capsStream = result.OpenRead();
        await using var __ = capsStream.ConfigureAwait(false);

        result = await MergeCapabilities(
            cli,
            outputPath,
            capsStream,
            _options.Value.CapabilitiesVersion,
            cancellationToken
            ).ConfigureAwait(false);

        result.Attributes |= FileAttributes.Temporary;
        return result;
    }

    // /// <inheritdoc />
    // public async Task<Stream> CompileBundle(
    //     string bundlePath,
    //     IEnumerable<string>? entrypoints = null,
    //     string? capabilitiesFilePath = null,
    //     CancellationToken cancellationToken = default)
    // {
    //     ArgumentException.ThrowIfNullOrEmpty(bundlePath);
    //
    //     bundlePath = NormalizePath(bundlePath);
    //     using var scope = _logger.BeginScope("Bundle {Path}", bundlePath);
    //
    //     var cli = await OpaCliWrapper.Create(CliPath, _logger, cancellationToken).ConfigureAwait(false);
    //
    //     var fullBundlePath = Path.GetFullPath(bundlePath);
    //
    //     // bundlePath can be directory or bundle archive.
    //     var bundleDirectory =
    //         File.GetAttributes(fullBundlePath).HasFlag(FileAttributes.Directory)
    //             ? new DirectoryInfo(fullBundlePath)
    //             : new FileInfo(fullBundlePath).Directory!;
    //
    //     var outDir = new DirectoryInfo(_options.Value.OutputPath ?? bundleDirectory.FullName);
    //     var outputPath = outDir.FullName;
    //     var outputFileName = Path.Combine(outputPath, $"{Guid.NewGuid()}.tar.gz");
    //
    //     string? capabilitiesFile = null;
    //     FileInfo? capsFile = null;
    //
    //     if (!string.IsNullOrWhiteSpace(capabilitiesFilePath))
    //     {
    //         var fi = new FileInfo(capabilitiesFilePath);
    //
    //         if (!fi.Exists)
    //         {
    //             throw new RegoCompilationException(
    //                 fullBundlePath,
    //                 $"Capabilities file {fi.FullName} was not found"
    //                 );
    //         }
    //
    //         if (!string.IsNullOrWhiteSpace(_options.Value.CapabilitiesVersion))
    //         {
    //             capsFile = await MergeCapabilities(
    //                 cli,
    //                 outputPath,
    //                 fi,
    //                 _options.Value.CapabilitiesVersion,
    //                 cancellationToken
    //                 ).ConfigureAwait(false);
    //         }
    //
    //         capabilitiesFile = capsFile?.FullName ?? fi.FullName;
    //     }
    //
    //     var sp = fullBundlePath;
    //
    //     if (!Path.IsPathRooted(bundlePath))
    //         sp = Path.GetRelativePath(AppContext.BaseDirectory, bundlePath);
    //
    //     var args = new OpaCliBuildArgs
    //     {
    //         IsBundle = true,
    //         SourcePath = sp,
    //         OutputFile = outputFileName,
    //         Entrypoints = entrypoints?.ToHashSet(),
    //         ExtraArguments = _options.Value.ExtraArguments,
    //         CapabilitiesFile = capabilitiesFile,
    //         CapabilitiesVersion = _options.Value.CapabilitiesVersion,
    //         PruneUnused = _options.Value.PruneUnused,
    //         Debug = _options.Value.Debug,
    //     };
    //
    //     try
    //     {
    //         return await Build(cli, args, cancellationToken).ConfigureAwait(false);
    //     }
    //     finally
    //     {
    //         if (!_options.Value.PreserveBuildArtifacts)
    //             capsFile?.Delete();
    //     }
    // }
    //
    // /// <inheritdoc />
    // public async Task<Stream> CompileFile(
    //     string sourceFilePath,
    //     IEnumerable<string>? entrypoints = null,
    //     CancellationToken cancellationToken = default)
    // {
    //     ArgumentException.ThrowIfNullOrEmpty(sourceFilePath);
    //
    //     sourceFilePath = NormalizePath(sourceFilePath);
    //     using var scope = _logger.BeginScope("File {Path}", sourceFilePath);
    //
    //     var cli = await OpaCliWrapper.Create(CliPath, _logger, cancellationToken).ConfigureAwait(false);
    //
    //     var sourceFile = new FileInfo(sourceFilePath);
    //
    //     if (!sourceFile.Exists)
    //         throw new RegoCompilationException(sourceFilePath, $"Source file {sourceFilePath} not found");
    //
    //     var outDir = new DirectoryInfo(_options.Value.OutputPath ?? sourceFile.Directory!.FullName);
    //     var outputPath = outDir.FullName;
    //     var outputFileName = Path.Combine(outputPath, $"{Guid.NewGuid()}.tar.gz");
    //
    //     var sp = sourceFilePath;
    //
    //     if (!Path.IsPathRooted(sourceFilePath))
    //         sp = Path.GetRelativePath(AppContext.BaseDirectory, sourceFilePath);
    //
    //     var args = new OpaCliBuildArgs
    //     {
    //         SourcePath = sp,
    //         OutputFile = outputFileName,
    //         Entrypoints = entrypoints?.ToHashSet(),
    //         ExtraArguments = _options.Value.ExtraArguments,
    //         CapabilitiesVersion = _options.Value.CapabilitiesVersion,
    //         PruneUnused = _options.Value.PruneUnused,
    //         Debug = _options.Value.Debug,
    //     };
    //
    //     return await Build(cli, args, cancellationToken).ConfigureAwait(false);
    // }
    //
    // /// <inheritdoc />
    // public async Task<Stream> CompileStream(
    //     Stream bundle,
    //     IEnumerable<string>? entrypoints = null,
    //     Stream? capabilitiesJson = null,
    //     CancellationToken cancellationToken = default)
    // {
    //     ArgumentNullException.ThrowIfNull(bundle);
    //
    //     var path = _options.Value.OutputPath ?? AppContext.BaseDirectory;
    //     var fileName = Guid.NewGuid();
    //     var sourceFile = new FileInfo(Path.Combine(path, $"{fileName}.tar.gz"));
    //     var capsFile = new FileInfo(Path.Combine(path, $"{fileName}.json"));
    //
    //     try
    //     {
    //         var fs = new FileStream(sourceFile.FullName, FileMode.CreateNew);
    //         await using var _ = fs.ConfigureAwait(false);
    //
    //         await bundle.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
    //         await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
    //
    //         FileStream? capsFs = null;
    //
    //         if (capabilitiesJson != null)
    //         {
    //             capsFs = new FileStream(capsFile.FullName, FileMode.CreateNew);
    //             await using var __ = capsFs.ConfigureAwait(false);
    //
    //             await capabilitiesJson.CopyToAsync(capsFs, cancellationToken).ConfigureAwait(false);
    //             await capsFs.FlushAsync(cancellationToken).ConfigureAwait(false);
    //         }
    //
    //         return await CompileBundle(
    //             fs.Name,
    //             entrypoints,
    //             capsFs?.Name,
    //             cancellationToken
    //             ).ConfigureAwait(false);
    //     }
    //     finally
    //     {
    //         if (!_options.Value.PreserveBuildArtifacts)
    //         {
    //             sourceFile.Delete();
    //
    //             if (capsFile.Exists)
    //                 capsFile.Delete();
    //         }
    //     }
    // }

    private static async Task<FileInfo> MergeCapabilities(
        OpaCliWrapper cli,
        string outputPath,
        Stream capabilities,
        string version,
        CancellationToken cancellationToken)
    {
        var capsFileName = Path.Combine(outputPath, $"{Guid.NewGuid()}.json");
        var result = new FileInfo(capsFileName);

        await cli.Capabilities(result.FullName, version, cancellationToken).ConfigureAwait(false);

        if (!result.Exists)
            throw new RegoCompilationException(capsFileName, "Failed to locate capabilities file");

        try
        {
            var defaultCapsFs = result.Open(FileMode.Open, FileAccess.ReadWrite);
            await using var _ = defaultCapsFs.ConfigureAwait(false);

            var capsFs = capabilities;
            await using var __ = capsFs.ConfigureAwait(false);

            var ms = BundleWriter.MergeCapabilities(defaultCapsFs, capsFs);
            await using var ___ = ms.ConfigureAwait(false);

            defaultCapsFs.SetLength(0);
            await defaultCapsFs.FlushAsync(cancellationToken).ConfigureAwait(false);

            await ms.CopyToAsync(defaultCapsFs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new RegoCompilationException(
                outputPath,
                "Failed to parse capabilities",
                ex
                );
        }

        return result;
    }

    private async Task<Stream> Build(
        OpaCliWrapper cli,
        OpaCliBuildArgs args,
        CancellationToken cancellationToken)
    {
        await cli.Build(args, cancellationToken).ConfigureAwait(false);

        if (!File.Exists(args.OutputFile))
        {
            throw new RegoCompilationException(
                args.SourcePath,
                $"Failed to locate expected output file {args.OutputFile}"
                );
        }

        _logger.LogInformation("Compilation succeeded");

        return _options.Value.PreserveBuildArtifacts
            ? new FileStream(args.OutputFile, FileMode.Open)
            : new DeleteOnCloseFileStream(args.OutputFile, FileMode.Open);
    }

    [ExcludeFromCodeCoverage]
    private class DeleteOnCloseFileStream : FileStream
    {
        private readonly string _path;

        public DeleteOnCloseFileStream(string path, FileMode mode) : base(path, mode)
        {
            _path = path;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                DeleteFile();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            DeleteFile();
        }

        private void DeleteFile()
        {
            try
            {
                File.Delete(_path);
            }

            // ReSharper disable once EmptyGeneralCatchClause
            catch (Exception)
            {
            }
        }
    }
}