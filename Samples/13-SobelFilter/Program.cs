namespace DX12GameProgramming
{
    internal class Program
    {
        static void Main(string[] args)
        {
            using (var app = new SobelFilterApp())
            {
                app.Initialize();
                app.Run();
            }
        }
    }
}
