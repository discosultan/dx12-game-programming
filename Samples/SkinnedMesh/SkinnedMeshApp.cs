using System;
using System.Collections.Generic;
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
    public class SkinnedMeshApp : D3DApp
    {
        private const int ShadowMapSize = 2048;

        private readonly List<FrameResource> _frameResources = new List<FrameResource>(NumFrameResources);
        private readonly List<AutoResetEvent> _fenceEvents = new List<AutoResetEvent>(NumFrameResources);
        private int _currFrameResourceIndex;

        private RootSignature _rootSignature;
        private RootSignature _ssaoRootSignature;

        private DescriptorHeap _srvDescriptorHeap;
        private DescriptorHeap[] _descriptorHeaps;

        private readonly Dictionary<string, MeshGeometry> _geometries = new Dictionary<string, MeshGeometry>();
        private readonly Dictionary<string, Material> _materials = new Dictionary<string, Material>();
        private readonly Dictionary<string, Texture> _textures = new Dictionary<string, Texture>();
        private readonly Dictionary<string, ShaderBytecode> _shaders = new Dictionary<string, ShaderBytecode>();
        private readonly Dictionary<string, PipelineState> _psos = new Dictionary<string, PipelineState>();

        private InputLayoutDescription _inputLayout;
        private InputLayoutDescription _skinnedInputLayout;

        // List of all the render items.
        private readonly List<RenderItem> _allRitems = new List<RenderItem>();

        // Render items divided by PSO.
        private readonly Dictionary<RenderLayer, List<RenderItem>> _ritemLayers = new Dictionary<RenderLayer, List<RenderItem>>
        {
            [RenderLayer.Opaque] = new List<RenderItem>(),
            [RenderLayer.SkinnedOpaque] = new List<RenderItem>(),
            [RenderLayer.Debug] = new List<RenderItem>(),
            [RenderLayer.Sky] = new List<RenderItem>()
        };

        private int _skyTexHeapIndex;
        private int _shadowMapHeapIndex;
        private int _ssaoHeapIndexStart;

        private int _nullCubeSrvIndex;

        private GpuDescriptorHandle _nullSrv;

        private PassConstants _mainPassCB = PassConstants.Default;   // Index 0 of pass cbuffer.
        private PassConstants _shadowPassCB = PassConstants.Default; // Index 1 of pass cbuffer.

        private SkinnedConstants _skinnedConstants = SkinnedConstants.Default;

        private int _skinnedSrvHeapStart;
        private SkinnedModelInstance _skinnedModelInst;
        private SkinnedData _skinnedInfo;
        private List<M3DLoader.Subset> _skinnedSubsets;
        private List<M3DLoader.M3dMaterial> _skinnedMats;
        private readonly List<string> _skinnedTextureNames = new List<string>();

        private readonly Camera _camera = new Camera();

        private ShadowMap _shadowMap;

        private Ssao _ssao;
        private SsaoConstants _ssaoCB = SsaoConstants.Default;
        private readonly float[] _blurWeights = new float[12];

        private BoundingSphere _sceneBounds;

        private float _lightNearZ;
        private float _lightFarZ;
        private Vector3 _lightPosW;
        private Matrix _lightView = Matrix.Identity;
        private Matrix _lightProj = Matrix.Identity;
        private Matrix _shadowTransform = Matrix.Identity;

        private float _lightRotationAngle;
        private readonly Vector3[] _baseLightDirections =
        {
            new Vector3(0.57735f, -0.57735f, 0.57735f),
            new Vector3(-0.57735f, -0.57735f, 0.57735f),
            new Vector3(0.0f, -0.707f, -0.707f)
        };
        private readonly Vector3[] _rotatedLightDirections = new Vector3[3];

        private Point _lastMousePos;

        public SkinnedMeshApp(IntPtr hInstance) : base(hInstance)
        {
            MainWindowCaption = "Skinned Mesh";

            // Estimate the scene bounding sphere manually since we know how the scene was constructed.
            // The grid is the "widest object" with a width of 20 and depth of 30.0f, and centered at
            // the world space origin.  In general, you need to loop over every world space vertex
            // position and compute the bounding sphere.
            _sceneBounds.Center = Vector3.Zero;
            _sceneBounds.Radius = MathHelper.Sqrtf(10.0f * 10.0f + 15.0f * 15.0f);
        }

        private FrameResource CurrFrameResource => _frameResources[_currFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => _fenceEvents[_currFrameResourceIndex];

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            _camera.Position = new Vector3(0.0f, 2.0f, -15.0f);

            _shadowMap = new ShadowMap(Device, ShadowMapSize, ShadowMapSize);

            _ssao = new Ssao(Device, CommandList, ClientWidth, ClientHeight);

            LoadSkinnedModel();
            LoadTextures();
            BuildRootSignature();
            BuildSsaoRootSignature();
            BuildDescriptorHeaps();
            BuildShadersAndInputLayout();
            BuildShapeGeometry();
            BuildMaterials();
            BuildRenderItems();
            BuildFrameResources();
            BuildPSOs();

            _ssao.SetPSOs(_psos["ssao"], _psos["ssaoBlur"]);

            // Execute the initialization commands.
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            // Wait until initialization is complete.
            FlushCommandQueue();
        }

        // Add +1 for screen normal map, +2 for ambient maps.
        protected override int RtvDescriptorCount => SwapChainBufferCount + 3;
        // Add +1 DSV for shadow map.
        protected override int DsvDescriptorCount => 2;

        protected override void OnResize()
        {
            base.OnResize();

            // The window resized, so update the aspect ratio and recompute the projection matrix.
            _camera.SetLens(MathUtil.PiOverFour, AspectRatio, 1.0f, 1000.0f);

            _ssao?.OnResize(ClientWidth, ClientHeight);
            // Resources changed, so need to rebuild descriptors.
            _ssao?.RebuildDescriptors(DepthStencilBuffer);
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

            //
            // Animate the lights (and hence shadows).
            //

            _lightRotationAngle += 0.1f * gt.DeltaTime;

            Matrix r = Matrix.RotationY(_lightRotationAngle);
            for (int i = 0; i < 3; i++)
                _rotatedLightDirections[i] = Vector3.TransformNormal(_baseLightDirections[i], r);

            UpdateObjectCBs();
            UpdateSkinnedCBs(gt);
            UpdateMaterialBuffer();
            UpdateShadowTransform();
            UpdateMainPassCB(gt);
            UpdateShadowPassCB();
            UpdateSsaoCB();
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

            CommandList.SetGraphicsRootSignature(_rootSignature);

            //
            // Shadow map pass.
            //

            // Bind all the materials used in this scene. For structured buffers, we can bypass the heap and 
            // set as a root descriptor.
            Resource matBuffer = CurrFrameResource.MaterialBuffer.Resource;
            CommandList.SetGraphicsRootShaderResourceView(3, matBuffer.GPUVirtualAddress);

            // Bind null SRV for shadow map pass.
            CommandList.SetGraphicsRootDescriptorTable(4, _nullSrv);

            // Bind all the textures used in this scene. Observe
            // that we only have to specify the first descriptor in the table. 
            // The root signature knows how many descriptors are expected in the table.
            CommandList.SetGraphicsRootDescriptorTable(5, _srvDescriptorHeap.GPUDescriptorHandleForHeapStart);

            DrawSceneToShadowMap();

            //
            // Normal/depth pass.
            //

            DrawNormalsAndDepth();

            //
            // Compute SSAO.
            // 

            CommandList.SetGraphicsRootSignature(_ssaoRootSignature);
            _ssao.ComputeSsao(CommandList, CurrFrameResource, 2);

            //
            // Main rendering pass.
            //

            CommandList.SetGraphicsRootSignature(_rootSignature);

            // Rebind state whenever graphics root signature changes.

            // Bind all the materials used in this scene. For structured buffers, we can bypass the heap and 
            // set as a root descriptor.
            matBuffer = CurrFrameResource.MaterialBuffer.Resource;
            CommandList.SetGraphicsRootShaderResourceView(3, matBuffer.GPUVirtualAddress);

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

            // Clear the back buffer and depth buffer.
            CommandList.ClearRenderTargetView(CurrentBackBufferView, Color.LightSteelBlue);

            // WE ALREADY WROTE THE DEPTH INFO TO THE DEPTH BUFFER IN DrawNormalsAndDepth,
            // SO DO NOT CLEAR DEPTH.

            // Specify the buffers we are going to render to.            
            CommandList.SetRenderTargets(CurrentBackBufferView, CurrentDepthStencilView);

            // Bind all the textures used in this scene. Observe
            // that we only have to specify the first descriptor in the table.  
            // The root signature knows how many descriptors are expected in the table.
            CommandList.SetGraphicsRootDescriptorTable(5, _srvDescriptorHeap.GPUDescriptorHandleForHeapStart);

            Resource passCB = CurrFrameResource.PassCB.Resource;
            CommandList.SetGraphicsRootConstantBufferView(2, passCB.GPUVirtualAddress);

            // Bind the sky cube map. For our demos, we just use one "world" cube map representing the environment
            // from far away, so all objects will use the same cube map and we only need to set it once per-frame.  
            // If we wanted to use "local" cube maps, we would have to change them per-object, or dynamically
            // index into an array of cube maps.

            GpuDescriptorHandle skyTexDescriptor = _srvDescriptorHeap.GPUDescriptorHandleForHeapStart;
            skyTexDescriptor += _skyTexHeapIndex * CbvSrvUavDescriptorSize;
            CommandList.SetGraphicsRootDescriptorTable(4, skyTexDescriptor);

            CommandList.PipelineState = _psos["opaque"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Opaque]);

            CommandList.PipelineState = _psos["skinnedOpaque"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.SkinnedOpaque]);

            CommandList.PipelineState = _psos["debug"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Debug]);

            CommandList.PipelineState = _psos["sky"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Sky]);

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
                _ssao?.Dispose();
                _shadowMap?.Dispose();
                foreach (Texture texture in _textures.Values) texture.Dispose();
                foreach (FrameResource frameResource in _frameResources) frameResource.Dispose();
                _rootSignature?.Dispose();
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

        private void UpdateSkinnedCBs(GameTimer gt)
        {
            UploadBuffer<SkinnedConstants> currSkinnedCB = CurrFrameResource.SkinnedCB;

            // We only have one skinned model being animated.
            _skinnedModelInst.UpdateSkinnedAnimation(gt.DeltaTime);

            _skinnedModelInst.FinalTransforms.CopyTo(
                0, _skinnedConstants.BoneTransforms, 
                0, _skinnedModelInst.FinalTransforms.Count);

            currSkinnedCB.CopyData(0, ref _skinnedConstants);
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
                        DiffuseMapIndex = mat.DiffuseSrvHeapIndex,
                        NormalMapIndex = mat.NormalSrvHeapIndex
                    };

                    currMaterialCB.CopyData(mat.MatCBIndex, ref matConstants);

                    // Next FrameResource need to be updated too.
                    mat.NumFramesDirty--;
                }
            }
        }

        private void UpdateShadowTransform()
        {
            // Only the first "main" light casts a shadow.
            Vector3 lightDir = _rotatedLightDirections[0];
            Vector3 lightPos = -2.0f * _sceneBounds.Radius * lightDir;
            Vector3 targetPos = _sceneBounds.Center;
            Vector3 lightUp = Vector3.Up;
            Matrix lightView = Matrix.LookAtLH(lightPos, targetPos, lightUp);

            _lightPosW = lightPos;

            // Transform bounding sphere to light space.
            Vector3 sphereCenterLS = Vector3.TransformCoordinate(targetPos, lightView);

            // Ortho frustum in light space encloses scene.
            float l = sphereCenterLS.X - _sceneBounds.Radius;
            float b = sphereCenterLS.Y - _sceneBounds.Radius;
            float n = sphereCenterLS.Z - _sceneBounds.Radius;
            float r = sphereCenterLS.X + _sceneBounds.Radius;
            float t = sphereCenterLS.Y + _sceneBounds.Radius;
            float f = sphereCenterLS.Z + _sceneBounds.Radius;

            _lightNearZ = n;
            _lightFarZ = f;
            Matrix lightProj = Matrix.OrthoOffCenterLH(l, r, b, t, n, f);

            // Transform NDC space [-1,+1]^2 to texture space [0,1]^2
            var ndcToTexture = new Matrix(
                0.5f, 0.0f, 0.0f, 0.0f,
                0.0f, -0.5f, 0.0f, 0.0f,
                0.0f, 0.0f, 1.0f, 0.0f,
                0.5f, 0.5f, 0.0f, 1.0f);

            _shadowTransform = lightView * lightProj * ndcToTexture;
            _lightView = lightView;
            _lightProj = lightProj;
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
            _mainPassCB.ShadowTransform = Matrix.Transpose(_shadowTransform);
            _mainPassCB.EyePosW = _camera.Position;
            _mainPassCB.RenderTargetSize = new Vector2(ClientWidth, ClientHeight);
            _mainPassCB.InvRenderTargetSize = 1.0f / _mainPassCB.RenderTargetSize;
            _mainPassCB.NearZ = 1.0f;
            _mainPassCB.FarZ = 1000.0f;
            _mainPassCB.TotalTime = gt.TotalTime;
            _mainPassCB.DeltaTime = gt.DeltaTime;
            _mainPassCB.AmbientLight = new Vector4(0.25f, 0.25f, 0.35f, 1.0f);
            _mainPassCB.Lights[0].Direction = _rotatedLightDirections[0];
            _mainPassCB.Lights[0].Strength = new Vector3(0.9f, 0.9f, 0.7f);
            _mainPassCB.Lights[1].Direction = _rotatedLightDirections[1];
            _mainPassCB.Lights[1].Strength = new Vector3(0.4f);
            _mainPassCB.Lights[2].Direction = _rotatedLightDirections[2];
            _mainPassCB.Lights[2].Strength = new Vector3(0.2f);

            CurrFrameResource.PassCB.CopyData(0, ref _mainPassCB);
        }

        private void UpdateShadowPassCB()
        {
            Matrix view = _lightView;
            Matrix proj = _lightProj;

            Matrix viewProj = view * proj;
            Matrix invView = Matrix.Invert(view);
            Matrix invProj = Matrix.Invert(proj);
            Matrix invViewProj = Matrix.Invert(viewProj);

            _shadowPassCB.View = Matrix.Transpose(view);
            _shadowPassCB.InvView = Matrix.Transpose(invView);
            _shadowPassCB.Proj = Matrix.Transpose(proj);
            _shadowPassCB.InvProj = Matrix.Transpose(invProj);
            _shadowPassCB.ViewProj = Matrix.Transpose(viewProj);
            _shadowPassCB.InvViewProj = Matrix.Transpose(invViewProj);
            _shadowPassCB.EyePosW = _lightPosW;
            _shadowPassCB.RenderTargetSize = new Vector2(_shadowMap.Width, _shadowMap.Height);
            _shadowPassCB.InvRenderTargetSize = 1.0f / _shadowPassCB.RenderTargetSize;
            _shadowPassCB.NearZ = _lightNearZ;
            _shadowPassCB.FarZ = _lightFarZ;

            CurrFrameResource.PassCB.CopyData(1, ref _shadowPassCB);
        }

        private void UpdateSsaoCB()
        {
            // Transform NDC space [-1,+1]^2 to texture space [0,1]^2
            var ndcToTexture = new Matrix(
                0.5f, 0.0f, 0.0f, 0.0f,
                0.0f, -0.5f, 0.0f, 0.0f,
                0.0f, 0.0f, 1.0f, 0.0f,
                0.5f, 0.5f, 0.0f, 1.0f);

            _ssaoCB.Proj = _mainPassCB.Proj;
            _ssaoCB.InvProj = _mainPassCB.InvProj;
            _ssaoCB.ProjTex = Matrix.Transpose(_camera.Proj * ndcToTexture);

            _ssao.GetOffsetVectors(_ssaoCB.OffsetVectors);

            _ssao.CalcGaussWeights(2.5f, _blurWeights);
            _ssaoCB.BlurWeights[0] = new Vector4(_blurWeights[0], _blurWeights[1], _blurWeights[2], _blurWeights[3]);
            _ssaoCB.BlurWeights[1] = new Vector4(_blurWeights[4], _blurWeights[5], _blurWeights[6], _blurWeights[7]);
            _ssaoCB.BlurWeights[2] = new Vector4(_blurWeights[8], _blurWeights[9], _blurWeights[10], _blurWeights[11]);

            _ssaoCB.InvRenderTargetSize = new Vector2(1.0f / _ssao.SsaoMapWidth, 1.0f / _ssao.SsaoMapHeight);

            // Coordinates given in view space.
            _ssaoCB.OcclusionRadius = 0.5f;
            _ssaoCB.OcclusionFadeStart = 0.2f;
            _ssaoCB.OcclusionFadeEnd = 2.0f;
            _ssaoCB.SurfaceEpsilon = 0.05f;

            CurrFrameResource.SsaoCB.CopyData(0, ref _ssaoCB);
        }

        private void LoadTextures()
        {
            AddTexture("bricksDiffuseMap", "bricks2.dds");
            AddTexture("bricksNormalMap", "bricks2_nmap.dds");
            AddTexture("tileDiffuseMap", "tile.dds");
            AddTexture("tileNormalMap", "tile_nmap.dds");
            AddTexture("defaultDiffuseMap", "white1x1.dds");
            AddTexture("defaultNormalMap", "default_nmap.dds");
            AddTexture("skyCubeMap", "desertcube1024.dds");

            // Add skinned model textures to list so we can reference by name later.
            foreach (M3DLoader.M3dMaterial skinnedMat in _skinnedMats) {
                string diffuseName = skinnedMat.DiffuseMapName.Substring(0, skinnedMat.DiffuseMapName.LastIndexOf('.'));
                string normalName = skinnedMat.NormalMapName.Substring(0, skinnedMat.NormalMapName.LastIndexOf('.'));
                AddTexture(diffuseName, skinnedMat.DiffuseMapName);
                AddTexture(normalName, skinnedMat.NormalMapName);
                _skinnedTextureNames.Add(diffuseName);
                _skinnedTextureNames.Add(normalName);
            }
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
                new RootParameter(ShaderVisibility.All, new RootDescriptor(2, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 1), RootParameterType.ShaderResourceView),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 3, 0)),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 48, 3))
            };

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                slotRootParameters,
                GetStaticSamplers());

            _rootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildSsaoRootSignature()
        {
            // Root parameter can be a table, root descriptor or root constants.
            // Perfomance TIP: Order from most frequent to least frequent.
            var slotRootParameters = new[]
            {
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootConstants(1, 0, 1)),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 0)),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 2))
            };            

            StaticSamplerDescription[] staticSamplers =
            {
                new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
                {
                    Filter = Filter.MinMagMipPoint,
                    AddressUVW = TextureAddressMode.Clamp
                },
                new StaticSamplerDescription(ShaderVisibility.All, 1, 0)
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressUVW = TextureAddressMode.Clamp
                },
                new StaticSamplerDescription(ShaderVisibility.All, 2, 0)
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressUVW = TextureAddressMode.Border,
                    MipLODBias = 0.0f,
                    MaxAnisotropy = 0,
                    ComparisonFunc = Comparison.LessEqual,
                    BorderColor = StaticBorderColor.OpaqueWhite
                },
                new StaticSamplerDescription(ShaderVisibility.All, 3, 0)
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressUVW = TextureAddressMode.Wrap
                }
            };

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                slotRootParameters,
                staticSamplers);

            _ssaoRootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildDescriptorHeaps()
        {
            //
            // Create the SRV heap.
            //
            var srvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = 64,
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
                _textures["bricksDiffuseMap"].Resource,
                _textures["bricksNormalMap"].Resource,
                _textures["tileDiffuseMap"].Resource,
                _textures["tileNormalMap"].Resource,
                _textures["defaultDiffuseMap"].Resource,
                _textures["defaultNormalMap"].Resource
            };

            _skinnedSrvHeapStart = tex2DList.Count;

            foreach (string skinnedTextureName in _skinnedTextureNames)
            {
                Resource texResource = _textures[skinnedTextureName].Resource;
                tex2DList.Add(texResource);
            }

            Resource skyCubeMap = _textures["skyCubeMap"].Resource;

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

            srvDesc.Dimension = ShaderResourceViewDimension.TextureCube;
            srvDesc.TextureCube = new ShaderResourceViewDescription.TextureCubeResource
            {
                MostDetailedMip = 0,
                MipLevels = skyCubeMap.Description.MipLevels,
                ResourceMinLODClamp = 0.0f
            };
            srvDesc.Format = skyCubeMap.Description.Format;
            Device.CreateShaderResourceView(skyCubeMap, srvDesc, hDescriptor);

            _skyTexHeapIndex = tex2DList.Count;
            _shadowMapHeapIndex = _skyTexHeapIndex + 1;
            _ssaoHeapIndexStart = _shadowMapHeapIndex + 1;
            _nullCubeSrvIndex = _ssaoHeapIndexStart + 5;

            CpuDescriptorHandle nullSrv = GetCpuSrv(_nullCubeSrvIndex);
            _nullSrv = GetGpuSrv(_nullCubeSrvIndex);

            Device.CreateShaderResourceView(null, srvDesc, nullSrv);
            nullSrv += CbvSrvUavDescriptorSize;

            srvDesc.Dimension = ShaderResourceViewDimension.Texture2D;
            srvDesc.Format = Format.R8G8B8A8_UNorm;
            srvDesc.Texture2D = new ShaderResourceViewDescription.Texture2DResource
            {
                MostDetailedMip = 0,
                MipLevels = 1,
                ResourceMinLODClamp = 0.0f
            };
            Device.CreateShaderResourceView(null, srvDesc, nullSrv);

            nullSrv += CbvSrvUavDescriptorSize;
            Device.CreateShaderResourceView(null, srvDesc, nullSrv);

            _shadowMap.BuildDescriptors(
                GetCpuSrv(_shadowMapHeapIndex),
                GetGpuSrv(_shadowMapHeapIndex),
                GetDsv(1));

            _ssao.BuildDescriptors(
                DepthStencilBuffer,
                GetCpuSrv(_ssaoHeapIndexStart),
                GetGpuSrv(_ssaoHeapIndexStart),
                GetRtv(SwapChainBufferCount),
                CbvSrvUavDescriptorSize,
                RtvDescriptorSize);
        }    

        private void BuildShadersAndInputLayout()
        {
            ShaderMacro[] alphaTestDefines =
            {
                new ShaderMacro("ALPHA_TEST", "1")
            };

            ShaderMacro[] skinnedDefines =
            {
                new ShaderMacro("SKINNED", "1")
            };

            _shaders["standardVS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "VS", "vs_5_1");
            _shaders["skinnedVS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "VS", "vs_5_1", skinnedDefines);
            _shaders["opaquePS"] = D3DUtil.CompileShader("Shaders\\Default.hlsl", "PS", "ps_5_1");

            _shaders["shadowVS"] = D3DUtil.CompileShader("Shaders\\Shadows.hlsl", "VS", "vs_5_1");
            _shaders["skinnedShadowVS"] = D3DUtil.CompileShader("Shaders\\Shadows.hlsl", "VS", "vs_5_1", skinnedDefines);
            _shaders["shadowOpaquePS"] = D3DUtil.CompileShader("Shaders\\Shadows.hlsl", "PS", "ps_5_1");
            _shaders["shadowAlphaTestedPS"] = D3DUtil.CompileShader("Shaders\\Shadows.hlsl", "PS", "ps_5_1", alphaTestDefines);

            _shaders["debugVS"] = D3DUtil.CompileShader("Shaders\\ShadowDebug.hlsl", "VS", "vs_5_1");
            _shaders["debugPS"] = D3DUtil.CompileShader("Shaders\\ShadowDebug.hlsl", "PS", "ps_5_1");

            _shaders["drawNormalsVS"] = D3DUtil.CompileShader("Shaders\\DrawNormals.hlsl", "VS", "vs_5_1");
            _shaders["skinnedDrawNormalsVS"] = D3DUtil.CompileShader("Shaders\\DrawNormals.hlsl", "VS", "vs_5_1", skinnedDefines);
            _shaders["drawNormalsPS"] = D3DUtil.CompileShader("Shaders\\DrawNormals.hlsl", "PS", "ps_5_1");

            _shaders["ssaoVS"] = D3DUtil.CompileShader("Shaders\\Ssao.hlsl", "VS", "vs_5_1");
            _shaders["ssaoPS"] = D3DUtil.CompileShader("Shaders\\Ssao.hlsl", "PS", "ps_5_1");

            _shaders["ssaoBlurVS"] = D3DUtil.CompileShader("Shaders\\SsaoBlur.hlsl", "VS", "vs_5_1");
            _shaders["ssaoBlurPS"] = D3DUtil.CompileShader("Shaders\\SsaoBlur.hlsl", "PS", "ps_5_1");

            _shaders["skyVS"] = D3DUtil.CompileShader("Shaders\\Sky.hlsl", "VS", "vs_5_1");
            _shaders["skyPS"] = D3DUtil.CompileShader("Shaders\\Sky.hlsl", "PS", "ps_5_1");

            _inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
                new InputElement("TANGENT", 0, Format.R32G32B32_Float, 32, 0)
            });

            _skinnedInputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
                new InputElement("TANGENT", 0, Format.R32G32B32_Float, 32, 0),
                new InputElement("WEIGHTS", 0, Format.R32G32B32_Float, 44, 0),
                new InputElement("BONEINDICES", 0, Format.R8G8B8A8_UInt, 56, 0)
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

            SubmeshGeometry box = AppendMeshData(GeometryGenerator.CreateBox(1.0f, 1.0f, 1.0f, 3), vertices, indices);
            SubmeshGeometry grid = AppendMeshData(GeometryGenerator.CreateGrid(20.0f, 30.0f, 60, 40), vertices, indices);
            SubmeshGeometry sphere = AppendMeshData(GeometryGenerator.CreateSphere(0.5f, 20, 20), vertices, indices);
            SubmeshGeometry cylinder = AppendMeshData(GeometryGenerator.CreateCylinder(0.5f, 0.3f, 3.0f, 20, 20), vertices, indices);
            SubmeshGeometry quad = AppendMeshData(GeometryGenerator.CreateQuad(0.0f, 0.0f, 1.0f, 1.0f, 0.0f), vertices, indices);

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices, "shapeGeo");

            geo.DrawArgs["box"] = box;
            geo.DrawArgs["grid"] = grid;
            geo.DrawArgs["sphere"] = sphere;
            geo.DrawArgs["cylinder"] = cylinder;
            geo.DrawArgs["quad"] = quad;

            _geometries[geo.Name] = geo;
        }

        private SubmeshGeometry AppendMeshData(GeometryGenerator.MeshData meshData, List<Vertex> vertices, List<short> indices)
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
                Normal = vertex.Normal,
                TexC = vertex.TexC,
                TangentU = vertex.TangentU
            }));
            indices.AddRange(meshData.GetIndices16());

            return submesh;
        }

        private void LoadSkinnedModel()
        {
            List<M3DLoader.SkinnedVertex> vertices;
            List<short> indices;
            M3DLoader.LoadM3D("Models\\Soldier.m3d", out vertices, out indices, out _skinnedSubsets, out _skinnedMats, out _skinnedInfo);

            _skinnedModelInst = new SkinnedModelInstance
            {
                SkinnedInfo = _skinnedInfo,
                ClipName = "Take1"
            };

            MeshGeometry geo = MeshGeometry.New(Device, CommandList, vertices, indices, "soldier");
            for (int i = 0; i < _skinnedSubsets.Count; i++)
            {
                M3DLoader.Subset skinnedSubset = _skinnedSubsets[i];
                string name = $"sm_{i}";
                geo.DrawArgs[name] = new SubmeshGeometry
                {
                    IndexCount = skinnedSubset.FaceCount * 3,
                    StartIndexLocation = skinnedSubset.FaceStart * 3,
                    BaseVertexLocation = 0
                };
            }
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

            //
            // PSO for skinned pass.
            //

            GraphicsPipelineStateDescription skinnedOpaquePsoDesc = opaquePsoDesc.Copy();
            skinnedOpaquePsoDesc.InputLayout = _skinnedInputLayout;
            skinnedOpaquePsoDesc.VertexShader = _shaders["skinnedVS"];
            _psos["skinnedOpaque"] = Device.CreateGraphicsPipelineState(skinnedOpaquePsoDesc);

            //
            // PSO for shadow map pass.
            //

            GraphicsPipelineStateDescription smapPsoDesc = opaquePsoDesc.Copy();
            smapPsoDesc.RasterizerState.DepthBias = 100000;
            smapPsoDesc.RasterizerState.DepthBiasClamp = 0.0f;
            smapPsoDesc.RasterizerState.SlopeScaledDepthBias = 1.0f;
            smapPsoDesc.VertexShader = _shaders["shadowVS"];
            smapPsoDesc.PixelShader = _shaders["shadowOpaquePS"];
            // Shadow map pass does not have a render target.
            smapPsoDesc.RenderTargetFormats[0] = Format.Unknown;
            smapPsoDesc.RenderTargetCount = 0;
            _psos["shadow_opaque"] = Device.CreateGraphicsPipelineState(smapPsoDesc);

            //
            // PSO for skinned shadow map pass.
            //

            GraphicsPipelineStateDescription skinnedSmapPsoDesc = smapPsoDesc.Copy();
            skinnedSmapPsoDesc.InputLayout = _skinnedInputLayout;
            skinnedOpaquePsoDesc.VertexShader = _shaders["skinnedShadowVS"];
            _psos["skinnedShadow_opaque"] = Device.CreateGraphicsPipelineState(skinnedSmapPsoDesc);

            //
            // PSO for debug layer.
            //

            GraphicsPipelineStateDescription debugPsoDesc = opaquePsoDesc.Copy();
            debugPsoDesc.VertexShader = _shaders["debugVS"];
            debugPsoDesc.PixelShader = _shaders["debugPS"];
            _psos["debug"] = Device.CreateGraphicsPipelineState(debugPsoDesc);

            //
            // PSO for drawing normals.
            //

            GraphicsPipelineStateDescription drawNormalsPsoDesc = opaquePsoDesc.Copy();
            drawNormalsPsoDesc.VertexShader = _shaders["drawNormalsVS"];
            drawNormalsPsoDesc.PixelShader = _shaders["drawNormalsPS"];
            drawNormalsPsoDesc.RenderTargetFormats[0] = Ssao.NormalMapFormat;
            drawNormalsPsoDesc.SampleDescription = new SampleDescription(1, 0);
            _psos["drawNormals"] = Device.CreateGraphicsPipelineState(drawNormalsPsoDesc);

            //
            // PSO for drawing skinned normals.
            //

            GraphicsPipelineStateDescription skinnedDrawNormalsPsoDesc = drawNormalsPsoDesc.Copy();
            skinnedDrawNormalsPsoDesc.InputLayout = _skinnedInputLayout;
            skinnedDrawNormalsPsoDesc.VertexShader = _shaders["skinnedDrawNormalsVS"];
            _psos["skinnedDrawNormals"] = Device.CreateGraphicsPipelineState(skinnedDrawNormalsPsoDesc);

            //
            // PSO for SSAO.
            //

            GraphicsPipelineStateDescription ssaoPsoDesc = opaquePsoDesc.Copy();
            ssaoPsoDesc.InputLayout = null;
            ssaoPsoDesc.RootSignature = _ssaoRootSignature;
            ssaoPsoDesc.VertexShader = _shaders["ssaoVS"];
            ssaoPsoDesc.PixelShader = _shaders["ssaoPS"];
            // SSAO effect does not need the depth buffer.
            ssaoPsoDesc.DepthStencilState.IsDepthEnabled = false;
            ssaoPsoDesc.DepthStencilState.DepthWriteMask = DepthWriteMask.Zero;
            ssaoPsoDesc.RenderTargetFormats[0] = Ssao.AmbientMapFormat;
            ssaoPsoDesc.SampleDescription = new SampleDescription(1, 0);
            ssaoPsoDesc.DepthStencilFormat = Format.Unknown;
            _psos["ssao"] = Device.CreateGraphicsPipelineState(ssaoPsoDesc);

            //
            // PSO for SSAO blur.
            //

            GraphicsPipelineStateDescription ssaoBlurPsoDesc = ssaoPsoDesc.Copy();
            ssaoBlurPsoDesc.VertexShader = _shaders["ssaoBlurVS"];
            ssaoBlurPsoDesc.PixelShader = _shaders["ssaoBlurPS"];
            _psos["ssaoBlur"] = Device.CreateGraphicsPipelineState(ssaoBlurPsoDesc);

            //
            // PSO for sky.
            //

            GraphicsPipelineStateDescription skyPsoDesc = opaquePsoDesc.Copy();
            // The camera is inside the sky sphere, so just turn off culling.
            skyPsoDesc.RasterizerState.CullMode = CullMode.None;
            // Make sure the depth function is LESS_EQUAL and not just LESS.  
            // Otherwise, the normalized depth values at z = 1 (NDC) will 
            // fail the depth test if the depth buffer was cleared to 1.
            skyPsoDesc.DepthStencilState.DepthComparison = Comparison.LessEqual;
            skyPsoDesc.VertexShader = _shaders["skyVS"];
            skyPsoDesc.PixelShader = _shaders["skyPS"];
            _psos["sky"] = Device.CreateGraphicsPipelineState(skyPsoDesc);
        }

        private void BuildFrameResources()
        {
            for (int i = 0; i < NumFrameResources; i++)
            {
                _frameResources.Add(new FrameResource(Device, 2, _allRitems.Count, 1, _materials.Count));
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
                NormalSrvHeapIndex = 1,
                DiffuseAlbedo = Vector4.One,
                FresnelR0 = new Vector3(0.1f),
                Roughness = 0.3f
            });
            AddMaterial(new Material
            {
                Name = "tile0",
                MatCBIndex = 1,
                DiffuseSrvHeapIndex = 2,
                NormalSrvHeapIndex = 3,
                DiffuseAlbedo = new Vector4(0.9f, 0.9f, 0.9f, 1.0f),
                FresnelR0 = new Vector3(0.2f),
                Roughness = 0.1f
            });
            AddMaterial(new Material
            {
                Name = "mirror0",
                MatCBIndex = 2,
                DiffuseSrvHeapIndex = 4,
                NormalSrvHeapIndex = 5,
                DiffuseAlbedo = new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                FresnelR0 = new Vector3(0.98f, 0.97f, 0.95f),
                Roughness = 0.1f
            });
            AddMaterial(new Material
            {
                Name = "sky",
                MatCBIndex = 3,
                DiffuseSrvHeapIndex = 6,
                NormalSrvHeapIndex = 7,
                DiffuseAlbedo = Vector4.One,
                FresnelR0 = new Vector3(0.1f),
                Roughness = 1.0f
            });

            int matCBIndex = 4;
            int srvHeapIndex = _skinnedSrvHeapStart;
            foreach (M3DLoader.M3dMaterial skinnedMat in _skinnedMats)
            {
                AddMaterial(new Material
                {
                    Name = skinnedMat.Name,
                    MatCBIndex = matCBIndex++,
                    DiffuseSrvHeapIndex = srvHeapIndex++,
                    NormalSrvHeapIndex = srvHeapIndex++,
                    DiffuseAlbedo = skinnedMat.DiffuseAlbedo,
                    FresnelR0 = skinnedMat.FresnelR0,
                    Roughness = skinnedMat.Roughness                
                });
            }         
        }

        private void AddMaterial(Material mat)
        {
            _materials[mat.Name] = mat;
        }

        private void BuildRenderItems()
        {
            var skyRitem = new RenderItem();
            skyRitem.World = Matrix.Scaling(5000.0f);
            skyRitem.TexTransform = Matrix.Identity;
            skyRitem.ObjCBIndex = 0;
            skyRitem.Mat = _materials["sky"];
            skyRitem.Geo = _geometries["shapeGeo"];
            skyRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            skyRitem.IndexCount = skyRitem.Geo.DrawArgs["sphere"].IndexCount;
            skyRitem.StartIndexLocation = skyRitem.Geo.DrawArgs["sphere"].StartIndexLocation;
            skyRitem.BaseVertexLocation = skyRitem.Geo.DrawArgs["sphere"].BaseVertexLocation;
            AddRenderItem(skyRitem, RenderLayer.Sky);

            var quadRitem = new RenderItem();
            quadRitem.World = Matrix.Identity;
            quadRitem.TexTransform = Matrix.Identity;
            quadRitem.ObjCBIndex = 1;
            quadRitem.Mat = _materials["bricks0"];
            quadRitem.Geo = _geometries["shapeGeo"];
            quadRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            quadRitem.IndexCount = quadRitem.Geo.DrawArgs["quad"].IndexCount;
            quadRitem.StartIndexLocation = quadRitem.Geo.DrawArgs["quad"].StartIndexLocation;
            quadRitem.BaseVertexLocation = quadRitem.Geo.DrawArgs["quad"].BaseVertexLocation;
            AddRenderItem(quadRitem, RenderLayer.Debug);

            var boxRitem = new RenderItem();
            boxRitem.World = Matrix.Scaling(2.0f, 1.0f, 2.0f) * Matrix.Translation(0.0f, 0.5f, 0.0f);
            boxRitem.TexTransform = Matrix.Scaling(1.0f, 0.5f, 1.0f);
            boxRitem.ObjCBIndex = 2;
            boxRitem.Mat = _materials["bricks0"];
            boxRitem.Geo = _geometries["shapeGeo"];
            boxRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            boxRitem.IndexCount = boxRitem.Geo.DrawArgs["box"].IndexCount;
            boxRitem.StartIndexLocation = boxRitem.Geo.DrawArgs["box"].StartIndexLocation;
            boxRitem.BaseVertexLocation = boxRitem.Geo.DrawArgs["box"].BaseVertexLocation;
            AddRenderItem(boxRitem, RenderLayer.Opaque);

            var gridRitem = new RenderItem();
            gridRitem.World = Matrix.Identity;
            gridRitem.TexTransform = Matrix.Scaling(8.0f, 8.0f, 1.0f);
            gridRitem.ObjCBIndex = 3;
            gridRitem.Mat = _materials["tile0"];
            gridRitem.Geo = _geometries["shapeGeo"];
            gridRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            gridRitem.IndexCount = gridRitem.Geo.DrawArgs["grid"].IndexCount;
            gridRitem.StartIndexLocation = gridRitem.Geo.DrawArgs["grid"].StartIndexLocation;
            gridRitem.BaseVertexLocation = gridRitem.Geo.DrawArgs["grid"].BaseVertexLocation;
            AddRenderItem(gridRitem, RenderLayer.Opaque);

            Matrix brickTexTransform = Matrix.Scaling(1.5f, 2.0f, 1.0f);
            int objCBIndex = 4;
            for (int i = 0; i < 5; ++i)
            {
                var leftCylRitem = new RenderItem();
                leftCylRitem.World = Matrix.Translation(-5.0f, 1.5f, -10.0f + i * 5.0f);
                leftCylRitem.TexTransform = brickTexTransform;
                leftCylRitem.ObjCBIndex = objCBIndex++;
                leftCylRitem.Mat = _materials["bricks0"];
                leftCylRitem.Geo = _geometries["shapeGeo"];
                leftCylRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                leftCylRitem.IndexCount = leftCylRitem.Geo.DrawArgs["cylinder"].IndexCount;
                leftCylRitem.StartIndexLocation = leftCylRitem.Geo.DrawArgs["cylinder"].StartIndexLocation;
                leftCylRitem.BaseVertexLocation = leftCylRitem.Geo.DrawArgs["cylinder"].BaseVertexLocation;
                AddRenderItem(leftCylRitem, RenderLayer.Opaque);

                var rightCylRitem = new RenderItem();
                rightCylRitem.World = Matrix.Translation(+5.0f, 1.5f, -10.0f + i * 5.0f);
                rightCylRitem.TexTransform = brickTexTransform;
                rightCylRitem.ObjCBIndex = objCBIndex++;
                rightCylRitem.Mat = _materials["bricks0"];
                rightCylRitem.Geo = _geometries["shapeGeo"];
                rightCylRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                rightCylRitem.IndexCount = rightCylRitem.Geo.DrawArgs["cylinder"].IndexCount;
                rightCylRitem.StartIndexLocation = rightCylRitem.Geo.DrawArgs["cylinder"].StartIndexLocation;
                rightCylRitem.BaseVertexLocation = rightCylRitem.Geo.DrawArgs["cylinder"].BaseVertexLocation;
                AddRenderItem(rightCylRitem, RenderLayer.Opaque);

                var leftSphereRitem = new RenderItem();
                leftSphereRitem.World = Matrix.Translation(-5.0f, 3.5f, -10.0f + i * 5.0f);
                leftSphereRitem.TexTransform = Matrix.Identity;
                leftSphereRitem.ObjCBIndex = objCBIndex++;
                leftSphereRitem.Mat = _materials["mirror0"];
                leftSphereRitem.Geo = _geometries["shapeGeo"];
                leftSphereRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                leftSphereRitem.IndexCount = leftSphereRitem.Geo.DrawArgs["sphere"].IndexCount;
                leftSphereRitem.StartIndexLocation = leftSphereRitem.Geo.DrawArgs["sphere"].StartIndexLocation;
                leftSphereRitem.BaseVertexLocation = leftSphereRitem.Geo.DrawArgs["sphere"].BaseVertexLocation;
                AddRenderItem(leftSphereRitem, RenderLayer.Opaque);

                var rightSphereRitem = new RenderItem();
                rightSphereRitem.World = Matrix.Translation(+5.0f, 3.5f, -10.0f + i * 5.0f);
                rightSphereRitem.TexTransform = Matrix.Identity;
                rightSphereRitem.ObjCBIndex = objCBIndex++;
                rightSphereRitem.Mat = _materials["mirror0"];
                rightSphereRitem.Geo = _geometries["shapeGeo"];
                rightSphereRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                rightSphereRitem.IndexCount = rightSphereRitem.Geo.DrawArgs["sphere"].IndexCount;
                rightSphereRitem.StartIndexLocation = rightSphereRitem.Geo.DrawArgs["sphere"].StartIndexLocation;
                rightSphereRitem.BaseVertexLocation = rightSphereRitem.Geo.DrawArgs["sphere"].BaseVertexLocation;
                AddRenderItem(rightSphereRitem, RenderLayer.Opaque);
            }

            for (int i = 0; i < _skinnedMats.Count; i++)
            {
                string submeshName = $"sm_{i}";

                var ritem = new RenderItem();

                // Reflect to change coordinate system from the RHS the data was exported out as.
                Matrix modelScale = Matrix.Scaling(0.05f, 0.05f, -0.05f);
                Matrix modelRot = Matrix.RotationY(MathUtil.Pi);
                Matrix modelOffset = Matrix.Translation(0.0f, 0.0f, -5.0f);
                ritem.World = modelScale * modelRot * modelOffset;

                ritem.ObjCBIndex = objCBIndex++;
                ritem.Mat = _materials[_skinnedMats[i].Name];
                ritem.Geo = _geometries["soldier"];
                ritem.IndexCount = ritem.Geo.DrawArgs[submeshName].IndexCount;
                ritem.StartIndexLocation = ritem.Geo.DrawArgs[submeshName].StartIndexLocation;
                ritem.BaseVertexLocation = ritem.Geo.DrawArgs[submeshName].BaseVertexLocation;

                // All render items for this solider.m3d instance share
                // the same skinned model instance.
                ritem.SkinnedCBIndex = 0;
                ritem.SkinnedModelInst = _skinnedModelInst;

                AddRenderItem(ritem, RenderLayer.SkinnedOpaque);
            }
        }

        private void AddRenderItem(RenderItem item, RenderLayer layer)
        {
            _allRitems.Add(item);
            _ritemLayers[layer].Add(item);
        }

        private void DrawRenderItems(GraphicsCommandList cmdList, List<RenderItem> ritems)
        {
            int objCBByteSize = D3DUtil.CalcConstantBufferByteSize<ObjectConstants>();
            int skinnedCBByteSize = D3DUtil.CalcConstantBufferByteSize<SkinnedConstants>();

            Resource objectCB = CurrFrameResource.ObjectCB.Resource;
            Resource skinnedCB = CurrFrameResource.SkinnedCB.Resource;

            foreach (RenderItem ri in ritems)
            {
                cmdList.SetVertexBuffer(0, ri.Geo.VertexBufferView);
                cmdList.SetIndexBuffer(ri.Geo.IndexBufferView);
                cmdList.PrimitiveTopology = ri.PrimitiveType;

                long objCBAddress = objectCB.GPUVirtualAddress + ri.ObjCBIndex * objCBByteSize;

                cmdList.SetGraphicsRootConstantBufferView(0, objCBAddress);

                if (ri.SkinnedModelInst != null)
                {
                    long skinnedCBAddress = skinnedCB.GPUVirtualAddress + ri.SkinnedCBIndex * skinnedCBByteSize;
                    cmdList.SetGraphicsRootConstantBufferView(1, skinnedCBAddress);
                }
                else
                {
                    cmdList.SetGraphicsRootConstantBufferView(1, 0);
                }

                cmdList.DrawIndexedInstanced(ri.IndexCount, 1, ri.StartIndexLocation, ri.BaseVertexLocation, 0);
            }
        }

        private void DrawSceneToShadowMap()
        {
            CommandList.SetViewport(_shadowMap.Viewport);
            CommandList.SetScissorRectangles(_shadowMap.ScissorRectangle);

            // Change to DEPTH_WRITE.
            CommandList.ResourceBarrierTransition(_shadowMap.Resource, ResourceStates.GenericRead, ResourceStates.DepthWrite);

            int passCBByteSize = D3DUtil.CalcConstantBufferByteSize<PassConstants>();

            // Clear the depth buffer.
            CommandList.ClearDepthStencilView(_shadowMap.Dsv, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Set null render target because we are only going to draw to
            // depth buffer. Setting a null render target will disable color writes.
            // Note the active PSO also must specify a render target count of 0.
            CommandList.SetRenderTargets((CpuDescriptorHandle?)null, _shadowMap.Dsv);

            // Bind the pass constant buffer for shadow map pass.
            Resource passCB = CurrFrameResource.PassCB.Resource;
            long passCBAddress = passCB.GPUVirtualAddress + passCBByteSize;
            CommandList.SetGraphicsRootConstantBufferView(1, passCBAddress);

            CommandList.PipelineState = _psos["shadow_opaque"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Opaque]);

            CommandList.PipelineState = _psos["skinnedShadow_opaque"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.SkinnedOpaque]);

            // Change back to GENERIC_READ so we can read the texture in a shader.
            CommandList.ResourceBarrierTransition(_shadowMap.Resource, ResourceStates.DepthWrite, ResourceStates.GenericRead);
        }

        private void DrawNormalsAndDepth()
        {
            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            Resource normalMap = _ssao.NormalMap;
            CpuDescriptorHandle normalMapRtv = _ssao.NormalMapRtv;

            // Change to RENDER_TARGET.
            CommandList.ResourceBarrierTransition(normalMap, ResourceStates.GenericRead, ResourceStates.RenderTarget);

            // Clear the screen normal map and depth buffer.
            CommandList.ClearRenderTargetView(normalMapRtv, Color.Blue);
            CommandList.ClearDepthStencilView(CurrentDepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.
            CommandList.SetRenderTargets(normalMapRtv, CurrentDepthStencilView);

            // Bind the constant buffer for this pass.
            Resource passCB = CurrFrameResource.PassCB.Resource;
            CommandList.SetGraphicsRootConstantBufferView(1, passCB.GPUVirtualAddress);

            CommandList.PipelineState = _psos["drawNormals"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Opaque]);

            CommandList.PipelineState = _psos["skinnedDrawNormals"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.SkinnedOpaque]);

            // Change back to GENERIC_READ so we can read the texture in a shader.
            CommandList.ResourceBarrierTransition(normalMap, ResourceStates.RenderTarget, ResourceStates.GenericRead);
        }

        private CpuDescriptorHandle GetCpuSrv(int index) =>
            _srvDescriptorHeap.CPUDescriptorHandleForHeapStart + index * CbvSrvUavDescriptorSize;

        private GpuDescriptorHandle GetGpuSrv(int index) =>
            _srvDescriptorHeap.GPUDescriptorHandleForHeapStart + index * CbvSrvUavDescriptorSize;

        private CpuDescriptorHandle GetDsv(int index) =>
            DsvHeap.CPUDescriptorHandleForHeapStart + index * DsvDescriptorSize;

        private CpuDescriptorHandle GetRtv(int index) =>
            RtvHeap.CPUDescriptorHandleForHeapStart + index * RtvDescriptorSize;

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
                MaxAnisotropy = 8
            },
            // AnisotropicClamp
            new StaticSamplerDescription(ShaderVisibility.All, 5, 0)
            {
                Filter = Filter.Anisotropic,
                AddressUVW = TextureAddressMode.Clamp,
                MaxAnisotropy = 8
            },
            // Shadow            
            new StaticSamplerDescription(ShaderVisibility.All, 6, 0)
            {
                Filter = Filter.ComparisonMinMagLinearMipPoint,
                AddressUVW = TextureAddressMode.Border,
                MaxAnisotropy = 16,
                ComparisonFunc = Comparison.LessEqual,
                BorderColor = StaticBorderColor.OpaqueBlack
            }
        };
    }
}
