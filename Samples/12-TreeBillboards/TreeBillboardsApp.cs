using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Resource = SharpDX.Direct3D12.Resource;
using ShaderResourceViewDimension = SharpDX.Direct3D12.ShaderResourceViewDimension;

namespace DX12GameProgramming
{
    // TODO: Enable alpha to coverage for tree sprites
    // TODO: Enable 4x MSAA for alpha to coverage to take effect
    public class TreeBillboardsApp : D3DApp
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
        private InputLayoutDescription _treeSpriteInputLayout;

        private RenderItem _wavesRitem;

        // List of all the render items.
        private readonly List<RenderItem> _allRitems = new List<RenderItem>();

        // Render items divided by PSO.
        private readonly Dictionary<RenderLayer, List<RenderItem>> _ritemLayers = new Dictionary<RenderLayer, List<RenderItem>>
        {
            [RenderLayer.Opaque] = new List<RenderItem>(),
            [RenderLayer.Transparent] = new List<RenderItem>(),
            [RenderLayer.AlphaTested] = new List<RenderItem>(),
            [RenderLayer.AlphaTestedTreeSprites] = new List<RenderItem>()
        };

        private Waves _waves;

        private PassConstants _mainPassCB = PassConstants.Default;

        private Vector3 _eyePos;
        private Matrix _proj = Matrix.Identity;
        private Matrix _view = Matrix.Identity;

        private float _theta = 1.5f * MathUtil.Pi;
        private float _phi = MathUtil.PiOverTwo - 0.1f;
        private float _radius = 50.0f;

        private float _tBase;

        private Point _lastMousePos;

        public TreeBillboardsApp()
        {
            MainWindowCaption = "Tree Billboards";
        }

        private FrameResource CurrFrameResource => _frameResources[_currFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => _fenceEvents[_currFrameResourceIndex];

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            _waves = new Waves(128, 128, 1.0f, 0.03f, 4.0f, 0.2f);

            LoadTextures();
            BuildRootSignature();
            BuildDescriptorHeaps();
            BuildShadersAndInputLayout();
            BuildLandGeometry();
            BuildWavesGeometry();
            BuildBoxGeometry();
            BuildTreeSpritesGeometry();
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

            AnimateMaterials(gt);
            UpdateObjectCBs();
            UpdateMaterialCBs();
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
            CommandList.Reset(cmdListAlloc, _psos["opaque"]);

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

            // Clear the back buffer and depth buffer.
            CommandList.ClearRenderTargetView(CurrentBackBufferView, new Color(_mainPassCB.FogColor));
            CommandList.ClearDepthStencilView(DepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.
            CommandList.SetRenderTargets(CurrentBackBufferView, DepthStencilView);

            CommandList.SetDescriptorHeaps(_descriptorHeaps.Length, _descriptorHeaps);

            CommandList.SetGraphicsRootSignature(_rootSignature);

            Resource passCB = CurrFrameResource.PassCB.Resource;
            CommandList.SetGraphicsRootConstantBufferView(2, passCB.GPUVirtualAddress);
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Opaque]);

            CommandList.PipelineState = _psos["alphaTested"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.AlphaTested]);

            CommandList.PipelineState = _psos["treeSprites"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.AlphaTestedTreeSprites]);

            CommandList.PipelineState = _psos["transparent"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Transparent]);

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
                _srvDescriptorHeap?.Dispose();
                _rootSignature?.Dispose();
                foreach (Texture texture in _textures.Values) texture.Dispose();
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

        private void AnimateMaterials(GameTimer gt)
        {
            // Scroll the water material texture coordinates.
            Material waterMat = _materials["water"];

            Matrix matTransform = waterMat.MatTransform;

            float tu = matTransform.M41;
            float tv = matTransform.M42;

            tu += 0.1f * gt.DeltaTime;
            tv += 0.02f * gt.DeltaTime;

            if (tu >= 1.0f)
                tu -= 1.0f;

            if (tv >= 1.0f)
                tv -= 1.0f;

            matTransform.M41 = tu;
            matTransform.M42 = tv;

            waterMat.MatTransform = matTransform;

            // Material has changed, so need to update cbuffer.
            waterMat.NumFramesDirty = NumFrameResources;
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
                        TexTransform = Matrix.Transpose(e.TexTransform)
                    };
                    CurrFrameResource.ObjectCB.CopyData(e.ObjCBIndex, ref objConstants);

                    // Next FrameResource need to be updated too.
                    e.NumFramesDirty--;
                }
            }
        }

        private void UpdateMaterialCBs()
        {
            UploadBuffer<MaterialConstants> currMaterialCB = CurrFrameResource.MaterialCB;
            foreach (Material mat in _materials.Values)
            {
                // Only update the cbuffer data if the constants have changed. If the cbuffer
                // data changes, it needs to be updated for each FrameResource.
                if (mat.NumFramesDirty > 0)
                {
                    var matConstants = new MaterialConstants
                    {
                        DiffuseAlbedo = mat.DiffuseAlbedo,
                        FresnelR0 = mat.FresnelR0,
                        Roughness = mat.Roughness,
                        MatTransform = Matrix.Transpose(mat.MatTransform)
                    };

                    currMaterialCB.CopyData(mat.MatCBIndex, ref matConstants);

                    // Next FrameResource need to be updated too.
                    mat.NumFramesDirty--;
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
            _mainPassCB.AmbientLight = new Vector4(0.25f, 0.25f, 0.35f, 1.0f);
            _mainPassCB.Lights[0].Direction = new Vector3(0.57735f, -0.57735f, 0.57735f);
            _mainPassCB.Lights[0].Strength = new Vector3(0.6f);
            _mainPassCB.Lights[1].Direction = new Vector3(-0.57735f, -0.57735f, 0.57735f);
            _mainPassCB.Lights[1].Strength = new Vector3(0.3f);
            _mainPassCB.Lights[2].Direction = new Vector3(0.0f, -0.707f, -0.707f);
            _mainPassCB.Lights[2].Strength = new Vector3(0.15f);

            // Main pass stored in index 0.
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
                    Normal = _waves.Normal(i),
                };
                // Derive tex-coords from position by
                // mapping [-w/2,w/2] -. [0,1]
                v.TexC = new Vector2(
                    0.5f + v.Pos.X / _waves.Width,
                    0.5f - v.Pos.Z / _waves.Depth);
                currWavesVB.CopyData(i, ref v);
            }

            // Set the dynamic VB of the wave renderitem to the current frame VB.
            _wavesRitem.Geo.VertexBufferGPU = currWavesVB.Resource;
        }

        private void LoadTextures()
        {
            AddTexture("grassTex", "grass.dds");
            AddTexture("waterTex", "water1.dds");
            AddTexture("fenceTex", "WireFence.dds");
            AddTexture("treeArrayTex", "treeArray2.dds");
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
            var texTable = new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0);

            var descriptor1 = new RootDescriptor(0, 0);
            var descriptor2 = new RootDescriptor(1, 0);
            var descriptor3 = new RootDescriptor(2, 0);

            // Root parameter can be a table, root descriptor or root constants.
            // Perfomance TIP: Order from most frequent to least frequent.
            var slotRootParameters = new[]
            {
                new RootParameter(ShaderVisibility.Pixel, texTable),
                new RootParameter(ShaderVisibility.All, descriptor1, RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, descriptor2, RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, descriptor3, RootParameterType.ConstantBufferView)
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

            Resource[] tex2DList =
            {
                _textures["grassTex"].Resource,
                _textures["waterTex"].Resource,
                _textures["fenceTex"].Resource
            };
            Resource treeArrayTex = _textures["treeArrayTex"].Resource;

            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new ShaderResourceViewDescription.Texture2DResource
                {
                    MostDetailedMip = 0,
                    MipLevels = -1,
                }
            };

            foreach (Resource tex2D in tex2DList)
            {
                srvDesc.Format = tex2D.Description.Format;
                Device.CreateShaderResourceView(tex2D, srvDesc, hDescriptor);

                // Next descriptor.
                hDescriptor += CbvSrvUavDescriptorSize;
            }

            srvDesc.Format = treeArrayTex.Description.Format;
            srvDesc.Texture2DArray.MostDetailedMip = 0;
            srvDesc.Texture2DArray.MipLevels = -1;
            srvDesc.Texture2DArray.FirstArraySlice = 0;
            srvDesc.Texture2DArray.ArraySize = treeArrayTex.Description.DepthOrArraySize;
            Device.CreateShaderResourceView(treeArrayTex, srvDesc, hDescriptor);
        }

        private void BuildShadersAndInputLayout()
        {
            ShaderMacro[] defines =
            {
                new ShaderMacro("FOG", "1")
            };

            ShaderMacro[] alphaTestDefines =
            {
                new ShaderMacro("FOG", "1"),
                new ShaderMacro("ALPHA_TEST", "1")
            };

            _shaders["standardVS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "VS", "vs_5_0");
            _shaders["opaquePS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "PS", "ps_5_0", defines);
            _shaders["alphaTestedPS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "PS", "ps_5_0", alphaTestDefines);

            _shaders["treeSpriteVS"] = D3DUtil.CompileShader("Shaders\\TreeSprite.hlsl", "VS", "vs_5_0");
            _shaders["treeSpriteGS"] = D3DUtil.CompileShader("Shaders\\TreeSprite.hlsl", "GS", "gs_5_0");
            _shaders["treeSpritePS"] = D3DUtil.CompileShader("Shaders\\TreeSprite.hlsl", "PS", "ps_5_0", alphaTestDefines);

            _inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 24, 0)
            });

            _treeSpriteInputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("SIZE", 0, Format.R32G32_Float, 12, 0),
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
                vertices[i].Normal = GetHillsNormal(p.X, p.Z);
                vertices[i].TexC = grid.Vertices[i].TexC;
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
            Debug.Assert(_waves.VertexCount < short.MaxValue);

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

        private void BuildBoxGeometry()
        {
            GeometryGenerator.MeshData box = GeometryGenerator.CreateBox(8.0f, 8.0f, 8.0f, 3);

            var boxSubmesh = new SubmeshGeometry
            {
                IndexCount = box.Indices32.Count,
                StartIndexLocation = 0,
                BaseVertexLocation = 0
            };

            Vertex[] vertices = box.Vertices.Select(x => new Vertex
            {
                Pos = x.Position,
                Normal = x.Normal,
                TexC = x.TexC
            }).ToArray();

            short[] indices = box.GetIndices16().ToArray();

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices, "boxGeo");

            geo.DrawArgs["box"] = boxSubmesh;

            _geometries[geo.Name] = geo;
        }

        private void BuildTreeSpritesGeometry()
        {
            const int treeCount = 16;
            var vertices = new TreeSpriteVertex[treeCount];
            for (int i = 0; i < treeCount; i++)
            {
                float x = MathHelper.Randf(-45.0f, 45.0f);
                float z = MathHelper.Randf(-45.0f, 45.0f);
                float y = GetHillsHeight(x, z);

                // Move tree slightly above land height.
                y += 8.0f;

                vertices[i].Pos = new Vector3(x, y, z);
                vertices[i].Size = new Vector2(20.0f, 20.0f);
            }

            short[] indices =
            {
                0, 1, 2, 3, 4, 5, 6, 7,
                8, 9, 10, 11, 12, 13, 14, 15
            };

            var submesh = new SubmeshGeometry
            {
                IndexCount = indices.Length,
                StartIndexLocation = 0,
                BaseVertexLocation = 0
            };

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices, "treeSpritesGeo");
            geo.DrawArgs["points"] = submesh;

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
                SampleMask = unchecked((int)0xFFFFFFFF),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetCount = 1,
                SampleDescription = new SampleDescription(MsaaCount, MsaaQuality),
                DepthStencilFormat = DepthStencilFormat
            };
            opaquePsoDesc.RenderTargetFormats[0] = BackBufferFormat;

            _psos["opaque"] = Device.CreateGraphicsPipelineState(opaquePsoDesc);

            //
            // PSO for transparent objects.
            //

            GraphicsPipelineStateDescription transparentPsoDesc = opaquePsoDesc.Copy();

            var transparencyBlendDesc = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                LogicOpEnable = false,
                SourceBlend = BlendOption.SourceAlpha,
                DestinationBlend = BlendOption.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceAlphaBlend = BlendOption.One,
                DestinationAlphaBlend = BlendOption.Zero,
                AlphaBlendOperation = BlendOperation.Add,
                LogicOp = LogicOperation.Noop,
                RenderTargetWriteMask = ColorWriteMaskFlags.All
            };
            transparentPsoDesc.BlendState.RenderTarget[0] = transparencyBlendDesc;

            _psos["transparent"] = Device.CreateGraphicsPipelineState(transparentPsoDesc);

            //
            // PSO for alpha tested objects.
            //

            GraphicsPipelineStateDescription alphaTestedPsoDesc = opaquePsoDesc.Copy();
            alphaTestedPsoDesc.PixelShader = _shaders["alphaTestedPS"];
            alphaTestedPsoDesc.RasterizerState.CullMode = CullMode.None;

            _psos["alphaTested"] = Device.CreateGraphicsPipelineState(alphaTestedPsoDesc);

            //
            // PSO for tree sprites.
            //

            GraphicsPipelineStateDescription treeSpritePsoDesc = opaquePsoDesc.Copy();
            treeSpritePsoDesc.VertexShader = _shaders["treeSpriteVS"];
            treeSpritePsoDesc.GeometryShader = _shaders["treeSpriteGS"];
            treeSpritePsoDesc.PixelShader = _shaders["treeSpritePS"];
            treeSpritePsoDesc.PrimitiveTopologyType = PrimitiveTopologyType.Point;
            treeSpritePsoDesc.InputLayout = _treeSpriteInputLayout;
            treeSpritePsoDesc.RasterizerState.CullMode = CullMode.None;

            _psos["treeSprites"] = Device.CreateGraphicsPipelineState(treeSpritePsoDesc);
        }

        private void BuildFrameResources()
        {
            for (int i = 0; i < NumFrameResources; i++)
            {
                _frameResources.Add(new FrameResource(Device, 2, _allRitems.Count, _materials.Count, _waves.VertexCount));
                _fenceEvents.Add(new AutoResetEvent(false));
            }
        }

        private void BuildMaterials()
        {
            AddMaterial(new Material
            {
                Name = "grass",
                MatCBIndex = 0,
                DiffuseSrvHeapIndex = 0,
                DiffuseAlbedo = new Vector4(1.0f),
                FresnelR0 = new Vector3(0.01f),
                Roughness = 0.125f
            });
            // This is not a good water material definition, but we do not have all the rendering
            // tools we need (transparency, environment reflection), so we fake it for now.
            AddMaterial(new Material
            {
                Name = "water",
                MatCBIndex = 1,
                DiffuseSrvHeapIndex = 1,
                DiffuseAlbedo = new Vector4(1.0f, 1.0f, 1.0f, 0.5f),
                FresnelR0 = new Vector3(0.1f),
                Roughness = 0.0f
            });
            AddMaterial(new Material
            {
                Name = "wirefence",
                MatCBIndex = 2,
                DiffuseSrvHeapIndex = 2,
                DiffuseAlbedo = new Vector4(1.0f),
                FresnelR0 = new Vector3(0.02f),
                Roughness = 0.25f
            });
            AddMaterial(new Material
            {
                Name = "treeSprites",
                MatCBIndex = 3,
                DiffuseSrvHeapIndex = 3,
                DiffuseAlbedo = new Vector4(1.0f),
                FresnelR0 = new Vector3(0.01f),
                Roughness = 0.125f
            });
        }

        private void AddMaterial(Material mat) => _materials[mat.Name] = mat;

        private void BuildRenderItems()
        {
            _wavesRitem = AddRenderItem(RenderLayer.Transparent, 0, "water", "waterGeo", "grid",
                texTransform: Matrix.Scaling(5.0f, 5.0f, 1.0f));
            AddRenderItem(RenderLayer.Opaque, 1, "grass", "landGeo", "grid",
                texTransform: Matrix.Scaling(5.0f, 5.0f, 1.0f));
            AddRenderItem(RenderLayer.AlphaTested, 2, "wirefence", "boxGeo", "box",
                world: Matrix.Translation(3.0f, 2.0f, -9.0f));
            AddRenderItem(RenderLayer.AlphaTestedTreeSprites, 3, "treeSprites", "treeSpritesGeo", "points",
                topology: PrimitiveTopology.PointList);
        }

        private RenderItem AddRenderItem(RenderLayer layer, int objCBIndex, string matName, string geoName, string submeshName,
            Matrix? world = null, Matrix? texTransform = null, PrimitiveTopology topology = PrimitiveTopology.TriangleList)
        {
            MeshGeometry geo = _geometries[geoName];
            SubmeshGeometry submesh = geo.DrawArgs[submeshName];
            var renderItem = new RenderItem
            {
                ObjCBIndex = objCBIndex,
                Mat = _materials[matName],
                Geo = geo,
                IndexCount = submesh.IndexCount,
                StartIndexLocation = submesh.StartIndexLocation,
                BaseVertexLocation = submesh.BaseVertexLocation,
                World = world ?? Matrix.Identity,
                TexTransform = texTransform ?? Matrix.Identity,
                PrimitiveType = topology
            };
            _ritemLayers[layer].Add(renderItem);
            _allRitems.Add(renderItem);
            return renderItem;
        }

        private void DrawRenderItems(GraphicsCommandList cmdList, List<RenderItem> ritems)
        {
            int objCBByteSize = D3DUtil.CalcConstantBufferByteSize<ObjectConstants>();
            int matCBByteSize = D3DUtil.CalcConstantBufferByteSize<MaterialConstants>();

            Resource objectCB = CurrFrameResource.ObjectCB.Resource;
            Resource matCB = CurrFrameResource.MaterialCB.Resource;

            foreach (RenderItem ri in ritems)
            {
                cmdList.SetVertexBuffer(0, ri.Geo.VertexBufferView);
                cmdList.SetIndexBuffer(ri.Geo.IndexBufferView);
                cmdList.PrimitiveTopology = ri.PrimitiveType;

                GpuDescriptorHandle tex = _srvDescriptorHeap.GPUDescriptorHandleForHeapStart + ri.Mat.DiffuseSrvHeapIndex * CbvSrvUavDescriptorSize;

                long objCBAddress = objectCB.GPUVirtualAddress + ri.ObjCBIndex * objCBByteSize;
                long matCBAddress = matCB.GPUVirtualAddress + ri.Mat.MatCBIndex * matCBByteSize;

                cmdList.SetGraphicsRootDescriptorTable(0, tex);
                cmdList.SetGraphicsRootConstantBufferView(1, objCBAddress);
                cmdList.SetGraphicsRootConstantBufferView(3, matCBAddress);

                cmdList.DrawIndexedInstanced(ri.IndexCount, 1, ri.StartIndexLocation, ri.BaseVertexLocation, 0);
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
                AddressUVW = TextureAddressMode.Wrap
            },
            // PointClamp
            new StaticSamplerDescription(ShaderVisibility.All, 1, 0)
            {
                Filter = Filter.MinMagMipPoint,
                AddressUVW = TextureAddressMode.Clamp
            },
            // LinearWrap
            new StaticSamplerDescription(ShaderVisibility.All, 2, 0)
            {
                Filter = Filter.MinMagMipLinear,
                AddressUVW = TextureAddressMode.Wrap
            },
            // LinearClamp
            new StaticSamplerDescription(ShaderVisibility.All, 3, 0)
            {
                Filter = Filter.MinMagMipLinear,
                AddressUVW = TextureAddressMode.Clamp
            },
            // AnisotropicWrap
            new StaticSamplerDescription(ShaderVisibility.All, 4, 0)
            {
                Filter = Filter.Anisotropic,
                AddressUVW = TextureAddressMode.Wrap,
                MipLODBias = 0.0f,
                MaxAnisotropy = 8
            },
            // AnisotropicClamp
            new StaticSamplerDescription(ShaderVisibility.All, 5, 0)
            {
                Filter = Filter.Anisotropic,
                AddressUVW = TextureAddressMode.Clamp,
                MipLODBias = 0.0f,
                MaxAnisotropy = 8
            }
        };

        private static float GetHillsHeight(float x, float z) => 0.3f * (z * MathHelper.Sinf(0.1f * x) + x * MathHelper.Cosf(0.1f * z));

        private static Vector3 GetHillsNormal(float x, float z) => Vector3.Normalize(new Vector3(
            // n = (-df/dx, 1, -df/dz)
            -0.03f * z * MathHelper.Cosf(0.1f * x) - 0.3f * MathHelper.Cosf(0.1f * z),
            1.0f,
            -0.3f * MathHelper.Sinf(0.1f * x) + 0.03f * x * MathHelper.Sinf(0.1f * z)));
    }
}
