using SharpDX;
using SharpDX.Direct3D12;
using SharpDX.DXGI;

namespace DX12GameProgramming
{
    public class InitDirect3DApp : D3DApp
    {
        public InitDirect3DApp()
        {
            MainWindowCaption = "Init Direct3D";
        }

        protected override void Draw(GameTimer gt)
        {
            // Reuse the memory associated with command recording.
            // We can only reset when the associated command lists have finished execution on the GPU.
            DirectCmdListAlloc.Reset();

            // A command list can be reset after it has been added to the command queue via ExecuteCommandList.
            // Reusing the command list reuses memory.
            CommandList.Reset(DirectCmdListAlloc, null);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

            // Set the viewport and scissor rect. This needs to be reset whenever the command list is reset.
            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle); // TODO: API suggestion: SetScissorRectangle overload similar to SetViewport

            // Clear the back buffer and depth buffer.
            CommandList.ClearRenderTargetView(CurrentBackBufferView, Color.LightSteelBlue);
            // TODO: API suggestion: simplify flags naming to ClearFlags.Depth|Stencil
            CommandList.ClearDepthStencilView(DepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.
            CommandList.SetRenderTargets(CurrentBackBufferView, DepthStencilView);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

            // Done recording commands.
            CommandList.Close();

            // Add the command list to the queue for execution.
            CommandQueue.ExecuteCommandList(CommandList);

            // Present the buffer to the screen. Presenting will automatically swap the back and front buffers.
            // Ref: https://msdn.microsoft.com/en-us/library/windows/desktop/bb174576(v=vs.85).aspx
            SwapChain.Present(0, PresentFlags.None);

            // Wait until frame commands are complete. This waiting is inefficient and is
            // done for simplicity. Later we will show how to organize our rendering code
            // so we do not have to wait per frame.
            FlushCommandQueue();
        }
    }
}
