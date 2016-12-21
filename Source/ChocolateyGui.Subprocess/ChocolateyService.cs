using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using chocolatey;
using chocolatey.infrastructure.app;
using chocolatey.infrastructure.app.configuration;
using chocolatey.infrastructure.app.domain;
using chocolatey.infrastructure.app.nuget;
using chocolatey.infrastructure.results;
using chocolatey.infrastructure.services;
using ChocolateyGui.Models;
using ChocolateyGui.Rpc;
using Grpc.Core;
using Grpc.Core.Utils;
using Nito.AsyncEx;
using NuGet;
using ChocolateySource = ChocolateyGui.Rpc.ChocolateySource;
using ILogger = Serilog.ILogger;

namespace ChocolateyGui.Subprocess
{
    internal class ChocolateyService : PackageService.PackageServiceBase
    {
        private static readonly ILogger Logger = Serilog.Log.ForContext<ChocolateyService>();
        private static readonly AsyncReaderWriterLock Lock = new AsyncReaderWriterLock();

        private readonly StreamingLogger _streamingLogger;

        public ChocolateyService()
        {
            Logger.Debug("Connecting to streaming log endpoint.");
            _streamingLogger = new StreamingLogger();
        }

        public override Task<IsElevatedResult> IsElevated(Empty request, ServerCallContext context)
        {
            return Task.FromResult(new IsElevatedResult { IsElevated = Hacks.IsElevated });
        }

        public override async Task<PackagesList> GetInstalledPackages(Empty request, ServerCallContext context)
        {
            try
            {
                using (await Lock.ReaderLockAsync())
                {
                    var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger);
                    choco.Set(
                        config =>
                            {
                                config.CommandName = CommandNameType.list.ToString();
                                config.ListCommand.LocalOnly = true;
                            });
                    var packageResult =
                        (await choco.ListAsync<PackageResult>(context.CancellationToken)).Select(
                            package => GetMappedPackage(package, true)).ToList();
                    var result = new PackagesList();
                    result.Packages.Add(packageResult);
                    return result;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to retived install packages.");
                throw;
            }
        }

        public override async Task GetOutdatedPackages(OutdatedArgs request, IServerStreamWriter<OutdatedResult> responseStream, ServerCallContext context)
        {
            using (await Lock.ReaderLockAsync())
            {
                var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger);
                choco.Set(
                    config =>
                    {
                        config.CommandName = "outdated";
                        config.RegularOutput = false;
                        config.Prerelease = request.IncludePrerelease;
                    });

                var chocoConfig = choco.GetConfiguration();
                var nugetLogger = new ChocolateyNugetLogger();
                var packageManager = NugetCommon.GetPackageManager(
                    chocoConfig,
                    nugetLogger,
                    installSuccessAction: null,
                    uninstallSuccessAction: null,
                    addUninstallHandler: false);

                var packageInfoService = Hacks.GetPackageInformationService();
                var ids =
                    packageManager.LocalRepository.GetPackages()
                        .Where(p => !packageInfoService.get_package_information(p).IsPinned);
                var updateable =
                    await Task.Run(() => packageManager.SourceRepository.GetUpdates(ids, false, false).ToList());

                await responseStream.WriteAllAsync(
                    updateable.Select(p => new OutdatedResult { Id = p.Id, Version = p.Version.ToString() }));
            }
        }

        public override async Task<PackageSearchReply> Search(PackageSearchArgs request, ServerCallContext context)
        {
            using (await Lock.ReaderLockAsync())
            {
                var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger).Set(
                    config =>
                    {
                        config.CommandName = CommandNameType.list.ToString();
                        config.Input = request.Query;
                        config.AllowUnofficialBuild = true;
                        config.AllVersions = request.IncludeAllVersions;
                        config.Prerelease = request.IncludePrerelease;
                        config.ListCommand.Page = request.CurrentPage;
                        config.ListCommand.PageSize = request.PageSize;
                        if (string.IsNullOrWhiteSpace(request.Query) || !string.IsNullOrWhiteSpace(request.SortColumn))
                        {
                            config.ListCommand.OrderByPopularity = string.IsNullOrWhiteSpace(request.SortColumn)
                                                                   || request.SortColumn == "DownloadCount";
                        }

                        config.ListCommand.Exact = request.MatchQuery;
                        if (!string.IsNullOrWhiteSpace(request.Source))
                        {
                            config.Sources = request.Source;
                        }
#if !DEBUG
                        config.Verbose = false;
#endif // DEBUG
                    });

                var packages = (await choco.ListAsync<PackageResult>(context.CancellationToken)).Select(pckge => GetMappedPackage(pckge));
                var reply = new PackageSearchReply
                {
                    TotalCount = await Task.Run(() => choco.ListCount(), context.CancellationToken)
                };
                reply.Packages.Add(packages);
                return reply;
            }
        }

        public override async Task<Package> GetByVersionAndId(GetPackageArgs request, ServerCallContext context)
        {
            using (await Lock.ReaderLockAsync())
            {
                var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger);
                var chocoConfig = choco.GetConfiguration();
                var nugetLogger = new ChocolateyNugetLogger();
                var packageManager = NugetCommon.GetPackageManager(
                    chocoConfig,
                    nugetLogger,
                    installSuccessAction: null,
                    uninstallSuccessAction: null,
                    addUninstallHandler: false);

                var rawPackage =
                    await Task.Run(
                        () =>
                            packageManager.SourceRepository.FindPackage(
                                request.Id,
                                SemanticVersion.Parse(request.Version),
                                allowPrereleaseVersions: request.IsPrerelease,
                                allowUnlisted: true));

                if (rawPackage == null)
                {
                    context.Status = new Status(StatusCode.NotFound, $"There was no package with the id {request.Id} and version {request.Version}.");
                    return null;
                }

                return GetMappedPackage(new PackageResult(rawPackage, null, chocoConfig.Sources));
            }
        }

        public override async Task<PackageOperationResult> InstallPackage(InstallPackageArgs request, ServerCallContext context)
        {
            try
            {
                using (await Lock.WriterLockAsync())
                {
                    var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger);
                    choco.Set(
                        config =>
                            {
                                config.CommandName = CommandNameType.install.ToString();
                                config.PackageNames = request.Id;
                                config.Features.UsePackageExitCodes = false;

                                if (request.Version != null)
                                {
                                    config.Version = request.Version.ToString();
                                }

                                if (request.Source != null)
                                {
                                    config.Sources = request.Source;
                                }

                                if (request.Force)
                                {
                                    config.Force = true;
                                }
                            });

                    var errors = new List<string>();
                    Action<StreamingLogMessage> grabErrors = m =>
                        {
                            switch (m.LogLevel)
                            {
                                case StreamingLogLevel.Warn:
                                case StreamingLogLevel.Error:
                                case StreamingLogLevel.Fatal:
                                    errors.Add(m.Message);
                                    break;
                                case StreamingLogLevel.Debug:
                                case StreamingLogLevel.Verbose:
                                case StreamingLogLevel.Info:
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        };

                    using (_streamingLogger.Intercept(grabErrors))
                    {
                        await choco.RunAsync(context.CancellationToken);

                        if (Environment.ExitCode != 0)
                        {
                            Environment.ExitCode = 0;
                            var packageResult = new PackageOperationResult { Successful = false };
                            packageResult.Messages.Add(errors);
                            return packageResult;
                        }

                        return new PackageOperationResult { Successful = true };
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error while installing package.");
                throw;
            }
        }

        public override async Task<PackageOperationResult> UninstallPackage(UninstallPackageArgs request, ServerCallContext context)
        {
            using (await Lock.WriterLockAsync())
            {
                var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger);
                choco.Set(
                    config =>
                    {
                        config.CommandName = CommandNameType.uninstall.ToString();
                        config.PackageNames = request.Id;
                        config.Features.UsePackageExitCodes = false;

                        if (request.Version != null)
                        {
                            config.Version = request.Version;
                        }
                    });

                var errors = new List<string>();
                Action<StreamingLogMessage> grabErrors = m =>
                {
                    switch (m.LogLevel)
                    {
                        case StreamingLogLevel.Warn:
                        case StreamingLogLevel.Error:
                        case StreamingLogLevel.Fatal:
                            errors.Add(m.Message);
                            break;
                    }
                };

                using (_streamingLogger.Intercept(grabErrors))
                {
                    try
                    {
                        await choco.RunAsync(context.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        var packageResult = new PackageOperationResult { Successful = false, ExceptionMessage = ex.Message };
                        packageResult.Messages.Add(errors);
                        return packageResult;
                    }

                    if (Environment.ExitCode != 0)
                    {
                        Environment.ExitCode = 0;
                        var packageResult = new PackageOperationResult { Successful = false };
                        packageResult.Messages.Add(errors);
                        return packageResult;
                    }

                    return new PackageOperationResult { Successful = true };
                }
            }
        }

        public override async Task<PackageOperationResult> UpdatePackage(UpdatePackageArgs request, ServerCallContext context)
        {
            using (await Lock.WriterLockAsync())
            {
                var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger);
                choco.Set(
                    config =>
                    {
                        config.CommandName = CommandNameType.upgrade.ToString();
                        config.PackageNames = request.Id;
                        config.Features.UsePackageExitCodes = false;
                    });

                var errors = new List<string>();
                Action<StreamingLogMessage> grabErrors = m =>
                {
                    switch (m.LogLevel)
                    {
                        case StreamingLogLevel.Warn:
                        case StreamingLogLevel.Error:
                        case StreamingLogLevel.Fatal:
                            errors.Add(m.Message);
                            break;
                    }
                };

                using (_streamingLogger.Intercept(grabErrors))
                {
                    try
                    {
                        await choco.RunAsync(context.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        var packageResult = new PackageOperationResult { Successful = false, ExceptionMessage = ex.Message };
                        packageResult.Messages.Add(errors);
                        return packageResult;
                    }

                    if (Environment.ExitCode != 0)
                    {
                        Environment.ExitCode = 0;
                        var packageResult = new PackageOperationResult { Successful = false };
                        packageResult.Messages.Add(errors);
                        return packageResult;
                    }

                    return new PackageOperationResult { Successful = true };
                }
            }
        }

        public override async Task<PackageOperationResult> PinPackage(PinPackageArgs request, ServerCallContext context)
        {
            using (await Lock.WriterLockAsync())
            {
                var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger);
                choco.Set(
                    config =>
                    {
                        config.CommandName = "pin";
                        config.PinCommand.Command = PinCommandType.add;
                        config.PinCommand.Name = request.Id;
                        config.Version = request.Version;
                    });

                try
                {
                    await choco.RunAsync(context.CancellationToken);
                }
                catch (Exception ex)
                {
                    var packageResult = new PackageOperationResult { Successful = false, ExceptionMessage = ex.Message };
                    return packageResult;
                }

                return new PackageOperationResult { Successful = true };
            }
        }

        public override async Task<PackageOperationResult> UnpinPackage(PinPackageArgs request, ServerCallContext context)
        {
            using (await Lock.WriterLockAsync())
            {
                var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger);
                choco.Set(
                    config =>
                    {
                        config.CommandName = "pin";
                        config.PinCommand.Command = PinCommandType.remove;
                        config.PinCommand.Name = request.Id;
                        config.Version = request.Version;
                    });

                try
                {
                    await choco.RunAsync(context.CancellationToken);
                }
                catch (Exception ex)
                {
                    var packageResult = new PackageOperationResult { Successful = false, ExceptionMessage = ex.Message };
                    return packageResult;
                }

                return new PackageOperationResult { Successful = true };
            }
        }

        public override async Task GetFeatures(Empty request, IServerStreamWriter<ChocolateyFeature> responseStream, ServerCallContext context)
        {
            using (await Lock.ReaderLockAsync())
            {
                var xmlService = Hacks.GetInstance<IXmlService>();
                var config =
                    await Task.Run(
                        () => xmlService.deserialize<ConfigFileSettings>(ApplicationParameters.GlobalConfigFileLocation), context.CancellationToken);

                await responseStream.WriteAllAsync(config.Features.Select(Mapper.Map<ChocolateyFeature>));
            }
        }

        public override async Task<Empty> SetFeature(ChocolateyFeature request, ServerCallContext context)
        {
            using (await Lock.WriterLockAsync())
            {
                var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger).Set(
                    config =>
                    {
                        config.CommandName = "feature";
                        config.FeatureCommand.Command = request.Enabled ? FeatureCommandType.enable : FeatureCommandType.disable;
                        config.FeatureCommand.Name = request.Name;
                    });

                await choco.RunAsync(context.CancellationToken);
                return Empty.Instance;
            }
        }

        public override async Task GetSettings(Empty request, IServerStreamWriter<ChocolateySetting> responseStream, ServerCallContext context)
        {
            using (await Lock.ReaderLockAsync())
            {
                var xmlService = Hacks.GetInstance<IXmlService>();
                var config =
                    await Task.Run(
                        () => xmlService.deserialize<ConfigFileSettings>(ApplicationParameters.GlobalConfigFileLocation), context.CancellationToken);

                await responseStream.WriteAllAsync(config.ConfigSettings.Select(Mapper.Map<ChocolateySetting>));
            }
        }

        public override async Task<Empty> SetSetting(ChocolateySetting request, ServerCallContext context)
        {
            using (await Lock.WriterLockAsync())
            {
                var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger).Set(
                    config =>
                    {
                        config.CommandName = "config";
                        config.ConfigCommand.Command = ConfigCommandType.set;
                        config.ConfigCommand.Name = request.Key;
                        config.ConfigCommand.ConfigValue = request.Value;
                    });

                await choco.RunAsync(context.CancellationToken);
                return Empty.Instance;
            }
        }

        public override async Task GetSources(Empty request, IServerStreamWriter<ChocolateySource> responseStream, ServerCallContext context)
        {
            using (await Lock.ReaderLockAsync())
            {
                var xmlService = Hacks.GetInstance<IXmlService>();
                var config =
                    await Task.Run(
                        () => xmlService.deserialize<ConfigFileSettings>(ApplicationParameters.GlobalConfigFileLocation), context.CancellationToken);
                
                await responseStream.WriteAllAsync(config.Sources.Select(Mapper.Map<ChocolateySource>));
            }
        }

        public override async Task<Empty> AddSource(ChocolateySource request, ServerCallContext context)
        {
            using (await Lock.WriterLockAsync())
            {
                var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger).Set(
                    config =>
                    {
                        config.CommandName = "source";
                        config.SourceCommand.Command = SourceCommandType.add;
                        config.SourceCommand.Name = request.Id;
                        config.Sources = request.Value;
                        config.SourceCommand.Username = request.UserName;
                        config.SourceCommand.Password = request.Password;
                        config.SourceCommand.Certificate = request.Certificate;
                        config.SourceCommand.CertificatePassword = request.CertificatePassword;
                        config.SourceCommand.Priority = request.Priority;
                    });

                await choco.RunAsync(context.CancellationToken);

                if (request.Disabled)
                {
                    choco.Set(
                        config =>
                        {
                            config.CommandName = "source";
                            config.SourceCommand.Command = SourceCommandType.disable;
                            config.SourceCommand.Name = request.Id;
                        });
                    await choco.RunAsync(context.CancellationToken);
                }
                else
                {
                    choco.Set(
                       config =>
                       {
                           config.CommandName = "source";
                           config.SourceCommand.Command = SourceCommandType.enable;
                           config.SourceCommand.Name = request.Id;
                       });
                    await choco.RunAsync(context.CancellationToken);
                }
            }
            
            return Empty.Instance;
        }

        public override async Task<Empty> UpdateSource(UpdateSourceArgs request, ServerCallContext context)
        {
            if (request.Id != request.Source.Id)
            {
                await RemoveSource(new RemoveSourceArgs {Id = request.Id}, context);
            }

            await AddSource(request.Source, context);
            return Empty.Instance;
        }

        public override async Task<PackageOperationResult> RemoveSource(RemoveSourceArgs request, ServerCallContext context)
        {
            using (await Lock.WriterLockAsync())
            {
                var xmlService = Hacks.GetInstance<IXmlService>();
                var chocoCOnfig =
                    await Task.Run(
                        () => xmlService.deserialize<ConfigFileSettings>(ApplicationParameters.GlobalConfigFileLocation));

                var sources = chocoCOnfig.Sources.Select(Mapper.Map<ChocolateySource>).ToList();

                if (sources.All(source => source.Id != request.Id))
                {
                    return new PackageOperationResult();
                }

                var choco = Lets.GetChocolatey().SetCustomLogging(_streamingLogger).Set(
                        config =>
                        {
                            config.CommandName = "source";
                            config.SourceCommand.Command = SourceCommandType.remove;
                            config.SourceCommand.Name = request.Id;
                        });

                await choco.RunAsync(context.CancellationToken);
                return new PackageOperationResult {Successful = true};
            }
        }

        public override Task<Empty> Exit(Empty request, ServerCallContext context)
        {
            context.Status = new Status(StatusCode.Unavailable, "Exiting");
            Program.CanceledEvent.Set();
            return Task.FromResult(Empty.Instance);
        }

        private static Package GetMappedPackage(PackageResult package, bool forceInstalled = false)
        {
            var mappedPackage = package == null ? null : Mapper.Map<Package>(package.Package);
            if (mappedPackage != null)
            {
                var packageInfo = Hacks.GetPackageInformationService().get_package_information(package.Package);
                mappedPackage.IsPinned = packageInfo.IsPinned;
                mappedPackage.IsInstalled = !string.IsNullOrWhiteSpace(package.InstallLocation) || forceInstalled;
            }

            return mappedPackage;
        }
    }
}
