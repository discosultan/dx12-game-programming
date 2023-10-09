using SharpDX;
using SharpDX.Direct3D;

namespace DX12GameProgramming
{
    // Lightweight structure stores parameters to draw a shape. This will
    // vary from app-to-app.
    internal class RenderItem
    {
        // World matrix of the shape that describes the object's local space
        // relative to the world space, which defines the position, orientation,
        // and scale of the object in the world.
        public Matrix World { get; set; } = Matrix.Identity;

        public Matrix TexTransform { get; set; } = Matrix.Identity;

        // Dirty flag indicating the object data has changed and we need to update the constant buffer.
        // Because we have an object cbuffer for each FrameResource, we have to apply the
        // update to each FrameResource. Thus, when we modify obect data we should set
        // NumFramesDirty = gNumFrameResources so that each frame resource gets the update.
        public int NumFramesDirty { get; set; } = D3DApp.NumFrameResources;

        // Index into GPU constant buffer corresponding to the ObjectCB for this render item.
        public int ObjCBIndex { get; set; } = -1;

        public Material Mat { get; set; }
        public MeshGeometry Geo { get; set; }

        // Primitive topology.
        public PrimitiveTopology PrimitiveType { get; set; } = PrimitiveTopology.TriangleList;

        // DrawIndexedInstanced parameters.
        public int IndexCount { get; set; }
        public int StartIndexLocation { get; set; }
        public int BaseVertexLocation { get; set; }
    }

    internal enum RenderLayer
    {
        Opaque,
        OpaqueDynamicReflectors,
        Sky
    }
}
