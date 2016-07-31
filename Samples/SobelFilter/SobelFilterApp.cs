using System;
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
    public class SobelFilterApp : D3DApp
    {
        private readonly List<FrameResource> _frameResources = new List<FrameResource>(NumFrameResources);
        private readonly List<AutoResetEvent> _fenceEvents = new List<AutoResetEvent>(NumFrameResources);
        private int _currFrameResourceIndex;

        private RootSignature _rootSignature;        
        private RootSignature _wavesRootSignature;
        private RootSignature _postProcessRootSignature;

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
        private readonly Dictionary<RenderLayer, List<RenderItem>> _ritemLayers = new Dictionary<RenderLayer, List<RenderItem>>
        {
            [RenderLayer.Opaque] = new List<RenderItem>(),
            [RenderLayer.Transparent] = new List<RenderItem>(),
            [RenderLayer.AlphaTested] = new List<RenderItem>(),
            [RenderLayer.GpuWaves] = new List<RenderItem>()
        };

        private GpuWaves _waves;

        private RenderTarget _offscreenRT;

        private SobelFilter _sobelFilter;

        private PassConstants _mainPassCB = PassConstants.Default;

        private Vector3 _eyePos;
        private Matrix _proj = Matrix.Identity;
        private Matrix _view = Matrix.Identity;

        private float _theta = 1.5f * MathUtil.Pi;
        private float _phi = MathUtil.PiOverTwo - 0.1f;
        private float _radius = 50.0f;

        private float _tBase;

        private Point _lastMousePos;

        public SobelFilterApp(IntPtr hInstance) : base(hInstance)
        {
            MainWindowCaption = "Sobel Filter";
        }

        private FrameResource CurrFrameResource => _frameResources[_currFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => _fenceEvents[_currFrameResourceIndex];

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            _waves = new GpuWaves(Device, CommandList, 256, 256, 0.25f, 0.03f, 2.0f, 0.2f);

            _sobelFilter = new SobelFilter(Device, ClientWidth, ClientHeight, BackBufferFormat);

            _offscreenRT = new RenderTarget(Device, ClientWidth, ClientHeight, BackBufferFormat);

            LoadTextures();
            BuildRootSignature();
            BuildWavesRootSignature();
            BuildPostProcessRootSignature();
            BuildDescriptorHeaps();
            BuildShadersAndInputLayout();
            BuildLandGeometry();
            BuildWavesGeometry();
            BuildBoxGeometry();
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

        // Add +1 descriptor for offscreen render target.
        protected override int RtvDescriptorCount => SwapChainBufferCount + 1;        

        protected override void OnResize()
        {
            base.OnResize();

            // The window resized, so update the aspect ratio and recompute the projection matrix.
            _proj = Matrix.PerspectiveFovLH(0.25f * MathUtil.Pi, AspectRatio, 1.0f, 1000.0f);

            _sobelFilter?.OnResize(ClientWidth, ClientHeight);
            _offscreenRT?.OnResize(ClientWidth, ClientHeight);
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

            CommandList.SetDescriptorHeaps(_descriptorHeaps.Length, _descriptorHeaps);

            UpdateWavesGPU(gt);

            CommandList.PipelineState = _psos["opaque"];

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            // Change offscreen texture to be used as a a render target output.
            CommandList.ResourceBarrierTransition(_offscreenRT.Resource, ResourceStates.GenericRead, ResourceStates.RenderTarget);

            // Clear the back buffer and depth buffer.
            CommandList.ClearRenderTargetView(_offscreenRT.Rtv, new Color(_mainPassCB.FogColor));
            CommandList.ClearDepthStencilView(CurrentDepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.            
            CommandList.SetRenderTargets(_offscreenRT.Rtv, CurrentDepthStencilView);

            CommandList.SetGraphicsRootSignature(_rootSignature);            

            Resource passCB = CurrFrameResource.PassCB.Resource;
            CommandList.SetGraphicsRootConstantBufferView(2, passCB.GPUVirtualAddress);

            CommandList.SetGraphicsRootDescriptorTable(4, _waves.DisplacementMap);

            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Opaque]);

            CommandList.PipelineState = _psos["alphaTested"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.AlphaTested]);

            CommandList.PipelineState = _psos["transparent"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Transparent]);

            CommandList.PipelineState = _psos["wavesRender"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.GpuWaves]);

            // Change offscreen texture to be used as an input.
            CommandList.ResourceBarrierTransition(_offscreenRT.Resource, ResourceStates.RenderTarget, ResourceStates.GenericRead);

            _sobelFilter.Execute(CommandList, _postProcessRootSignature, _psos["sobel"], _offscreenRT.Srv);

            //
            // Switching back to back buffer rendering.
            //

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

            // Specify the buffers we are going to render to.
            CommandList.SetRenderTargets(CurrentBackBufferView, CurrentDepthStencilView);

            CommandList.SetGraphicsRootSignature(_postProcessRootSignature);
            CommandList.PipelineState = _psos["composite"];
            CommandList.SetGraphicsRootDescriptorTable(0, _offscreenRT.Srv);
            CommandList.SetGraphicsRootDescriptorTable(1, _sobelFilter.OutputSrv);
            DrawFullscreenQuad(CommandList);

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
                foreach (Texture texture in _textures.Values) texture.Dispose();
                foreach (FrameResource frameResource in _frameResources) frameResource.Dispose();
                _rootSignature.Dispose();
                _wavesRootSignature.Dispose();
                _postProcessRootSignature.Dispose();
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
                        TexTransform = Matrix.Transpose(e.TexTransform),
                        DisplacementMapTexelSize = e.DisplacementMapTexelSize,
                        GridSpatialStep = e.GridSpatialStep
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
            _mainPassCB.Lights.Light1.Direction = new Vector3(0.57735f, -0.57735f, 0.57735f);
            _mainPassCB.Lights.Light1.Strength = new Vector3(0.6f);
            _mainPassCB.Lights.Light2.Direction = new Vector3(-0.57735f, -0.57735f, 0.57735f);
            _mainPassCB.Lights.Light2.Strength = new Vector3(0.3f);
            _mainPassCB.Lights.Light3.Direction = new Vector3(0.0f, -0.707f, -0.707f);
            _mainPassCB.Lights.Light3.Strength = new Vector3(0.15f);            

            CurrFrameResource.PassCB.CopyData(0, ref _mainPassCB);
        }
        
        private void UpdateWavesGPU(GameTimer gt)
        {
            // Every quarter second, generate a random wave.
            if ((Timer.TotalTime - _tBase) >= 0.25f)
            {
                _tBase += 0.25f;

                int i = MathHelper.Rand(4, _waves.RowCount - 5);
                int j = MathHelper.Rand(4, _waves.ColumnCount - 5);

                float r = MathHelper.Randf(1.0f, 2.0f);

                _waves.Disturb(CommandList, _wavesRootSignature, _psos["wavesDisturb"], i, j, r);
            }

            // Update the wave simulation.
            _waves.Update(gt, CommandList, _wavesRootSignature, _psos["wavesUpdate"]);            
        }

        private void LoadTextures()
        {
            AddTexture("grassTex", "grass.dds");
            AddTexture("waterTex", "water1.dds");
            AddTexture("fenceTex", "WireFence.dds");
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
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(1, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(2, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 1))
            };

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                slotRootParameters,
                GetStaticSamplers());

            _rootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildWavesRootSignature()
        {
            var uavTable0 = new DescriptorRange(DescriptorRangeType.UnorderedAccessView, 1, 0);
            var uavTable1 = new DescriptorRange(DescriptorRangeType.UnorderedAccessView, 1, 1);
            var uavTable2 = new DescriptorRange(DescriptorRangeType.UnorderedAccessView, 1, 2);

            // Root parameter can be a table, root descriptor or root constants.
            // Perfomance TIP: Order from most frequent to least frequent.
            var slotRootParameters = new[]
            {
                new RootParameter(ShaderVisibility.All, new RootConstants(0, 0, 6)),
                new RootParameter(ShaderVisibility.All, uavTable0),
                new RootParameter(ShaderVisibility.All, uavTable1),
                new RootParameter(ShaderVisibility.All, uavTable2),
            };

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                slotRootParameters);

            _wavesRootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildPostProcessRootSignature()
        {
            // Root parameter can be a table, root descriptor or root constants.
            // Perfomance TIP: Order from most frequent to least frequent.
            var slotRootParameters = new[]
            {
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 1)),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.UnorderedAccessView, 1, 0))
            };

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                slotRootParameters,
                GetStaticSamplers());

            _postProcessRootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildDescriptorHeaps()
        {
            // Offscreen RTV goes after the swap chain descriptors.
            const int rtvOffset = SwapChainBufferCount;

            const int srvCount = 3;

            int waveSrvOffset = srvCount;
            int sobelSrvOffset = waveSrvOffset + _waves.DescriptorCount;
            int offscreenSrvOffset = sobelSrvOffset + _sobelFilter.DescriptorCount;

            //
            // Create the SRV heap.
            //
            var srvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = srvCount + _waves.DescriptorCount + _sobelFilter.DescriptorCount + 1, // Extra offscreen render target.
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible
            };
            _srvDescriptorHeap = Device.CreateDescriptorHeap(srvHeapDesc);
            _descriptorHeaps = new[] { _srvDescriptorHeap };

            //
            // Fill out the heap with actual descriptors.
            //
            CpuDescriptorHandle hDescriptor = _srvDescriptorHeap.CPUDescriptorHandleForHeapStart;

            Resource grassTex = _textures["grassTex"].Resource;
            Resource waterTex = _textures["waterTex"].Resource;
            Resource fenceTex = _textures["fenceTex"].Resource;

            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
                Format = grassTex.Description.Format,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new ShaderResourceViewDescription.Texture2DResource
                {
                    MostDetailedMip = 0,
                    MipLevels = -1,                    
                }
            };

            Device.CreateShaderResourceView(grassTex, srvDesc, hDescriptor);

            // Next descriptor.
            hDescriptor += CbvSrvUavDescriptorSize;

            srvDesc.Format = waterTex.Description.Format;
            Device.CreateShaderResourceView(waterTex, srvDesc, hDescriptor);

            // Next descriptor.
            hDescriptor += CbvSrvUavDescriptorSize;

            srvDesc.Format = fenceTex.Description.Format;
            Device.CreateShaderResourceView(fenceTex, srvDesc, hDescriptor);

            CpuDescriptorHandle srvCpuStart = _srvDescriptorHeap.CPUDescriptorHandleForHeapStart;
            GpuDescriptorHandle srvGpuStart = _srvDescriptorHeap.GPUDescriptorHandleForHeapStart;

            CpuDescriptorHandle rtvCpuStart = RtvHeap.CPUDescriptorHandleForHeapStart;

            _waves.BuildDescriptors(                
                _srvDescriptorHeap.CPUDescriptorHandleForHeapStart + waveSrvOffset * CbvSrvUavDescriptorSize,
                _srvDescriptorHeap.GPUDescriptorHandleForHeapStart + waveSrvOffset * CbvSrvUavDescriptorSize,
                CbvSrvUavDescriptorSize);

            _sobelFilter.BuildDescriptors(
                srvCpuStart + sobelSrvOffset * CbvSrvUavDescriptorSize,
                srvGpuStart + sobelSrvOffset * CbvSrvUavDescriptorSize,
                CbvSrvUavDescriptorSize);

            _offscreenRT.BuildDescriptors(
                srvCpuStart + offscreenSrvOffset * CbvSrvUavDescriptorSize,
                srvGpuStart + offscreenSrvOffset * CbvSrvUavDescriptorSize,
                rtvCpuStart + rtvOffset * RtvDescriptorSize);
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

            ShaderMacro[] waveDefines =
            {
                new ShaderMacro("DISPLACEMENT_MAP", "1")
            };

            _shaders["standardVS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "VS", "vs_5_0");
            _shaders["wavesVS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "VS", "vs_5_0", waveDefines);
            _shaders["opaquePS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "PS", "ps_5_0", defines);
            _shaders["alphaTestedPS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "PS", "ps_5_0", alphaTestDefines);
            _shaders["wavesUpdateCS"] = D3DUtil.CompileShader("Shaders\\WaveSim.hlsl", "UpdateWavesCS", "cs_5_0");
            _shaders["wavesDisturbCS"] = D3DUtil.CompileShader("Shaders\\WaveSim.hlsl", "DisturbWavesCS", "cs_5_0");
            _shaders["compositeVS"] = D3DUtil.CompileShader("Shaders\\composite.hlsl", "VS", "vs_5_0");
            _shaders["compositePS"] = D3DUtil.CompileShader("Shaders\\composite.hlsl", "PS", "ps_5_0");
            _shaders["sobelCS"] = D3DUtil.CompileShader("Shaders\\Sobel.hlsl", "SobelCS", "cs_5_0");

            _inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 24, 0)
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
            GeometryGenerator.MeshData grid = GeometryGenerator.CreateGrid(160.0f, 160.0f, _waves.RowCount, _waves.ColumnCount);

            var vertices = new Vertex[grid.Vertices.Count];
            for (int i = 0; i < grid.Vertices.Count; i++)
            {
                vertices[i].Pos = grid.Vertices[i].Position;
                vertices[i].Normal = grid.Vertices[i].Normal;
                vertices[i].TexC = grid.Vertices[i].TexC;
            }

            var indices = new int[3 * _waves.TriangleCount]; // 3 indices per face.
            Debug.Assert(_waves.VertexCount < int.MaxValue);

            // Iterate over each quad.
            int m = _waves.RowCount;
            int n = _waves.ColumnCount;
            int k = 0;
            for (int i = 0; i < m - 1; ++i)
            {
                for (int j = 0; j < n - 1; ++j)
                {
                    indices[k + 0] = i * n + j;
                    indices[k + 1] = i * n + j + 1;
                    indices[k + 2] = (i + 1) * n + j;

                    indices[k + 3] = (i + 1) * n + j;
                    indices[k + 4] = i * n + j + 1;
                    indices[k + 5] = (i + 1) * n + j + 1;

                    k += 6; // Next quad.
                }
            }

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices, "waterGeo");
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
            // PSO for transparent objects.
            //

            GraphicsPipelineStateDescription transparentPsoDesc = opaquePsoDesc.Copy();

            var transparencyBlendDesc = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                LogicOpEnable = false, // TODO: rename to IsLogicOpEnabled
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
            // PSO for drawing waves.
            //

            GraphicsPipelineStateDescription wavesRenderPSO = transparentPsoDesc.Copy();
            wavesRenderPSO.VertexShader = _shaders["wavesVS"];

            _psos["wavesRender"] = Device.CreateGraphicsPipelineState(wavesRenderPSO);

            //
            // PSO for compositing post process.
            //

            GraphicsPipelineStateDescription compositePSO = opaquePsoDesc.Copy();
            compositePSO.RootSignature = _postProcessRootSignature;

            // Disable depth test.
            compositePSO.DepthStencilState.IsDepthEnabled = false;
            compositePSO.DepthStencilState.DepthWriteMask = DepthWriteMask.Zero;
            compositePSO.DepthStencilState.DepthComparison = Comparison.Always;
            compositePSO.VertexShader = _shaders["compositeVS"];
            compositePSO.PixelShader = _shaders["compositePS"];

            _psos["composite"] = Device.CreateGraphicsPipelineState(compositePSO);

            //
            // PSO for disturbing waves.
            //

            var wavesDisturbPSO = new ComputePipelineStateDescription
            {
                RootSignature = _wavesRootSignature,
                ComputeShader = _shaders["wavesDisturbCS"],
                Flags = PipelineStateFlags.None
            };

            _psos["wavesDisturb"] = Device.CreateComputePipelineState(wavesDisturbPSO);

            //
            // PSO for updating waves.
            //

            var wavesUpdatePSO = new ComputePipelineStateDescription
            {
                RootSignature = _wavesRootSignature,
                ComputeShader = _shaders["wavesUpdateCS"],
                Flags = PipelineStateFlags.None
            };

            _psos["wavesUpdate"] = Device.CreateComputePipelineState(wavesUpdatePSO);

            //
            // PSO for sobel.
            //

            var sobelPSO = new ComputePipelineStateDescription
            {
                RootSignature = _postProcessRootSignature,
                ComputeShader = _shaders["sobelCS"],
                Flags = PipelineStateFlags.None
            };

            _psos["sobel"] = Device.CreateComputePipelineState(sobelPSO);
        }

        private void BuildFrameResources()
        {
            for (int i = 0; i < NumFrameResources; i++)
            {
                _frameResources.Add(new FrameResource(Device, 1, _allRitems.Count, _materials.Count, _waves.VertexCount));
                _fenceEvents.Add(new AutoResetEvent(false));
            }
        }

        private void BuildMaterials()
        {
            _materials["grass"] = new Material
            {
                Name = "grass",
                MatCBIndex = 0,
                DiffuseSrvHeapIndex = 0,
                DiffuseAlbedo = new Vector4(1.0f),
                FresnelR0 = new Vector3(0.01f),
                Roughness = 0.125f
            };

            // This is not a good water material definition, but we do not have all the rendering
            // tools we need (transparency, environment reflection), so we fake it for now.
            _materials["water"] = new Material
            {
                Name = "water",
                MatCBIndex = 1,
                DiffuseSrvHeapIndex = 1,
                DiffuseAlbedo = new Vector4(1.0f, 1.0f, 1.0f, 0.5f),
                FresnelR0 = new Vector3(0.1f),
                Roughness = 0.0f
            };

            _materials["wirefence"] = new Material
            {
                Name = "wirefence",
                MatCBIndex = 2,
                DiffuseSrvHeapIndex = 2,
                DiffuseAlbedo = new Vector4(1.0f),
                FresnelR0 = new Vector3(0.02f),
                Roughness = 0.25f
            };
        }

        private void BuildRenderItems()
        {
            var wavesRitem = new RenderItem();
            wavesRitem.World = Matrix.Identity;
            wavesRitem.TexTransform = Matrix.Scaling(5.0f, 5.0f, 1.0f);
            wavesRitem.DisplacementMapTexelSize = new Vector2(1.0f / _waves.ColumnCount, 1.0f / _waves.RowCount);
            wavesRitem.GridSpatialStep = _waves.SpatialStep;
            wavesRitem.ObjCBIndex = 0;
            wavesRitem.Mat = _materials["water"];
            wavesRitem.Geo = _geometries["waterGeo"];
            wavesRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            wavesRitem.IndexCount = wavesRitem.Geo.DrawArgs["grid"].IndexCount;
            wavesRitem.StartIndexLocation = wavesRitem.Geo.DrawArgs["grid"].StartIndexLocation;
            wavesRitem.BaseVertexLocation = wavesRitem.Geo.DrawArgs["grid"].BaseVertexLocation;
            _ritemLayers[RenderLayer.Transparent].Add(wavesRitem);
            _allRitems.Add(wavesRitem);

            var gridRitem = new RenderItem();
            gridRitem.World = Matrix.Identity;
            gridRitem.TexTransform = Matrix.Scaling(5.0f, 5.0f, 1.0f);
            gridRitem.ObjCBIndex = 1;
            gridRitem.Mat = _materials["grass"];
            gridRitem.Geo = _geometries["landGeo"];
            gridRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            gridRitem.IndexCount = gridRitem.Geo.DrawArgs["grid"].IndexCount;
            gridRitem.StartIndexLocation = gridRitem.Geo.DrawArgs["grid"].StartIndexLocation;
            gridRitem.BaseVertexLocation = gridRitem.Geo.DrawArgs["grid"].BaseVertexLocation;
            _ritemLayers[RenderLayer.Opaque].Add(gridRitem);
            _allRitems.Add(gridRitem);

            var boxItem = new RenderItem();
            boxItem.World = Matrix.Translation(3.0f, 2.0f, -9.0f);            
            boxItem.ObjCBIndex = 2;
            boxItem.Mat = _materials["wirefence"];
            boxItem.Geo = _geometries["boxGeo"];
            boxItem.PrimitiveType = PrimitiveTopology.TriangleList;
            boxItem.IndexCount = boxItem.Geo.DrawArgs["box"].IndexCount;
            boxItem.StartIndexLocation = boxItem.Geo.DrawArgs["box"].StartIndexLocation;
            boxItem.BaseVertexLocation = boxItem.Geo.DrawArgs["box"].BaseVertexLocation;
            _ritemLayers[RenderLayer.AlphaTested].Add(boxItem);
            _allRitems.Add(boxItem);
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

        private void DrawFullscreenQuad(GraphicsCommandList cmdList)
        {
            // Null-out IA stage since we build the vertex off the SV_VertexID in the shader.
            cmdList.SetVertexBuffers(0, null, 1);
            cmdList.SetIndexBuffer(null);
            cmdList.PrimitiveTopology = PrimitiveTopology.TriangleList;

            cmdList.DrawInstanced(6, 1, 0, 0);
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
                AddressW = TextureAddressMode.Wrap
            },
            // AnisotropicClamp
            new StaticSamplerDescription(ShaderVisibility.All, 5, 0)
            {
                Filter = Filter.Anisotropic,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp
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
