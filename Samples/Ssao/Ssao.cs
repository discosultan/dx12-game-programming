//using System;
//using System.Diagnostics;
//using SharpDX;
//using SharpDX.Direct3D;
//using SharpDX.Direct3D12;
//using SharpDX.DXGI;
//using Device = SharpDX.Direct3D12.Device;
//using Resource = SharpDX.Direct3D12.Resource;
//using ShaderResourceViewDimension = SharpDX.Direct3D12.ShaderResourceViewDimension;

//namespace DX12GameProgramming
//{
//    internal class Ssao : IDisposable
//    {
//        private static readonly Format AmbientMapFormat = Format.R16_UNorm;
//        private static readonly Format NormalMapFormat = Format.R16G16B16A16_UNorm;

//        private const int MaxBlurRadius = 5;

//        private readonly Device _device;

//        private RootSignature _ssaoRootSig;

//        private PipelineState _ssaoPso;
//        private PipelineState _blurPso;

//        private Resource _randomVectorMap;
//        private Resource _randomVectorMapUploadBuffer;
//        private Resource _normalMap;
//        private Resource _ambientMap0;
//        private Resource _ambientMap1;

//        private CpuDescriptorHandle _normalMapCpuSrv;
//        private GpuDescriptorHandle _normalMapGpuSrv;
//        private CpuDescriptorHandle _normalMapCpuRtv;

//        private CpuDescriptorHandle _depthMapCpuSrv;
//        private GpuDescriptorHandle _depthMapGpuSrv;

//        private CpuDescriptorHandle _randomVectorMapCpuSrv;
//        private GpuDescriptorHandle _randomVectorMapGpuSrv;

//        // Need two for ping-ponging during blur.
//        private CpuDescriptorHandle _ambientMap0CpuSrv;
//        private GpuDescriptorHandle _ambientMap0GpuSrv;
//        private CpuDescriptorHandle _ambientMap0CpuRtv;

//        private CpuDescriptorHandle _ambientMap1CpuSrv;
//        private GpuDescriptorHandle _ambientMap1GpuSrv;
//        private CpuDescriptorHandle _ambientMap1CpuRtv;

//        private int _renderTargetWidth;
//        private int _renderTargetHeight;

//        private readonly Vector4[] _offsets = new Vector4[16];

//        private ViewportF _viewport;
//        private RectangleF _scissorRectangle;

//        public Ssao(Device device, GraphicsCommandList cmdList, int width, int height)
//        {
//            _device = device;

//            OnResize(width, height);

//            BuildOffsetVectors();
//            BuildRandomVectorTexture(cmdList);
//        }

//        public int Width => _renderTargetWidth / 2;
//        public int Height => _renderTargetHeight / 2;

//        public Resource NormalMap => _normalMap;
//        public Resource AmbientMap => _ambientMap0;
//        public CpuDescriptorHandle NormalMapRtv => _normalMapCpuRtv;
//        public GpuDescriptorHandle NormalMapSrv => _normalMapGpuSrv;
//        public GpuDescriptorHandle AmbientMapSrv => _ambientMap0GpuSrv;

//        public void GetOffsetVectors(Vector4[] offsets) => Array.Copy(_offsets, offsets, _offsets.Length);

//        public float[] CalcGaussWeights(float sigma)
//        {
//            float twoSigma2 = 2.0f * sigma * sigma;

//            // Estimate the blur radius based on sigma since sigma controls the "width" of the bell curve.
//            // For example, for sigma = 3, the width of the bell curve is 
//            int blurRadius = (int)Math.Ceiling(2.0f * sigma);

//            Debug.Assert(blurRadius <= MaxBlurRadius);

//            var weights = new float[2 * blurRadius + 1];

//            float weightSum = 0.0f;

//            for (int i = -blurRadius; i <= blurRadius; ++i)
//            {
//                float x = i;

//                weights[i + blurRadius] = MathHelper.Expf(-x * x / twoSigma2);

//                weightSum += weights[i + blurRadius];
//            }

//            // Divide by the sum so all the weights add up to 1.0.
//            for (int i = 0; i < weights.Length; ++i)
//            {
//                weights[i] /= weightSum;
//            }

//            return weights;
//        }        

//        public void BuildDescriptors(
//            Resource depthStencilBuffer,
//            CpuDescriptorHandle cpuSrv, 
//            GpuDescriptorHandle gpuSrv,
//            CpuDescriptorHandle cpuRtv,
//            int cbvSrvUavDescriptorSize,
//            int rtvDescriptorSize)
//        {
//            // Save references to the descriptors. The Ssao reserves heap space
//            // for 5 contiguous Srvs.

//            _ambientMap0CpuSrv = cpuSrv;
//            _ambientMap1CpuSrv = cpuSrv + cbvSrvUavDescriptorSize;
//            _normalMapCpuSrv = cpuSrv + cbvSrvUavDescriptorSize;
//            _depthMapCpuSrv = cpuSrv + cbvSrvUavDescriptorSize;
//            _randomVectorMapCpuSrv = cpuSrv + cbvSrvUavDescriptorSize;

//            _ambientMap0GpuSrv = gpuSrv;
//            _ambientMap1GpuSrv = gpuSrv + cbvSrvUavDescriptorSize;
//            _normalMapGpuSrv = gpuSrv + cbvSrvUavDescriptorSize;
//            _depthMapGpuSrv = gpuSrv + cbvSrvUavDescriptorSize;
//            _randomVectorMapGpuSrv = gpuSrv + cbvSrvUavDescriptorSize;

//            _normalMapCpuRtv = cpuRtv;
//            _ambientMap0CpuRtv = cpuRtv + rtvDescriptorSize;
//            _ambientMap1CpuRtv = cpuRtv + rtvDescriptorSize;

//            //  Create the descriptors
//            RebuildDescriptors(depthStencilBuffer);
//        }

//        private void RebuildDescriptors(Resource depthStencilBuffer)
//        {
//            var srvDesc = new ShaderResourceViewDescription
//            {
//                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
//                Dimension = ShaderResourceViewDimension.Texture2D,
//                Format = NormalMapFormat,
//                Texture2D = new ShaderResourceViewDescription.Texture2DResource
//                {
//                    MostDetailedMip = 0,
//                    MipLevels = 1
//                }
//            };
//            _device.CreateShaderResourceView(_normalMap, srvDesc, _normalMapCpuSrv);

//            srvDesc.Format = Format.R24_UNorm_X8_Typeless;
//            _device.CreateShaderResourceView(depthStencilBuffer, srvDesc, _depthMapCpuSrv);

//            srvDesc.Format = Format.R8G8B8A8_UNorm;
//            _device.CreateShaderResourceView(_randomVectorMap, srvDesc, _randomVectorMapCpuSrv);

//            srvDesc.Format = AmbientMapFormat;
//            _device.CreateShaderResourceView(_ambientMap0, srvDesc, _ambientMap0CpuSrv);
//            _device.CreateShaderResourceView(_ambientMap1, srvDesc, _ambientMap1CpuSrv);

//            var rtvDesc = new RenderTargetViewDescription
//            {
//                Dimension = RenderTargetViewDimension.Texture2D,
//                Format = NormalMapFormat,
//                Texture2D = new RenderTargetViewDescription.Texture2DResource
//                {
//                    MipSlice = 0,
//                    PlaneSlice = 0
//                }
//            };
//            _device.CreateRenderTargetView(_normalMap, rtvDesc, _normalMapCpuRtv);

//            rtvDesc.Format = AmbientMapFormat;
//            _device.CreateRenderTargetView(_ambientMap0, rtvDesc, _ambientMap0CpuRtv);
//            _device.CreateRenderTargetView(_ambientMap1, rtvDesc, _ambientMap1CpuRtv);
//        }

//        public void SetPSOs(PipelineState ssaoPso, PipelineState ssaoBlurPso)
//        {
//            _ssaoPso = ssaoPso;
//            _blurPso = ssaoBlurPso;
//        }

//        public void OnResize(int newWidth, int newHeight)
//        {
//            if (_renderTargetWidth != newWidth || _renderTargetHeight != newHeight)
//            {
//                _renderTargetWidth = newWidth;
//                _renderTargetHeight = newHeight;

//                // We render to ambient map at half the resolution.
//                _viewport.X = 0.0f;
//                _viewport.Y = 0.0f;
//                _viewport.Width = _renderTargetWidth / 2.0f;
//                _viewport.Height = _renderTargetHeight / 2.0f;
//                _viewport.MinDepth = 0.0f;
//                _viewport.MaxDepth = 1.0f;

//                _scissorRectangle = new RectangleF(0, 0, _renderTargetWidth / 2.0f, _renderTargetHeight / 2.0f);

//                Dispose();

//                BuildResources();
//            }
//        }

//        public void ComputeSsao(GraphicsCommandList cmdList, FrameResource currFrame, int blurCount)
//        {
//            cmdList.SetViewports(_viewport);
//            cmdList.SetScissorRectangles(_scissorRectangle);

//            // We compute the initial SSAO to AmbientMap0.

//            // Change to RENDER_TARGET.
//            cmdList.ResourceBarrierTransition(_ambientMap0, ResourceStates.GenericRead, ResourceStates.RenderTarget);

//            var clearValue = Color.White;
//            cmdList.ClearRenderTargetView(_ambientMap0CpuRtv, clearValue);

//            // Specify the buffers we are going to render to.
//            cmdList.SetRenderTargets(_ambientMap0CpuRtv, null);

//            // Bind the constant buffer for this pass.
//            long ssaoCBAddress = currFrame.SsaoCB.Resource.GPUVirtualAddress;
//            cmdList.SetGraphicsRootConstantBufferView(0, ssaoCBAddress);
//            cmdList.SetGraphicsRoot32BitConstant(1, 0, 0);

//            // Bind the normal and depth maps.
//            cmdList.SetGraphicsRootDescriptorTable(2, _normalMapGpuSrv);

//            // Bind the random vector map.
//            cmdList.SetGraphicsRootDescriptorTable(3, _randomVectorMapGpuSrv);

//            cmdList.PipelineState = _ssaoPso;

//            // Draw fullscreen quad.
//            cmdList.SetVertexBuffers(0, null, 0);
//            cmdList.SetIndexBuffer(null);
//            cmdList.PrimitiveTopology = PrimitiveTopology.TriangleList;
//            cmdList.DrawInstanced(6, 1, 0, 0);

//            // Change back to GENERIC_READ so we can read the texture in a shader.
//            cmdList.ResourceBarrierTransition(_ambientMap0, ResourceStates.RenderTarget, ResourceStates.GenericRead);

//            BlurAmbientMap(cmdList, currFrame, blurCount);
//        }

//        private void BlurAmbientMap(GraphicsCommandList cmdList, FrameResource currFrame, int blurCount)
//        {
//            ID3D12Resource* output = nullptr;
//            CD3DX12_GPU_DESCRIPTOR_HANDLE inputSrv;
//            CD3DX12_CPU_DESCRIPTOR_HANDLE outputRtv;

//            // Ping-pong the two ambient map textures as we apply
//            // horizontal and vertical blur passes.
//            if (horzBlur == true)
//            {
//                output = mAmbientMap1.Get();
//                inputSrv = mhAmbientMap0GpuSrv;
//                outputRtv = mhAmbientMap1CpuRtv;
//                cmdList->SetGraphicsRoot32BitConstant(1, 1, 0);
//            }
//            else
//            {
//                output = mAmbientMap0.Get();
//                inputSrv = mhAmbientMap1GpuSrv;
//                outputRtv = mhAmbientMap0CpuRtv;
//                cmdList->SetGraphicsRoot32BitConstant(1, 0, 0);
//            }

//            cmdList->ResourceBarrier(1, &CD3DX12_RESOURCE_BARRIER::Transition(output,
//                D3D12_RESOURCE_STATE_GENERIC_READ, D3D12_RESOURCE_STATE_RENDER_TARGET));

//            float clearValue[] = { 1.0f, 1.0f, 1.0f, 1.0f };
//            cmdList->ClearRenderTargetView(outputRtv, clearValue, 0, nullptr);

//            cmdList->OMSetRenderTargets(1, &outputRtv, true, nullptr);

//            // Normal/depth map still bound.


//            // Bind the normal and depth maps.
//            cmdList->SetGraphicsRootDescriptorTable(2, mhNormalMapGpuSrv);

//            // Bind the input ambient map to second texture table.
//            cmdList->SetGraphicsRootDescriptorTable(3, inputSrv);

//            // Draw fullscreen quad.
//            cmdList->IASetVertexBuffers(0, 0, nullptr);
//            cmdList->IASetIndexBuffer(nullptr);
//            cmdList->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
//            cmdList->DrawInstanced(6, 1, 0, 0);

//            cmdList->ResourceBarrier(1, &CD3DX12_RESOURCE_BARRIER::Transition(output,
//                D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_GENERIC_READ));
//        }

//        private void BuildDescriptors()
//        {
//            // Create SRV to resource so we can sample the shadow map in a shader program.
//            var srvDesc = new ShaderResourceViewDescription
//            {
//                Shader4ComponentMapping = D3DUtil.DefaultShader4ComponentMapping,
//                Format = Format.R24_UNorm_X8_Typeless,
//                Dimension = ShaderResourceViewDimension.Texture2D,
//                Texture2D = new ShaderResourceViewDescription.Texture2DResource
//                {
//                    MostDetailedMip = 0,
//                    MipLevels = 1,
//                    ResourceMinLODClamp = 0.0f,
//                    PlaneSlice = 0
//                }
//            };
//            _device.CreateShaderResourceView(Resource, srvDesc, _cpuSrv);

//            // Create DSV to resource so we can render to the shadow map.
//            var dsvDesc = new DepthStencilViewDescription
//            {                                
//                Flags = DepthStencilViewFlags.None,
//                Dimension = DepthStencilViewDimension.Texture2D,
//                Format = Format.D24_UNorm_S8_UInt,
//                Texture2D = new DepthStencilViewDescription.Texture2DResource
//                {
//                    MipSlice = 0
//                }
//            };
//            _device.CreateDepthStencilView(Resource, dsvDesc, _cpuDsv);
//        }

//        private void BuildResources()
//        {
//            var texDesc = new ResourceDescription
//            {
//                Dimension = ResourceDimension.Texture2D,
//                Alignment = 0,
//                Width = Width,
//                Height = Height,
//                DepthOrArraySize = 1,
//                MipLevels = 1,
//                Format = _format,
//                SampleDescription = new SampleDescription(1, 0),
//                Layout = TextureLayout.Unknown,
//                Flags = ResourceFlags.AllowDepthStencil
//            };

//            var optClear = new ClearValue
//            {
//                Format = Format.D24_UNorm_S8_UInt,
//                DepthStencil = new DepthStencilValue
//                {
//                    Depth = 1.0f,
//                    Stencil = 0
//                }
//            };

//            Resource = _device.CreateCommittedResource(
//                new HeapProperties(HeapType.Default),
//                HeapFlags.None,
//                texDesc,
//                ResourceStates.GenericRead,
//                optClear);
//        }

//        public void Dispose()
//        {
//            Resource?.Dispose();
//        }
//    }
//}
