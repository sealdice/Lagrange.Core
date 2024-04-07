using System.Reflection;
using System.Runtime;
using System.Text;
using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Hosting;


namespace Lagrange.OneBot;

internal abstract class Program
{
    public static void Main(string[] args)
    {
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
            Console.ReadLine();
        }

        var hostBuilder = new LagrangeAppBuilder(args)
            .ConfigureConfiguration("appsettings.json", false, true)
            .ConfigureBots()
            .ConfigureOneBot();

        hostBuilder.Build().Run();
    }
}