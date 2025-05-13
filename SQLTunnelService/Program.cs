using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;

namespace SQLTunnelService
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Check for service commands (install, uninstall, etc.)
            if (ServiceHelper.HandleServiceCommand(args))
            {
                return;
            }

            var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";

            // Set up config directory
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(Path.Combine(baseDir, "logs"));

            try
            {
                // Build and run the host
                await CreateHostBuilder(args).Build().RunAsync();
            }
            catch (Exception ex)
            {
                // Since the host failed to start, we need to log manually
                File.AppendAllText(
                    Path.Combine(baseDir, "logs", "startup-error.log"),
                    $"{DateTime.Now}: Fatal error during startup: {ex}\n\n");

                Console.WriteLine($"Fatal error during startup: {ex.Message}");
                Console.WriteLine($"Check the log file for details: {Path.Combine(baseDir, "logs", "startup-error.log")}");

                // If running interactively, wait for key press
                if (Environment.UserInteractive)
                {
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                }

                Environment.Exit(1);
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = "SQLTunnelService";
                })
                .ConfigureAppConfiguration((hostContext, config) =>
                {
                    config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                    config.AddJsonFile("appsettings.json", optional: false);
                    config.AddJsonFile($"appsettings.{hostContext.HostingEnvironment.EnvironmentName}.json", optional: true);
                    config.AddEnvironmentVariables();
                    config.AddCommandLine(args);
                })
                .ConfigureLogging((hostContext, logging) =>
                {
                    logging.ClearProviders();
                    logging.AddConfiguration(hostContext.Configuration.GetSection("Logging"));
                    logging.AddNLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nlog.config"));
                    logging.AddConsole();
                })
                .ConfigureServices((hostContext, services) =>
                {
                    // Register configurations
                    services.Configure<ServiceSettings>(
                        hostContext.Configuration.GetSection("ServiceSettings"));

                    // Register the background service
                    services.AddHostedService<SQLTunnelWorker>();

                    // Register HttpClient factory
                    services.AddHttpClient("SQLTunnelClient", client =>
                    {
                        // Will be configured from settings when building the client
                    }).ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        return new System.Net.Http.HttpClientHandler
                        {
                            // For development only
                            ServerCertificateCustomValidationCallback =
                                (message, cert, chain, errors) => true
                        };
                    });
                });
    }
}