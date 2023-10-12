namespace DX12GameProgramming
{
    internal class Program
    {
        static void Main(string[] args)
        {
            using (var app = new InitDirect3DApp())
            {
                app.Initialize();
                app.Run();
            }
        }
    }
}
