using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D12;
using SharpDX.DXGI;

namespace DX12GameProgramming
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct Vertex
    {
        public Vector3 Pos;
        public Vector4 Color;
    }

    struct ObjectConstants
    {
        public Matrix WorldViewProj;
    }

    class BoxApp : D3DApp
    {
        private RootSignature _rootSignature;
        private DescriptorHeap _cbvHeap;
        private DescriptorHeap[] _descriptorHeaps;

        private UploadBuffer<ObjectConstants> _objectCB;

        private MeshGeometry _boxGeo;

        private ShaderBytecode _mvsByteCode;
        private ShaderBytecode _mpsByteCode;

        private InputLayoutDescription _inputLayout;

        private PipelineState _pso;

        private Matrix _proj = Matrix.Identity;
        private Matrix _view = Matrix.Identity;

        private float _theta = 1.5f * MathUtil.Pi;
        private float _phi = MathUtil.PiOverFour;
        private float _radius = 5.0f;

        private Point _lastMousePos;

        public BoxApp(IntPtr hInstance) : base(hInstance)
        {
            MainWindowCaption = "Box";
        }

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            BuildDescriptorHeaps();
            BuildConstantBuffers();
            BuildRootSignature();
            BuildShadersAndInputLayout();
            BuildBoxGeometry();
            BuildPSO();

            // Execute the initialization commands.
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            // Wait until initialization is complete.
            FlushCommandQueue();
        }

        protected override void OnResize()
        {
            base.OnResize();

            // The window resized, so update the aspect ratio and recompute the projection matrix.
            _proj = Matrix.PerspectiveFovLH(MathUtil.PiOverFour, AspectRatio, 1.0f, 1000.0f);
        }

        protected override void Update(GameTimer gt)
        {
            // Convert Spherical to Cartesian coordinates.
            float x = _radius * MathHelper.Sinf(_phi) * MathHelper.Cosf(_theta);
            float z = _radius * MathHelper.Sinf(_phi) * MathHelper.Sinf(_theta);
            float y = _radius * MathHelper.Cosf(_phi);

            // Build the view matrix.
            _view = Matrix.LookAtLH(new Vector3(x, y, z), Vector3.Zero, Vector3.Up);

            // Simply use identity for world matrix for this demo.
            Matrix world = Matrix.Identity;

            var cb = new ObjectConstants
            {
                WorldViewProj = Matrix.Transpose(world * _view * _proj)
            };

            // Update the constant buffer with the latest worldViewProj matrix.
            _objectCB.CopyData(0, ref cb);
        }

        protected override void Draw(GameTimer gt)
        {
            // Reuse the memory associated with command recording.
            // We can only reset when the associated command lists have finished execution on the GPU.
            DirectCmdListAlloc.Reset();

            // A command list can be reset after it has been added to the command queue via ExecuteCommandList.
            // Reusing the command list reuses memory.
            CommandList.Reset(DirectCmdListAlloc, _pso);

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);                       

            // Clear the back buffer and depth buffer.
            CommandList.ClearRenderTargetView(CurrentBackBufferView, Color.LightSteelBlue);
            CommandList.ClearDepthStencilView(CurrentDepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.            
            CommandList.SetRenderTargets(CurrentBackBufferView, CurrentDepthStencilView);

            CommandList.SetDescriptorHeaps(_descriptorHeaps.Length, _descriptorHeaps); // TODO: rename descriptorHeapsOut; setdescriptorheap?

            CommandList.SetGraphicsRootSignature(_rootSignature);

            CommandList.SetVertexBuffer(0, _boxGeo.VertexBufferView);
            CommandList.SetIndexBuffer(_boxGeo.IndexBufferView);
            CommandList.PrimitiveTopology = PrimitiveTopology.TriangleList;

            CommandList.SetGraphicsRootDescriptorTable(0, _cbvHeap.GPUDescriptorHandleForHeapStart);

            CommandList.DrawIndexedInstanced(_boxGeo.IndexCount, 1, 0, 0, 0);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

            // Done recording commands.
            CommandList.Close();

            // Add the command list to the queue for execution.
            CommandQueue.ExecuteCommandList(CommandList);

            // Present the buffer to the screen. Presenting will automatically swap the back and front buffers.
            SwapChain.Present(0, PresentFlags.None);

            // Wait until frame commands are complete. This waiting is inefficient and is
            // done for simplicity. Later we will show how to organize our rendering code
            // so we do not have to wait per frame.
            FlushCommandQueue();
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
                float dx = 0.005f * (location.X - _lastMousePos.X);
                float dy = 0.005f * (location.Y - _lastMousePos.Y);

                // Update the camera radius based on input.
                _radius += dx - dy;

                // Restrict the radius.
                _radius = MathUtil.Clamp(_radius, 3.0f, 15.0f);
            }

            _lastMousePos = location;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rootSignature.Dispose();
                _cbvHeap.Dispose();
                _objectCB.Dispose();             
                _boxGeo.Dispose();
                _pso.Dispose();
            }

            base.Dispose(disposing);
        }

        private void BuildDescriptorHeaps()
        {
            var cbvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = 1,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible,
                NodeMask = 0
            };
            _cbvHeap = Device.CreateDescriptorHeap(cbvHeapDesc);
            _descriptorHeaps = new[] { _cbvHeap };
        }

        private void BuildConstantBuffers()
        {
            int sizeInBytes = D3DUtil.CalcConstantBufferByteSize<ObjectConstants>();

            _objectCB = new UploadBuffer<ObjectConstants>(Device, 1, true);

            var cbvDesc = new ConstantBufferViewDescription
            {
                BufferLocation = _objectCB.Resource.GPUVirtualAddress,
                SizeInBytes = sizeInBytes
            };
            CpuDescriptorHandle cbvHeapHandle = _cbvHeap.CPUDescriptorHandleForHeapStart;
            Device.CreateConstantBufferView(cbvDesc, cbvHeapHandle);
        }

        private void BuildRootSignature()
        {
            // Shader programs typically require resources as input (constant buffers,
            // textures, samplers). The root signature defines the resources the shader
            // programs expect. If we think of the shader programs as a function, and
            // the input resources as function parameters, then the root signature can be
            // thought of as defining the function signature.

            // Root parameter can be a table, root descriptor or root constants.

            // Create a single descriptor table of CBVs.
            var cbvTable = new DescriptorRange(DescriptorRangeType.ConstantBufferView, 1, 0);

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout, new[]
            {
                new RootParameter(ShaderVisibility.Vertex, cbvTable)
            });

            _rootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildShadersAndInputLayout()
        {
            _mvsByteCode = D3DUtil.CompileShader("Shaders\\Color.hlsl", "VS", "vs_5_0");
            _mpsByteCode = D3DUtil.CompileShader("Shaders\\Color.hlsl", "PS", "ps_5_0");

            _inputLayout = new InputLayoutDescription(new [] // TODO: Add params overload
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0)
            });
        }

        private void BuildBoxGeometry()
        {
            Vertex[] vertices =
            {
                new Vertex { Pos = new Vector3(-1.0f, -1.0f, -1.0f), Color = Color.White.ToVector4() },
                new Vertex { Pos = new Vector3(-1.0f, +1.0f, -1.0f), Color = Color.Black.ToVector4() },
                new Vertex { Pos = new Vector3(+1.0f, +1.0f, -1.0f), Color = Color.Red.ToVector4() },
                new Vertex { Pos = new Vector3(+1.0f, -1.0f, -1.0f), Color = Color.Green.ToVector4() },
                new Vertex { Pos = new Vector3(-1.0f, -1.0f, +1.0f), Color = Color.Blue.ToVector4() },
                new Vertex { Pos = new Vector3(-1.0f, +1.0f, +1.0f), Color = Color.Yellow.ToVector4() },
                new Vertex { Pos = new Vector3(+1.0f, +1.0f, +1.0f), Color = Color.Cyan.ToVector4() },
                new Vertex { Pos = new Vector3(+1.0f, -1.0f, +1.0f), Color = Color.Magenta.ToVector4() }
            };

            short[] indices =
            {
                // front face
		        0, 1, 2,
                0, 2, 3,

		        // back face
		        4, 6, 5,
                4, 7, 6,

		        // left face
		        4, 5, 1,
                4, 1, 0,

		        // right face
		        3, 2, 6,
                3, 6, 7,

		        // top face
		        1, 5, 6,
                1, 6, 2,

		        // bottom face
		        4, 0, 3,
                4, 3, 7
            };            

            _boxGeo = MeshGeometry.New(Device, CommandList, vertices, indices);
        }

        private void BuildPSO()
        {
            var psoDesc = new GraphicsPipelineStateDescription
            {
                InputLayout = _inputLayout,
                RootSignature = _rootSignature,
                VertexShader = _mvsByteCode,
                PixelShader = _mpsByteCode,
                RasterizerState = RasterizerStateDescription.Default(),
                BlendState = BlendStateDescription.Default(),
                DepthStencilState = DepthStencilStateDescription.Default(),
                SampleMask = int.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetCount = 1,
                SampleDescription = new SampleDescription(MsaaCount, MsaaQuality),
                DepthStencilFormat = DepthStencilFormat
            };
            psoDesc.RenderTargetFormats[0] = BackBufferFormat;

            _pso = Device.CreateGraphicsPipelineState(psoDesc);
        }        
    }
}
