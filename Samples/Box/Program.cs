using System.Diagnostics;

namespace DX12GameProgramming
{
    class Program
    {
        static void Main(string[] args)
        {
            using (D3DApp app = new BoxApp(Process.GetCurrentProcess().Handle))
            {
                app.Initialize();
                app.Run();
            }
        }
    }
}
