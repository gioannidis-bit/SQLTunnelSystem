using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace SQLTunnelService
{
    /// <summary>
    /// Helper class for service installation and management from command line
    /// </summary>
    public static class ServiceHelper
    {
        private const string ServiceName = "SQLTunnelService";

        public static bool HandleServiceCommand(string[] args)
        {
            if (args == null || args.Length == 0)
                return false;

            string command = args[0].ToLower();
            if (command.StartsWith("--service:") || command.StartsWith("/service:"))
            {
                command = command.Substring(10).Trim();
            }
            else if (command == "--service" || command == "/service")
            {
                if (args.Length > 1)
                    command = args[1].ToLower();
                else
                    command = "status";
            }
            else
            {
                return false;
            }

            switch (command)
            {
                case "install":
                    InstallService();
                    return true;
                case "uninstall":
                    UninstallService();
                    return true;
                case "start":
                    StartService();
                    return true;
                case "stop":
                    StopService();
                    return true;
                case "status":
                    CheckServiceStatus();
                    return true;
                default:
                    Console.WriteLine("Unknown service command. Available commands: install, uninstall, start, stop, status");
                    return true;
            }
        }

        private static void InstallService()
        {
            Console.WriteLine("Installing SQL Tunnel Service...");

            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;

                // Check if running on Windows
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Console.WriteLine("Service installation is only supported on Windows.");
                    Console.WriteLine("For Linux, please create a systemd service file. See README.md for instructions.");
                    return;
                }

                // Create the service
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = $"create {ServiceName} binPath= \"{exePath}\" start= auto",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                var process = Process.Start(startInfo);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"Failed to install service. Error: {output}");
                    return;
                }

                // Set the service description
                startInfo.Arguments = $"description {ServiceName} \"SQL Server Tunnel Service for secure remote connections\"";
                process = Process.Start(startInfo);
                process.WaitForExit();

                Console.WriteLine("Service installed successfully.");
                Console.WriteLine("You can start the service using 'sc start SQLTunnelService' or through the Services management console.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error installing service: {ex.Message}");
            }
        }

        private static void UninstallService()
        {
            Console.WriteLine("Uninstalling SQL Tunnel Service...");

            try
            {
                // Check if running on Windows
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Console.WriteLine("Service uninstallation is only supported on Windows.");
                    Console.WriteLine("For Linux, please remove the systemd service file. See README.md for instructions.");
                    return;
                }

                // Stop the service first
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = $"stop {ServiceName}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                var process = Process.Start(startInfo);
                process.WaitForExit();

                // Delete the service
                startInfo.Arguments = $"delete {ServiceName}";
                process = Process.Start(startInfo);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"Failed to uninstall service. Error: {output}");
                    return;
                }

                Console.WriteLine("Service uninstalled successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error uninstalling service: {ex.Message}");
            }
        }

        private static void StartService()
        {
            Console.WriteLine("Starting SQL Tunnel Service...");

            try
            {
                // Check if running on Windows
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Console.WriteLine("Service start command is only supported on Windows.");
                    Console.WriteLine("For Linux, use 'sudo systemctl start sqltunnel'");
                    return;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = $"start {ServiceName}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                var process = Process.Start(startInfo);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"Failed to start service. Error: {output}");
                    return;
                }

                Console.WriteLine("Service started successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting service: {ex.Message}");
            }
        }

        private static void StopService()
        {
            Console.WriteLine("Stopping SQL Tunnel Service...");

            try
            {
                // Check if running on Windows
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Console.WriteLine("Service stop command is only supported on Windows.");
                    Console.WriteLine("For Linux, use 'sudo systemctl stop sqltunnel'");
                    return;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = $"stop {ServiceName}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                var process = Process.Start(startInfo);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"Failed to stop service. Error: {output}");
                    return;
                }

                Console.WriteLine("Service stopped successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping service: {ex.Message}");
            }
        }

        private static void CheckServiceStatus()
        {
            Console.WriteLine("Checking SQL Tunnel Service status...");

            try
            {
                // Check if running on Windows
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Console.WriteLine("Service status command is only supported on Windows.");
                    Console.WriteLine("For Linux, use 'sudo systemctl status sqltunnel'");
                    return;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = $"query {ServiceName}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                var process = Process.Start(startInfo);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"Service not found. You may need to install it first.");
                    return;
                }

                Console.WriteLine("Service status:");
                Console.WriteLine(output);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking service status: {ex.Message}");
            }
        }
    }
}