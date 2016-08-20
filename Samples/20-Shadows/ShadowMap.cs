using System;
using SharpDX;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D12.Device;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    internal class ShadowMap : IDisposable
    {
        private static readonly Format Format = Format.R24G8_Typeless;

        private readonly Device _device;

        private CpuDescriptorHandle _cpuSrv;
        private GpuDescriptorHandle _gpuSrv;
        private CpuDescriptorHandle _cpuDsv;

        public ShadowMap(Device device, int width, int height)
        {
            _device = device;

            Width = width;
            Height = height;

            Viewport = new ViewportF(0, 0, width, height);
            ScissorRectangle = new RectangleF(0, 0, width, height);

            BuildResource();
        }

        public int Width { get; private set; }
        public int Height { get; private set; }

        public Resource Resource { get; private set; }
        public GpuDescriptorHandle Srv => _gpuSrv;
        public CpuDescriptorHandle Dsv => _cpuDsv;

        public ViewportF Viewport { get; private set; }
        public RectangleF ScissorRectangle { get; private set; }

        public void BuildDescriptors(
            CpuDescriptorHandle cpuSrv, 
            GpuDescriptorHandle gpuSrv,
            CpuDescriptorHandle cpuDsv)
        {
            // Save references to the descriptors. 
            _cpuSrv = cpuSrv;
            _gpuSrv = gpuSrv;
            _cpuDsv = cpuDsv;

            //  Create the descriptors
            BuildDescriptors();
        }

        public void OnResize(int newWidth, int newHeight)
        {
            if (Width != newWidth || Height != newHeight)
            {
                Width = newWidth;
                Height = newHeight;

                Dispose();

                BuildResource();
            }
        }

        private void BuildDescriptors()
        {
            // Create SRV to resource so we can sample the shadow map in a shader program.
            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
                Format = Format.R24_UNorm_X8_Typeless,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new ShaderResourceViewDescription.Texture2DResource
                {
                    MostDetailedMip = 0,
                    MipLevels = 1,
                    ResourceMinLODClamp = 0.0f,
                    PlaneSlice = 0
                }
            };
            _device.CreateShaderResourceView(Resource, srvDesc, _cpuSrv);

            // Create DSV to resource so we can render to the shadow map.
            var dsvDesc = new DepthStencilViewDescription
            {                                
                Flags = DepthStencilViewFlags.None,
                Dimension = DepthStencilViewDimension.Texture2D,
                Format = Format.D24_UNorm_S8_UInt,
                Texture2D = new DepthStencilViewDescription.Texture2DResource
                {
                    MipSlice = 0
                }
            };
            _device.CreateDepthStencilView(Resource, dsvDesc, _cpuDsv);
        }

        private void BuildResource()
        {
            var texDesc = new ResourceDescription
            {
                Dimension = ResourceDimension.Texture2D,
                Alignment = 0,
                Width = Width,
                Height = Height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = Format,
                SampleDescription = new SampleDescription(1, 0),
                Layout = TextureLayout.Unknown,
                Flags = ResourceFlags.AllowDepthStencil
            };

            var optClear = new ClearValue
            {
                Format = Format.D24_UNorm_S8_UInt,
                DepthStencil = new DepthStencilValue
                {
                    Depth = 1.0f,
                    Stencil = 0
                }
            };

            Resource = _device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                texDesc,
                ResourceStates.GenericRead,
                optClear);
        }

        public void Dispose()
        {
            Resource?.Dispose();
        }
    }
}
