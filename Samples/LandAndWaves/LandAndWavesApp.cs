using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    public class LandAndWavesApp : D3DApp
    {
        private readonly List<FrameResource> _frameResources = new List<FrameResource>(NumFrameResources);
        private readonly List<AutoResetEvent> _fenceEvents = new List<AutoResetEvent>(NumFrameResources);
        private int _currFrameResourceIndex;

        private RootSignature _rootSignature;

        private readonly Dictionary<string, MeshGeometry> _geometries = new Dictionary<string, MeshGeometry>();
        private readonly Dictionary<string, ShaderBytecode> _shaders = new Dictionary<string, ShaderBytecode>();
        private readonly Dictionary<string, PipelineState> _psos = new Dictionary<string, PipelineState>();

        private InputLayoutDescription _inputLayout;

        private RenderItem _wavesRitem;

        // List of all the render items.
        private readonly List<RenderItem> _allRitems = new List<RenderItem>();

        // Render items divided by PSO.
        private readonly Dictionary<RenderLayer, List<RenderItem>> _ritemLayers = new Dictionary<RenderLayer, List<RenderItem>>(1)
        {
            [RenderLayer.Opaque] = new List<RenderItem>()
        };

        private Waves _waves;

        private PassConstants _mainPassCB;

        private bool _isWireframe;

        private Vector3 _eyePos;
        private Matrix _proj = Matrix.Identity;
        private Matrix _view = Matrix.Identity;

        private float _theta = 1.5f * MathUtil.Pi;
        private float _phi = MathUtil.PiOverTwo - 0.1f;
        private float _radius = 50.0f;

        private float _tBase;

        private Point _lastMousePos;

        public LandAndWavesApp(IntPtr hInstance) : base(hInstance)
        {
            MainWindowCaption = "Land and Waves";
        }

        private FrameResource CurrFrameResource => _frameResources[_currFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => _fenceEvents[_currFrameResourceIndex];

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            _waves = new Waves(128, 128, 1.0f, 0.03f, 4.0f, 0.2f);

            BuildRootSignature();
            BuildShadersAndInputLayout();
            BuildLandGeometry();
            BuildWavesGeometry();
            BuildRenderItems();
            BuildFrameResources();
            BuildPSOs();

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
            UpdateCamera();

            // Cycle through the circular frame resource array.
            _currFrameResourceIndex = (_currFrameResourceIndex + 1) % NumFrameResources;

            // Has the GPU finished processing the commands of the current frame resource?
            // If not, wait until the GPU has completed commands up to this fence point.
            if (CurrFrameResource.Fence != 0 && Fence.CompletedValue < CurrFrameResource.Fence)
            {
                Fence.SetEventOnCompletion(CurrFrameResource.Fence, CurrentFenceEvent.SafeWaitHandle.DangerousGetHandle());
                CurrentFenceEvent.WaitOne();
            }

            UpdateObjectCBs();
            UpdateMainPassCB(gt);
            UpdateWaves(gt);
        }

        protected override void Draw(GameTimer gt)
        {
            CommandAllocator cmdListAlloc = CurrFrameResource.CmdListAlloc;

            // Reuse the memory associated with command recording.
            // We can only reset when the associated command lists have finished execution on the GPU.
            cmdListAlloc.Reset();

            // A command list can be reset after it has been added to the command queue via ExecuteCommandList.
            // Reusing the command list reuses memory.
            CommandList.Reset(cmdListAlloc, _isWireframe ? _psos["opaque_wireframe"] : _psos["opaque"]);

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

            // Clear the back buffer and depth buffer.
            CommandList.ClearRenderTargetView(CurrentBackBufferView, Color.LightSteelBlue);
            CommandList.ClearDepthStencilView(DepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.            
            CommandList.SetRenderTargets(CurrentBackBufferView, DepthStencilView);

            CommandList.SetGraphicsRootSignature(_rootSignature);

            // Bind per-pass constant buffer. We only need to do this once per-pass.
            Resource passCB = CurrFrameResource.PassCB.Resource;
            CommandList.SetGraphicsRootConstantBufferView(1, passCB.GPUVirtualAddress);

            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Opaque]);

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

        protected override void OnKeyDown(Keys keyCode)
        {
            if (keyCode == Keys.D1)
                _isWireframe = true;
        }

        protected override void OnKeyUp(Keys keyCode)
        {
            base.OnKeyUp(keyCode);
            if (keyCode == Keys.D1)
                _isWireframe = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rootSignature?.Dispose();
                foreach (FrameResource frameResource in _frameResources) frameResource.Dispose();
                foreach (MeshGeometry geometry in _geometries.Values) geometry.Dispose();
                foreach (PipelineState pso in _psos.Values) pso.Dispose();
            }
            base.Dispose(disposing);
        }

        private void UpdateCamera()
        {
            // Convert Spherical to Cartesian coordinates.
            _eyePos.X = _radius * MathHelper.Sinf(_phi) * MathHelper.Cosf(_theta);
            _eyePos.Z = _radius * MathHelper.Sinf(_phi) * MathHelper.Sinf(_theta);
            _eyePos.Y = _radius * MathHelper.Cosf(_phi);

            // Build the view matrix.
            _view = Matrix.LookAtLH(_eyePos, Vector3.Zero, Vector3.Up);
        }

        private void UpdateObjectCBs()
        {
            foreach (RenderItem e in _allRitems)
            {
                // Only update the cbuffer data if the constants have changed.  
                // This needs to be tracked per frame resource. 
                if (e.NumFramesDirty > 0)
                {
                    var objConstants = new ObjectConstants { World = Matrix.Transpose(e.World) };
                    CurrFrameResource.ObjectCB.CopyData(e.ObjCBIndex, ref objConstants);

                    // Next FrameResource need to be updated too.
                    e.NumFramesDirty--;
                }
            }
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

            CurrFrameResource.PassCB.CopyData(0, ref _mainPassCB);
        }
        
        private void UpdateWaves(GameTimer gt)
        {
            // Every quarter second, generate a random wave.
            if ((Timer.TotalTime - _tBase) >= 0.25f)
            {
                _tBase += 0.25f;

                int i = MathHelper.Rand(4, _waves.RowCount - 5);
                int j = MathHelper.Rand(4, _waves.ColumnCount - 5);

                float r = MathHelper.Randf(0.2f, 0.5f);

                _waves.Disturb(i, j, r);
            }

            // Update the wave simulation.
            _waves.Update(gt.DeltaTime);

            // Update the wave vertex buffer with the new solution.
            UploadBuffer<Vertex> currWavesVB = CurrFrameResource.WavesVB;
            for (int i = 0; i < _waves.VertexCount; ++i)
            {
                var v = new Vertex
                {
                    Pos = _waves.Position(i),
                    Color = Color.Blue.ToVector4()
                };
                currWavesVB.CopyData(i, ref v);
            }

            // Set the dynamic VB of the wave renderitem to the current frame VB.            
            _wavesRitem.Geo.VertexBufferGPU = currWavesVB.Resource;
        }

        private void BuildRootSignature()
        {
            // Root parameter can be a table, root descriptor or root constants.
            var slotRootParameters = new[]
            {
                // TODO: Register space default value = 0
                new RootParameter(ShaderVisibility.Vertex, new RootDescriptor(0, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.Vertex, new RootDescriptor(1, 0), RootParameterType.ConstantBufferView)
            };

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                slotRootParameters);

            // Create a root signature with a single slot which points to a descriptor range consisting of a single constant buffer.
            _rootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildShadersAndInputLayout()
        {
            _shaders["standardVS"] = D3DUtil.CompileShader("Shaders\\Color.hlsl", "VS", "vs_5_0");
            _shaders["opaquePS"] = D3DUtil.CompileShader("Shaders\\Color.hlsl", "PS", "ps_5_0");

            _inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0)
            });
        }

        private void BuildLandGeometry()
        {
            GeometryGenerator.MeshData grid = GeometryGenerator.CreateGrid(160.0f, 160.0f, 50, 50);

            //
            // Extract the vertex elements we are interested and apply the height function to
            // each vertex. In addition, color the vertices based on their height so we have
            // sandy looking beaches, grassy low hills, and snow mountain peaks.
            //

            var vertices = new Vertex[grid.Vertices.Count];
            for (int i = 0; i < grid.Vertices.Count; i++)
            {
                Vector3 p = grid.Vertices[i].Position;
                vertices[i].Pos = p;
                vertices[i].Pos.Y = GetHillsHeight(p.X, p.Z);

                // Color the vertex based on its height.
                if (vertices[i].Pos.Y < -10.0f)
                {
                    // Sandy beach color.
                    vertices[i].Color = new Vector4(1.0f, 0.96f, 0.62f, 1.0f);
                }
                else if (vertices[i].Pos.Y < 5.0f)
                {
                    // Light yellow-green.
                    vertices[i].Color = new Vector4(0.48f, 0.77f, 0.46f, 1.0f);
                }
                else if (vertices[i].Pos.Y < 12.0f)
                {
                    // Dark yellow-green.
                    vertices[i].Color = new Vector4(0.1f, 0.48f, 0.19f, 1.0f);
                }
                else if (vertices[i].Pos.Y < 20.0f)
                {
                    // Dark brown.
                    vertices[i].Color = new Vector4(0.45f, 0.39f, 0.34f, 1.0f);
                }
                else
                {
                    // White snow.
                    vertices[i].Color = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
                }
            }

            List<short> indices = grid.GetIndices16();

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices.ToArray(), "landGeo");

            var submesh = new SubmeshGeometry
            {
                IndexCount = indices.Count,
                StartIndexLocation = 0,
                BaseVertexLocation = 0
            };

            geo.DrawArgs["grid"] = submesh;

            _geometries["landGeo"] = geo;
        }

        private void BuildWavesGeometry()
        {
            var indices = new short[3 * _waves.TriangleCount]; // 3 indices per face.
            Debug.Assert(_waves.VertexCount < 0x0000ffff);

            // Iterate over each quad.
            int m = _waves.RowCount;
            int n = _waves.ColumnCount;
            int k = 0;
            for (int i = 0; i < m - 1; ++i)
            {
                for (int j = 0; j < n - 1; ++j)
                {
                    indices[k + 0] = (short)(i * n + j);
                    indices[k + 1] = (short)(i * n + j + 1);
                    indices[k + 2] = (short)((i + 1) * n + j);

                    indices[k + 3] = (short)((i + 1) * n + j);
                    indices[k + 4] = (short)(i * n + j + 1);
                    indices[k + 5] = (short)((i + 1) * n + j + 1);

                    k += 6; // Next quad.
                }
            }

            // Vertices are set dynamically.
            var geo = MeshGeometry.New(Device, CommandList, indices, "waterGeo");
            geo.VertexByteStride = Utilities.SizeOf<Vertex>();
            geo.VertexBufferByteSize = geo.VertexByteStride * _waves.VertexCount;

            var submesh = new SubmeshGeometry
            {
                IndexCount = indices.Length,
                StartIndexLocation = 0,
                BaseVertexLocation = 0
            };

            geo.DrawArgs["grid"] = submesh;

            _geometries["waterGeo"] = geo;
        }

        private void BuildPSOs()
        {
            //
            // PSO for opaque objects.
            //

            var opaquePsoDesc = new GraphicsPipelineStateDescription
            {
                InputLayout = _inputLayout,
                RootSignature = _rootSignature,
                VertexShader = _shaders["standardVS"],
                PixelShader = _shaders["opaquePS"],
                RasterizerState = RasterizerStateDescription.Default(),
                BlendState = BlendStateDescription.Default(),
                DepthStencilState = DepthStencilStateDescription.Default(),
                SampleMask = int.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetCount = 1,
                SampleDescription = new SampleDescription(MsaaCount, MsaaQuality),
                DepthStencilFormat = DepthStencilFormat
            };
            opaquePsoDesc.RenderTargetFormats[0] = BackBufferFormat;

            _psos["opaque"] = Device.CreateGraphicsPipelineState(opaquePsoDesc);

            //
            // PSO for opaque wireframe objects.
            //

            var opaqueWireframePsoDesc = opaquePsoDesc;
            opaqueWireframePsoDesc.RasterizerState.FillMode = FillMode.Wireframe;

            _psos["opaque_wireframe"] = Device.CreateGraphicsPipelineState(opaqueWireframePsoDesc);
        }

        private void BuildFrameResources()
        {
            for (int i = 0; i < NumFrameResources; i++)
            {
                _frameResources.Add(new FrameResource(Device, 1, _allRitems.Count, _waves.VertexCount));
                _fenceEvents.Add(new AutoResetEvent(false));
            }
        }

        private void BuildRenderItems()
        {
            _wavesRitem = new RenderItem();
            _wavesRitem.World = Matrix.Identity;
            _wavesRitem.ObjCBIndex = 0;
            _wavesRitem.Geo = _geometries["waterGeo"];
            _wavesRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            _wavesRitem.IndexCount = _wavesRitem.Geo.DrawArgs["grid"].IndexCount;
            _wavesRitem.StartIndexLocation = _wavesRitem.Geo.DrawArgs["grid"].StartIndexLocation;
            _wavesRitem.BaseVertexLocation = _wavesRitem.Geo.DrawArgs["grid"].BaseVertexLocation;
            _ritemLayers[RenderLayer.Opaque].Add(_wavesRitem);
            _allRitems.Add(_wavesRitem);

            var gridRitem = new RenderItem();
            gridRitem.World = Matrix.Identity;
            gridRitem.ObjCBIndex = 1;
            gridRitem.Geo = _geometries["landGeo"];
            gridRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            gridRitem.IndexCount = gridRitem.Geo.DrawArgs["grid"].IndexCount;
            gridRitem.StartIndexLocation = gridRitem.Geo.DrawArgs["grid"].StartIndexLocation;
            gridRitem.BaseVertexLocation = gridRitem.Geo.DrawArgs["grid"].BaseVertexLocation;
            _ritemLayers[RenderLayer.Opaque].Add(gridRitem);
            _allRitems.Add(gridRitem);
        }

        private void DrawRenderItems(GraphicsCommandList cmdList, List<RenderItem> ritems)
        {
            int objCBByteSize = D3DUtil.CalcConstantBufferByteSize<ObjectConstants>();
            Resource objectCB = CurrFrameResource.ObjectCB.Resource;

            foreach (RenderItem ri in ritems)
            {
                cmdList.SetVertexBuffer(0, ri.Geo.VertexBufferView);
                cmdList.SetIndexBuffer(ri.Geo.IndexBufferView);
                cmdList.PrimitiveTopology = ri.PrimitiveType;

                long objCBAddress = objectCB.GPUVirtualAddress + ri.ObjCBIndex * objCBByteSize;
                cmdList.SetGraphicsRootConstantBufferView(0, objCBAddress);

                cmdList.DrawIndexedInstanced(ri.IndexCount, 1, ri.StartIndexLocation, ri.BaseVertexLocation, 0);
            }
        }

        private static float GetHillsHeight(float x, float z) => 0.3f * (z * MathHelper.Sinf(0.1f * x) + x * MathHelper.Cosf(0.1f * z));
    }
}
