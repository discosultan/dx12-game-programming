using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D12;

namespace DX12GameProgramming
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct ObjectConstants
    {
        public Matrix World;

        public static ObjectConstants Default => new ObjectConstants
        {
            World = Matrix.Identity
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PassConstants
    {
        public Matrix View;
        public Matrix InvView;
        public Matrix Proj;
        public Matrix InvProj;
        public Matrix ViewProj;
        public Matrix InvViewProj;
        public Vector3 EyePosW;
        public float PerObjectPad1;
        public Vector2 RenderTargetSize;
        public Vector2 InvRenderTargetSize;
        public float NearZ;
        public float FarZ;
        public float TotalTime;
        public float DeltaTime;

        public static PassConstants Default => new PassConstants
        {
            View = Matrix.Identity,
            InvView = Matrix.Identity,
            Proj = Matrix.Identity,
            InvProj = Matrix.Identity,
            ViewProj = Matrix.Identity,
            InvViewProj = Matrix.Identity
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Vertex
    {
        public Vector3 Pos;
        public Vector4 Color;
    }

    internal class FrameResource : IDisposable
    {
        public FrameResource(Device device, int passCount, int objectCount, int waveVertCount)
        {
            CmdListAlloc = device.CreateCommandAllocator(CommandListType.Direct);

            PassCB = new UploadBuffer<PassConstants>(device, passCount, true);
            ObjectCB = new UploadBuffer<ObjectConstants>(device, objectCount, true);

            WavesVB = new UploadBuffer<Vertex>(device, waveVertCount, false);
        }

        // We cannot reset the allocator until the GPU is done processing the commands.
        // So each frame needs their own allocator.
        public CommandAllocator CmdListAlloc { get; }

        // We cannot update a cbuffer until the GPU is done processing the commands
        // that reference it. So each frame needs their own cbuffers.
        public UploadBuffer<PassConstants> PassCB { get; }
        public UploadBuffer<ObjectConstants> ObjectCB { get; }

        // We cannot update a dynamic vertex buffer until the GPU is done processing
        // the commands that reference it. So each frame needs their own.
        public UploadBuffer<Vertex> WavesVB { get; }

        // Fence value to mark commands up to this fence point.  This lets us
        // check if these frame resources are still in use by the GPU.
        public long Fence { get; set; }

        public void Dispose()
        {
            WavesVB.Dispose();
            ObjectCB.Dispose();
            PassCB.Dispose();
            CmdListAlloc.Dispose();
        }
    }
}
