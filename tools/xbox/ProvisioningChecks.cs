using System;
using System.Threading.Tasks;
using DroidDeckXboxHost.Runtime.Provisioning;

internal static class ProvisioningChecks
{
    private static async Task<int> Main()
    {
        try
        {
            if (!await BackendProvisionerTests.RunAllTestsAsync()) return 1;
            Console.WriteLine("Backend provisioning checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
