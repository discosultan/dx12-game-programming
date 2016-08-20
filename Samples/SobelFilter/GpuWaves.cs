using SharpDX;
using System.Diagnostics;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D12.Device;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    internal class GpuWaves
    {
        // Simulation constants we can precompute.
        private readonly float[] _k;

        private float _t;
        private readonly float _timeStep;
        private readonly float _spatialStep;

        private readonly Device _device;

        private GpuDescriptorHandle _prevSolSrv;
        private GpuDescriptorHandle _currSolSrv;
        private GpuDescriptorHandle _nextSolSrv;

        private GpuDescriptorHandle _prevSolUav;
        private GpuDescriptorHandle _currSolUav;
        private GpuDescriptorHandle _nextSolUav;

        // Two for ping-ponging the textures.
        private Resource _prevSol;
        private Resource _currSol;
        private Resource _nextSol;

        private Resource _prevUploadBuffer;
        private Resource _currUploadBuffer;

        public GpuWaves(Device device, GraphicsCommandList cmdList, int m, int n, float dx, float dt, float speed, float damping)
        {
            _device = device;

            RowCount = m;
            ColumnCount = n;

            Debug.Assert((m * n) % 256 == 0);

            VertexCount = m * n;
            TriangleCount = (m - 1) * (n - 1) * 2;

            _timeStep = dt;
            _spatialStep = dx;

            float d = damping * dt + 2.0f;
            float e = (speed * speed) * (dt * dt) / (dx * dx);
            _k = new[]
            {
                (damping * dt - 2.0f) / d,
                (4.0f - 8.0f * e) / d,
                (2.0f * e) / d
            };

            BuildResources(cmdList);
        }

        public float SpatialStep => _spatialStep;
        public GpuDescriptorHandle DisplacementMap => _currSolSrv;

        public int RowCount { get; }
        public int ColumnCount { get; }
        public int VertexCount { get; }
        public int TriangleCount { get; }
        public float Width => ColumnCount * _spatialStep;
        public float Depth => RowCount * _spatialStep;

        // Number of descriptors in heap to reserve for GpuWaves.
        public int DescriptorCount { get; } = 6;

        public void BuildResources(GraphicsCommandList cmdList)
        {
            // All the textures for the wave simulation will be bound as a shader resource and
            // unordered access view at some point since we ping-pong the buffers.

            var texDesc = new ResourceDescription
            {
                Dimension = ResourceDimension.Texture2D,
                Alignment = 0,
                Width = ColumnCount,
                Height = RowCount,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Layout = TextureLayout.Unknown,
                Flags = ResourceFlags.AllowUnorderedAccess
            };

            _prevSol = _device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                texDesc,
                ResourceStates.Common);
            _currSol = _device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                texDesc,
                ResourceStates.Common);
            _nextSol = _device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                texDesc,
                ResourceStates.Common);

            //
            // In order to copy CPU memory data into our default buffer, we need to create
            // an intermediate upload heap. 
            //

            int num2DSubresources = texDesc.DepthOrArraySize * texDesc.MipLevels;
            long uploadBufferSize;
            _device.GetCopyableFootprints(ref texDesc, 0, num2DSubresources, 0, null, null, null, out uploadBufferSize);

            _prevUploadBuffer = _device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload),
                HeapFlags.None,
                ResourceDescription.Buffer(uploadBufferSize),
                ResourceStates.GenericRead);
            _currUploadBuffer = _device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload),
                HeapFlags.None,
                ResourceDescription.Buffer(uploadBufferSize),
                ResourceStates.GenericRead);            

            //
            // Schedule to copy the data to the default resource, and change states.
            // Note that mCurrSol is put in the GENERIC_READ state so it can be 
            // read by a shader.
            //

            cmdList.ResourceBarrierTransition(_prevSol, ResourceStates.Common, ResourceStates.CopyDestination);
            cmdList.CopyResource(_prevSol, _prevUploadBuffer);
            cmdList.ResourceBarrierTransition(_prevSol, ResourceStates.CopyDestination, ResourceStates.UnorderedAccess);

            cmdList.ResourceBarrierTransition(_currSol, ResourceStates.Common, ResourceStates.CopyDestination);
            cmdList.CopyResource(_currSol, _currUploadBuffer);
            cmdList.ResourceBarrierTransition(_currSol, ResourceStates.CopyDestination, ResourceStates.GenericRead);

            cmdList.ResourceBarrierTransition(_nextSol, ResourceStates.Common, ResourceStates.UnorderedAccess);
        }

        public void BuildDescriptors(CpuDescriptorHandle cpuDescriptor, GpuDescriptorHandle gpuDescriptor, int descriptorSize)
        {
            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
                Format = Format.R32_Float,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new ShaderResourceViewDescription.Texture2DResource
                {
                    MostDetailedMip = 0,
                    MipLevels = 1
                }
            };

            var uavDesc = new UnorderedAccessViewDescription
            {
                Format = Format.R32_Float,
                Dimension = UnorderedAccessViewDimension.Texture2D,
                Texture2D = new UnorderedAccessViewDescription.Texture2DResource
                {
                    MipSlice = 0
                }
            };
            _device.CreateShaderResourceView(_prevSol, srvDesc, cpuDescriptor);
            _device.CreateShaderResourceView(_currSol, srvDesc, cpuDescriptor + descriptorSize);
            _device.CreateShaderResourceView(_nextSol, srvDesc, cpuDescriptor + descriptorSize * 2);

            _device.CreateUnorderedAccessView(_prevSol, null, uavDesc, cpuDescriptor + descriptorSize * 3);
            _device.CreateUnorderedAccessView(_currSol, null, uavDesc, cpuDescriptor + descriptorSize * 4);
            _device.CreateUnorderedAccessView(_nextSol, null, uavDesc, cpuDescriptor + descriptorSize * 5);

            // Save references to the GPU descriptors.
            _prevSolSrv = gpuDescriptor;
            _currSolSrv = gpuDescriptor + descriptorSize;
            _nextSolSrv = gpuDescriptor + descriptorSize * 2;
            _prevSolUav = gpuDescriptor + descriptorSize * 3;
            _currSolUav = gpuDescriptor + descriptorSize * 4;
            _nextSolUav = gpuDescriptor + descriptorSize * 5;
        }

        public void Update(GameTimer gt, GraphicsCommandList cmdList, RootSignature rootSig, PipelineState pso)
        {
            // Accumulate time.
            _t += gt.DeltaTime;

            cmdList.PipelineState = pso;
            cmdList.SetComputeRootSignature(rootSig);

            // Only update the simulation at the specified time step.
            if (_t >= _timeStep)
            {
                // Set the update constants.
                Utilities.Pin(_k, ptr => cmdList.SetComputeRoot32BitConstants(0, 3, ptr, 0));                

                cmdList.SetComputeRootDescriptorTable(1, _prevSolUav);
                cmdList.SetComputeRootDescriptorTable(2, _currSolUav);
                cmdList.SetComputeRootDescriptorTable(3, _nextSolUav);

                // How many groups do we need to dispatch to cover the wave grid.  
                // Note that RowCount and ColumnCount should be divisible by 16
                // so there is no remainder.
                int numGroupsX = ColumnCount / 16;
                int numGroupsY = RowCount / 16;
                cmdList.Dispatch(numGroupsX, numGroupsY, 1);

                //
                // Ping-pong buffers in preparation for the next update.
                // The previous solution is no longer needed and becomes the target of the next solution in the next update.
                // The current solution becomes the previous solution.
                // The next solution becomes the current solution.
                //

                Resource resTemp = _prevSol;
                _prevSol = _currSol;
                _currSol = _nextSol;
                _nextSol = resTemp;

                GpuDescriptorHandle srvTemp = _prevSolSrv;
                _prevSolSrv = _currSolSrv;
                _currSolSrv = _nextSolSrv;
                _nextSolSrv = srvTemp;

                GpuDescriptorHandle uavTemp = _prevSolUav;
                _prevSolUav = _currSolUav;
                _currSolUav = _nextSolUav;
                _nextSolUav = uavTemp;

                // Reset time.
                _t = 0.0f;

                // The current solution needs to be able to be read by the vertex shader, so change its state to GENERIC_READ.
                cmdList.ResourceBarrierTransition(_currSol, ResourceStates.UnorderedAccess, ResourceStates.GenericRead);
            }
        }

        public void Disturb(GraphicsCommandList cmdList, RootSignature rootSig, PipelineState pso, int i, int j, float magnitude)
        {
            cmdList.PipelineState = pso;
            cmdList.SetComputeRootSignature(rootSig);

            // Set the disturb constants.
            int[] disturbIndex = { j, i };
            Utilities.Pin(ref magnitude, ptr => cmdList.SetComputeRoot32BitConstants(0, 1, ptr, 3));
            Utilities.Pin(disturbIndex, ptr => cmdList.SetComputeRoot32BitConstants(0, 2, ptr, 4));

            cmdList.SetComputeRootDescriptorTable(3, _currSolUav);

            // The current solution is in the GENERIC_READ state so it can be read by the vertex shader.
            // Change it to UNORDERED_ACCESS for the compute shader. Note that a UAV can still be
            // read in a compute shader.
            cmdList.ResourceBarrierTransition(_currSol, ResourceStates.GenericRead, ResourceStates.UnorderedAccess);

            // One thread group kicks off one thread, which displaces the height of one
            // vertex and its neighbors.
            cmdList.Dispatch(1, 1, 1);
        }
    }
}
