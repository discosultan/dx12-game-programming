using System.Diagnostics;

namespace DX12GameProgramming
{
    class Program
    {
        static void Main(string[] args)
        {
            using (D3DApp app = new WavesCSApp(Process.GetCurrentProcess().Handle))
            {
                app.Initialize();
                app.Run();
            }
        }
    }
}
