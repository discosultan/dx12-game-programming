using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    public class ShapesApp : D3DApp
    {
        private readonly List<FrameResource> _frameResources = new List<FrameResource>(NumFrameResources);
        private readonly List<AutoResetEvent> _fenceEvents = new List<AutoResetEvent>(NumFrameResources);
        private int _currFrameResourceIndex;

        private RootSignature _rootSignature;
        private DescriptorHeap _cbvHeap;
        private DescriptorHeap[] _descriptorHeaps;

        private readonly Dictionary<string, MeshGeometry> _geometries = new Dictionary<string, MeshGeometry>();
        private readonly Dictionary<string, ShaderBytecode> _shaders = new Dictionary<string, ShaderBytecode>();
        private readonly Dictionary<string, PipelineState> _psos = new Dictionary<string, PipelineState>();

        private InputLayoutDescription _inputLayout;

        // List of all the render items.
        private readonly List<RenderItem> _allRitems = new List<RenderItem>();

        // Render items divided by PSO.
        private readonly Dictionary<RenderLayer, List<RenderItem>> _ritemLayers = new Dictionary<RenderLayer, List<RenderItem>>(1)
        {
            [RenderLayer.Opaque] = new List<RenderItem>()
        };

        private PassConstants _mainPassCB;

        private int _passCbvOffset;

        private bool _isWireframe = true;

        private Vector3 _eyePos;
        private Matrix _proj = Matrix.Identity;
        private Matrix _view = Matrix.Identity;

        private float _theta = 1.5f * MathUtil.Pi;
        private float _phi = 0.2f * MathUtil.Pi;
        private float _radius = 15.0f;

        private Point _lastMousePos;

        public ShapesApp()
        {
            MainWindowCaption = "Shapes";
        }

        private FrameResource CurrFrameResource => _frameResources[_currFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => _fenceEvents[_currFrameResourceIndex];

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            BuildRootSignature();
            BuildShadersAndInputLayout();
            BuildShapeGeometry();
            BuildRenderItems();
            BuildFrameResources();
            BuildDescriptorHeaps();
            BuildConstantBufferViews();
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

            CommandList.SetDescriptorHeaps(_descriptorHeaps.Length, _descriptorHeaps);

            CommandList.SetGraphicsRootSignature(_rootSignature);

            int passCbvIndex = _passCbvOffset + _currFrameResourceIndex;
            GpuDescriptorHandle passCbvHandle = _cbvHeap.GPUDescriptorHandleForHeapStart;
            passCbvHandle += passCbvIndex * CbvSrvUavDescriptorSize;
            CommandList.SetGraphicsRootDescriptorTable(1, passCbvHandle);

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
                float dx = 0.05f * (location.X - _lastMousePos.X);
                float dy = 0.05f * (location.Y - _lastMousePos.Y);

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
                _isWireframe = false;
        }

        protected override void OnKeyUp(Keys keyCode)
        {
            base.OnKeyUp(keyCode);
            if (keyCode == Keys.D1)
                _isWireframe = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rootSignature?.Dispose();
                _cbvHeap?.Dispose();
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

        private void BuildDescriptorHeaps()
        {
            int objCount = _allRitems.Count;

            // Need a CBV descriptor for each object for each frame resource,
            // +1 for the perPass CBV for each frame resource.
            int numDescriptors = (objCount + 1) * NumFrameResources;

            // Save an offset to the start of the pass CBVs.  These are the last 3 descriptors.
            _passCbvOffset = objCount * NumFrameResources;

            var cbvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = numDescriptors,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible
            };
            _cbvHeap = Device.CreateDescriptorHeap(cbvHeapDesc);
            _descriptorHeaps = new[] { _cbvHeap };
        }

        private void BuildConstantBufferViews()
        {
            int objCBByteSize = D3DUtil.CalcConstantBufferByteSize<ObjectConstants>();

            int objCount = _allRitems.Count;

            // Need a CBV descriptor for each object for each frame resource.
            for (int frameIndex = 0; frameIndex < NumFrameResources; frameIndex++)
            {
                Resource objectCB = _frameResources[frameIndex].ObjectCB.Resource;
                for (int i = 0; i < objCount; i++)
                {
                    long cbAddress = objectCB.GPUVirtualAddress;

                    // Offset to the ith object constant buffer in the buffer.
                    cbAddress += i * objCBByteSize;

                    // Offset to the object cbv in the descriptor heap.
                    int heapIndex = frameIndex * objCount + i;
                    CpuDescriptorHandle handle = _cbvHeap.CPUDescriptorHandleForHeapStart;
                    handle += heapIndex * CbvSrvUavDescriptorSize;

                    var cbvDesc = new ConstantBufferViewDescription
                    {
                        BufferLocation = cbAddress,
                        SizeInBytes = objCBByteSize
                    };

                    Device.CreateConstantBufferView(cbvDesc, handle);
                }
            }

            int passCBByteSize = D3DUtil.CalcConstantBufferByteSize<PassConstants>();

            // Last three descriptors are the pass CBVs for each frame resource.
            for (int frameIndex = 0; frameIndex < NumFrameResources; frameIndex++)
            {
                Resource passCB = _frameResources[frameIndex].PassCB.Resource;
                long cbAddress = passCB.GPUVirtualAddress;

                // Offset to the pass cbv in the descriptor heap.
                int heapIndex = _passCbvOffset + frameIndex;
                CpuDescriptorHandle handle = _cbvHeap.CPUDescriptorHandleForHeapStart;
                handle += heapIndex * CbvSrvUavDescriptorSize;

                var cbvDesc = new ConstantBufferViewDescription
                {
                    BufferLocation = cbAddress,
                    SizeInBytes = passCBByteSize
                };

                Device.CreateConstantBufferView(cbvDesc, handle);
            }
        }

        private void BuildRootSignature()
        {
            var cbvTable0 = new DescriptorRange(DescriptorRangeType.ConstantBufferView, 1, 0);
            var cbvTable1 = new DescriptorRange(DescriptorRangeType.ConstantBufferView, 1, 1);

            // Root parameter can be a table, root descriptor or root constants.
            var slotRootParameters = new[]
            {
                new RootParameter(ShaderVisibility.Vertex, cbvTable0),
                new RootParameter(ShaderVisibility.Vertex, cbvTable1)
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

        private void BuildShapeGeometry()
        {
            //
            // We are concatenating all the geometry into one big vertex/index buffer. So
            // define the regions in the buffer each submesh covers.
            //

            var vertices = new List<Vertex>();
            var indices = new List<short>();

            SubmeshGeometry box = AppendMeshData(GeometryGenerator.CreateBox(1.5f, 0.5f, 1.5f, 3), Color.DarkGreen, vertices, indices);
            SubmeshGeometry grid = AppendMeshData(GeometryGenerator.CreateGrid(20.0f, 30.0f, 60, 40), Color.ForestGreen, vertices,indices);
            SubmeshGeometry sphere = AppendMeshData(GeometryGenerator.CreateSphere(0.5f, 20, 20), Color.Crimson, vertices, indices);
            SubmeshGeometry cylinder = AppendMeshData(GeometryGenerator.CreateCylinder(0.5f, 0.3f, 3.0f, 20, 20), Color.SteelBlue, vertices, indices);

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices.ToArray(), "shapeGeo");

            geo.DrawArgs["box"] = box;
            geo.DrawArgs["grid"] = grid;
            geo.DrawArgs["sphere"] = sphere;
            geo.DrawArgs["cylinder"] = cylinder;

            _geometries[geo.Name] = geo;
        }

        private SubmeshGeometry AppendMeshData(GeometryGenerator.MeshData meshData, Color color, List<Vertex> vertices, List<short> indices)
        {
            //
            // Define the SubmeshGeometry that cover different
            // regions of the vertex/index buffers.
            //

            var submesh = new SubmeshGeometry
            {
                IndexCount = meshData.Indices32.Count,
                StartIndexLocation = indices.Count,
                BaseVertexLocation = vertices.Count
            };

            //
            // Extract the vertex elements we are interested in and pack the
            // vertices and indices of all the meshes into one vertex/index buffer.
            //

            vertices.AddRange(meshData.Vertices.Select(vertex => new Vertex
            {
                Pos = vertex.Position,
                Color = color.ToVector4()
            }));
            indices.AddRange(meshData.GetIndices16());

            return submesh;
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
                _frameResources.Add(new FrameResource(Device, 1, _allRitems.Count));
                _fenceEvents.Add(new AutoResetEvent(false));
            }
        }

        private void BuildRenderItems()
        {
            AddRenderItem(RenderLayer.Opaque, 0, "shapeGeo", "box",
                world: Matrix.Scaling(2.0f, 2.0f, 2.0f) * Matrix.Translation(0.0f, 0.5f, 0.0f));
            AddRenderItem(RenderLayer.Opaque, 1, "shapeGeo", "grid");

            int objCBIndex = 2;
            for (int i = 0; i < 5; ++i)
            {
                AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "cylinder",
                    world: Matrix.Translation(-5.0f, 1.5f, -10.0f + i * 5.0f));
                AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "cylinder",
                    world: Matrix.Translation(+5.0f, 1.5f, -10.0f + i * 5.0f));

                AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "sphere",
                    world: Matrix.Translation(-5.0f, 3.5f, -10.0f + i * 5.0f));
                AddRenderItem(RenderLayer.Opaque, objCBIndex++, "shapeGeo", "sphere",
                    world: Matrix.Translation(+5.0f, 3.5f, -10.0f + i * 5.0f));
            }
        }

        private void AddRenderItem(RenderLayer layer, int objCBIndex, string geoName, string submeshName, Matrix? world = null)
        {
            MeshGeometry geo = _geometries[geoName];
            SubmeshGeometry submesh = geo.DrawArgs[submeshName];
            var renderItem = new RenderItem
            {
                ObjCBIndex = objCBIndex,
                Geo = geo,
                IndexCount = submesh.IndexCount,
                StartIndexLocation = submesh.StartIndexLocation,
                BaseVertexLocation = submesh.BaseVertexLocation,
                World = world ?? Matrix.Identity
            };
            _ritemLayers[layer].Add(renderItem);
            _allRitems.Add(renderItem);
        }

        private void DrawRenderItems(GraphicsCommandList cmdList, List<RenderItem> ritems)
        {
            // For each render item...
            foreach (RenderItem ri in ritems)
            {
                cmdList.SetVertexBuffer(0, ri.Geo.VertexBufferView);
                cmdList.SetIndexBuffer(ri.Geo.IndexBufferView);
                cmdList.PrimitiveTopology = ri.PrimitiveType;

                // Offset to the CBV in the descriptor heap for this object and for this frame resource.
                int cbvIndex = _currFrameResourceIndex * _allRitems.Count + ri.ObjCBIndex;
                GpuDescriptorHandle cbvHandle = _cbvHeap.GPUDescriptorHandleForHeapStart;
                cbvHandle += cbvIndex * CbvSrvUavDescriptorSize;

                cmdList.SetGraphicsRootDescriptorTable(0, cbvHandle);

                cmdList.DrawIndexedInstanced(ri.IndexCount, 1, ri.StartIndexLocation, ri.BaseVertexLocation, 0);
            }
        }
    }
}
