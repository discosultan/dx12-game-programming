using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
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

        private readonly List<FrameResource> _frameResources = new List<FrameResource>(NumFrameResources);
        private readonly List<AutoResetEvent> _fenceEvents = new List<AutoResetEvent>(NumFrameResources);
        private int _currFrameResourceIndex;

        private RootSignature _rootSignature;

        private readonly Dictionary<string, ShaderBytecode> _shaders = new Dictionary<string, ShaderBytecode>();
        private readonly Dictionary<string, PipelineState> _psos = new Dictionary<string, PipelineState>();

        private Resource _inputBufferA;
        private Resource _inputUploadBufferA;
        private Resource _inputBufferB;
        private Resource _inputUploadBufferB;
        private Resource _outputBuffer;
        private Resource _readBackBuffer;

        private PassConstants _mainPassCB = PassConstants.Default;

        private Vector3 _eyePos;
        private Matrix _proj = Matrix.Identity;
        private Matrix _view = Matrix.Identity;

        private float _theta = 1.5f * MathUtil.Pi;
        private float _phi = MathUtil.PiOverTwo - 0.1f;
        private float _radius = 50.0f;

        private Point _lastMousePos;

        public VecAddApp(IntPtr hInstance) : base(hInstance)
        {
        }

        private FrameResource CurrFrameResource => _frameResources[_currFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => _fenceEvents[_currFrameResourceIndex];

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            BuildBuffers();
            BuildRootSignature();
            BuildShadersAndInputLayout();
            BuildFrameResources();
            BuildPSOs();

            // Execute the initialization commands.
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            // Wait until initialization is complete.
            FlushCommandQueue();

            DoComputeWork();
        }

        protected override void OnResize()
        {
            base.OnResize();

            // The window resized, so update the aspect ratio and recompute the projection matrix.
            _proj = Matrix.PerspectiveFovLH(0.25f * MathUtil.Pi, AspectRatio, 1.0f, 1000.0f);
        }

        protected override void Update(GameTimer gt)
        {
            // Cycle through the circular frame resource array.
            _currFrameResourceIndex = (_currFrameResourceIndex + 1) % NumFrameResources;

            // Has the GPU finished processing the commands of the current frame resource?
            // If not, wait until the GPU has completed commands up to this fence point.
            if (CurrFrameResource.Fence != 0 && Fence.CompletedValue < CurrFrameResource.Fence)
            {
                Fence.SetEventOnCompletion(CurrFrameResource.Fence, CurrentFenceEvent.SafeWaitHandle.DangerousGetHandle());
                CurrentFenceEvent.WaitOne();
            }
        }

        protected override void Draw(GameTimer gt)
        {
            CommandAllocator cmdListAlloc = CurrFrameResource.CmdListAlloc;

            // Reuse the memory associated with command recording.
            // We can only reset when the associated command lists have finished execution on the GPU.
            cmdListAlloc.Reset();

            // A command list can be reset after it has been added to the command queue via ExecuteCommandList.
            // Reusing the command list reuses memory.
            CommandList.Reset(cmdListAlloc, _psos["opaque"]);

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            // Clear the back buffer and depth buffer.
            CommandList.ClearRenderTargetView(CurrentBackBufferView, new Color(_mainPassCB.FogColor));
            CommandList.ClearDepthStencilView(CurrentDepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.            
            CommandList.SetRenderTargets(CurrentBackBufferView, CurrentDepthStencilView);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

            // Done recording commands.
            CommandList.Close();

            // Add the command list to the queue for execution.
            CommandQueue.ExecuteCommandList(CommandList);

            // Present the buffer to the screen. Presenting will automatically swap the back and front buffers.
            SwapChain.Present(0, PresentFlags.None);

            // Advance the fence value to mark commands up to this fence point.
            CurrFrameResource.Fence = ++CurrentFence;

            // Add an instruction to the command queue to set a new fence point. 
            // Because we are on the GPU timeline, the new fence point won't be 
            // set until the GPU finishes processing all the commands prior to this Signal().
            CommandQueue.Signal(Fence, CurrentFence);
        }

        protected override void OnMouseDown(MouseButtons button, Point location)
        {
            base.OnMouseDown(button, location);
            _lastMousePos = location;
        }

        protected override void OnMouseMove(MouseButtons button, Point location)
        {
            if ((button & MouseButtons.Left) != 0)
            {
                // Make each pixel correspond to a quarter of a degree.                
                float dx = MathUtil.DegreesToRadians(0.25f * (location.X - _lastMousePos.X));
                float dy = MathUtil.DegreesToRadians(0.25f * (location.Y - _lastMousePos.Y));

                // Update angles based on input to orbit camera around box.
                _theta += dx;
                _phi += dy;

                // Restrict the angle mPhi.
                _phi = MathUtil.Clamp(_phi, 0.1f, MathUtil.Pi - 0.1f);
            }
            else if ((button & MouseButtons.Right) != 0)
            {
                // Make each pixel correspond to a quarter of a degree.                
                float dx = 0.2f * (location.X - _lastMousePos.X);
                float dy = 0.2f * (location.Y - _lastMousePos.Y);

                // Update the camera radius based on input.
                _radius += dx - dy;

                // Restrict the radius.
                _radius = MathUtil.Clamp(_radius, 5.0f, 150.0f);
            }

            _lastMousePos = location;
        }        

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (FrameResource frameResource in _frameResources) frameResource.Dispose();
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
                    for (int i = 0; i < NumDataElements; i++)
                    {
                        Data data = mappedData[i];
                        strWriter.WriteLine($"({data.V1.X}, {data.V1.Y}, {data.V1.Z}, {data.V2.X}, {data.V2.Y})");
                    }
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

        private void UpdateMainPassCB(GameTimer gt)
        {
            Matrix viewProj = _view * _proj;
            Matrix invView = Matrix.Invert(_view);
            Matrix invProj = Matrix.Invert(_proj);
            Matrix invViewProj = Matrix.Invert(viewProj);

            _mainPassCB.View = Matrix.Transpose(_view);
            _mainPassCB.InvView = Matrix.Transpose(invView);
            _mainPassCB.Proj = Matrix.Transpose(_proj);
            _mainPassCB.InvProj = Matrix.Transpose(invProj);
            _mainPassCB.ViewProj = Matrix.Transpose(viewProj);
            _mainPassCB.InvViewProj = Matrix.Transpose(invViewProj);
            _mainPassCB.EyePosW = _eyePos;
            _mainPassCB.RenderTargetSize = new Vector2(ClientWidth, ClientHeight);
            _mainPassCB.InvRenderTargetSize = 1.0f / _mainPassCB.RenderTargetSize;
            _mainPassCB.NearZ = 1.0f;
            _mainPassCB.FarZ = 1000.0f;
            _mainPassCB.TotalTime = gt.TotalTime;
            _mainPassCB.DeltaTime = gt.DeltaTime;
            _mainPassCB.AmbientLight = new Vector4(0.25f, 0.25f, 0.35f, 1.0f);
            _mainPassCB.Lights.Light1.Direction = new Vector3(0.57735f, -0.57735f, 0.57735f);
            _mainPassCB.Lights.Light1.Strength = new Vector3(0.9f);
            _mainPassCB.Lights.Light2.Direction = new Vector3(-0.57735f, -0.57735f, 0.57735f);
            _mainPassCB.Lights.Light2.Strength = new Vector3(0.5f);
            _mainPassCB.Lights.Light3.Direction = new Vector3(0.0f, -0.707f, -0.707f);
            _mainPassCB.Lights.Light3.Strength = new Vector3(0.2f);            

            CurrFrameResource.PassCB.CopyData(0, ref _mainPassCB);
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
                slotRootParameters,
                GetStaticSamplers());

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

        private void BuildFrameResources()
        {
            for (int i = 0; i < NumFrameResources; i++)
            {
                _frameResources.Add(new FrameResource(Device, 1));
                _fenceEvents.Add(new AutoResetEvent(false));
            }
        }

        // Applications usually only need a handful of samplers. So just define them all up front
        // and keep them available as part of the root signature.
        private static StaticSamplerDescription[] GetStaticSamplers() => new[]
        {
            // PointWrap
            new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap
            },
            // PointClamp
            new StaticSamplerDescription(ShaderVisibility.All, 1, 0)
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp
            },
            // LinearWrap
            new StaticSamplerDescription(ShaderVisibility.All, 2, 0)
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap
            },
            // LinearClamp
            new StaticSamplerDescription(ShaderVisibility.All, 3, 0)
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp
            },
            // AnisotropicWrap
            new StaticSamplerDescription(ShaderVisibility.All, 4, 0)
            {
                Filter = Filter.Anisotropic,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                MipLODBias = 0.0f,
                MaxAnisotropy = 8
            },
            // AnisotropicClamp
            new StaticSamplerDescription(ShaderVisibility.All, 5, 0)
            {
                Filter = Filter.Anisotropic,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MipLODBias = 0.0f,
                MaxAnisotropy = 8
            }
        };
    }
}
