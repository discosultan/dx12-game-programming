using SharpDX;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DX12GameProgramming
{
    internal static class LoadM3d
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Vertex
        {
            public Vector3 Pos;
            public Vector3 Normal;
            public Vector2 TexC;
            public Vector4 TangentU;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SkinnedVertex
        {
            public Vector3 Pos;
            public Vector3 Normal;
            public Vector2 TexC;
            public Vector4 TangentU;
            public Vector3 BoneWeights;
            public Color BoneIndices;
        }

        public class Subset
        {
            public int Id { get; set; } = -1;
            public int VertexStart { get; set; }
            public int VertexCount { get; set; }
            public int FaceStart { get; set; }
            public int FaceCount { get; set; }
        }

        public class M3dMaterial
        {
            public string Name { get; set; }

            public Vector4 DiffuseAlbedo { get; set; } = Vector4.One;
            public Vector3 FresnelR0 { get; set; } = new Vector3(0.01f);
            public float Roughness { get; set; } = 0.8f;
            public bool AlphaClip { get; set; } = false;

            public string MaterialTypeName { get; set; }
            public string DiffuseMapName { get; set; }
            public string NormalMapName { get; set; }
        }

        public static bool LoadM3D(string fileName,
            List<Vertex> vertices,
            List<short> indices,
            List<Subset> subsets,
            List<M3dMaterial> mats)
        {
            return false;
        }

        public static bool LoadM3D(string fileName,
            List<Vertex> vertices,
            List<short> indices,
            List<Subset> subsets,
            List<M3dMaterial> mats,
            SkinnedData skinInfo)
        {
            return false;
        }
    }
}
