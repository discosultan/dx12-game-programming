using System;
using System.Collections.Generic;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Resource = SharpDX.Direct3D12.Resource;
using ShaderResourceViewDimension = SharpDX.Direct3D12.ShaderResourceViewDimension;

namespace DX12GameProgramming
{
    public class CameraAndDynamicIndexingApp : D3DApp
    {
        private readonly List<FrameResource> _frameResources = new List<FrameResource>(NumFrameResources);
        private readonly List<AutoResetEvent> _fenceEvents = new List<AutoResetEvent>(NumFrameResources);
        private int _currFrameResourceIndex;

        private RootSignature _rootSignature;

        private DescriptorHeap _srvDescriptorHeap;
        private DescriptorHeap[] _descriptorHeaps;

        private readonly Dictionary<string, MeshGeometry> _geometries = new Dictionary<string, MeshGeometry>();
        private readonly Dictionary<string, Material> _materials = new Dictionary<string, Material>();
        private readonly Dictionary<string, Texture> _textures = new Dictionary<string, Texture>();
        private readonly Dictionary<string, ShaderBytecode> _shaders = new Dictionary<string, ShaderBytecode>();
        private readonly Dictionary<string, PipelineState> _psos = new Dictionary<string, PipelineState>();

        private InputLayoutDescription _inputLayout;

        // List of all the render items.
        private readonly List<RenderItem> _allRitems = new List<RenderItem>();

        // Render items divided by PSO.
        private readonly List<RenderItem> _opaqueRitems = new List<RenderItem>();

        private PassConstants _mainPassCB;

        private readonly Camera _camera = new Camera();

        private Point _lastMousePos;

        public CameraAndDynamicIndexingApp(IntPtr hInstance) : base(hInstance)
        {
            MainWindowCaption = "Camera and Dynamic Indexing";
        }

        private FrameResource CurrFrameResource => _frameResources[_currFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => _fenceEvents[_currFrameResourceIndex];

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            _camera.Position = new Vector3(0.0f, 2.0f, -15.0f);

            LoadTextures();
            BuildRootSignature();
            BuildDescriptorHeaps();
            BuildShadersAndInputLayout();
            BuildShapeGeometry();            
            BuildMaterials();
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
            _camera.SetLens(MathUtil.PiOverFour, AspectRatio, 1.0f, 1000.0f);
        }

        protected override void Update(GameTimer gt)
        {
            OnKeyboardInput(gt);

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
            UpdateMaterialBuffer();
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
            CommandList.Reset(cmdListAlloc, _psos["opaque"]);

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

            // Clear the back buffer and depth buffer.
            CommandList.ClearRenderTargetView(CurrentBackBufferView, Color.LightSteelBlue);
            CommandList.ClearDepthStencilView(CurrentDepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.            
            CommandList.SetRenderTargets(CurrentBackBufferView, CurrentDepthStencilView);

            CommandList.SetDescriptorHeaps(_descriptorHeaps.Length, _descriptorHeaps);

            CommandList.SetGraphicsRootSignature(_rootSignature);

            Resource passCB = CurrFrameResource.PassCB.Resource;
            CommandList.SetGraphicsRootConstantBufferView(1, passCB.GPUVirtualAddress);

            // Bind all the materials used in this scene. For structured buffers, we can bypass the heap and 
            // set as a root descriptor.
            Resource matBuffer = CurrFrameResource.MaterialBuffer.Resource;
            CommandList.SetGraphicsRootShaderResourceView(2, matBuffer.GPUVirtualAddress);

            // Bind all the textures used in this scene. Observe
            // that we only have to specify the first descriptor in the table. 
            // The root signature knows how many descriptors are expected in the table.
            CommandList.SetGraphicsRootDescriptorTable(3, _srvDescriptorHeap.GPUDescriptorHandleForHeapStart);

            DrawRenderItems(CommandList, _opaqueRitems);

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

                _camera.Pitch(dy);
                _camera.RotateY(dx);
            }

            _lastMousePos = location;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Texture texture in _textures.Values) texture.Dispose();
                foreach (FrameResource frameResource in _frameResources) frameResource.Dispose();
                _rootSignature.Dispose();
                foreach (MeshGeometry geometry in _geometries.Values) geometry.Dispose();
                foreach (PipelineState pso in _psos.Values) pso.Dispose();
            }
            base.Dispose(disposing);
        }

        private void OnKeyboardInput(GameTimer gt)
        {
            float dt = gt.DeltaTime;

            if (IsKeyDown(Keys.W))
                _camera.Walk(10.0f * dt);
            if (IsKeyDown(Keys.S))
                _camera.Walk(-10.0f * dt);
            if (IsKeyDown(Keys.A))
                _camera.Strafe(-10.0f * dt);
            if (IsKeyDown(Keys.D))
                _camera.Strafe(10.0f * dt);

            _camera.UpdateViewMatrix();
        }

        private void UpdateObjectCBs()
        {
            foreach (RenderItem e in _allRitems)
            {
                // Only update the cbuffer data if the constants have changed.  
                // This needs to be tracked per frame resource. 
                if (e.NumFramesDirty > 0)
                {
                    var objConstants = new ObjectConstants
                    {
                        World = Matrix.Transpose(e.World),
                        TexTransform = Matrix.Transpose(e.TexTransform),
                        MaterialIndex = e.Mat.MatCBIndex
                    };
                    CurrFrameResource.ObjectCB.CopyData(e.ObjCBIndex, ref objConstants);

                    // Next FrameResource need to be updated too.
                    e.NumFramesDirty--;
                }
            }
        }

        private void UpdateMaterialBuffer()
        {
            UploadBuffer<MaterialData> currMaterialCB = CurrFrameResource.MaterialBuffer;
            foreach (Material mat in _materials.Values)
            {
                // Only update the cbuffer data if the constants have changed. If the cbuffer
                // data changes, it needs to be updated for each FrameResource.
                if (mat.NumFramesDirty > 0)
                {
                    var matConstants = new MaterialData
                    {
                        DiffuseAlbedo = mat.DiffuseAlbedo,
                        FresnelR0 = mat.FresnelR0,
                        Roughness = mat.Roughness,
                        MatTransform = Matrix.Transpose(mat.MatTransform),
                        DiffuseMapIndex = mat.DiffuseSrvHeapIndex
                    };

                    currMaterialCB.CopyData(mat.MatCBIndex, ref matConstants);

                    // Next FrameResource need to be updated too.
                    mat.NumFramesDirty--;
                }
            }
        }

        private void UpdateMainPassCB(GameTimer gt)
        {
            Matrix view = _camera.View;
            Matrix proj = _camera.Proj;

            Matrix viewProj = view * proj;
            Matrix invView = Matrix.Invert(view);
            Matrix invProj = Matrix.Invert(proj);
            Matrix invViewProj = Matrix.Invert(viewProj);

            _mainPassCB.View = Matrix.Transpose(view);
            _mainPassCB.InvView = Matrix.Transpose(invView);
            _mainPassCB.Proj = Matrix.Transpose(proj);
            _mainPassCB.InvProj = Matrix.Transpose(invProj);
            _mainPassCB.ViewProj = Matrix.Transpose(viewProj);
            _mainPassCB.InvViewProj = Matrix.Transpose(invViewProj);
            _mainPassCB.EyePosW = _camera.Position;
            _mainPassCB.RenderTargetSize = new Vector2(ClientWidth, ClientHeight);
            _mainPassCB.InvRenderTargetSize = 1.0f / _mainPassCB.RenderTargetSize;
            _mainPassCB.NearZ = 1.0f;
            _mainPassCB.FarZ = 1000.0f;
            _mainPassCB.TotalTime = gt.TotalTime;
            _mainPassCB.DeltaTime = gt.DeltaTime;
            _mainPassCB.AmbientLight = new Vector4(0.25f, 0.25f, 0.35f, 1.0f);
            _mainPassCB.Lights[0].Direction = new Vector3(0.57735f, -0.57735f, 0.57735f);
            _mainPassCB.Lights[0].Strength = new Vector3(0.8f);
            _mainPassCB.Lights[1].Direction = new Vector3(-0.57735f, -0.57735f, 0.57735f);
            _mainPassCB.Lights[1].Strength = new Vector3(0.4f);
            _mainPassCB.Lights[2].Direction = new Vector3(0.0f, -0.707f, -0.707f);
            _mainPassCB.Lights[2].Strength = new Vector3(0.2f);

            CurrFrameResource.PassCB.CopyData(0, ref _mainPassCB);
        }

        private void LoadTextures()
        {
            AddTexture("bricksTex", "bricks.dds");
            AddTexture("stoneTex", "stone.dds");
            AddTexture("tileTex", "tile.dds");
            AddTexture("crateTex", "WoodCrate01.dds");
        }

        private void AddTexture(string name, string filename)
        {
            var tex = new Texture
            {
                Name = name,
                Filename = $"Textures\\{filename}"
            };
            tex.Resource = TextureUtilities.CreateTextureFromDDS(Device, tex.Filename);
            _textures[tex.Name] = tex;
        }

        private void BuildRootSignature()
        {
            // Root parameter can be a table, root descriptor or root constants.
            // Perfomance TIP: Order from most frequent to least frequent.
            var slotRootParameters = new[]
            {
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(1, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 1), RootParameterType.ShaderResourceView),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 4, 0))
            };

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                slotRootParameters,
                GetStaticSamplers());

            _rootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildDescriptorHeaps()
        {
            //
            // Create the SRV heap.
            //
            var srvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = 4,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible
            };
            _srvDescriptorHeap = Device.CreateDescriptorHeap(srvHeapDesc);
            _descriptorHeaps = new[] { _srvDescriptorHeap };

            //
            // Fill out the heap with actual descriptors.
            //
            CpuDescriptorHandle hDescriptor = _srvDescriptorHeap.CPUDescriptorHandleForHeapStart;

            Resource bricksTex = _textures["bricksTex"].Resource;
            Resource stoneTex = _textures["stoneTex"].Resource;
            Resource tileTex = _textures["tileTex"].Resource;
            Resource crateTex = _textures["crateTex"].Resource;

            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
                Format = bricksTex.Description.Format,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new ShaderResourceViewDescription.Texture2DResource
                {
                    MostDetailedMip = 0,
                    MipLevels = bricksTex.Description.MipLevels,
                    ResourceMinLODClamp = 0.0f
                }
            };
            Device.CreateShaderResourceView(bricksTex, srvDesc, hDescriptor);

            // Next descriptor.
            hDescriptor += CbvSrvUavDescriptorSize;

            srvDesc.Format = stoneTex.Description.Format;
            srvDesc.Texture2D.MipLevels = stoneTex.Description.MipLevels;
            Device.CreateShaderResourceView(stoneTex, srvDesc, hDescriptor);

            // Next descriptor.
            hDescriptor += CbvSrvUavDescriptorSize;

            srvDesc.Format = tileTex.Description.Format;
            srvDesc.Texture2D.MipLevels = tileTex.Description.MipLevels;
            Device.CreateShaderResourceView(tileTex, srvDesc, hDescriptor);

            // Next descriptor.
            hDescriptor += CbvSrvUavDescriptorSize;

            srvDesc.Format = crateTex.Description.Format;
            srvDesc.Texture2D.MipLevels = crateTex.Description.MipLevels;
            Device.CreateShaderResourceView(crateTex, srvDesc, hDescriptor);
        }

        private void BuildShadersAndInputLayout()
        {
            _shaders["standardVS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "VS", "vs_5_1");
            _shaders["opaquePS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "PS", "ps_5_1");

            _inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 24, 0)
            });
        }

        private void BuildShapeGeometry()
        {
            GeometryGenerator.MeshData box = GeometryGenerator.CreateBox(1.0f, 1.0f, 1.0f, 3);
            GeometryGenerator.MeshData grid = GeometryGenerator.CreateGrid(20.0f, 30.0f, 60, 40);
            GeometryGenerator.MeshData sphere = GeometryGenerator.CreateSphere(0.5f, 20, 20);
            GeometryGenerator.MeshData cylinder = GeometryGenerator.CreateCylinder(0.5f, 0.3f, 3.0f, 20, 20);

            //
            // We are concatenating all the geometry into one big vertex/index buffer. So
            // define the regions in the buffer each submesh covers.
            //

            // Cache the vertex offsets to each object in the concatenated vertex buffer.
            int boxVertexOffset = 0;
            int gridVertexOffset = box.Vertices.Count;
            int sphereVertexOffset = gridVertexOffset + grid.Vertices.Count;
            int cylinderVertexOffset = sphereVertexOffset + sphere.Vertices.Count;

            // Cache the starting index for each object in the concatenated index buffer.
            int boxIndexOffset = 0;
            int gridIndexOffset = box.Indices32.Count;
            int sphereIndexOffset = gridIndexOffset + grid.Indices32.Count;
            int cylinderIndexOffset = sphereIndexOffset + sphere.Indices32.Count;

            // Define the SubmeshGeometry that cover different 
            // regions of the vertex/index buffers.

            var boxSubmesh = new SubmeshGeometry
            {
                IndexCount = box.Indices32.Count,
                StartIndexLocation = boxIndexOffset,
                BaseVertexLocation = boxVertexOffset
            };

            var gridSubmesh = new SubmeshGeometry
            {
                IndexCount = grid.Indices32.Count,
                StartIndexLocation = gridIndexOffset,
                BaseVertexLocation = gridVertexOffset
            };

            var sphereSubmesh = new SubmeshGeometry
            {
                IndexCount = sphere.Indices32.Count,
                StartIndexLocation = sphereIndexOffset,
                BaseVertexLocation = sphereVertexOffset
            };

            var cylinderSubmesh = new SubmeshGeometry
            {
                IndexCount = cylinder.Indices32.Count,
                StartIndexLocation = cylinderIndexOffset,
                BaseVertexLocation = cylinderVertexOffset
            };

            //
            // Extract the vertex elements we are interested in and pack the
            // vertices of all the meshes into one vertex buffer.
            //

            int totalVertexCount =
                box.Vertices.Count +
                grid.Vertices.Count +
                sphere.Vertices.Count +
                cylinder.Vertices.Count;

            var vertices = new Vertex[totalVertexCount];

            int k = 0;
            for (int i = 0; i < box.Vertices.Count; ++i, ++k)
            {
                vertices[k].Pos = box.Vertices[i].Position;
                vertices[k].Normal = box.Vertices[i].Normal;
                vertices[k].TexC = box.Vertices[i].TexC;
            }

            for (int i = 0; i < grid.Vertices.Count; ++i, ++k)
            {
                vertices[k].Pos = grid.Vertices[i].Position;
                vertices[k].Normal = grid.Vertices[i].Normal;
                vertices[k].TexC = grid.Vertices[i].TexC;
            }

            for (int i = 0; i < sphere.Vertices.Count; ++i, ++k)
            {
                vertices[k].Pos = sphere.Vertices[i].Position;
                vertices[k].Normal = sphere.Vertices[i].Normal;
                vertices[k].TexC = sphere.Vertices[i].TexC;
            }

            for (int i = 0; i < cylinder.Vertices.Count; ++i, ++k)
            {
                vertices[k].Pos = cylinder.Vertices[i].Position;
                vertices[k].Normal = cylinder.Vertices[i].Normal;
                vertices[k].TexC = cylinder.Vertices[i].TexC;
            }

            var indices = new List<short>();
            indices.AddRange(box.GetIndices16());
            indices.AddRange(grid.GetIndices16());
            indices.AddRange(sphere.GetIndices16());
            indices.AddRange(cylinder.GetIndices16());

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices.ToArray(), "shapeGeo");

            geo.DrawArgs["box"] = boxSubmesh;
            geo.DrawArgs["grid"] = gridSubmesh;
            geo.DrawArgs["sphere"] = sphereSubmesh;
            geo.DrawArgs["cylinder"] = cylinderSubmesh;

            _geometries[geo.Name] = geo;
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
                SampleMask = unchecked((int)uint.MaxValue),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetCount = 1,
                SampleDescription = new SampleDescription(MsaaCount, MsaaQuality),
                DepthStencilFormat = DepthStencilFormat
            };
            opaquePsoDesc.RenderTargetFormats[0] = BackBufferFormat;

            _psos["opaque"] = Device.CreateGraphicsPipelineState(opaquePsoDesc);
        }

        private void BuildFrameResources()
        {
            for (int i = 0; i < NumFrameResources; i++)
            {
                _frameResources.Add(new FrameResource(Device, 1, _allRitems.Count, _materials.Count));
                _fenceEvents.Add(new AutoResetEvent(false));
            }
        }

        private void BuildMaterials()
        {
            _materials["bricks0"] = new Material
            {
                Name = "bricks0",
                MatCBIndex = 0,
                DiffuseSrvHeapIndex = 0,
                DiffuseAlbedo = Color.ForestGreen.ToVector4(),
                FresnelR0 = new Vector3(0.02f),
                Roughness = 0.1f
            };

            _materials["stone0"] = new Material
            {
                Name = "stone0",
                MatCBIndex = 1,
                DiffuseSrvHeapIndex = 1,
                DiffuseAlbedo = Color.LightSteelBlue.ToVector4(),
                FresnelR0 = new Vector3(0.05f),
                Roughness = 0.3f
            };

            _materials["tile0"] = new Material
            {
                Name = "tile0",
                MatCBIndex = 2,
                DiffuseSrvHeapIndex = 2,
                DiffuseAlbedo = Color.LightGray.ToVector4(),
                FresnelR0 = new Vector3(0.02f),
                Roughness = 0.2f
            };

            _materials["crate0"] = new Material
            {
                Name = "crate0",
                MatCBIndex = 3,
                DiffuseSrvHeapIndex = 3,
                DiffuseAlbedo = Color.White.ToVector4(),
                FresnelR0 = new Vector3(0.05f),
                Roughness = 0.2f
            };
        }

        private void BuildRenderItems()
        {
            var boxRitem = new RenderItem();
            boxRitem.World = Matrix.Scaling(2.0f, 2.0f, 2.0f) * Matrix.Translation(0.0f, 1.0f, 0.0f);
            boxRitem.ObjCBIndex = 0;
            boxRitem.Mat = _materials["crate0"];
            boxRitem.Geo = _geometries["shapeGeo"];
            boxRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            boxRitem.IndexCount = boxRitem.Geo.DrawArgs["box"].IndexCount;
            boxRitem.StartIndexLocation = boxRitem.Geo.DrawArgs["box"].StartIndexLocation;
            boxRitem.BaseVertexLocation = boxRitem.Geo.DrawArgs["box"].BaseVertexLocation;
            _allRitems.Add(boxRitem);

            var gridRitem = new RenderItem();
            gridRitem.World = Matrix.Identity;
            gridRitem.ObjCBIndex = 1;
            gridRitem.Mat = _materials["tile0"];
            gridRitem.Geo = _geometries["shapeGeo"];
            gridRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            gridRitem.IndexCount = gridRitem.Geo.DrawArgs["grid"].IndexCount;
            gridRitem.StartIndexLocation = gridRitem.Geo.DrawArgs["grid"].StartIndexLocation;
            gridRitem.BaseVertexLocation = gridRitem.Geo.DrawArgs["grid"].BaseVertexLocation;
            _allRitems.Add(gridRitem);

            int objCBIndex = 2;
            for (int i = 0; i < 5; ++i)
            {
                var leftCylRitem = new RenderItem();
                var rightCylRitem = new RenderItem();
                var leftSphereRitem = new RenderItem();
                var rightSphereRitem = new RenderItem();

                leftCylRitem.World = Matrix.Translation(-5.0f, 1.5f, -10.0f + i * 5.0f);
                leftCylRitem.ObjCBIndex = objCBIndex++;
                leftCylRitem.Mat = _materials["bricks0"];
                leftCylRitem.Geo = _geometries["shapeGeo"];
                leftCylRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                leftCylRitem.IndexCount = leftCylRitem.Geo.DrawArgs["cylinder"].IndexCount;
                leftCylRitem.StartIndexLocation = leftCylRitem.Geo.DrawArgs["cylinder"].StartIndexLocation;
                leftCylRitem.BaseVertexLocation = leftCylRitem.Geo.DrawArgs["cylinder"].BaseVertexLocation;

                rightCylRitem.World = Matrix.Translation(+5.0f, 1.5f, -10.0f + i * 5.0f);
                rightCylRitem.ObjCBIndex = objCBIndex++;
                rightCylRitem.Mat = _materials["bricks0"];
                rightCylRitem.Geo = _geometries["shapeGeo"];
                rightCylRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                rightCylRitem.IndexCount = rightCylRitem.Geo.DrawArgs["cylinder"].IndexCount;
                rightCylRitem.StartIndexLocation = rightCylRitem.Geo.DrawArgs["cylinder"].StartIndexLocation;
                rightCylRitem.BaseVertexLocation = rightCylRitem.Geo.DrawArgs["cylinder"].BaseVertexLocation;

                leftSphereRitem.World = Matrix.Translation(-5.0f, 3.5f, -10.0f + i * 5.0f);
                leftSphereRitem.ObjCBIndex = objCBIndex++;
                leftSphereRitem.Mat = _materials["stone0"];
                leftSphereRitem.Geo = _geometries["shapeGeo"];
                leftSphereRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                leftSphereRitem.IndexCount = leftSphereRitem.Geo.DrawArgs["sphere"].IndexCount;
                leftSphereRitem.StartIndexLocation = leftSphereRitem.Geo.DrawArgs["sphere"].StartIndexLocation;
                leftSphereRitem.BaseVertexLocation = leftSphereRitem.Geo.DrawArgs["sphere"].BaseVertexLocation;

                rightSphereRitem.World = Matrix.Translation(+5.0f, 3.5f, -10.0f + i * 5.0f);
                rightSphereRitem.ObjCBIndex = objCBIndex++;
                rightSphereRitem.Mat = _materials["stone0"];
                rightSphereRitem.Geo = _geometries["shapeGeo"];
                rightSphereRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                rightSphereRitem.IndexCount = rightSphereRitem.Geo.DrawArgs["sphere"].IndexCount;
                rightSphereRitem.StartIndexLocation = rightSphereRitem.Geo.DrawArgs["sphere"].StartIndexLocation;
                rightSphereRitem.BaseVertexLocation = rightSphereRitem.Geo.DrawArgs["sphere"].BaseVertexLocation;

                _allRitems.Add(leftCylRitem);
                _allRitems.Add(rightCylRitem);
                _allRitems.Add(leftSphereRitem);
                _allRitems.Add(rightSphereRitem);
            }

            // All the render items are opaque.
            _opaqueRitems.AddRange(_allRitems);
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

        // Applications usually only need a handful of samplers. So just define them all up front
        // and keep them available as part of the root signature.
        private static StaticSamplerDescription[] GetStaticSamplers() => new[]
        {
            // PointWrap
            new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap
            },
            // PointClamp
            new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0)
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp
            },
            // LinearWrap
            new StaticSamplerDescription(ShaderVisibility.Pixel, 2, 0)
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap
            },
            // LinearClamp
            new StaticSamplerDescription(ShaderVisibility.Pixel, 3, 0)
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp
            },
            // AnisotropicWrap
            new StaticSamplerDescription(ShaderVisibility.Pixel, 4, 0)
            {
                Filter = Filter.Anisotropic,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                MipLODBias = 0.0f,
                MaxAnisotropy = 8
            },
            // AnisotropicClamp
            new StaticSamplerDescription(ShaderVisibility.Pixel, 5, 0)
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
