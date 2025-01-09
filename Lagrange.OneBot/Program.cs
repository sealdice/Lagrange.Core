using System.Reflection;
using System.Runtime;
using System.Text;
using System.CommandLine;
using System.CommandLine.Invocation;
using Lagrange.OneBot.Extensions;
using Microsoft.Extensions.Hosting;


namespace Lagrange.OneBot;

internal abstract class Program
{
    public static void Main(string[] args)
    {
        string version = Assembly.GetAssembly(typeof(Program))?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "Unknown Lagrange.OneBot Version";
        Console.WriteLine($"Lagrange.OneBot Version: {version}\n");

        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        GCSettings.LatencyMode = GCLatencyMode.Batch;

        if (!(args.Length > 0 && args[0] == "")) {
            // 跳过空参数
            var rootCommand = new RootCommand();
            rootCommand.Invoke(args);

            if (args.Length > 0) {
                Environment.Exit(0);
            }
        }

        if (!File.Exists("appsettings.json"))
        {
            Console.WriteLine("No exist config file, create it now...");

            var assm = Assembly.GetExecutingAssembly();
            using var istr = assm.GetManifestResourceStream("Lagrange.OneBot.Resources.appsettings.json")!;
            using var temp = File.Create("appsettings.json");
            istr.CopyTo(temp);

            istr.Close();
            temp.Close();

            Console.WriteLine("Please Edit the appsettings.json to set configs and press any key to continue");
            Console.ReadKey(true);
        }

        Host.CreateApplicationBuilder()
            .ConfigureLagrangeCore()
            .ConfigureOneBot()
            .Build()
            .InitializeMusicSigner() // Very ugly (
            .Run();
    }
}