// --------------------------------------------------------------------------------------------------------------------
// <copyright company="Chocolatey" file="ChocolateyRemotePackageService.cs">
//   Copyright 2014 - Present Rob Reynolds, the maintainers of Chocolatey, and RealDimensions Software, LLC
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AutoMapper;
using Caliburn.Micro;
using ChocolateyGui.Models.Messages;
using ChocolateyGui.Providers;
using ChocolateyGui.Rpc;
using ChocolateyGui.Utilities;
using ChocolateyGui.ViewModels.Items;
using Grpc.Core;
using NuGet;
using Serilog;
using PackageSearchResults = ChocolateyGui.Models.PackageSearchResults;

namespace ChocolateyGui.Services
{
    public class ChocolateyRemotePackageService : IChocolateyPackageService, IDisposable
    {
        private static readonly Serilog.ILogger Logger = Log.ForContext<ChocolateyRemotePackageService>();
        private readonly IProgressService _progressService;
        private readonly IMapper _mapper;
        private readonly IEventAggregator _eventAggregator;
        private readonly IConfigService _configService;
        private readonly Func<IPackageViewModel> _packageFactory;
        private readonly AsyncLock _lock = new AsyncLock();

        private Process _chocolateyProcess;
        private Channel _channel;
        private PackageService.PackageServiceClient _chocolateyService;
        private bool _isInitialized;
        private bool? _requiresElevation;
        private Lazy<bool> _forceElevation;

        public ChocolateyRemotePackageService(
            IProgressService progressService,
            IMapper mapper,
            IEventAggregator eventAggregator,
            IConfigService configService,
            Func<IPackageViewModel> packageFactory)
        {
            _progressService = progressService;
            _mapper = mapper;
            _eventAggregator = eventAggregator;
            _configService = configService;
            _packageFactory = packageFactory;
            _forceElevation = new Lazy<bool>(() => _configService.GetSettings().ElevateByDefault);
        }

        public async Task<PackageSearchResults> Search(PackageSearchArgs options)
        {
            await Initialize();
            var results = await _chocolateyService.SearchAsync(options);
            return new PackageSearchResults
                       {
                           Packages =
                               results.Packages.Select(
                                   pcgke => _mapper.Map(pcgke, _packageFactory())),
                           TotalCount = results.TotalCount
                       };
        }

        public async Task<IPackageViewModel> GetByVersionAndIdAsync(string id, SemanticVersion version, bool isPrerelease)
        {
            await Initialize();
            var result = await _chocolateyService.GetByVersionAndIdAsync(new GetPackageArgs { Id = id, Version = version.ToString(), IsPrerelease = isPrerelease});
            return _mapper.Map(result, _packageFactory());
        }

        public async Task<IEnumerable<IPackageViewModel>> GetInstalledPackages(bool force = false)
        {
            await Initialize().ConfigureAwait(false);
            var reply = await _chocolateyService.GetInstalledPackagesAsync(Empty.Instance);
            var vms = reply.Packages.Select(p => _mapper.Map(p, _packageFactory()));
            return vms;
        }

        public async Task<IReadOnlyList<Tuple<string, SemanticVersion>>> GetOutdatedPackages(bool includePrerelease = false)
        {
            await Initialize();
            var results = _chocolateyService.GetOutdatedPackages(new OutdatedArgs { IncludePrerelease = includePrerelease });
            var outdated = new List<Tuple<string, SemanticVersion>>();
            while (await results.ResponseStream.MoveNext())
            {
                var current = results.ResponseStream.Current;
                outdated.Add(Tuple.Create(current.Id, new SemanticVersion(current.Version)));
            }

            return outdated.ToList();
        }

        public async Task InstallPackage(string id, SemanticVersion version = null, string source = null, bool force = false)
        {
            await Initialize(true);
            var result = await _chocolateyService.InstallPackageAsync(new InstallPackageArgs {Id = id, Version = version?.ToString(), Source = source, Force = force});
            if (await HandleError(result, "install", id, version?.ToString()))
            {
                return;
            }

            _eventAggregator.BeginPublishOnUIThread(new PackageChangedMessage(id, PackageChangeType.Installed, version));
        }

        public async Task UninstallPackage(string id, SemanticVersion version, bool force = false)
        {
            await Initialize(true);
            var result = await _chocolateyService.UninstallPackageAsync(new UninstallPackageArgs { Id = id, Version = version.ToString(), Force = force});
            if (await HandleError(result, "uninstall", id, version.ToString()))
            {
                return;
            }

            _eventAggregator.BeginPublishOnUIThread(new PackageChangedMessage(id, PackageChangeType.Uninstalled, version));
        }

        public async Task UpdatePackage(string id, string source = null)
        {
            await Initialize(true);
            var result = await _chocolateyService.UpdatePackageAsync(new UpdatePackageArgs { Id = id, Source = source});
            if (await HandleError(result, "update", id))
            {
                return;
            }

            _eventAggregator.BeginPublishOnUIThread(new PackageChangedMessage(id, PackageChangeType.Updated));
        }

        public async Task PinPackage(string id, SemanticVersion version)
        {
            await Initialize(true);
            var result = await _chocolateyService.PinPackageAsync(new PinPackageArgs { Id = id, Version = version.ToString()});
            if (await HandleError(result, "pin", id, version.ToString()))
            {
                return;
            }

            _eventAggregator.BeginPublishOnUIThread(new PackageChangedMessage(id, PackageChangeType.Pinned, version));
        }

        public async Task UnpinPackage(string id, SemanticVersion version)
        {
            await Initialize(true);
            var result = await _chocolateyService.UnpinPackageAsync(new PinPackageArgs {Id = id, Version = version.ToString()});
            if (await HandleError(result, "unpin", id, version.ToString()))
            {
                return;
            }

            _eventAggregator.BeginPublishOnUIThread(new PackageChangedMessage(id, PackageChangeType.Unpinned, version));
        }

        public async Task<IReadOnlyList<ChocolateyFeature>> GetFeatures()
        {
            await Initialize();
            return await ToList(_chocolateyService.GetFeatures(Empty.Instance));
        }

        public async Task SetFeature(ChocolateyFeature feature)
        {
            await Initialize(true);
            await _chocolateyService.SetFeatureAsync(feature);
        }

        public async Task<IReadOnlyList<ChocolateySetting>> GetSettings()
        {
            await Initialize();
            return await ToList(_chocolateyService.GetSettings(Empty.Instance));
        }

        public async Task SetSetting(ChocolateySetting setting)
        {
            await Initialize(true);
            await _chocolateyService.SetSettingAsync(setting);
        }

        public async Task<IReadOnlyList<ChocolateySource>> GetSources()
        {
            await Initialize().ConfigureAwait(false);
            return await ToList(_chocolateyService.GetSources(Empty.Instance));
        }

        public async Task AddSource(ChocolateySource source)
        {
            await Initialize(true);
            await _chocolateyService.AddSourceAsync(source);
        }

        public async Task UpdateSource(string id, ChocolateySource source)
        {
            await Initialize(true);
            await _chocolateyService.UpdateSourceAsync(new UpdateSourceArgs { Id = id, Source = source});
        }

        public async Task<bool> RemoveSource(string id)
        {
            await Initialize(true);
            var reply = await _chocolateyService.RemoveSourceAsync(new RemoveSourceArgs { Id = id });
            return reply.Successful;
        }

        public ValueTask<bool> RequiresElevation()
        {
            return _requiresElevation.HasValue ? new ValueTask<bool>(_requiresElevation.Value) : new ValueTask<bool>(RequiresElevationImpl());
        }

        public void Dispose()
        {
            try
            {
                _chocolateyService.Exit(Empty.Instance);
            }
            catch (RpcException)
            {
            }
        }

        private async Task<bool> RequiresElevationImpl()
        {
            await Initialize();
            _requiresElevation = !(await _chocolateyService.IsElevatedAsync(Empty.Instance)).IsElevated;
            return _requiresElevation.Value;
        }
        
        private async Task<bool> HandleError(PackageOperationResult result, string verb, string id, string version = null)
        {
            if (!result.Successful)
            {
                var exceptionMessage = result.ExceptionMessage == null ? "" : $"\nException: {result.ExceptionMessage}";
                if (version != null)
                {
                    await _progressService.ShowMessageAsync(
                        "Failed to {verb} package.",
                        $"Failed to {verb} package \"{id}\", version \"{version}\".\nError: {string.Join("\n", result.Messages)}{exceptionMessage}");
                    Logger.Warning(
                        "Failed to {verb} {Package}, version {Version}. Errors: {Errors}. Exception: {Exception}",
                        id,
                        version,
                        result.Messages,
                        result.ExceptionMessage);
                }
                else
                {
                    await _progressService.ShowMessageAsync(
                        "Failed to {verb} package.",
                        $"Failed to {verb} package \"{id}\".\nError: {string.Join("\n", result.Messages)}{exceptionMessage}");
                    Logger.Warning(
                        "Failed to {verb} {Package}. Errors: {Errors}. Exception: {Exception}",
                        id,
                        result.Messages,
                        result.ExceptionMessage);
                }
            }

            return !result.Successful;
        }

        private async Task<IReadOnlyList<T>> ToList<T>(AsyncServerStreamingCall<T> stream)
        {
            var result = new List<T>();
            while (await stream.ResponseStream.MoveNext())
            {
                result.Add(stream.ResponseStream.Current);
            }

            return result;
        } 

        private async Task Initialize(bool requireAdmin = false)
        {
            await Task.Run(() => InitializeImpl(requireAdmin)).ConfigureAwait(false);
        }

        private async Task InitializeImpl(bool requireAdmin = false)
        {
            requireAdmin = requireAdmin || _forceElevation.Value;

            // Check if we're not already initialized or running, as well as our permissions level.
            if (_isInitialized)
            {
                if (!requireAdmin || (await _chocolateyService.IsElevatedAsync(Empty.Instance)).IsElevated)
                {
                    return;
                }
            }

            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                // Double check our initialization and permissions status.
                if (_isInitialized)
                {
                    if (!requireAdmin || (await _chocolateyService.IsElevatedAsync(Empty.Instance)).IsElevated)
                    {
                        return;
                    }

                    _isInitialized = false;
                    await _channel.ShutdownAsync();
                    _channel = null;
                    _chocolateyService = null;

                    if (!_chocolateyProcess.HasExited)
                    {
                        if (!_chocolateyProcess.WaitForExit(2000))
                        {
                            _chocolateyProcess.Kill();
                        }
                    }
                }

                const string Port = "24606";
                var subprocessPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ChocolateyGui.Subprocess.exe");
                var startInfo = new ProcessStartInfo
                                    {
                                        Arguments = Port,
                                        UseShellExecute = true,
                                        FileName = subprocessPath,
                                        WindowStyle = ProcessWindowStyle.Hidden
                                    };

                if (requireAdmin)
                {
                    startInfo.Verb = "runas";
                }

                using (
                    var subprocessHandle = new EventWaitHandle(false, EventResetMode.ManualReset, "ChocolateyGui_Wait"))
                {
                    try
                    {
                        _chocolateyProcess = Process.Start(startInfo);
                    }
                    catch (Win32Exception ex)
                    {
                        Logger.Error(ex, "Failed to start chocolatey gui subprocess.");
                        throw new ApplicationException($"Failed to elevate chocolatey: {ex.Message}.");
                    }

                    Debug.Assert(_chocolateyProcess != null, "_chocolateyProcess != null");

                    if (!subprocessHandle.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        if (_chocolateyProcess.HasExited)
                        {
                            Log.Logger.Fatal(
                                "Failed to start Chocolatey subprocess. Exit Code {ExitCode}.",
                                _chocolateyProcess.ExitCode);
                            throw new ApplicationException(
                                $"Failed to start chocolatey subprocess.\n"
                                + $"You can check the log file at {Path.Combine(Bootstrapper.AppDataPath, "ChocolateyGui.Subprocess.[Date].log")} for errors");
                        }

                        if (!_chocolateyProcess.WaitForExit(TimeSpan.FromSeconds(3).Milliseconds)
                            && !subprocessHandle.WaitOne(0))
                        {
                            _chocolateyProcess.Kill();
                            Log.Logger.Fatal(
                                "Failed to start Chocolatey subprocess. Process appears to be broken or otherwise non-functional.",
                                _chocolateyProcess.ExitCode);
                            throw new ApplicationException(
                                $"Failed to start chocolatey subprocess.\n"
                                + $"You can check the log file at {Path.Combine(Bootstrapper.AppDataPath, "ChocolateyGui.Subprocess.[Date].log")} for errors");
                        }
                        else
                        {
                            if (_chocolateyProcess.HasExited)
                            {
                                Log.Logger.Fatal(
                                    "Failed to start Chocolatey subprocess. Exit Code {ExitCode}.",
                                    _chocolateyProcess.ExitCode);
                                throw new ApplicationException(
                                    $"Failed to start chocolatey subprocess.\n"
                                    + $"You can check the log file at {Path.Combine(Bootstrapper.AppDataPath, "ChocolateyGui.Subprocess.[Date].log")} for errors");
                            }
                        }
                    }

                    if (_chocolateyProcess.WaitForExit(500))
                    {
                        Log.Logger.Fatal(
                            "Failed to start Chocolatey subprocess. Exit Code {ExitCode}.",
                            _chocolateyProcess.ExitCode);
                        throw new ApplicationException(
                            $"Failed to start chocolatey subprocess.\n"
                            + $"You can check the log file at {Path.Combine(Bootstrapper.AppDataPath, "ChocolateyGui.Subprocess.[Date].log")} for errors");
                    }
                }

                App.Job.AddProcess(_chocolateyProcess.Handle);

                _channel = new Channel($"127.0.0.1:{Port}", ChannelCredentials.Insecure);
                _isInitialized = true;

                _chocolateyService = new PackageService.PackageServiceClient(_channel);

                // ReSharper disable once PossibleNullReferenceException
                ((ElevationStatusProvider)Application.Current.FindResource("Elevation")).IsElevated =
                    (await _chocolateyService.IsElevatedAsync(Empty.Instance)).IsElevated;
            }
        }
    }
}
