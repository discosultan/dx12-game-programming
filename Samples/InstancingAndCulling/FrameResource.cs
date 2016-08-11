using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D12;

namespace DX12GameProgramming
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct InstanceData
    {
        public Matrix World;
        public Matrix TexTransform;
        public int MaterialIndex;
        public int InstancePad0;
        public int InstancePad1;
        public int InstancePad2;

        public static InstanceData Default => new InstanceData
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
            Lights = Light.DefaultArray
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct MaterialData
    {
        public Vector4 DiffuseAlbedo;
        public Vector3 FresnelR0;
        public float Roughness;

        // Used in texture mapping.
        public Matrix MatTransform;

        public int DiffuseMapIndex;
        public int MaterialPad0;
        public int MaterialPad1;
        public int MaterialPad2;

        public static MaterialData Default => new MaterialData
        {
            DiffuseAlbedo = Vector4.One,
            FresnelR0 = new Vector3(0.01f),
            Roughness = 64.0f,
            MatTransform = Matrix.Identity
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Vertex
    {
        public Vector3 Pos;
        public Vector3 Normal;
        public Vector2 TexC;
    }

    internal class FrameResource : IDisposable
    {
        public FrameResource(Device device, int passCount, int maxInstanceCount, int materialCount)
        {
            CmdListAlloc = device.CreateCommandAllocator(CommandListType.Direct);

            PassCB = new UploadBuffer<PassConstants>(device, passCount, true);
            MaterialBuffer = new UploadBuffer<MaterialData>(device, materialCount, false);
            InstanceBuffer = new UploadBuffer<InstanceData>(device, maxInstanceCount, false);
        }

        // We cannot reset the allocator until the GPU is done processing the commands.
        // So each frame needs their own allocator.
        public CommandAllocator CmdListAlloc { get; }

        // We cannot update a cbuffer until the GPU is done processing the commands
        // that reference it. So each frame needs their own cbuffers.
        public UploadBuffer<PassConstants> PassCB { get; }
        public UploadBuffer<MaterialData> MaterialBuffer { get; }

        // NOTE: In this demo, we instance only one render-item, so we only have one structured buffer to 
        // store instancing data. To make this more general (i.e., to support instancing multiple render-items), 
        // you would need to have a structured buffer for each render-item, and allocate each buffer with enough
        // room for the maximum number of instances you would ever draw.  
        // This sounds like a lot, but it is actually no more than the amount of per-object constant data we 
        // would need if we were not using instancing. For example, if we were drawing 1000 objects without instancing,
        // we would create a constant buffer with enough room for a 1000 objects. With instancing, we would just
        // create a structured buffer large enough to store the instance data for 1000 instances.  
        public UploadBuffer<InstanceData> InstanceBuffer { get; }

        // Fence value to mark commands up to this fence point.  This lets us
        // check if these frame resources are still in use by the GPU.
        public long Fence { get; set; }

        public void Dispose()
        {
            InstanceBuffer.Dispose();
            MaterialBuffer.Dispose();
            PassCB.Dispose();
            CmdListAlloc.Dispose();
        }
    }
}
