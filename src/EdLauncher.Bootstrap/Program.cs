using System.Diagnostics;

namespace EdLauncher.Bootstrap
{
    // C# instead of F# since final exe output is quite a bit smaller
    class Program
    {
        static void Main(string[] args)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "EdLauncher.exe",
                Arguments = string.Join(" ", args),
                CreateNoWindow = false,
                UseShellExecute = false
            };

            var p = Process.Start(startInfo);
            p?.WaitForExit();
        }
    }
}