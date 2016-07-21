using System.Diagnostics;

namespace DX12GameProgramming
{
    internal class Program
    {
        internal static void Main(string[] args)
        {
            using (D3DApp app = new TreeBillboardsApp(Process.GetCurrentProcess().Handle))
            {
                app.Initialize();
                app.Run();
            }
        }
    }
}
