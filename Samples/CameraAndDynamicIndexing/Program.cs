using System;
using System.Diagnostics;

namespace DX12GameProgramming
{
    internal class Program
    {
        [STAThread]
        internal static void Main(string[] args)
        {
            using (D3DApp app = new CameraAndDynamicIndexingApp(Process.GetCurrentProcess().Handle))
            {
                app.Initialize();
                app.Run();
            }
        }
    }
}
