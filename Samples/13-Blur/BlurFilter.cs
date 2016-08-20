using System;
using SharpDX;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D12.Device;
using Resource = SharpDX.Direct3D12.Resource;
using System.Diagnostics;

namespace DX12GameProgramming
{
    internal class BlurFilter : IDisposable
    {
        private const int MaxBlurRadius = 5;

        private readonly Device _device;        
        private readonly Format _format;
        private int _width;
        private int _height;

        private CpuDescriptorHandle _blur0CpuSrv;
        private CpuDescriptorHandle _blur0CpuUav;

        private CpuDescriptorHandle _blur1CpuSrv;
        private CpuDescriptorHandle _blur1CpuUav;

        private GpuDescriptorHandle _blur0GpuSrv;
        private GpuDescriptorHandle _blur0GpuUav;

        private GpuDescriptorHandle _blur1GpuSrv;
        private GpuDescriptorHandle _blur1GpuUav;

        // Two for ping-ponging the textures.
        Resource _blurMap0;
        Resource _blurMap1;

        public BlurFilter(Device device, int width, int height, Format format)
        {
            _format = format;
            _height = height;
            _width = width;
            _device = device;

            BuildResources();
        }

        public Resource Output => _blurMap0;

        public void BuildDescriptors(CpuDescriptorHandle cpuDescriptor, GpuDescriptorHandle gpuDescriptor, int descriptorSize)
        {
            _blur0CpuSrv = cpuDescriptor;
            _blur0CpuUav = cpuDescriptor + descriptorSize;
            _blur1CpuSrv = cpuDescriptor + descriptorSize * 2;
            _blur1CpuUav = cpuDescriptor + descriptorSize * 3;

            _blur0GpuSrv = gpuDescriptor;
            _blur0GpuUav = gpuDescriptor + descriptorSize;
            _blur1GpuSrv = gpuDescriptor + descriptorSize * 2;
            _blur1GpuUav = gpuDescriptor + descriptorSize * 3;

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

        public void Execute(
            GraphicsCommandList cmdList, 
            RootSignature rootSig, 
            PipelineState horzBlurPso,
            PipelineState vertBlurPso,
            Resource input,
            int blurCount)
        {
            float[] weights = CalcGaussWeights(2.5f);
            int blurRadius = weights.Length / 2;

            cmdList.SetComputeRootSignature(rootSig);
            
            Utilities.Pin(ref blurRadius, ptr => cmdList.SetComputeRoot32BitConstants(0, 1, ptr, 0));
            Utilities.Pin(weights, ptr => cmdList.SetComputeRoot32BitConstants(0, weights.Length, ptr, 1));

            cmdList.ResourceBarrierTransition(input, ResourceStates.RenderTarget, ResourceStates.CopySource);
            cmdList.ResourceBarrierTransition(_blurMap0, ResourceStates.GenericRead, ResourceStates.CopyDestination);

            // Copy the input (back-buffer in this example) to BlurMap0.
            cmdList.CopyResource(_blurMap0, input);

            cmdList.ResourceBarrierTransition(_blurMap0, ResourceStates.CopyDestination, ResourceStates.GenericRead);

            for (int i = 0; i < blurCount; i++)
            {
                //
                // Horizontal Blur pass.
                //

                cmdList.PipelineState = horzBlurPso;

                cmdList.SetComputeRootDescriptorTable(1, _blur0GpuSrv);
                cmdList.SetComputeRootDescriptorTable(2, _blur1GpuUav);

                // How many groups do we need to dispatch to cover a row of pixels, where each
                // group covers 256 pixels (the 256 is defined in the ComputeShader).
                int numGroupsX = (int)Math.Ceiling(_width / 256.0f);
                cmdList.Dispatch(numGroupsX, _height, 1);

                cmdList.ResourceBarrierTransition(_blurMap0, ResourceStates.GenericRead, ResourceStates.UnorderedAccess);
                cmdList.ResourceBarrierTransition(_blurMap1, ResourceStates.UnorderedAccess, ResourceStates.GenericRead);

                //
                // Vertical Blur pass.
                //

                cmdList.PipelineState = vertBlurPso;

                cmdList.SetComputeRootDescriptorTable(1, _blur1GpuSrv);
                cmdList.SetComputeRootDescriptorTable(2, _blur0GpuUav);

                // How many groups do we need to dispatch to cover a column of pixels, where each
                // group covers 256 pixels  (the 256 is defined in the ComputeShader).
                int numGroupsY = (int)Math.Ceiling(_height / 256.0f);
                cmdList.Dispatch(_width, numGroupsY, 1);

                cmdList.ResourceBarrierTransition(_blurMap0, ResourceStates.UnorderedAccess, ResourceStates.GenericRead);
                cmdList.ResourceBarrierTransition(_blurMap1, ResourceStates.GenericRead, ResourceStates.UnorderedAccess);
            }
        }

        public void Dispose()
        {
            _blurMap1?.Dispose();
            _blurMap0?.Dispose();
        }

        private float[] CalcGaussWeights(float sigma)
        {
            float twoSigma2 = 2.0f * sigma * sigma;

            // Estimate the blur radius based on sigma since sigma controls the "width" of the bell curve.
            // For example, for sigma = 3, the width of the bell curve is.
            int blurRadius = (int)Math.Ceiling(2.0f * sigma);

            Debug.Assert(blurRadius <= MaxBlurRadius);

            var weights = new float[2 * blurRadius + 1];

            float weightSum = 0.0f;

            for (int i = -blurRadius; i <= blurRadius; i++)
            {
                float x = i;

                weights[i + blurRadius] =  MathHelper.Expf(-x * x / twoSigma2);

                weightSum += weights[i + blurRadius];
            }

            // Divide by the sum so all the weights add up to 1.0.
            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] /= weightSum;
            }

            return weights;
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

            _device.CreateShaderResourceView(_blurMap0, srvDesc, _blur0CpuSrv);
            _device.CreateUnorderedAccessView(_blurMap0, null, uavDesc, _blur0CpuUav);

            _device.CreateShaderResourceView(_blurMap1, srvDesc, _blur1CpuSrv);
            _device.CreateUnorderedAccessView(_blurMap1, null, uavDesc, _blur1CpuUav);
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

            _blurMap0 = _device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                texDesc,
                ResourceStates.GenericRead);

            _blurMap1 = _device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                texDesc,
                ResourceStates.UnorderedAccess);
        }
    }
}
