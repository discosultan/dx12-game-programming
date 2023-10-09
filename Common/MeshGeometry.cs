using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SharpDX;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D12.Device;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    public class SubmeshGeometry
    {
        public int IndexCount { get; set; }
        public int StartIndexLocation { get; set; }
        public int BaseVertexLocation { get; set; }

        // Bounding box of the geometry defined by this submesh.
        // This is used in later chapters of the book.
        public BoundingBox Bounds { get; set; }
    }

    public class MeshGeometry : IDisposable
    {
        private readonly List<IDisposable> _toDispose = new List<IDisposable>();

        // Use MeshGeometry.New factory method instead to construct a new instance of MeshGeometry.
        private MeshGeometry() { }

        // Give it a name so we can look it up by name.
        public string Name { get; set; }

        public Resource VertexBufferGPU { get; set; }
        public Resource IndexBufferGPU { get; set; }

        public object VertexBufferCPU { get; set; }
        public object IndexBufferCPU { get; set; }

        // Data about the buffers.
        public int VertexByteStride { get; set; }
        public int VertexBufferByteSize { get; set; }
        public Format IndexFormat { get; set; }
        public int IndexBufferByteSize { get; set; }
        public int IndexCount { get; set; }

        // A MeshGeometry may store multiple geometries in one vertex/index buffer.
        // Use this container to define the Submesh geometries so we can draw
        // the Submeshes individually.
        public Dictionary<string, SubmeshGeometry> DrawArgs { get; } = new Dictionary<string, SubmeshGeometry>();

        public VertexBufferView VertexBufferView => new VertexBufferView
        {
            BufferLocation = VertexBufferGPU.GPUVirtualAddress,
            StrideInBytes = VertexByteStride,
            SizeInBytes = VertexBufferByteSize
        };

        public IndexBufferView IndexBufferView => new IndexBufferView
        {
            BufferLocation = IndexBufferGPU.GPUVirtualAddress,
            Format = IndexFormat,
            SizeInBytes = IndexBufferByteSize
        };

        public void Dispose()
        {
            foreach (IDisposable disposable in _toDispose)
                disposable.Dispose();
        }

        // Below are helper factory methods in order to make use generic type inference.
        // Note that constructors do not support such inference.

        public static MeshGeometry New<TVertex, TIndex>(
            Device device,
            GraphicsCommandList commandList,
            IEnumerable<TVertex> vertices,
            IEnumerable<TIndex> indices,
            string name = "Default")
            where TVertex : struct
            where TIndex : struct
        {
            TVertex[] vertexArray = vertices.ToArray();
            TIndex[] indexArray = indices.ToArray();

            int vertexBufferByteSize = Utilities.SizeOf(vertexArray);
            Resource vertexBuffer = D3DUtil.CreateDefaultBuffer(
                device,
                commandList,
                vertexArray,
                vertexBufferByteSize,
                out Resource vertexBufferUploader);

            int indexBufferByteSize = Utilities.SizeOf(indexArray);
            Resource indexBuffer = D3DUtil.CreateDefaultBuffer(
                device, commandList,
                indexArray,
                indexBufferByteSize,
                out Resource indexBufferUploader);

            return new MeshGeometry
            {
                Name = name,
                VertexByteStride = Utilities.SizeOf<TVertex>(),
                VertexBufferByteSize = vertexBufferByteSize,
                VertexBufferGPU = vertexBuffer,
                VertexBufferCPU = vertexArray,
                IndexCount = indexArray.Length,
                IndexFormat = GetIndexFormat<TIndex>(),
                IndexBufferByteSize = indexBufferByteSize,
                IndexBufferGPU = indexBuffer,
                IndexBufferCPU = indexArray,
                _toDispose =
                {
                    vertexBuffer, vertexBufferUploader,
                    indexBuffer, indexBufferUploader
                }
            };
        }

        public static MeshGeometry New<TIndex>(
            Device device,
            GraphicsCommandList commandList,
            IEnumerable<TIndex> indices,
            string name = "Default")
            where TIndex : struct
        {
            TIndex[] indexArray = indices.ToArray();

            int indexBufferByteSize = Utilities.SizeOf(indexArray);
            Resource indexBuffer = D3DUtil.CreateDefaultBuffer(
                device,
                commandList,
                indexArray,
                indexBufferByteSize,
                out Resource indexBufferUploader);

            return new MeshGeometry
            {
                Name = name,
                IndexCount = indexArray.Length,
                IndexFormat = GetIndexFormat<TIndex>(),
                IndexBufferByteSize = indexBufferByteSize,
                IndexBufferGPU = indexBuffer,
                IndexBufferCPU = indexArray,
                _toDispose = { indexBuffer, indexBufferUploader }
            };
        }

        private static Format GetIndexFormat<TIndex>()
        {
            var format = Format.Unknown;
            if (typeof(TIndex) == typeof(int))
                format = Format.R32_UInt;
            else if (typeof(TIndex) == typeof(short))
                format = Format.R16_UInt;

            Debug.Assert(format != Format.Unknown);

            return format;
        }
    }
}
