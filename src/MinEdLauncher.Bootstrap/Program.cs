using System.Diagnostics;

namespace MinEdLauncher.Bootstrap
{
    // C# instead of F# since final exe output is quite a bit smaller
    class Program
    {
        static void Main(string[] args)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "MinEdLauncher.exe",
                Arguments = string.Join(" ", args),
                CreateNoWindow = false,
                UseShellExecute = true
            };

            Process.Start(startInfo);
        }
    }
}