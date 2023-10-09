using System;
using System.Runtime.InteropServices;
using SharpDX.Direct3D12;

namespace DX12GameProgramming
{
    public class UploadBuffer<T> : IDisposable where T : struct
    {
        private readonly int _elementByteSize;
        private readonly IntPtr _resourcePointer;

        public UploadBuffer(Device device, int elementCount, bool isConstantBuffer)
        {
            // Constant buffer elements need to be multiples of 256 bytes.
            // This is because the hardware can only view constant data
            // at m*256 byte offsets and of n*256 byte lengths.
            // typedef struct D3D12_CONSTANT_BUFFER_VIEW_DESC {
            // UINT64 OffsetInBytes; // multiple of 256
            // UINT   SizeInBytes;   // multiple of 256
            // } D3D12_CONSTANT_BUFFER_VIEW_DESC;
            _elementByteSize = isConstantBuffer
                ? D3DUtil.CalcConstantBufferByteSize<T>()
                : Marshal.SizeOf(typeof(T));

            Resource = device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload),
                HeapFlags.None,
                ResourceDescription.Buffer(_elementByteSize * elementCount),
                ResourceStates.GenericRead);

            _resourcePointer = Resource.Map(0);

            // We do not need to unmap until we are done with the resource. However, we must not write to
            // the resource while it is in use by the GPU (so we must use synchronization techniques).
        }

        public Resource Resource { get; }

        public void CopyData(int elementIndex, ref T data)
        {
            Marshal.StructureToPtr(data, _resourcePointer + elementIndex * _elementByteSize, true);
        }

        public void Dispose()
        {
            Resource.Unmap(0);
            Resource.Dispose();
        }
    }
}
