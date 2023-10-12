using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX;

namespace DX12GameProgramming
{
    internal static class M3DLoader
    {
        private static readonly char[] Separator = { ' ' };
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct Vertex
        {
            public Vector3 Pos;
            public Vector3 Normal;
            public Vector2 TexC;
            public Vector4 TangentU;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct SkinnedVertex
        {
            public Vector3 Pos;
            public Vector3 Normal;
            public Vector2 TexC;
            public Vector3 TangentU;
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
            public bool AlphaClip { get; set; }

            public string MaterialTypeName { get; set; }
            public string DiffuseMapName { get; set; }
            public string NormalMapName { get; set; }
        }

        public static void LoadM3D(string fileName,
            out List<Vertex> vertices,
            out List<short> indices,
            out List<Subset> subsets,
            out List<M3dMaterial> mats)
        {
            using (var reader = new StreamReader(fileName))
            {
                reader.ReadLine(); // File header text.
                string[] split = ReadAndSplitLine(reader);
                int numMaterials = int.Parse(split[1]);
                split = ReadAndSplitLine(reader);
                int numVertices = int.Parse(split[1]);
                split = ReadAndSplitLine(reader);
                int numTriangles = int.Parse(split[1]);
                reader.ReadLine();
                reader.ReadLine();

                ReadMaterials(reader, numMaterials, out mats);
                ReadSubsetTable(reader, numMaterials, out subsets);
                ReadVertices(reader, numVertices, out vertices);
                ReadTriangles(reader, numTriangles, out indices);
            }
        }

        public static void LoadM3D(string fileName,
            out List<SkinnedVertex> vertices,
            out List<short> indices,
            out List<Subset> subsets,
            out List<M3dMaterial> mats,
            out SkinnedData skinInfo)
        {
            using (var reader = new StreamReader(fileName))
            {
                reader.ReadLine(); // File header text.
                string[] split = ReadAndSplitLine(reader);
                int numMaterials = int.Parse(split[1]);
                split = ReadAndSplitLine(reader);
                int numVertices = int.Parse(split[1]);
                split = ReadAndSplitLine(reader);
                int numTriangles = int.Parse(split[1]);
                split = ReadAndSplitLine(reader);
                int numBones = int.Parse(split[1]);
                split = ReadAndSplitLine(reader);
                int numAnimationClips = int.Parse(split[1]);
                reader.ReadLine();

                List<Matrix> boneOffsets;
                List<int> boneIndexToParentIndex;
                Dictionary<string, AnimationClip> animations;

                ReadMaterials(reader, numMaterials, out mats);
                ReadSubsetTable(reader, numMaterials, out subsets);
                ReadSkinnedVertices(reader, numVertices, out vertices);
                ReadTriangles(reader, numTriangles, out indices);
                ReadBoneOffsets(reader, numBones, out boneOffsets);
                ReadBoneHierarchy(reader, numBones, out boneIndexToParentIndex);
                ReadAnimationClips(reader, numBones, numAnimationClips, out animations);

                skinInfo = new SkinnedData(boneIndexToParentIndex, boneOffsets, animations);
            }
        }

        private static void ReadMaterials(StreamReader reader, int numMaterials, out List<M3dMaterial> mats)
        {
            mats = new List<M3dMaterial>(numMaterials);
            reader.ReadLine(); // Materials header text.
            for (int i = 0; i < numMaterials; i++)
            {
                var mat = new M3dMaterial();
                string[] split = ReadAndSplitLine(reader);
                mat.Name = split[1];
                split = ReadAndSplitLine(reader);
                mat.DiffuseAlbedo = new Vector4(
                    float.Parse(split[1], Culture),
                    float.Parse(split[2], Culture),
                    float.Parse(split[3], Culture),
                    0.0f);
                split = ReadAndSplitLine(reader);
                mat.FresnelR0 = new Vector3(
                    float.Parse(split[1], Culture),
                    float.Parse(split[2], Culture),
                    float.Parse(split[3], Culture));
                split = ReadAndSplitLine(reader);
                mat.Roughness = float.Parse(split[1], Culture);
                split = ReadAndSplitLine(reader);
                mat.AlphaClip = split[1] == "1";
                split = ReadAndSplitLine(reader);
                mat.MaterialTypeName = split[1];
                split = ReadAndSplitLine(reader);
                mat.DiffuseMapName = split[1];
                split = ReadAndSplitLine(reader);
                mat.NormalMapName = split[1];
                mats.Add(mat);
                reader.ReadLine();
            }
        }

        private static void ReadSubsetTable(StreamReader reader, int numSubsets, out List<Subset> subsets)
        {
            subsets = new List<Subset>(numSubsets);
            reader.ReadLine(); // Subset header text.
            for (int i = 0; i < numSubsets; i++)
            {
                var subset = new Subset();
                string[] split = ReadAndSplitLine(reader);
                subset.Id = int.Parse(split[1]);
                subset.VertexStart = int.Parse(split[3]);
                subset.VertexCount = int.Parse(split[5]);
                subset.FaceStart = int.Parse(split[7]);
                subset.FaceCount = int.Parse(split[9]);
                subsets.Add(subset);
            }
            reader.ReadLine();
        }

        private static void ReadVertices(StreamReader reader, int numVertices, out List<Vertex> vertices)
        {
            vertices = new List<Vertex>(numVertices);
            reader.ReadLine(); // Vertices header text.
            for (int i = 0; i < numVertices; i++)
            {
                var vertex = new Vertex();
                string[] split = ReadAndSplitLine(reader);
                vertex.Pos = new Vector3(
                    float.Parse(split[1], Culture),
                    float.Parse(split[2], Culture),
                    float.Parse(split[3], Culture));
                split = ReadAndSplitLine(reader);
                vertex.TangentU = new Vector4(
                    float.Parse(split[1], Culture),
                    float.Parse(split[2], Culture),
                    float.Parse(split[3], Culture),
                    float.Parse(split[3], Culture));
                split = ReadAndSplitLine(reader);
                vertex.Normal = new Vector3(
                    float.Parse(split[1], Culture),
                    float.Parse(split[2], Culture),
                    float.Parse(split[3], Culture));
                split = ReadAndSplitLine(reader);
                vertex.TexC = new Vector2(
                    float.Parse(split[1], Culture),
                    float.Parse(split[2], Culture));
                vertices.Add(vertex);
                reader.ReadLine();
            }
        }

        private static void ReadSkinnedVertices(StreamReader reader, int numVertices, out List<SkinnedVertex> vertices)
        {
            vertices = new List<SkinnedVertex>(numVertices);
            reader.ReadLine(); // Vertices header text.
            for (int i = 0; i < numVertices; i++)
            {
                var vertex = new SkinnedVertex();
                string[] split = ReadAndSplitLine(reader);
                vertex.Pos = new Vector3(
                    float.Parse(split[1], Culture),
                    float.Parse(split[2], Culture),
                    float.Parse(split[3], Culture));
                split = ReadAndSplitLine(reader);
                vertex.TangentU = new Vector3(
                    float.Parse(split[1], Culture),
                    float.Parse(split[2], Culture),
                    float.Parse(split[3], Culture));
                split = ReadAndSplitLine(reader);
                vertex.Normal = new Vector3(
                    float.Parse(split[1], Culture),
                    float.Parse(split[2], Culture),
                    float.Parse(split[3], Culture));
                split = ReadAndSplitLine(reader);
                vertex.TexC = new Vector2(
                    float.Parse(split[1], Culture),
                    float.Parse(split[2], Culture));
                split = ReadAndSplitLine(reader);
                vertex.BoneWeights = new Vector3(
                    float.Parse(split[1], Culture),
                    float.Parse(split[2], Culture),
                    float.Parse(split[3], Culture));
                split = ReadAndSplitLine(reader);
                vertex.BoneIndices = new Color(
                    byte.Parse(split[1]),
                    byte.Parse(split[2]),
                    byte.Parse(split[3]),
                    byte.Parse(split[4]));
                vertices.Add(vertex);
                reader.ReadLine();
            }
        }

        private static void ReadTriangles(StreamReader reader, int numTriangles, out List<short> indices)
        {
            indices = new List<short>(numTriangles * 3);
            reader.ReadLine(); // Triangles header text.
            for (int i = 0; i < numTriangles; i++)
            {
                string[] split = ReadAndSplitLine(reader);
                indices.Add(short.Parse(split[0]));
                indices.Add(short.Parse(split[1]));
                indices.Add(short.Parse(split[2]));
            }
            reader.ReadLine();
        }

        private static void ReadBoneOffsets(StreamReader reader, int numBones, out List<Matrix> boneOffsets)
        {
            boneOffsets = new List<Matrix>(numBones);
            reader.ReadLine(); // Bone offsets header text.
            for (int i = 0; i < numBones; i++)
            {
                string[] split = ReadAndSplitLine(reader);
                boneOffsets.Add(new Matrix(
                    float.Parse(split[1], Culture), float.Parse(split[2], Culture), float.Parse(split[3], Culture), float.Parse(split[4], Culture),
                    float.Parse(split[5], Culture), float.Parse(split[6], Culture), float.Parse(split[7], Culture), float.Parse(split[8], Culture),
                    float.Parse(split[9], Culture), float.Parse(split[10], Culture), float.Parse(split[11], Culture), float.Parse(split[12], Culture),
                    float.Parse(split[13], Culture), float.Parse(split[14], Culture), float.Parse(split[15], Culture), float.Parse(split[16], Culture)));
            }
            reader.ReadLine();
        }

        private static void ReadBoneHierarchy(StreamReader reader, int numBones, out List<int> boneIndexToParentIndex)
        {
            boneIndexToParentIndex = new List<int>(numBones);
            reader.ReadLine(); // Bone hierarchy header text.
            for (int i = 0; i < numBones; i++)
            {
                string[] split = ReadAndSplitLine(reader);
                boneIndexToParentIndex.Add(int.Parse(split[1]));
            }
            reader.ReadLine();
        }

        private static void ReadAnimationClips(StreamReader reader, int numBones, int numAnimationClips, out Dictionary<string, AnimationClip> animations)
        {
            animations = new Dictionary<string, AnimationClip>(numAnimationClips);
            reader.ReadLine(); // Animation clips header text.
            for (int clipIndex = 0; clipIndex < numAnimationClips; clipIndex++)
            {
                string[] split = ReadAndSplitLine(reader);
                string clipName = split[1];
                reader.ReadLine(); // {

                var clip = new AnimationClip();
                clip.BoneAnimations.Capacity = numBones;

                for (int boneIndex = 0; boneIndex < numBones; boneIndex++)
                    ReadBoneKeyframe(reader, clip.BoneAnimations);

                reader.ReadLine(); // }

                animations[clipName] = clip;
            }
            reader.ReadLine();
        }

        private static void ReadBoneKeyframe(StreamReader reader, List<BoneAnimation> boneAnimations)
        {
            var boneAnimation = new BoneAnimation();
            string[] split = ReadAndSplitLine(reader);
            int numKeyframes = int.Parse(split[2]);
            reader.ReadLine(); // {

            boneAnimation.Keyframes.Capacity = numKeyframes;
            for (int i = 0; i < numKeyframes; i++)
            {
                var keyframe = new Keyframe();
                split = ReadAndSplitLine(reader);
                keyframe.Time = float.Parse(split[1], Culture);
                keyframe.Translation = new Vector3(
                    float.Parse(split[3], Culture),
                    float.Parse(split[4], Culture),
                    float.Parse(split[5], Culture));
                // We are only using uniform scalings.
                keyframe.Scale = float.Parse(split[7], Culture);
                keyframe.Rotation = new Quaternion(
                    float.Parse(split[11], Culture),
                    float.Parse(split[12], Culture),
                    float.Parse(split[13], Culture),
                    float.Parse(split[14], Culture));
                boneAnimation.Keyframes.Add(keyframe);
            }

            reader.ReadLine(); // }
            boneAnimations.Add(boneAnimation);
            reader.ReadLine();
        }

        private static string[] ReadAndSplitLine(StreamReader reader)
        {
            string line = reader.ReadLine();
            return line.Split(Separator);
        }
    }
}
