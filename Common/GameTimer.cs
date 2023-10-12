using System.Diagnostics;

namespace DX12GameProgramming
{
    public class GameTimer
    {
        private readonly double _secondsPerCount;
        private double _deltaTime;

        private long _baseTime;
        private long _pausedTime;
        private long _stopTime;
        private long _prevTime;
        private long _currTime;

        private bool _stopped;

        public GameTimer()
        {
            Debug.Assert(Stopwatch.IsHighResolution,
                "System does not support high-resolution performance counter");

            _secondsPerCount = 0.0;
            _deltaTime = -1.0;
            _baseTime = 0;
            _pausedTime = 0;
            _prevTime = 0;
            _currTime = 0;
            _stopped = false;

            long countsPerSec = Stopwatch.Frequency;
            _secondsPerCount = 1.0 / countsPerSec;
        }

        public float TotalTime
        {
            get
            {
                if (_stopped)
                    return (float)(((_stopTime - _pausedTime) - _baseTime) * _secondsPerCount);

                return (float)(((_currTime - _pausedTime) - _baseTime) * _secondsPerCount);
            }
        }

        public float DeltaTime => (float)_deltaTime;

        public void Reset()
        {
            long curTime = Stopwatch.GetTimestamp();
            _baseTime = curTime;
            _prevTime = curTime;
            _stopTime = 0;
            _stopped = false;
        }

        public void Start()
        {
            long startTime = Stopwatch.GetTimestamp();
            if (_stopped)
            {
                _pausedTime += (startTime - _stopTime);
                _prevTime = startTime;
                _stopTime = 0;
                _stopped = false;
            }
        }

        public void Stop()
        {
            if (!_stopped)
            {
                long curTime = Stopwatch.GetTimestamp();
                _stopTime = curTime;
                _stopped = true;
            }
        }

        public void Tick()
        {
            if (_stopped)
            {
                _deltaTime = 0.0;
                return;
            }

            long curTime = Stopwatch.GetTimestamp();
            _currTime = curTime;
            _deltaTime = (_currTime - _prevTime) * _secondsPerCount;

            _prevTime = _currTime;
            if (_deltaTime < 0.0)
                _deltaTime = 0.0;
        }
    }
}
