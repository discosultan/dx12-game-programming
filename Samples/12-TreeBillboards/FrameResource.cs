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
        public Matrix TexTransform;

        public static ObjectConstants Default => new ObjectConstants
        {
            World = Matrix.Identity,
            TexTransform = Matrix.Identity
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

        public Vector4 AmbientLight;

        public Vector4 FogColor;
        public float FogStart;
        public float FogRange;
        public Vector2 PerObjectPad2;

        // Indices [0, NUM_DIR_LIGHTS) are directional lights;
        // indices [NUM_DIR_LIGHTS, NUM_DIR_LIGHTS+NUM_POINT_LIGHTS) are point lights;
        // indices [NUM_DIR_LIGHTS+NUM_POINT_LIGHTS, NUM_DIR_LIGHTS+NUM_POINT_LIGHT+NUM_SPOT_LIGHTS)
        // are spot lights for a maximum of MaxLights per object.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = Light.MaxLights)]
        public Light[] Lights;

        public static PassConstants Default => new PassConstants
        {
            View = Matrix.Identity,
            InvView = Matrix.Identity,
            Proj = Matrix.Identity,
            InvProj = Matrix.Identity,
            ViewProj = Matrix.Identity,
            InvViewProj = Matrix.Identity,
            AmbientLight = Vector4.UnitW,
            FogColor = new Vector4(0.7f, 0.7f, 0.7f, 1.0f),
            FogStart = 5.0f,
            FogRange = 150.0f,
            Lights = Light.DefaultArray
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Vertex
    {
        public Vector3 Pos;
        public Vector3 Normal;
        public Vector2 TexC;

        public Vertex(float x, float y, float z, float nx, float ny, float nz, float u, float v)
        {
            Pos = new Vector3(x, y, z);
            Normal = new Vector3(nx, ny, nz);
            TexC = new Vector2(u, v);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct TreeSpriteVertex
    {
        public Vector3 Pos;
        public Vector2 Size;
    }

    internal class FrameResource : IDisposable
    {
        public FrameResource(Device device, int passCount, int objectCount, int materialCount, int waveVertCount)
        {
            CmdListAlloc = device.CreateCommandAllocator(CommandListType.Direct);

            PassCB = new UploadBuffer<PassConstants>(device, passCount, true);
            MaterialCB = new UploadBuffer<MaterialConstants>(device, materialCount, true);
            ObjectCB = new UploadBuffer<ObjectConstants>(device, objectCount, true);

            WavesVB = new UploadBuffer<Vertex>(device, waveVertCount, false);
        }

        // We cannot reset the allocator until the GPU is done processing the commands.
        // So each frame needs their own allocator.
        public CommandAllocator CmdListAlloc { get; }

        // We cannot update a cbuffer until the GPU is done processing the commands
        // that reference it. So each frame needs their own cbuffers.
        public UploadBuffer<PassConstants> PassCB { get; }
        public UploadBuffer<MaterialConstants> MaterialCB { get; }
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
            MaterialCB.Dispose();
            PassCB.Dispose();
            CmdListAlloc.Dispose();
        }
    }
}
