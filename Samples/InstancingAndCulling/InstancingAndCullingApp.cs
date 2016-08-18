using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Resource = SharpDX.Direct3D12.Resource;
using ShaderResourceViewDimension = SharpDX.Direct3D12.ShaderResourceViewDimension;

namespace DX12GameProgramming
{
    public class InstancingAndCullingApp : D3DApp
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

        private bool _frustumCullingEnabled = true;

        private PassConstants _mainPassCB = PassConstants.Default;

        private readonly Camera _camera = new Camera();

        private Point _lastMousePos;

        public InstancingAndCullingApp(IntPtr hInstance) : base(hInstance)
        {
            MainWindowCaption = "Instancing and Culling";
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
            BuildSkullGeometry();            
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

            UpdateInstanceData();
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
            CommandList.ClearDepthStencilView(DepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.            
            CommandList.SetRenderTargets(CurrentBackBufferView, DepthStencilView);

            CommandList.SetDescriptorHeaps(_descriptorHeaps.Length, _descriptorHeaps);

            CommandList.SetGraphicsRootSignature(_rootSignature);            

            // Bind all the materials used in this scene. For structured buffers, we can bypass the heap and 
            // set as a root descriptor.
            Resource matBuffer = CurrFrameResource.MaterialBuffer.Resource;
            CommandList.SetGraphicsRootShaderResourceView(1, matBuffer.GPUVirtualAddress);

            Resource passCB = CurrFrameResource.PassCB.Resource;
            CommandList.SetGraphicsRootConstantBufferView(2, passCB.GPUVirtualAddress);

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
                _rootSignature?.Dispose();
                _srvDescriptorHeap?.Dispose();
                foreach (Texture texture in _textures.Values) texture.Dispose();
                foreach (FrameResource frameResource in _frameResources) frameResource.Dispose();                
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

            if (IsKeyDown(Keys.D1))
                _frustumCullingEnabled = true;
            if (IsKeyDown(Keys.D2))
                _frustumCullingEnabled = false;

            _camera.UpdateViewMatrix();
        }

        private void UpdateInstanceData()
        {
            UploadBuffer<InstanceData> currInstanceBuffer = CurrFrameResource.InstanceBuffer;
            foreach (RenderItem e in _allRitems)
            {
                int visibleInstanceCount = 0;

                foreach (InstanceData instance in e.Instances)
                {                    
                    var box = new BoundingBox(
                        Vector3.TransformCoordinate(e.Bounds.Minimum, instance.World),
                        Vector3.TransformCoordinate(e.Bounds.Maximum, instance.World));

                    // Perform the box/frustum intersection test in local space.
                    if (!_frustumCullingEnabled || _camera.Frustum.Intersects(ref box))
                    {
                        var data = new InstanceData
                        {
                            World = Matrix.Transpose(instance.World),
                            TexTransform = Matrix.Transpose(instance.TexTransform),
                            MaterialIndex = instance.MaterialIndex
                        };

                        // Write the instance data to structured buffer for the visible objects.
                        currInstanceBuffer.CopyData(visibleInstanceCount++, ref data);
                    }
                }

                e.InstanceCount = visibleInstanceCount;

                MainWindowCaption = $"Instancing and Culling    {e.InstanceCount} objects visible out of {e.Instances.Length}";
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
            AddTexture("iceTex", "ice.dds");
            AddTexture("grassTex", "grass.dds");
            AddTexture("defaultTex", "white1x1.dds");
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
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 1), RootParameterType.ShaderResourceView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(1, 1), RootParameterType.ShaderResourceView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 7, 0))
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
                DescriptorCount = 7,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible
            };
            _srvDescriptorHeap = Device.CreateDescriptorHeap(srvHeapDesc);
            _descriptorHeaps = new[] { _srvDescriptorHeap };

            //
            // Fill out the heap with actual descriptors.
            //
            CpuDescriptorHandle hDescriptor = _srvDescriptorHeap.CPUDescriptorHandleForHeapStart;

            var tex2DList = new List<Resource>
            {
                _textures["bricksTex"].Resource,
                _textures["stoneTex"].Resource,
                _textures["tileTex"].Resource,
                _textures["crateTex"].Resource,
                _textures["iceTex"].Resource,
                _textures["grassTex"].Resource,
                _textures["defaultTex"].Resource
            };

            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new ShaderResourceViewDescription.Texture2DResource
                {
                    MostDetailedMip = 0,
                    ResourceMinLODClamp = 0.0f
                }
            };

            foreach (Resource tex2D in tex2DList)
            {
                srvDesc.Format = tex2D.Description.Format;
                srvDesc.Texture2D.MipLevels = tex2D.Description.MipLevels;

                Device.CreateShaderResourceView(tex2D, srvDesc, hDescriptor);

                // Next descriptor.
                hDescriptor += CbvSrvUavDescriptorSize;
            }
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

        private void BuildSkullGeometry()
        {
            var vertices = new List<Vertex>();
            var indices = new List<int>();
            int vCount = 0, tCount = 0;
            var vMin = new Vector3(float.MaxValue);
            var vMax = new Vector3(float.MinValue);
            using (var reader = new StreamReader("Models\\Skull.txt"))
            {
                var input = reader.ReadLine();
                if (input != null)
                    vCount = Convert.ToInt32(input.Split(':')[1].Trim());

                input = reader.ReadLine();
                if (input != null)
                    tCount = Convert.ToInt32(input.Split(':')[1].Trim());

                do
                {
                    input = reader.ReadLine();
                } while (input != null && !input.StartsWith("{", StringComparison.Ordinal));
                
                for (int i = 0; i < vCount; i++)
                {
                    input = reader.ReadLine();
                    if (input != null)
                    {
                        string[] vals = input.Split(' ');

                        var pos = new Vector3(
                                Convert.ToSingle(vals[0].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[1].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[2].Trim(), CultureInfo.InvariantCulture));

                        var normal = new Vector3(
                                Convert.ToSingle(vals[3].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[4].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[5].Trim(), CultureInfo.InvariantCulture));

                        vertices.Add(new Vertex
                        {
                            Pos = pos,
                            Normal = normal
                        });

                        vMin = Vector3.Min(vMin, pos);
                        vMax = Vector3.Max(vMax, pos);
                    }
                }

                do
                {
                    input = reader.ReadLine();
                } while (input != null && !input.StartsWith("{", StringComparison.Ordinal));

                for (var i = 0; i < tCount; i++)
                {
                    input = reader.ReadLine();
                    if (input == null)
                    {
                        break;
                    }
                    var m = input.Trim().Split(' ');
                    indices.Add(Convert.ToInt32(m[0].Trim()));
                    indices.Add(Convert.ToInt32(m[1].Trim()));
                    indices.Add(Convert.ToInt32(m[2].Trim()));
                }
            }

            var geo = MeshGeometry.New(Device, CommandList, vertices.ToArray(), indices.ToArray(), "skullGeo");
            var submesh = new SubmeshGeometry
            {
                IndexCount = indices.Count,
                StartIndexLocation = 0,
                BaseVertexLocation = 0,
                Bounds = new BoundingBox(vMin, vMax)
            };

            geo.DrawArgs["skull"] = submesh;

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
            AddMaterial(new Material
            {
                Name = "bricks0",
                MatCBIndex = 0,
                DiffuseSrvHeapIndex = 0,
                DiffuseAlbedo = Color.White.ToVector4(),
                FresnelR0 = new Vector3(0.02f),
                Roughness = 0.1f
            });
            AddMaterial(new Material
            {
                Name = "stone0",
                MatCBIndex = 1,
                DiffuseSrvHeapIndex = 1,
                DiffuseAlbedo = Color.White.ToVector4(),
                FresnelR0 = new Vector3(0.05f),
                Roughness = 0.3f
            });
            AddMaterial(new Material
            {
                Name = "tile0",
                MatCBIndex = 2,
                DiffuseSrvHeapIndex = 2,
                DiffuseAlbedo = Color.White.ToVector4(),
                FresnelR0 = new Vector3(0.02f),
                Roughness = 0.2f
            });
            AddMaterial(new Material
            {
                Name = "crate0",
                MatCBIndex = 3,
                DiffuseSrvHeapIndex = 3,
                DiffuseAlbedo = Color.White.ToVector4(),
                FresnelR0 = new Vector3(0.05f),
                Roughness = 0.2f
            });
            AddMaterial(new Material
            {
                Name = "ice0",
                MatCBIndex = 4,
                DiffuseSrvHeapIndex = 4,
                DiffuseAlbedo = Color.White.ToVector4(),
                FresnelR0 = new Vector3(0.1f),
                Roughness = 0.0f
            });
            AddMaterial(new Material
            {
                Name = "grass0",
                MatCBIndex = 5,
                DiffuseSrvHeapIndex = 5,
                DiffuseAlbedo = Color.White.ToVector4(),
                FresnelR0 = new Vector3(0.05f),
                Roughness = 0.2f
            });
            AddMaterial(new Material
            {
                Name = "skullMat",
                MatCBIndex = 6,
                DiffuseSrvHeapIndex = 6,
                DiffuseAlbedo = Color.White.ToVector4(),
                FresnelR0 = new Vector3(0.05f),
                Roughness = 0.5f
            });
        }

        private void AddMaterial(Material material) => _materials[material.Name] = material;

        private void BuildRenderItems()
        {
            var skullRitem = new RenderItem();
            skullRitem.ObjCBIndex = 0;
            skullRitem.Mat = _materials["tile0"];
            skullRitem.Geo = _geometries["skullGeo"];
            skullRitem.IndexCount = skullRitem.Geo.DrawArgs["skull"].IndexCount;
            skullRitem.StartIndexLocation = skullRitem.Geo.DrawArgs["skull"].StartIndexLocation;
            skullRitem.BaseVertexLocation = skullRitem.Geo.DrawArgs["skull"].BaseVertexLocation;
            skullRitem.Bounds = skullRitem.Geo.DrawArgs["skull"].Bounds;
            // Instance count for the render item is set during update based on frustum culling.

            // Generate instance data.
            const int n = 5;
            skullRitem.Instances = new InstanceData[n * n * n];

            float width = 200.0f;
            float height = 200.0f;
            float depth = 200.0f;

            float x = -0.5f * width;
            float y = -0.5f * height;
            float z = -0.5f * depth;
            float dx = width / (n - 1);
            float dy = height / (n - 1);
            float dz = depth / (n - 1);
            for (int k = 0; k < n; k++)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        int index = k * n * n + i * n + j;
                        // Position instanced along a 3D grid.
                        skullRitem.Instances[index].World = new Matrix(
                            1.0f, 0.0f, 0.0f, 0.0f,
                            0.0f, 1.0f, 0.0f, 0.0f,
                            0.0f, 0.0f, 1.0f, 0.0f,
                            x + j * dx, y + i * dy, z + k * dz, 1.0f);

                        skullRitem.Instances[index].TexTransform = Matrix.Scaling(2.0f, 2.0f, 1.0f);
                        skullRitem.Instances[index].MaterialIndex = index % _materials.Count;
                    }
                }
            }

            _allRitems.Add(skullRitem);

            // All the render items are opaque.
            _opaqueRitems.AddRange(_allRitems);
        }

        private void DrawRenderItems(GraphicsCommandList cmdList, List<RenderItem> ritems)
        {
            foreach (RenderItem ri in ritems)
            {
                cmdList.SetVertexBuffer(0, ri.Geo.VertexBufferView);
                cmdList.SetIndexBuffer(ri.Geo.IndexBufferView);
                cmdList.PrimitiveTopology = ri.PrimitiveType;

                // Set the instance buffer to use for this render-item. For structured buffers, we can bypass 
                // the heap and set as a root descriptor.
                Resource instanceBuffer = CurrFrameResource.InstanceBuffer.Resource;
                cmdList.SetGraphicsRootShaderResourceView(0, instanceBuffer.GPUVirtualAddress);

                cmdList.DrawIndexedInstanced(ri.IndexCount, ri.InstanceCount, ri.StartIndexLocation, ri.BaseVertexLocation, 0);
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
