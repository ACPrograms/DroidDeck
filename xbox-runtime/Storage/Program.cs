using System;

namespace DroidDeck.XboxRuntime.Storage
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("DroidDeck Xbox Storage Provisioner Test Runner");
            try
            {
                int exitCode = SparseProvisionerTests.RunAllTests();
                if (exitCode == 0) exitCode = StreamingProvisionerTests.RunAllTests();
                Console.WriteLine("Execution completed successfully with exit code: " + exitCode);
                return exitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL ERROR in test execution: " + ex);
                return 1;
            }
        }
    }
}
