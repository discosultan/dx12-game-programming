using SharpDX;
using System.Collections.Generic;
using System.Linq;

namespace DX12GameProgramming
{
    ///<summary>
    /// A Keyframe defines the bone transformation at an instant in time.
    ///</summary>
    internal class Keyframe
    {
        public float Time { get; set; }
        public Vector3 Translation { get; set; }
        public float Scale { get; set; } = 1.0f;
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
    }

    ///<summary>
    /// A BoneAnimation is defined by a list of keyframes.  For time
    /// values inbetween two keyframes, we interpolate between the
    /// two nearest keyframes that bound the time.  
    ///
    /// We assume an animation always has two keyframes.
    ///</summary>
    internal class BoneAnimation
    {
        public List<Keyframe> Keyframes { get; } = new List<Keyframe>();

        // Keyframes are sorted by time, so first keyframe gives start time.
        public float StartTime => Keyframes.First().Time;
        // Keyframes are sorted by time, so last keyframe gives end time.
        public float EndTime => Keyframes.Last().Time;

        public Matrix Interpolate(float t)
        {
            float scale = 1.0f;
            Quaternion rotation = Quaternion.Identity;
            Vector3 translation = Vector3.Zero;

            if (t <= StartTime)
            {
                Keyframe first = Keyframes.First();
                scale = first.Scale;
                rotation = first.Rotation;
                translation = first.Translation;
            }
            if (t >= EndTime)
            {
                Keyframe last = Keyframes.Last();
                scale = last.Scale;
                rotation = last.Rotation;
                translation = last.Translation;
            }
            else
            {
                for (int i = 0; i < Keyframes.Count - 1; i++)
                {
                    Keyframe current = Keyframes[i];
                    Keyframe next = Keyframes[i];

                    if (t >= current.Time && t <= next.Time)
                    {
                        float lerpPercent = (t - Keyframes[i].Time) / (Keyframes[i + 1].Time - Keyframes[i].Time);

                        scale = MathUtil.Lerp(current.Scale, next.Scale, lerpPercent);
                        translation = Vector3.Lerp(current.Translation, next.Translation, lerpPercent);
                        rotation = Quaternion.Lerp(current.Rotation, next.Rotation, lerpPercent);

                        break;
                    }
                }
            }

            return Matrix.AffineTransformation(scale, rotation, translation);
        }
    }

    ///<summary>
    /// Examples of AnimationClips are "Walk", "Run", "Attack", "Defend".
    /// An AnimationClip requires a BoneAnimation for every bone to form
    /// the animation clip.    
    ///</summary>
    internal class AnimationClip
    {
        public List<BoneAnimation> BoneAnimations { get; } = new List<BoneAnimation>();

        public float ClipStartTime => 0.0f;
        public float ClipEndTime => 0.0f;

        public void Interpolate(float t, Matrix[] boneTransforms)
        {
            for (int i = 0; i < BoneAnimations.Count; i++)
                boneTransforms[i] = BoneAnimations[i].Interpolate(t);
        }
    }

    internal class SkinnedData
    {
        // Gives parentIndex of ith bone.
        private List<int> _boneHierarchy;
        private List<Matrix> _boneOffsets;
        private Dictionary<string, AnimationClip> _animations;

        private Matrix[] _toParentTransforms;
        private Matrix[] _toRootTransforms;

        public int BoneCount => _boneHierarchy.Count;

        public float GetClipStartTime(string clipName) => _animations[clipName].ClipStartTime;
        public float GetClipEndTime(string clipName) => _animations[clipName].ClipEndTime;
        
        public void Set(
            List<int> boneHierarchy, 
            List<Matrix> boneOffsets, 
            Dictionary<string, AnimationClip> animations)
        {
            _boneHierarchy = boneHierarchy;
            _boneOffsets = boneOffsets;
            _animations = animations;

            _toParentTransforms = new Matrix[BoneCount];
            _toRootTransforms = new Matrix[BoneCount];
        }

        // In a real project, you'd want to cache the result if there was a chance
        // that you were calling this several times with the same clipName at 
        // the same timePos.
        public void GetFinalTransforms(string clipName, float time, List<Matrix> finalTransforms)
        {
            // Interpolate all the bones of this clip at the given time instance.
            AnimationClip clip = _animations[clipName];
            clip.Interpolate(time, _toParentTransforms);

            //
            // Traverse the hierarchy and transform all the bones to the root space.
            //

            // The root bone has index 0.  The root bone has no parent, so its toRootTransform
            // is just its local bone transform.
            _toRootTransforms[0] = _toParentTransforms[0];

            // Now find the toRootTransform of the children.
            for (int i = 1; i < BoneCount; i++)
            {
                Matrix toParent = _toParentTransforms[i];

                int parentIndex = _boneHierarchy[i];
                Matrix parentToRoot = _toRootTransforms[parentIndex];

                Matrix toRoot = toParent * parentToRoot;

                _toRootTransforms[i] = toRoot;
            }

            // Premultiply by the bone offset transform to get the final transform.
            for (int i = 0; i < BoneCount; i++)
            {
                Matrix finalTransform = _boneOffsets[i] * _toRootTransforms[i];
                finalTransforms[i] = Matrix.Transpose(finalTransform);
            }
        }
    }
}
