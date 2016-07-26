using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D12;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Data
    {
        public Vector3 V1;
        public Vector2 V2;
    }

    public class VecAddApp : D3DApp
    {
        private const int NumDataElements = 32;        

        private RootSignature _rootSignature;

        private readonly Dictionary<string, ShaderBytecode> _shaders = new Dictionary<string, ShaderBytecode>();
        private readonly Dictionary<string, PipelineState> _psos = new Dictionary<string, PipelineState>();

        private Resource _inputBufferA;
        private Resource _inputUploadBufferA;
        private Resource _inputBufferB;
        private Resource _inputUploadBufferB;
        private Resource _outputBuffer;
        private Resource _readBackBuffer;

        public VecAddApp(IntPtr hInstance) : base(hInstance)
        {
        }

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            BuildBuffers();
            BuildRootSignature();
            BuildShadersAndInputLayout();
            BuildPSOs();

            // Execute the initialization commands.
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            // Wait until initialization is complete.
            FlushCommandQueue();

            DoComputeWork();
        }    

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _readBackBuffer.Dispose();
                _outputBuffer.Dispose();
                _inputUploadBufferB.Dispose();
                _inputBufferB.Dispose();
                _inputUploadBufferA.Dispose();
                _inputBufferA.Dispose();
                _rootSignature.Dispose();
                foreach (PipelineState pso in _psos.Values) pso.Dispose();
            }
            base.Dispose(disposing);
        }

        private void DoComputeWork()
        {
            // Reuse the memory associated with command recording.
            // We can only reset when the associated command lists have finished execution on the GPU.
            DirectCmdListAlloc.Reset();

            // A command list can be reset after it has been added to the command queue via ExecuteCommandList.
            // Reusing the command list reuses memory.
            CommandList.Reset(DirectCmdListAlloc, _psos["vecAdd"]);

            CommandList.SetComputeRootSignature(_rootSignature);

            CommandList.SetComputeRootShaderResourceView(0, _inputBufferA.GPUVirtualAddress);
            CommandList.SetComputeRootShaderResourceView(1, _inputBufferB.GPUVirtualAddress);
            CommandList.SetComputeRootUnorderedAccessView(2, _outputBuffer.GPUVirtualAddress);

            CommandList.Dispatch(1, 1, 1);

            // Schedule to copy the data to the default buffer to the readback buffer.
            CommandList.ResourceBarrierTransition(_outputBuffer, ResourceStates.Common, ResourceStates.CopySource);

            CommandList.CopyResource(_readBackBuffer, _outputBuffer);

            CommandList.ResourceBarrierTransition(_outputBuffer, ResourceStates.CopySource, ResourceStates.Common);

            // Done recording commands.
            CommandList.Close();

            // Add the command list to the queue for execution.
            CommandQueue.ExecuteCommandList(CommandList);

            // Wait for the work to finish.
            FlushCommandQueue();

            // Map the data so we can read it on CPU.
            var mappedData = new Data[NumDataElements];
            IntPtr ptr = _readBackBuffer.Map(0);
            Utilities.Read(ptr, mappedData, 0, NumDataElements);

            using (var fstream = File.OpenWrite("results.txt"))
            {
                using (var strWriter = new StreamWriter(fstream))
                {
                    foreach (Data data in mappedData)
                        strWriter.WriteLine($"({data.V1.X}, {data.V1.Y}, {data.V1.Z}, {data.V2.X}, {data.V2.Y})");
                }
            }

            _readBackBuffer.Unmap(0);
        }

        private void BuildBuffers()
        {
            // Generate some data.
            var dataA = new Data[NumDataElements];
            var dataB = new Data[NumDataElements];
            for (int i = 0; i < NumDataElements; i++)
            {
                dataA[i].V1 = new Vector3(i, i, i);
                dataA[i].V2 = new Vector2(i, 0);
                              
                dataB[i].V1 = new Vector3(-i, i, 0.0f);
                dataB[i].V2 = new Vector2(0, -i);
            }

            long byteSize = dataA.Length * Utilities.SizeOf<Data>();

            // Create some buffers to be used as SRVs.
            _inputBufferA = D3DUtil.CreateDefaultBuffer(
                Device,
                CommandList,
                dataA,
                byteSize,
                out _inputUploadBufferA);

            _inputBufferB = D3DUtil.CreateDefaultBuffer(
                Device,
                CommandList,
                dataB,
                byteSize,
                out _inputUploadBufferB);

            // Create the buffer that will be a UAV.
            _outputBuffer = Device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                ResourceDescription.Buffer(byteSize, ResourceFlags.AllowUnorderedAccess),
                ResourceStates.UnorderedAccess);

            _readBackBuffer = Device.CreateCommittedResource(
                new HeapProperties(HeapType.Readback),
                HeapFlags.None,
                ResourceDescription.Buffer(byteSize),
                ResourceStates.CopyDestination);
        }

        private void BuildRootSignature()
        {
            // Root parameter can be a table, root descriptor or root constants.
            // Perfomance TIP: Order from most frequent to least frequent.
            var slotRootParameters = new[]
            {
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 0), RootParameterType.ShaderResourceView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(1, 0), RootParameterType.ShaderResourceView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 0), RootParameterType.UnorderedAccessView)
            };

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                slotRootParameters);

            _rootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildShadersAndInputLayout()
        {
            _shaders["vecAddCS"] = D3DUtil.CompileShader("Shaders\\VecAdd.hlsl", "CS", "cs_5_0");
        }

        private void BuildPSOs()
        {
            var computePsoDesc = new ComputePipelineStateDescription // TODO: desc to struct
            {
                RootSignature = _rootSignature,
                ComputeShader = _shaders["vecAddCS"],
                Flags = PipelineStateFlags.None
            };
            _psos["vecAdd"] = Device.CreateComputePipelineState(computePsoDesc);
        }
    }
}
