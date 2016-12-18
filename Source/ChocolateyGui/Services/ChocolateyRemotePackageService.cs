﻿// --------------------------------------------------------------------------------------------------------------------
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
using AutoMapper;
using Caliburn.Micro;
using ChocolateyGui.Interface;
using ChocolateyGui.Models;
using ChocolateyGui.Models.Messages;
using ChocolateyGui.Services.PackageServices;
using ChocolateyGui.Subprocess;
using ChocolateyGui.Utilities;
using ChocolateyGui.ViewModels.Items;
using NuGet;
using Serilog;
using WampSharp.V2;
using WampSharp.V2.Client;
using WampSharp.V2.Core.Contracts;
using WampSharp.V2.Fluent;
using PackageSearchResults = ChocolateyGui.Models.PackageSearchResults;

namespace ChocolateyGui.Services
{
    public class ChocolateyRemotePackageService : IChocolateyPackageService, IDisposable
    {
        private static readonly Serilog.ILogger Logger = Log.ForContext<ChocolateyRemotePackageService>();
        private readonly IProgressService _progressService;
        private readonly IMapper _mapper;
        private readonly IEventAggregator _eventAggregator;
        private readonly Func<IPackageViewModel> _packageFactory;
        private readonly AsyncLock _lock = new AsyncLock();

        private Process _chocolateyProcess;
        private IWampChannel _wampChannel;
        private IChocolateyService _chocolateyService;
        private IDisposable _logStream;
        private bool _isInitialized;

        public ChocolateyRemotePackageService(
            IProgressService progressService,
            IMapper mapper,
            IEventAggregator eventAggregator,
            Func<IPackageViewModel> packageFactory)
        {
            _progressService = progressService;
            _mapper = mapper;
            _eventAggregator = eventAggregator;
            _packageFactory = packageFactory;
        }

        public async Task<PackageSearchResults> Search(string query, PackageSearchOptions options)
        {
            await Initialize();
            var results = await _chocolateyService.Search(query, options);
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
            var result = await _chocolateyService.GetByVersionAndIdAsync(id, version.ToString(), isPrerelease);
            return _mapper.Map(result, _packageFactory());
        }

        public async Task<IEnumerable<IPackageViewModel>> GetInstalledPackages(bool force = false)
        {
            await Initialize();
            var packages = await _chocolateyService.GetInstalledPackages();
            var vms = packages.Select(p => _mapper.Map(p, _packageFactory()));
            return vms;
        }

        public async Task<IReadOnlyList<Tuple<string, SemanticVersion>>> GetOutdatedPackages(bool includePrerelease = false)
        {
            await Initialize();
            var results = await _chocolateyService.GetOutdatedPackages(includePrerelease);
            var parsed = results.Select(result => Tuple.Create(result.Item1, new SemanticVersion(result.Item2)));
            return parsed.ToList();
        }

        public async Task InstallPackage(string id, SemanticVersion version = null, Uri source = null, bool force = false)
        {
            await Initialize(true);
            var result = await _chocolateyService.InstallPackage(id, version?.ToString(), source, force);
            if (!result.Successful)
            {
                var exceptionMessage = result.Exception == null ? "" : $"\nException: {result.Exception}";
                await _progressService.ShowMessageAsync(
                    "Failed to install package.",
                    $"Failed to install package \"{id}\", version \"{version}\".\nError: {string.Join("\n", result.Messages)}{exceptionMessage}");
                Logger.Warning(result.Exception, "Failed to install {Package}, version {Version}. Errors: {Errors}", id, version, result.Messages);
                return;
            }

            _eventAggregator.BeginPublishOnUIThread(new PackageChangedMessage(id, PackageChangeType.Installed, version));
        }

        public async Task UninstallPackage(string id, SemanticVersion version, bool force = false)
        {
            await Initialize(true);
            var result = await _chocolateyService.UninstallPackage(id, version.ToString(), force);
            if (!result.Successful)
            {
                var exceptionMessage = result.Exception == null ? "" : $"\nException: {result.Exception}";
                await _progressService.ShowMessageAsync(
                    "Failed to uninstall package.",
                    $"Failed to uninstall package \"{id}\", version \"{version}\".\nError: {string.Join("\n", result.Messages)}{exceptionMessage}");
                Logger.Warning(result.Exception, "Failed to uninstall {Package}, version {Version}. Errors: {Errors}", id, version, result.Messages);
                return;
            }

            _eventAggregator.BeginPublishOnUIThread(new PackageChangedMessage(id, PackageChangeType.Uninstalled, version));
        }

        public async Task UpdatePackage(string id, Uri source = null)
        {
            await Initialize(true);
            var result = await _chocolateyService.UpdatePackage(id, source);
            if (!result.Successful)
            {
                var exceptionMessage = result.Exception == null ? "" : $"\nException: {result.Exception}";
                await _progressService.ShowMessageAsync(
                    "Failed to update package.",
                    $"Failed to update package \"{id}\".\nError: {string.Join("\n", result.Messages)}{exceptionMessage}");
                Logger.Warning(result.Exception, "Failed to update {Package}. Errors: {Errors}", id, result.Messages);
                return;
            }

            _eventAggregator.BeginPublishOnUIThread(new PackageChangedMessage(id, PackageChangeType.Updated));
        }

        public async Task PinPackage(string id, SemanticVersion version)
        {
            await Initialize(true);
            var result = await _chocolateyService.PinPackage(id, version.ToString());
            if (!result.Successful)
            {
                var exceptionMessage = result.Exception == null ? "" : $"\nException: {result.Exception}";
                await _progressService.ShowMessageAsync(
                    "Failed to pin package.",
                    $"Failed to pin package \"{id}\", version \"{version}\".\nError: {string.Join("\n", result.Messages)}{exceptionMessage}");
                Logger.Warning(result.Exception, "Failed to pin {Package}, version {Version}. Errors: {Errors}", id, version, result.Messages);
                return;
            }

            _eventAggregator.BeginPublishOnUIThread(new PackageChangedMessage(id, PackageChangeType.Pinned, version));
        }

        public async Task UnpinPackage(string id, SemanticVersion version)
        {
            await Initialize(true);
            var result = await _chocolateyService.UnpinPackage(id, version.ToString());
            if (!result.Successful)
            {
                var exceptionMessage = result.Exception == null ? "" : $"\nException: {result.Exception}";
                await _progressService.ShowMessageAsync(
                    "Failed to unpin package.",
                    $"Failed to unpin package \"{id}\", version \"{version}\".\nError: {string.Join("\n", result.Messages)}{exceptionMessage}");
                Logger.Warning(result.Exception, "Failed to unpin {Package}, version {Version}. Errors: {Errors}", id, version, result.Messages);
                return;
            }

            _eventAggregator.BeginPublishOnUIThread(new PackageChangedMessage(id, PackageChangeType.Unpinned, version));
        }

        public void Dispose()
        {
            _logStream?.Dispose();
            _wampChannel?.Close("Exiting", new GoodbyeDetails { Message = "Exiting" });
        }

        private Task Initialize(bool requireAdmin = false)
        {
            return Task.Run(() => InitializeImpl(requireAdmin));
        }

        private async Task InitializeImpl(bool requireAdmin = false)
        {
            // Check if we're not already initialized or running, as well as our permissions level.
            if (_isInitialized)
            {
                if (!requireAdmin || await _chocolateyService.IsElevated())
                {
                    return;
                }
            }

            using (await _lock.LockAsync())
            {
                // Double check our initialization and permissions status.
                if (_isInitialized)
                {
                    if (!requireAdmin || await _chocolateyService.IsElevated())
                    {
                        return;
                    }

                    _isInitialized = false;
                    _logStream.Dispose();
                    _logStream = null;
                    _wampChannel.Close("Escalating", new GoodbyeDetails { Message = "Escalating" });
                    _wampChannel = null;
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
                        throw new ApplicationException(
                            $"Failed to elevate chocolatey: {ex.Message}.");
                    }

                    Debug.Assert(_chocolateyProcess != null, "_chocolateyProcess != null");

                    if (!subprocessHandle.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        if (_chocolateyProcess.HasExited)
                        {
                            Log.Logger.Fatal(
                                "Failed to start Chocolatey subprocess. Exit Code {ExitCode}.",
                                _chocolateyProcess.ExitCode);
                            throw new ApplicationException($"Failed to start chocolatey subprocess.\n"
                                                           + $"You can check the log file at {Path.Combine(Bootstrapper.AppDataPath, "ChocolateyGui.Subprocess.[Date].log")} for errors");
                        }

                        if (!_chocolateyProcess.WaitForExit(TimeSpan.FromSeconds(3).Milliseconds)
                            && !subprocessHandle.WaitOne(0))
                        {
                            _chocolateyProcess.Kill();
                            Log.Logger.Fatal(
                                "Failed to start Chocolatey subprocess. Process appears to be broken or otherwise non-functional.",
                                _chocolateyProcess.ExitCode);
                            throw new ApplicationException($"Failed to start chocolatey subprocess.\n"
                                                           + $"You can check the log file at {Path.Combine(Bootstrapper.AppDataPath, "ChocolateyGui.Subprocess.[Date].log")} for errors");
                        }
                        else
                        {
                            if (_chocolateyProcess.HasExited)
                            {
                                Log.Logger.Fatal(
                                    "Failed to start Chocolatey subprocess. Exit Code {ExitCode}.",
                                    _chocolateyProcess.ExitCode);
                                throw new ApplicationException($"Failed to start chocolatey subprocess.\n"
                                                               + $"You can check the log file at {Path.Combine(Bootstrapper.AppDataPath, "ChocolateyGui.Subprocess.[Date].log")} for errors");
                            }
                        }
                    }

                    if (_chocolateyProcess.WaitForExit(500))
                    {
                        Log.Logger.Fatal(
                            "Failed to start Chocolatey subprocess. Exit Code {ExitCode}.",
                            _chocolateyProcess.ExitCode);
                        throw new ApplicationException($"Failed to start chocolatey subprocess.\n"
                                                       + $"You can check the log file at {Path.Combine(Bootstrapper.AppDataPath, "ChocolateyGui.Subprocess.[Date].log")} for errors");
                    }
                }

                var factory = new WampChannelFactory();
                _wampChannel =
                    factory.ConnectToRealm("default")
                        .WebSocketTransport($"ws://127.0.0.1:{Port}/ws")
                        .JsonSerialization()
                        .Build();

                await _wampChannel.Open().ConfigureAwait(false);
                _isInitialized = true;

                _chocolateyService = _wampChannel.RealmProxy.Services.GetCalleeProxy<IChocolateyService>();

                // Create pipe for chocolatey stream output.
                var logStream = _wampChannel.RealmProxy.Services.GetSubject<StreamingLogMessage>("com.chocolatey.log");
                _logStream = logStream.Subscribe(
                    message =>
                        {
                            PowerShellLineType powerShellLineType;
                            switch (message.LogLevel)
                            {
                                case StreamingLogLevel.Debug:
                                    powerShellLineType = PowerShellLineType.Debug;
                                    break;
                                case StreamingLogLevel.Verbose:
                                    powerShellLineType = PowerShellLineType.Verbose;
                                    break;
                                case StreamingLogLevel.Info:
                                    powerShellLineType = PowerShellLineType.Output;
                                    break;
                                case StreamingLogLevel.Warn:
                                    powerShellLineType = PowerShellLineType.Warning;
                                    break;
                                case StreamingLogLevel.Error:
                                    powerShellLineType = PowerShellLineType.Error;
                                    break;
                                case StreamingLogLevel.Fatal:
                                    powerShellLineType = PowerShellLineType.Error;
                                    break;
                                default:
                                    powerShellLineType = PowerShellLineType.Output;
                                    break;
                            }

                            _progressService.WriteMessage(message.Message, powerShellLineType);
                        });
            }
        }
    }
}