using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D12.Device;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    internal class RenderTarget
    {
        private readonly Device _device;
        private readonly Format _format;
        private int _width;
        private int _height;

        private CpuDescriptorHandle _cpuSrv;
        private GpuDescriptorHandle _gpuSrv;
        private CpuDescriptorHandle _cpuRtv;

        private Resource _offscreenTex;

        public RenderTarget(Device device, int width, int height, Format format)
        {
            _device = device;
            _format = format;
            _width = width;
            _height = height;

            BuildResource();
        }

        public Resource Resource => _offscreenTex;
        public GpuDescriptorHandle Srv => _gpuSrv;
        public CpuDescriptorHandle Rtv => _cpuRtv;

        public void BuildDescriptors(CpuDescriptorHandle cpuSrv, GpuDescriptorHandle gpuSrv, CpuDescriptorHandle cpuRtv)
        {
            // Save references to the descriptors.
            _cpuSrv = cpuSrv;
            _gpuSrv = gpuSrv;
            _cpuRtv = cpuRtv;

            BuildDescriptors();
        }

        public void OnResize(int newWidth, int newHeight)
        {
            if ((_width != newWidth) || (_height != newHeight))
            {
                _width = newWidth;
                _height = newHeight;

                BuildResource();

                // New resource, so we need new descriptors to that resource.
                BuildDescriptors();
            }
        }

        private void BuildDescriptors()
        {
            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
                Format = _format,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new ShaderResourceViewDescription.Texture2DResource
                {
                    MostDetailedMip = 0,
                    MipLevels = 1
                }
            };

            _device.CreateShaderResourceView(_offscreenTex, srvDesc, _cpuSrv);
            _device.CreateRenderTargetView(_offscreenTex, null, _cpuRtv);
        }

        private void BuildResource()
        {
            // Note, compressed formats cannot be used for UAV.  We get error like:
            // ERROR: ID3D11Device::CreateTexture2D: The format (0x4d, BC3_UNORM)
            // cannot be bound as an UnorderedAccessView, or cast to a format that
            // could be bound as an UnorderedAccessView.  Therefore this format
            // does not support D3D11_BIND_UNORDERED_ACCESS.

            var texDesc = new ResourceDescription
            {
                Dimension = ResourceDimension.Texture2D,
                Alignment = 0,
                Width = _width,
                Height = _height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = _format,
                SampleDescription = new SampleDescription(1, 0),
                Layout = TextureLayout.Unknown,
                Flags = ResourceFlags.AllowRenderTarget
            };

            _offscreenTex = _device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                texDesc,
                ResourceStates.GenericRead);
        }
    }
}
