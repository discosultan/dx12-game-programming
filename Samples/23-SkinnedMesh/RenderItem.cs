using SharpDX;
using SharpDX.Direct3D;
using System.Collections.Generic;

namespace DX12GameProgramming
{
    internal class SkinnedModelInstance
    {
        private SkinnedData _skinnedInfo;
        public SkinnedData SkinnedInfo {
            get { return _skinnedInfo; }
            set
            {
                _skinnedInfo = value;
                FinalTransforms = new List<Matrix>(_skinnedInfo.BoneCount);
            }
        }
        public List<Matrix> FinalTransforms { get; private set; }
        public string ClipName { get; set; }
        public float TimePos { get; set; }

        // Called every frame and increments the time position, interpolates the 
        // animations for each bone based on the current animation clip, and 
        // generates the final transforms which are ultimately set to the effect
        // for processing in the vertex shader.
        public void UpdateSkinnedAnimation(float dt)
        {
            TimePos += dt;

            // Loop animation
            if (TimePos > SkinnedInfo.GetClipEndTime(ClipName))
                TimePos = 0.0f;

            // Compute the final transforms for this time position.
            SkinnedInfo.GetFinalTransforms(ClipName, TimePos, FinalTransforms);
        }
    }

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

        // Only applicable to skinned render-items.
        public int SkinnedCBIndex { get; set; } = -1;

        // Null if this render-item is not animated by skinned mesh.
        public SkinnedModelInstance SkinnedModelInst { get; set; }
    }

    internal enum RenderLayer
    {
        Opaque,
        SkinnedOpaque,
        Debug,
        Sky
    }
}
