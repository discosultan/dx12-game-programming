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
        public int MaterialIndex;
        public int ObjPad0;
        public int ObjPad1;
        public int ObjPad2;

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
        public Matrix ShadowTransform;
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
            ShadowTransform = Matrix.Identity,
            AmbientLight = Vector4.UnitW,
            Lights = Light.DefaultArray
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal  struct SsaoConstants
    {
        public Matrix Proj;
        public Matrix InvProj;
        public Matrix ProjTex;
        public OffsetVectors OffsetVectors;

        // For SsaoBlur.hlsl
        public BlurWeights BlurWeights;

        public Vector2 InvRenderTargetSize;

        // Coordinates given in view space.
        public float OcclusionRadius;
        public float OcclusionFadeStart;
        public float OcclusionFadeEnd;
        public float SurfaceEpsilon;

        public static SsaoConstants Default => new SsaoConstants
        {            
            OcclusionRadius = 0.5f,
            OcclusionFadeStart = 0.2f,
            OcclusionFadeEnd = 2.0f,
            SurfaceEpsilon = 0.05f
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
        public int NormalMapIndex;
        public int MaterialPad0;
        public int MaterialPad1;

        public static MaterialData Default => new MaterialData
        {
            DiffuseAlbedo = Vector4.One,
            FresnelR0 = new Vector3(0.01f),
            Roughness = 64.0f,
            MatTransform = Matrix.Identity
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Vertex
    {
        public Vector3 Pos;
        public Vector3 Normal;
        public Vector2 TexC;
        public Vector3 TangentU;
    }

    internal class FrameResource : IDisposable
    {
        public FrameResource(Device device, int passCount, int objectCount, int materialCount)
        {
            CmdListAlloc = device.CreateCommandAllocator(CommandListType.Direct);

            PassCB = new UploadBuffer<PassConstants>(device, passCount, true);
            ObjectCB = new UploadBuffer<ObjectConstants>(device, objectCount, true);
            SsaoCB = new UploadBuffer<SsaoConstants>(device, 1, true);
            MaterialBuffer = new UploadBuffer<MaterialData>(device, materialCount, false);
        }

        // We cannot reset the allocator until the GPU is done processing the commands.
        // So each frame needs their own allocator.
        public CommandAllocator CmdListAlloc { get; }

        // We cannot update a cbuffer until the GPU is done processing the commands
        // that reference it. So each frame needs their own cbuffers.
        public UploadBuffer<PassConstants> PassCB { get; }
        public UploadBuffer<ObjectConstants> ObjectCB { get; }
        public UploadBuffer<SsaoConstants> SsaoCB { get; }
        public UploadBuffer<MaterialData> MaterialBuffer { get; }

        // Fence value to mark commands up to this fence point.  This lets us
        // check if these frame resources are still in use by the GPU.
        public long Fence { get; set; }

        public void Dispose()
        {
            MaterialBuffer.Dispose();
            SsaoCB.Dispose();
            ObjectCB.Dispose();
            PassCB.Dispose();
            CmdListAlloc.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct OffsetVectors
    {
        public Vector4 Offset1;
        public Vector4 Offset2;
        public Vector4 Offset3;
        public Vector4 Offset4;
        public Vector4 Offset5;
        public Vector4 Offset6;
        public Vector4 Offset7;
        public Vector4 Offset8;
        public Vector4 Offset9;
        public Vector4 Offset10;
        public Vector4 Offset11;
        public Vector4 Offset12;
        public Vector4 Offset13;
        public Vector4 Offset14;

        public Vector4 this[int index]
        {
            get
            {
                switch (index)
                {
                    case 0: return Offset1;
                    case 1: return Offset2;
                    case 2: return Offset3;
                    case 3: return Offset4;
                    case 4: return Offset5;
                    case 5: return Offset6;
                    case 6: return Offset7;
                    case 7: return Offset8;
                    case 8: return Offset9;
                    case 9: return Offset10;
                    case 10: return Offset11;
                    case 11: return Offset12;
                    case 12: return Offset13;
                    default: return Offset14;
                }
            }
            set
            {
                switch (index)
                {
                    case 0: Offset1 = value; break;
                    case 1: Offset2 = value; break;
                    case 2: Offset3 = value; break;
                    case 3: Offset4 = value; break;
                    case 4: Offset5 = value; break;
                    case 5: Offset6 = value; break;
                    case 6: Offset7 = value; break;
                    case 7: Offset8 = value; break;
                    case 8: Offset9 = value; break;
                    case 9: Offset10 = value; break;
                    case 10: Offset11 = value; break;
                    case 11: Offset12 = value; break;
                    case 12: Offset13 = value; break;
                    default: Offset14 = value; break;
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct BlurWeights
    {
        public Vector4 Weight1;
        public Vector4 Weight2;
        public Vector4 Weight3;

        public Vector4 this[int index]
        {
            get
            {
                switch (index)
                {
                    case 0: return Weight1;
                    case 1: return Weight2;
                    default: return Weight3;
                }
            }
            set
            {
                switch (index)
                {
                    case 0: Weight1 = value; break;
                    case 1: Weight2 = value; break;
                    default: Weight3 = value; break;
                }
            }
        }
    }
}
