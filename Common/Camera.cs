using SharpDX;

namespace DX12GameProgramming
{
    public class Camera
    {
        private bool _viewDirty = true;

        public Camera()
        {
            SetLens(MathUtil.PiOverFour, 1.0f, 1.0f, 1000.0f);
        }

        public Vector3 Position { get; set; }
        public Vector3 Right { get; private set; } = Vector3.UnitX;
        public Vector3 Up { get; private set; } = Vector3.UnitY;
        public Vector3 Look { get; private set; } = Vector3.UnitZ;

        public float NearZ { get; private set; }
        public float FarZ { get; private set; }
        public float Aspect { get; private set; }
        public float FovY { get; private set; }
        public float FovX
        {
            get
            {
                float halfWidth = 0.5f * NearWindowWidth;
                return 2.0f * MathHelper.Atanf(halfWidth / NearZ);
            }
        }
        public float NearWindowHeight { get; private set; }
        public float NearWindowWidth => Aspect * NearWindowHeight;
        public float FarWindowHeight { get; private set; }
        public float FarWindowWidth => Aspect * FarWindowHeight;

        public Matrix View { get; private set; } = Matrix.Identity;
        public Matrix Proj { get; private set; } = Matrix.Identity;

        public Matrix ViewProj => View * Proj;
        public BoundingFrustum Frustum => new BoundingFrustum(ViewProj);

        public void SetLens(float fovY, float aspect, float zn, float zf)
        {
            FovY = fovY;
            Aspect = aspect;
            NearZ = zn;
            FarZ = zf;

            NearWindowHeight = 2.0f * zn * MathHelper.Tanf(0.5f * fovY);
            FarWindowHeight = 2.0f * zf * MathHelper.Tanf(0.5f * fovY);

            Proj = Matrix.PerspectiveFovLH(fovY, aspect, zn, zf);
        }

        public void LookAt(Vector3 pos, Vector3 target, Vector3 up)
        {
            Position = pos;
            Look = Vector3.Normalize(target - pos);
            Right = Vector3.Normalize(Vector3.Cross(up, Look));
            Up = Vector3.Cross(Look, Right);
            _viewDirty = true;
        }

        public void Strafe(float d)
        {
            Position += Right * d;
            _viewDirty = true;
        }

        public void Walk(float d)
        {
            Position += Look * d;
            _viewDirty = true;
        }

        public void Pitch(float angle)
        {
            // Rotate up and look vector about the right vector.

            Matrix r = Matrix.RotationAxis(Right, angle);

            Up = Vector3.TransformNormal(Up, r);
            Look = Vector3.TransformNormal(Look, r);

            _viewDirty = true;
        }

        public void RotateY(float angle)
        {
            // Rotate the basis vectors about the world y-axis.

            Matrix r = Matrix.RotationY(angle);

            Right = Vector3.TransformNormal(Right, r);
            Up = Vector3.TransformNormal(Up, r);
            Look = Vector3.TransformNormal(Look, r);

            _viewDirty = true;
        }

        public void UpdateViewMatrix()
        {
            if (!_viewDirty) return;

            // Keep camera's axes orthogonal to each other and of unit length.
            Look = Vector3.Normalize(Look);
            Up = Vector3.Normalize(Vector3.Cross(Look, Right));

            // U, L already ortho-normal, so no need to normalize cross product.
            Right = Vector3.Cross(Up, Look);

            // Fill in the view matrix entries.
            float x = -Vector3.Dot(Position, Right);
            float y = -Vector3.Dot(Position, Up);
            float z = -Vector3.Dot(Position, Look);

            View = new Matrix(
                Right.X, Up.X, Look.X, 0.0f,
                Right.Y, Up.Y, Look.Y, 0.0f,
                Right.Z, Up.Z, Look.Z, 0.0f,
                x, y, z, 1.0f
            );

            _viewDirty = false;
        }

        public Ray GetPickingRay(Point sp, int clientWidth, int clientHeight)
        {
            Matrix p = Proj;

            // Convert screen pixel to view space.
            float vx = (2f * sp.X / clientWidth - 1f) / p.M11;
            float vy = (-2f * sp.Y / clientHeight + 1f) / p.M22;

            var ray = new Ray(Vector3.Zero, new Vector3(vx, vy, 1));
            Matrix v = View;
            Matrix invView = Matrix.Invert(v);

            Matrix toWorld = invView;

            ray = new Ray(
                Vector3.TransformCoordinate(ray.Position, toWorld),
                Vector3.TransformNormal(ray.Direction, toWorld));

            return ray;
        }
    }
}
