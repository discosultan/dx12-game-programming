using System;
using SharpDX;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D12.Device;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    internal class CubeRenderTarget : IDisposable
    {
        private readonly Device _device;
        private readonly Format _format;

        private CpuDescriptorHandle _cpuSrv;
        private GpuDescriptorHandle _gpuSrv;

        public CubeRenderTarget(Device device, int width, int height, Format format)
        {
            _device = device;
            _format = format;

            Width = width;
            Height = height;

            Viewport = new ViewportF(0, 0, Width, Height);
            ScissorRectangle = new RectangleF(0, 0, width, height);

            BuildResource();
        }

        public int Width { get; private set; }
        public int Height { get; private set; }

        public Resource Resource { get; private set; }
        public GpuDescriptorHandle Srv => _gpuSrv;
        public CpuDescriptorHandle[] Rtvs { get; private set; }

        public ViewportF Viewport { get; private set; }
        public RectangleF ScissorRectangle { get; private set; }

        public void BuildDescriptors(
            CpuDescriptorHandle cpuSrv,
            GpuDescriptorHandle gpuSrv,
            CpuDescriptorHandle[] cpuRtvs)
        {
            // Save references to the descriptors.
            _cpuSrv = cpuSrv;
            _gpuSrv = gpuSrv;

            Rtvs = cpuRtvs;

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
            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
                Format = _format,
                Dimension = ShaderResourceViewDimension.TextureCube,
                TextureCube = new ShaderResourceViewDescription.TextureCubeResource
                {
                    MostDetailedMip = 0,
                    MipLevels = 1,
                    ResourceMinLODClamp = 0.0f
                }
            };

            // Create SRV to the entire cubemap resource.
            _device.CreateShaderResourceView(Resource, srvDesc, _cpuSrv);

            // Create RTV to each cube face.
            for (int i = 0; i < 6; i++)
            {
                var rtvDesc = new RenderTargetViewDescription
                {
                    Dimension = RenderTargetViewDimension.Texture2DArray,
                    Format = _format,
                    Texture2DArray = new RenderTargetViewDescription.Texture2DArrayResource
                    {
                        MipSlice = 0,
                        PlaneSlice = 0,

                        // Render target to ith element.
                        FirstArraySlice = i,

                        // Only view one element of the array.
                        ArraySize = 1
                    }
                };

                // Create RTV to ith cubemap face.
                _device.CreateRenderTargetView(Resource, rtvDesc, Rtvs[i]);
            }
        }

        private void BuildResource()
        {
            // Note, compressed formats cannot be used for UAV. We get error like:
            // ERROR: ID3D11Device::CreateTexture2D: The format (0x4d, BC3_UNORM)
            // cannot be bound as an UnorderedAccessView, or cast to a format that
            // could be bound as an UnorderedAccessView. Therefore this format
            // does not support D3D11_BIND_UNORDERED_ACCESS.

            var texDesc = new ResourceDescription
            {
                Dimension = ResourceDimension.Texture2D,
                Alignment = 0,
                Width = Width,
                Height = Height,
                DepthOrArraySize = 6,
                MipLevels = 1,
                Format = _format,
                SampleDescription = new SampleDescription(1, 0),
                Layout = TextureLayout.Unknown,
                Flags = ResourceFlags.AllowRenderTarget
            };

            var optClear = new ClearValue
            {
                Format = _format,
                Color = Color.LightSteelBlue.ToVector4()
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
