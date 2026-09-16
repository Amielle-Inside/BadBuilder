using BadBuilder.Application;
using BadBuilder.Services;
using System.Security.Principal;
using System.Runtime.InteropServices;

internal static class Program
{
    private static bool Verbose { get; set; }

    static async Task Main(string[] args)
    {
        ParseArgs(args);

        if (!IsElevated())
        {
            Console.WriteLine("This application must be run with elevated privileges (as Administrator or root).");
            Console.Write("Press any key to exit...");
            Console.ReadKey();
            return;
        }

        await BuilderApp.RunAsync(Verbose, CancellationToken.None);
    }

    static void ParseArgs(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg is "--verbose" or "-v")
                Verbose = true;
            else if (arg is "--help" or "-h")
            {
                PrintHelp();
                Environment.Exit(0);
            }
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine(@"
BadBuilder - Xbox 360 BadUpdate/ABadAvatar USB Builder

Usage: BadBuilder [options]

Options:
  -v, --verbose    Enable verbose output for debugging
  -h, --help       Show this help message
");
    }

    static bool IsElevated()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        else
            return GetEffectiveUserID() == 0;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserID();
}