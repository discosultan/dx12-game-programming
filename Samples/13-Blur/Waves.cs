using System.Diagnostics;
using System.Threading.Tasks;
using SharpDX;

namespace DX12GameProgramming
{
    internal class Waves
    {
        // Simulation constants we can precompute.
        private readonly float _k1;
        private readonly float _k2;
        private readonly float _k3;

        private float _t;
        private readonly float _timeStep;
        private readonly float _spatialStep;

        private Vector3[] _prevSolution;
        private Vector3[] _currSolution;
        private readonly Vector3[] _normals;
        private readonly Vector3[] _tangentX;

        public Waves(int m, int n, float dx, float dt, float speed, float damping)
        {
            RowCount = m;
            ColumnCount = n;

            VertexCount = m * n;
            TriangleCount = (m - 1) * (n - 1) * 2;

            _timeStep = dt;
            _spatialStep = dx;

            float d = damping * dt + 2.0f;
            float e = (speed * speed) * (dt * dt) / (dx * dx);
            _k1 = (damping * dt - 2.0f) / d;
            _k2 = (4.0f - 8.0f * e) / d;
            _k3 = (2.0f * e) / d;

            _prevSolution = new Vector3[VertexCount];
            _currSolution = new Vector3[VertexCount];
            _normals = new Vector3[VertexCount];
            _tangentX = new Vector3[VertexCount];

            // Generate grid vertices in system memory.

            float halfWidth = (n - 1) * dx * 0.5f;
            float halfDepth = (m - 1) * dx * 0.5f;
            for (int i = 0; i < m; i++)
            {
                float z = halfDepth - i * dx;
                for (int j = 0; j < n; j++)
                {
                    float x = -halfWidth + j * dx;

                    _prevSolution[i * n + j] = new Vector3(x, 0.0f, z);
                    _currSolution[i * n + j] = new Vector3(x, 0.0f, z);
                    _normals[i * n + j] = Vector3.UnitY;
                    _tangentX[i * n + j] = Vector3.UnitX;
                }
            }
        }

        public int RowCount { get; }
        public int ColumnCount { get; }
        public int VertexCount { get; }
        public int TriangleCount { get; }
        public float Width => ColumnCount * _spatialStep;
        public float Depth => RowCount * _spatialStep;

        // Returns the solution at the ith grid point.
        public Vector3 Position(int i) => _currSolution[i];

        // Returns the solution normal at the ith grid point.
        public Vector3 Normal(int i) => _normals[i];

        // Returns the unit tangent vector at the ith grid point in the local x-axis direction.
        public Vector3 TangentX(int i) => _tangentX[i];

        public void Update(float dt)
        {
            // Accumulate time.
            _t += dt;

            // Only update the simulation at the specified time step.
            if (_t >= _timeStep)
            {
                // Only update interior points; we use zero boundary conditions.
                Parallel.For(1, RowCount - 1, i =>
                //for(int i = 1; i < RowCount - 1; i++)
                {
                    for (int j = 1; j < ColumnCount - 1; j++)
                    {
                        // After this update we will be discarding the old previous
                        // buffer, so overwrite that buffer with the new update.
                        // Note how we can do this inplace (read/write to same element)
                        // because we won't need prev_ij again and the assignment happens last.

                        // Note j indexes x and i indexes z: h(x_j, z_i, t_k)
                        // Moreover, our +z axis goes "down"; this is just to
                        // keep consistent with our row indices going down.

                        _prevSolution[i * ColumnCount + j].Y =
                            _k1 * _prevSolution[i * ColumnCount + j].Y +
                            _k2 * _currSolution[i * ColumnCount + j].Y +
                            _k3 * (_currSolution[(i + 1) * ColumnCount + j].Y +
                                 _currSolution[(i - 1) * ColumnCount + j].Y +
                                 _currSolution[i * ColumnCount + j + 1].Y +
                                 _currSolution[i * ColumnCount + j - 1].Y);
                    }
                });

                // We just overwrote the previous buffer with the new data, so
                // this data needs to become the current solution and the old
                // current solution becomes the new previous solution.
                Vector3[] temp = _prevSolution;
                _prevSolution = _currSolution;
                _currSolution = temp;

                // Reset time.
                _t = 0.0f;

                //
                // Compute normals using finite difference scheme.
                //
                Parallel.For(1, RowCount - 1, i =>
                //for(int i = 1; i < RowCount - 1; i++)
                {
                    for (int j = 1; j < ColumnCount - 1; j++)
                    {
                        float l = _currSolution[i * ColumnCount + j - 1].Y;
                        float r = _currSolution[i * ColumnCount + j + 1].Y;
                        float t = _currSolution[(i - 1) * ColumnCount + j].Y;
                        float b = _currSolution[(i + 1) * ColumnCount + j].Y;

                        _normals[i * ColumnCount + j] = Vector3.Normalize(new Vector3(-r + l, 2.0f * _spatialStep, b - t));
                        _tangentX[i * ColumnCount + j] = Vector3.Normalize(new Vector3(2.0f * _spatialStep, r - l, 0.0f));
                    }
                });
            }
        }

        public void Disturb(int i, int j, float magnitude)
        {
            // Don't disturb boundaries.
            Debug.Assert(i > 1 && i < RowCount - 2);
            Debug.Assert(j > 1 && j < ColumnCount - 2);

            float halfMag = 0.5f * magnitude;

            // Disturb the ijth vertex height and its neighbors.
            _currSolution[i * ColumnCount + j].Y += magnitude;
            _currSolution[i * ColumnCount + j + 1].Y += halfMag;
            _currSolution[i * ColumnCount + j - 1].Y += halfMag;
            _currSolution[(i + 1) * ColumnCount + j].Y += halfMag;
            _currSolution[(i - 1) * ColumnCount + j].Y += halfMag;
        }
    }
}
