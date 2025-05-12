using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace SQLTunnelService
{
    public static class Program
    {
        static void Main(string[] args)
        {
            if (Environment.UserInteractive)
            {
                // Εκτέλεση ως εφαρμογή κονσόλας για debugging
                var service = new SQLTunnelService();

                Console.WriteLine("SQL Tunnel Service - Console Mode");
                Console.WriteLine("Press Enter to start the service...");
                Console.ReadLine();

                // Καλούμε τη μέθοδο Start αντί της OnStart
                string[] serviceArgs = { };
                service.Start(serviceArgs);

                Console.WriteLine("Service started. Press Enter to stop...");
                Console.ReadLine();

                // Καλούμε τη μέθοδο Stop αντί της OnStop
                service.Stop();

                Console.WriteLine("Service stopped. Press any key to exit...");
                Console.ReadKey();
            }
            else
            {
                // Εκτέλεση ως Windows Service
                ServiceBase[] ServicesToRun = new ServiceBase[]
                {
                    new SQLTunnelService()
                };
                ServiceBase.Run(ServicesToRun);
            }
        }
    }
}