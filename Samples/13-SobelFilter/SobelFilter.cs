using System;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D12.Device;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    internal class SobelFilter : IDisposable
    {
        private readonly Device _device;
        private readonly Format _format;
        private int _width;
        private int _height;

        private CpuDescriptorHandle _cpuSrv;
        private CpuDescriptorHandle _cpuUav;

        private GpuDescriptorHandle _gpuSrv;
        private GpuDescriptorHandle _gpuUav;

        private Resource _output;

        public SobelFilter(Device device, int width, int height, Format format)
        {
            _format = format;
            _height = height;
            _width = width;
            _device = device;

            BuildResources();
        }

        public GpuDescriptorHandle OutputSrv => _gpuSrv;
        public int DescriptorCount => 2;

        public void BuildDescriptors(CpuDescriptorHandle cpuDescriptor, GpuDescriptorHandle gpuDescriptor, int descriptorSize)
        {
            // Save references to the descriptors.
            _cpuSrv = cpuDescriptor;
            _cpuUav = cpuDescriptor + descriptorSize;
            _gpuSrv = gpuDescriptor;
            _gpuUav = gpuDescriptor + descriptorSize;

            BuildDescriptors();
        }

        public void OnResize(int newWidth, int newHeight)
        {
            if (_width != newWidth || _height != newHeight)
            {
                _width = newWidth;
                _height = newHeight;

                Dispose();

                BuildResources();

                // New resource, so we need new descriptors to that resource.
                BuildDescriptors();
            }
        }

        public void Execute(GraphicsCommandList cmdList, RootSignature rootSig, PipelineState pso, GpuDescriptorHandle input)
        {
            cmdList.SetComputeRootSignature(rootSig);
            cmdList.PipelineState = pso;

            cmdList.SetComputeRootDescriptorTable(0, input);
            cmdList.SetComputeRootDescriptorTable(2, _gpuUav);

            cmdList.ResourceBarrierTransition(_output, ResourceStates.GenericRead, ResourceStates.UnorderedAccess);

            // How many groups do we need to dispatch to cover image, where each
            // group covers 16x16 pixels.
            int numGroupsX = (int)Math.Ceiling(_width / 16.0);
            int numGroupsY = (int)Math.Ceiling(_height / 16.0);
            cmdList.Dispatch(numGroupsX, numGroupsY, 1);

            cmdList.ResourceBarrierTransition(_output, ResourceStates.UnorderedAccess, ResourceStates.GenericRead);
        }

        public void Dispose()
        {
            _output?.Dispose();
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

            var uavDesc = new UnorderedAccessViewDescription
            {
                Format = _format,
                Dimension = UnorderedAccessViewDimension.Texture2D,
                Texture2D = new UnorderedAccessViewDescription.Texture2DResource
                {
                    MipSlice = 0
                }
            };

            _device.CreateShaderResourceView(_output, srvDesc, _cpuSrv);
            _device.CreateUnorderedAccessView(_output, null, uavDesc, _cpuUav);
        }

        private void BuildResources()
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
                Flags = ResourceFlags.AllowUnorderedAccess
            };

            _output = _device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                texDesc,
                ResourceStates.Common);
        }
    }
}
