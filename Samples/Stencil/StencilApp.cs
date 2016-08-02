using System;
using SharpDX;
using System.Collections.Generic;
using System.Threading;
using SharpDX.Direct3D;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Resource = SharpDX.Direct3D12.Resource;
using System.IO;
using System.Globalization;
using ShaderResourceViewDimension = SharpDX.Direct3D12.ShaderResourceViewDimension;

namespace DX12GameProgramming
{
    public class StencilApp : D3DApp
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
        
        // Cache render items of interest.
        private RenderItem _skullRitem;
        private RenderItem _reflectedSkullRitem;
        private RenderItem _shadowedSkullRitem;

        // List of all the render items.
        private readonly List<RenderItem> _allRitems = new List<RenderItem>();

        // Render items divided by PSO.
        private readonly Dictionary<RenderLayer, List<RenderItem>> _ritemLayers = new Dictionary<RenderLayer, List<RenderItem>>
        {
            [RenderLayer.Opaque] = new List<RenderItem>(),
            [RenderLayer.Transparent] = new List<RenderItem>(),
            [RenderLayer.Mirrors] = new List<RenderItem>(),
            [RenderLayer.Reflected] = new List<RenderItem>(),
            [RenderLayer.Shadow] = new List<RenderItem>()
        };

        private PassConstants _mainPassCB = PassConstants.Default;
        private PassConstants _reflectedPassCB = PassConstants.Default;

        private Vector3 _skullTranslation = new Vector3(0.0f, 1.0f, -5.0f);

        private Vector3 _eyePos;
        private Matrix _proj = Matrix.Identity;
        private Matrix _view = Matrix.Identity;

        private float _theta = 1.24f * MathUtil.Pi;
        private float _phi = 0.42f * MathUtil.Pi;
        private float _radius = 12.0f;

        private Point _lastMousePos;

        public StencilApp(IntPtr hInstance) : base(hInstance)
        {
            MainWindowCaption = "Stencil";
        }

        private FrameResource CurrFrameResource => _frameResources[_currFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => _fenceEvents[_currFrameResourceIndex];

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            LoadTextures();
            BuildRootSignature();
            BuildDescriptorHeaps();
            BuildShadersAndInputLayout();
            BuildRoomGeometry();
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
            _proj = Matrix.PerspectiveFovLH(MathUtil.PiOverFour, AspectRatio, 1.0f, 1000.0f);
        }

        protected override void Update(GameTimer gt)
        {
            OnKeyboardInput(gt);
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
            UpdateMaterialCBs();
            UpdateMainPassCB(gt);
            UpdateReflectedPassCB();
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
            CommandList.ClearDepthStencilView(CurrentDepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.            
            CommandList.SetRenderTargets(CurrentBackBufferView, CurrentDepthStencilView);

            CommandList.SetDescriptorHeaps(_descriptorHeaps.Length, _descriptorHeaps);

            CommandList.SetGraphicsRootSignature(_rootSignature);

            var passCBByteSize = D3DUtil.CalcConstantBufferByteSize<PassConstants>();

            // Draw opaque items--floors, walls, skull.
            Resource passCB = CurrFrameResource.PassCB.Resource;
            CommandList.SetGraphicsRootConstantBufferView(2, passCB.GPUVirtualAddress);
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Opaque]);

            // Mark the visible mirror pixels in the stencil buffer with the value 1
            CommandList.StencilReference = 1;
            CommandList.PipelineState = _psos["markStencilMirrors"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Mirrors]);

            // Draw the reflection into the mirror only (only for pixels where the stencil buffer is 1).
            // Note that we must supply a different per-pass constant buffer--one with the lights reflected.
            CommandList.SetGraphicsRootConstantBufferView(2, passCB.GPUVirtualAddress + passCBByteSize);
            CommandList.PipelineState = _psos["drawStencilReflections"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Reflected]);

            // Restore main pass constants and stencil ref.
            CommandList.SetGraphicsRootConstantBufferView(2, passCB.GPUVirtualAddress);
            CommandList.StencilReference = 0;

            // Draw mirror with transparency so reflection blends through.
            CommandList.PipelineState = _psos["transparent"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Transparent]);

            // Draw shadows
            CommandList.PipelineState = _psos["shadow"];
            DrawRenderItems(CommandList, _ritemLayers[RenderLayer.Shadow]);

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

        private void OnKeyboardInput(GameTimer gt)
        {
            //
            // Allow user to move skull.
            //

            float dt = gt.DeltaTime;

            if (IsKeyDown(Keys.A))
                _skullTranslation.X -= 1.0f * dt;

            if (IsKeyDown(Keys.D))
                _skullTranslation.X += 1.0f * dt;

            if (IsKeyDown(Keys.W))
                _skullTranslation.Y += 1.0f * dt;

            if (IsKeyDown(Keys.S))
                _skullTranslation.Y -= 1.0f * dt;

            // Don't let user move below ground plane.
            _skullTranslation.Y = Math.Max(_skullTranslation.Y, 0.0f);

            // Update the new world matrix.
            Matrix skullRotate = Matrix.RotationY(0.5f * MathUtil.Pi);
            Matrix skullScale = Matrix.Scaling(0.45f);
            Matrix skullOffset = Matrix.Translation(_skullTranslation.X, _skullTranslation.Y, _skullTranslation.Z);
            Matrix skullWorld = skullRotate * skullScale * skullOffset;
            _skullRitem.World = skullWorld;

            // Update reflection world matrix.
            var mirrorPlane = new Plane(new Vector3(0, 0, 1), 0); // XY plane.
            Matrix r = MathHelper.Reflection(mirrorPlane);
            _reflectedSkullRitem.World = skullWorld * r;

            // Update shadow world matrix.            
            var shadowPlane = new Plane(new Vector3(0, 1, 0), 0); // XZ plane.
            Vector3 toMainLight = -_mainPassCB.Lights[0].Direction;
            Matrix s = MathHelper.Shadow(new Vector4(toMainLight, 0.0f), shadowPlane);
            Matrix shadowOffsetY = Matrix.Translation(0.0f, 0.001f, 0.0f);
            _shadowedSkullRitem.World = skullWorld * s * shadowOffsetY;

            _skullRitem.NumFramesDirty = NumFrameResources;
            _reflectedSkullRitem.NumFramesDirty = NumFrameResources;
            _shadowedSkullRitem.NumFramesDirty = NumFrameResources;
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
            _mainPassCB.Lights.Light1.Direction = new Vector3(0.57735f, -0.57735f, 0.57735f);
            _mainPassCB.Lights.Light1.Strength = new Vector3(0.6f);
            _mainPassCB.Lights.Light2.Direction = new Vector3(-0.57735f, -0.57735f, 0.57735f);
            _mainPassCB.Lights.Light2.Strength = new Vector3(0.3f);
            _mainPassCB.Lights.Light3.Direction = new Vector3(0.0f, -0.707f, -0.707f);
            _mainPassCB.Lights.Light3.Strength = new Vector3(0.15f);            

            // Main pass stored in index 0.
            CurrFrameResource.PassCB.CopyData(0, ref _mainPassCB);
        }

        private void UpdateReflectedPassCB()
        {
            _reflectedPassCB = _mainPassCB;

            var mirrorPlane = new Plane(new Vector3(0, 0, 1), 0); // XY plane.
            Matrix r = MathHelper.Reflection(mirrorPlane);

            // Reflect the lighting.
            for (int i = 0; i < 3; i++)
            {
                Vector3 lightDir = _mainPassCB.Lights[i].Direction;
                Vector3 reflectedLightDir = Vector3.TransformNormal(lightDir, r);
                Light reflectedLight = _reflectedPassCB.Lights[i];
                reflectedLight.Direction = reflectedLightDir;
                _reflectedPassCB.Lights[i] = reflectedLight;
            }

            // Reflected pass stored in index 1.
            CurrFrameResource.PassCB.CopyData(1, ref _reflectedPassCB);
        }

        private void LoadTextures()
        {
            AddTexture("bricksTex", "bricks3.dds");
            AddTexture("checkboardTex", "checkboard.dds");
            AddTexture("iceTex", "ice.dds");
            AddTexture("white1x1Tex", "white1x1.dds");
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
                new RootParameter(ShaderVisibility.All, new RootDescriptor(2, 0), RootParameterType.ConstantBufferView)
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
            Resource checkboardTex = _textures["checkboardTex"].Resource;
            Resource iceTex = _textures["iceTex"].Resource;
            Resource white1x1Tex = _textures["white1x1Tex"].Resource;

            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
                Format = bricksTex.Description.Format,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new ShaderResourceViewDescription.Texture2DResource
                {
                    MostDetailedMip = 0,
                    MipLevels = -1,
                }
            };

            Device.CreateShaderResourceView(bricksTex, srvDesc, hDescriptor);

            // Next descriptor.
            hDescriptor += CbvSrvUavDescriptorSize;

            srvDesc.Format = checkboardTex.Description.Format;
            Device.CreateShaderResourceView(checkboardTex, srvDesc, hDescriptor);

            // Next descriptor.
            hDescriptor += CbvSrvUavDescriptorSize;

            srvDesc.Format = iceTex.Description.Format;
            Device.CreateShaderResourceView(iceTex, srvDesc, hDescriptor);

            // Next descriptor.
            hDescriptor += CbvSrvUavDescriptorSize;

            srvDesc.Format = white1x1Tex.Description.Format;
            Device.CreateShaderResourceView(white1x1Tex, srvDesc, hDescriptor);
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

            _inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 24, 0)
            });
        }

        private void BuildRoomGeometry()
        {
            // Create and specify geometry.  For this sample we draw a floor
            // and a wall with a mirror on it.  We put the floor, wall, and
            // mirror geometry in one vertex buffer.
            //
            //   |--------------|
            //   |              |
            //   |----|----|----|
            //   |Wall|Mirr|Wall|
            //   |    | or |    |
            //   /--------------/
            //  /   Floor      /
            // /--------------/

            Vertex[] vertices =
            {
                // Floor: Observe we tile texture coordinates.
                new Vertex(-3.5f, 0.0f, -10.0f, 0.0f, 1.0f, 0.0f, 0.0f, 4.0f), // 0 
		        new Vertex(-3.5f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f),
		        new Vertex(7.5f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 4.0f, 0.0f),
		        new Vertex(7.5f, 0.0f, -10.0f, 0.0f, 1.0f, 0.0f, 4.0f, 4.0f),

		        // Wall: Observe we tile texture coordinates, and that we
		        // leave a gap in the middle for the mirror.
		        new Vertex(-3.5f, 0.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.0f, 2.0f), // 4
		        new Vertex(-3.5f, 4.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.0f, 0.0f),
		        new Vertex(-2.5f, 4.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.5f, 0.0f),
		        new Vertex(-2.5f, 0.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.5f, 2.0f),

		        new Vertex(2.5f, 0.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.0f, 2.0f), // 8 
		        new Vertex(2.5f, 4.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.0f, 0.0f),
		        new Vertex(7.5f, 4.0f, 0.0f, 0.0f, 0.0f, -1.0f, 2.0f, 0.0f),
		        new Vertex(7.5f, 0.0f, 0.0f, 0.0f, 0.0f, -1.0f, 2.0f, 2.0f),

		        new Vertex(-3.5f, 4.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.0f, 1.0f), // 12
		        new Vertex(-3.5f, 6.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.0f, 0.0f),
		        new Vertex(7.5f, 6.0f, 0.0f, 0.0f, 0.0f, -1.0f, 6.0f, 0.0f),
		        new Vertex(7.5f, 4.0f, 0.0f, 0.0f, 0.0f, -1.0f, 6.0f, 1.0f),

		        // Mirror
		        new Vertex(-2.5f, 0.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.0f, 1.0f), // 16
		        new Vertex(-2.5f, 4.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.0f, 0.0f),
		        new Vertex(2.5f, 4.0f, 0.0f, 0.0f, 0.0f, -1.0f, 1.0f, 0.0f),
		        new Vertex(2.5f, 0.0f, 0.0f, 0.0f, 0.0f, -1.0f, 1.0f, 1.0f)

            };

            short[] indices =
            {
                // Floor
                0, 1, 2,	
		        0, 2, 3,

		        // Walls
		        4, 5, 6,
		        4, 6, 7,

		        8, 9, 10,
		        8, 10, 11,

		        12, 13, 14,
		        12, 14, 15,

		        // Mirror
		        16, 17, 18,
		        16, 18, 19

            };

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices, "roomGeo");

            geo.DrawArgs["floor"] = new SubmeshGeometry
            {
                IndexCount = 6,
                StartIndexLocation = 0,
                BaseVertexLocation = 0
            };
            geo.DrawArgs["wall"] = new SubmeshGeometry
            {
                IndexCount = 18,
                StartIndexLocation = 6,
                BaseVertexLocation = 0
            };
            geo.DrawArgs["mirror"] = new SubmeshGeometry
            {
                IndexCount = 6,
                StartIndexLocation = 24,
                BaseVertexLocation = 0
            };

            _geometries[geo.Name] = geo;
        }

        private void BuildSkullGeometry()
        {
            var vertices = new List<Vertex>();
            var indices = new List<int>();
            int vCount = 0, tCount = 0;
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
                        var vals = input.Split(' ');
                        vertices.Add(new Vertex
                        {
                            Pos = new Vector3(
                                Convert.ToSingle(vals[0].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[1].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[2].Trim(), CultureInfo.InvariantCulture)),
                            Normal = new Vector3(
                                Convert.ToSingle(vals[3].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[4].Trim(), CultureInfo.InvariantCulture),
                                Convert.ToSingle(vals[5].Trim(), CultureInfo.InvariantCulture))
                        });
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

            geo.DrawArgs["skull"] = new SubmeshGeometry
            {
                IndexCount = indices.Count,
                StartIndexLocation = 0,
                BaseVertexLocation = 0
            };

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
            // PSO for marking stencil mirrors.
            //

            // We are not rendering backfacing polygons, so these settings do not matter.
            var backFaceDSO = new DepthStencilOperationDescription
            {
                FailOperation = StencilOperation.Keep,
                DepthFailOperation = StencilOperation.Keep,
                PassOperation = StencilOperation.Replace,
                Comparison = Comparison.Always
            };

            var mirrorBlendState = BlendStateDescription.Default();
            mirrorBlendState.RenderTarget[0].RenderTargetWriteMask = 0;

            var mirrorDSS = new DepthStencilStateDescription
            {
                IsDepthEnabled = true,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthComparison = Comparison.Less,
                IsStencilEnabled = true,
                StencilReadMask = 0xff,
                StencilWriteMask = 0xff,

                FrontFace = new DepthStencilOperationDescription
                {
                    FailOperation = StencilOperation.Keep,
                    DepthFailOperation = StencilOperation.Keep,
                    PassOperation = StencilOperation.Replace,
                    Comparison = Comparison.Always
                },
                BackFace = backFaceDSO
            };

            GraphicsPipelineStateDescription markMirrorsPsoDesc = opaquePsoDesc.Copy();
            markMirrorsPsoDesc.BlendState = mirrorBlendState;
            markMirrorsPsoDesc.DepthStencilState = mirrorDSS;
            _psos["markStencilMirrors"] = Device.CreateGraphicsPipelineState(markMirrorsPsoDesc);

            //
            // PSO for stencil reflections.
            //

            var reflectionDSS = new DepthStencilStateDescription
            {
                IsDepthEnabled = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthComparison = Comparison.Less,
                IsStencilEnabled = true,
                StencilReadMask = 0xff,
                StencilWriteMask = 0xff,

                FrontFace = new DepthStencilOperationDescription
                {
                    FailOperation = StencilOperation.Keep,
                    DepthFailOperation = StencilOperation.Keep,
                    PassOperation = StencilOperation.Keep,
                    Comparison = Comparison.Equal
                },
                BackFace = backFaceDSO
            };

            GraphicsPipelineStateDescription drawReflectionsPsoDesc = opaquePsoDesc.Copy();
            drawReflectionsPsoDesc.DepthStencilState = reflectionDSS;
            drawReflectionsPsoDesc.RasterizerState.CullMode = CullMode.Back;
            drawReflectionsPsoDesc.RasterizerState.IsFrontCounterClockwise = true;
            _psos["drawStencilReflections"] = Device.CreateGraphicsPipelineState(drawReflectionsPsoDesc);

            //
            // PSO for shadow objects.
            //

            var shadowDSS = new DepthStencilStateDescription
            {
                IsDepthEnabled = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthComparison = Comparison.Less,
                IsStencilEnabled = true,
                StencilReadMask = 0xff,
                StencilWriteMask = 0xff,

                FrontFace = new DepthStencilOperationDescription
                {
                    FailOperation = StencilOperation.Keep,
                    DepthFailOperation = StencilOperation.Keep,
                    PassOperation = StencilOperation.Increment,
                    Comparison = Comparison.Equal
                },
                BackFace = backFaceDSO
            };

            GraphicsPipelineStateDescription shadowPsoDesc = transparentPsoDesc.Copy();
            shadowPsoDesc.DepthStencilState = shadowDSS;
            _psos["shadow"] = Device.CreateGraphicsPipelineState(shadowPsoDesc);
        }

        private void BuildFrameResources()
        {
            for (int i = 0; i < NumFrameResources; i++)
            {
                _frameResources.Add(new FrameResource(Device, 2, _allRitems.Count, _materials.Count));
                _fenceEvents.Add(new AutoResetEvent(false));
            }
        }

        private void BuildMaterials()
        {
            _materials["bricks"] = new Material
            {
                Name = "bricks",
                MatCBIndex = 0,
                DiffuseSrvHeapIndex = 0,
                DiffuseAlbedo = Color.White.ToVector4(),
                FresnelR0 = new Vector3(0.05f),
                Roughness = 0.25f
            };

            _materials["checkertile"] = new Material
            {
                Name = "checkertile",
                MatCBIndex = 1,
                DiffuseSrvHeapIndex = 1,
                DiffuseAlbedo = Color.White.ToVector4(),
                FresnelR0 = new Vector3(0.07f),
                Roughness = 0.3f
            };

            _materials["icemirror"] = new Material
            {
                Name = "icemirror",
                MatCBIndex = 2,
                DiffuseSrvHeapIndex = 2,
                DiffuseAlbedo = new Vector4(1.0f, 1.0f, 1.0f, 0.3f),
                FresnelR0 = new Vector3(0.1f),
                Roughness = 0.5f
            };

            _materials["skullMat"] = new Material
            {
                Name = "skullMat",
                MatCBIndex = 3,
                DiffuseSrvHeapIndex = 3,
                DiffuseAlbedo = Color.White.ToVector4(),
                FresnelR0 = new Vector3(0.05f),
                Roughness = 0.3f
            };

            _materials["shadowMat"] = new Material
            {
                Name = "shadowMat",
                MatCBIndex = 4,
                DiffuseSrvHeapIndex = 3,
                DiffuseAlbedo = new Vector4(0.0f, 0.0f, 0.0f, 0.5f),
                FresnelR0 = new Vector3(0.001f),
                Roughness = 0.0f
            };
        }

        private void BuildRenderItems()
        {
            var floorRitem = new RenderItem();
            floorRitem.World = Matrix.Identity;
            floorRitem.TexTransform = Matrix.Identity;
            floorRitem.ObjCBIndex = 0;
            floorRitem.Mat = _materials["checkertile"];
            floorRitem.Geo = _geometries["roomGeo"];
            floorRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            floorRitem.IndexCount = floorRitem.Geo.DrawArgs["floor"].IndexCount;
            floorRitem.StartIndexLocation = floorRitem.Geo.DrawArgs["floor"].StartIndexLocation;
            floorRitem.BaseVertexLocation = floorRitem.Geo.DrawArgs["floor"].BaseVertexLocation;
            _ritemLayers[RenderLayer.Opaque].Add(floorRitem);
            _allRitems.Add(floorRitem);

            var wallsRitem = new RenderItem();
            wallsRitem.World = Matrix.Identity;
            wallsRitem.TexTransform = Matrix.Identity;
            wallsRitem.ObjCBIndex = 1;
            wallsRitem.Mat = _materials["bricks"];
            wallsRitem.Geo = _geometries["roomGeo"];
            wallsRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            wallsRitem.IndexCount = wallsRitem.Geo.DrawArgs["wall"].IndexCount;
            wallsRitem.StartIndexLocation = wallsRitem.Geo.DrawArgs["wall"].StartIndexLocation;
            wallsRitem.BaseVertexLocation = wallsRitem.Geo.DrawArgs["wall"].BaseVertexLocation;
            _ritemLayers[RenderLayer.Opaque].Add(wallsRitem);
            _allRitems.Add(wallsRitem);

            _skullRitem = new RenderItem();
            _skullRitem.World = Matrix.Identity;
            _skullRitem.TexTransform = Matrix.Identity;
            _skullRitem.ObjCBIndex = 2;
            _skullRitem.Mat = _materials["skullMat"];
            _skullRitem.Geo = _geometries["skullGeo"];
            _skullRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            _skullRitem.IndexCount = _skullRitem.Geo.DrawArgs["skull"].IndexCount;
            _skullRitem.StartIndexLocation = _skullRitem.Geo.DrawArgs["skull"].StartIndexLocation;
            _skullRitem.BaseVertexLocation = _skullRitem.Geo.DrawArgs["skull"].BaseVertexLocation;
            _ritemLayers[RenderLayer.Opaque].Add(_skullRitem);
            _allRitems.Add(_skullRitem);

            // Reflected skull will have different world matrix, so it needs to be its own render item.
            _reflectedSkullRitem = _skullRitem.Copy();
            _reflectedSkullRitem.ObjCBIndex = 3;
            _ritemLayers[RenderLayer.Reflected].Add(_reflectedSkullRitem);
            _allRitems.Add(_reflectedSkullRitem);

            // Shadowed skull will have different world matrix, so it needs to be its own render item.
            _shadowedSkullRitem = _skullRitem.Copy();
            _shadowedSkullRitem.ObjCBIndex = 4;
            _shadowedSkullRitem.Mat = _materials["shadowMat"];
            _ritemLayers[RenderLayer.Shadow].Add(_shadowedSkullRitem);
            _allRitems.Add(_shadowedSkullRitem);

            var mirrorRitem = new RenderItem();
            mirrorRitem.World = Matrix.Identity;
            mirrorRitem.TexTransform = Matrix.Identity;
            mirrorRitem.ObjCBIndex = 5;
            mirrorRitem.Mat = _materials["icemirror"];
            mirrorRitem.Geo = _geometries["roomGeo"];
            mirrorRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            mirrorRitem.IndexCount = mirrorRitem.Geo.DrawArgs["mirror"].IndexCount;
            mirrorRitem.StartIndexLocation = mirrorRitem.Geo.DrawArgs["mirror"].StartIndexLocation;
            mirrorRitem.BaseVertexLocation = mirrorRitem.Geo.DrawArgs["mirror"].BaseVertexLocation;
            _ritemLayers[RenderLayer.Mirrors].Add(mirrorRitem);
            _ritemLayers[RenderLayer.Transparent].Add(mirrorRitem);
            _allRitems.Add(mirrorRitem);
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
