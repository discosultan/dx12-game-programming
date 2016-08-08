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
}
