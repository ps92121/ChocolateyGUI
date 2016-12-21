using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using chocolatey.infrastructure.app.configuration;
using ChocolateyGui.Rpc;
using ChocolateyGui.Subprocess.Mapping;
using Google.Protobuf.Collections;
using Grpc.Core;
using NuGet;
using Serilog;
using ILogger = Serilog.ILogger;

namespace ChocolateyGui.Subprocess
{
    public class Program
    {
        public static ManualResetEventSlim CanceledEvent { get; private set; }

        public static ILogger Logger { get; private set; }

        public static int Main(string[] args)
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData,
                    Environment.SpecialFolderOption.DoNotVerify),
                "ChocolateyGUI");

            var logFolder = Path.Combine(appDataPath, "Logs");
            var directPath = Path.Combine(logFolder, "ChocolateyGui.Subprocess.{Date}.log");

            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .WriteTo.Async(config => config.LiterateConsole())
                .WriteTo.Async(config =>
                    config.RollingFile(directPath, retainedFileCountLimit: 10, fileSizeLimitBytes: 150 * 1000 * 1000))
                .CreateLogger();

            Logger = Log.ForContext<Program>();

            var source = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                CanceledEvent.Set();
                source.Cancel();
            };

            CanceledEvent = new ManualResetEventSlim();
            var eventHandle = EventWaitHandle.OpenExisting("ChocolateyGui_Wait");

            try
            {
                Mapper.Initialize(
                    config =>
                        {
                            config.AddDefaults();

                            config.CreateMap<IPackage, Package>()
                                .ForSourceMember(p => p.Authors, o => o.Ignore())
                                .AfterMap(
                                    (s, d) =>
                                        {
                                            d.Authors.Add(s.Authors);
                                            d.Owners.Add(s.Owners);
                                        });

                            config.CreateMap<ConfigFileFeatureSetting, ChocolateyFeature>();
                            config.CreateMap<ConfigFileConfigSetting, ChocolateySetting>();
                            config.CreateMap<ConfigFileSourceSetting, Rpc.ChocolateySource>();
                        });

                if (args.Length != 1)
                {
                    Log.Fatal("Expected 1 argument and got {ArgumentCount} instead. {Args}", args.Length, args);
                    eventHandle.Set();
                    return 1;
                }

                int port;
                if (!int.TryParse(args[0], out port))
                {
                    Log.Fatal("Missing port number! Got {args} instead :<.", args);
                    eventHandle.Set();
                    return 1;
                }


                Logger.Information("Starting Server on port {port}.", port);
                
                var server = new Server
                {
                    Services = { PackageService.BindService(new ChocolateyService()) },
                    Ports = { { "localhost", port, ServerCredentials.Insecure } }
                };

                eventHandle.Set();
                server.Start();
                CanceledEvent.Wait(source.Token);
                Logger.Information("Stopping Server.", port);
                server.ShutdownAsync().GetAwaiter().GetResult();

                return 0; // Success.
            }
            catch (OperationCanceledException)
            {
                eventHandle.Set();
                return 1223; // Cancelled.
            }
            catch (Exception ex)
            {
                Logger.Fatal(ex, "Fatal error while running server. Exception: {Exception}", ex);
                throw;
            }
        }
    }
}
