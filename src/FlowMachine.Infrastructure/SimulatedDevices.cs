using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlowMachine.Core;

namespace FlowMachine.Infrastructure
{
    public sealed class SimulatedDeviceService : IDeviceService
    {
        public SimulatedDeviceService()
        {
            ResourceLock = new DeviceResourceLock();
            Io = new SimulatedIoService();
            Axes = new SimulatedAxisService(ResourceLock);
            Cylinders = new SimulatedCylinderService(ResourceLock);
        }

        public IIoService Io { get; private set; }
        public IAxisService Axes { get; private set; }
        public ICylinderService Cylinders { get; private set; }
        public IDeviceResourceLock ResourceLock { get; private set; }
    }

    public sealed class SimulatedIoService : IIoService
    {
        private readonly IList<IoPoint> _points;
        private readonly ConcurrentDictionary<string, bool> _values;

        public SimulatedIoService()
        {
            _points = new List<IoPoint>
            {
                new IoPoint { Id = "DI-01", Name = "Simulated input 1", Direction = IoDirection.Input },
                new IoPoint { Id = "DI-02", Name = "Simulated input 2", Direction = IoDirection.Input },
                new IoPoint { Id = "DO-01", Name = "Simulated output 1", Direction = IoDirection.Output },
                new IoPoint { Id = "DO-02", Name = "Simulated output 2", Direction = IoDirection.Output }
            };
            _values = new ConcurrentDictionary<string, bool>();
        }

        public IList<IoPoint> GetPoints(IoDirection direction)
        {
            return _points.Where(point => point.Direction == direction)
                .Select(point => new IoPoint
                {
                    Id = point.Id,
                    Name = point.Name,
                    Direction = point.Direction
                }).ToList();
        }

        public Task<bool> ReadAsync(string pointId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDirection(pointId, IoDirection.Input);
            bool value;
            _values.TryGetValue(pointId, out value);
            return Task.FromResult(value);
        }

        public Task WriteAsync(string pointId, bool value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDirection(pointId, IoDirection.Output);
            _values[pointId] = value;
            return Task.FromResult(0);
        }

        private void EnsureDirection(string pointId, IoDirection direction)
        {
            if (!_points.Any(point => point.Id == pointId && point.Direction == direction))
            {
                throw new InvalidOperationException("Unknown " + direction + " point: " + pointId + ".");
            }
        }
    }

    public sealed class SimulatedAxisService : IAxisService
    {
        private readonly IDeviceResourceLock _resourceLock;
        private readonly ConcurrentDictionary<string, double> _positions;
        private readonly IList<string> _axisIds;

        public SimulatedAxisService(IDeviceResourceLock resourceLock)
        {
            _resourceLock = resourceLock;
            _positions = new ConcurrentDictionary<string, double>();
            _axisIds = new List<string> { "Axis-X", "Axis-Y" };
        }

        public IList<string> GetAxisIds()
        {
            return _axisIds.ToList();
        }

        public async Task HomeAsync(string axisId, CancellationToken cancellationToken)
        {
            using (await _resourceLock.AcquireAsync("axis:" + axisId, cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                _positions[axisId] = 0;
            }
        }

        public async Task MoveAbsoluteAsync(string axisId, double position, double speed,
            double acceleration, CancellationToken cancellationToken)
        {
            ValidateMotion(axisId, speed, acceleration);
            using (await _resourceLock.AcquireAsync("axis:" + axisId, cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                _positions[axisId] = position;
            }
        }

        public async Task MoveRelativeAsync(string axisId, double distance, double speed,
            double acceleration, CancellationToken cancellationToken)
        {
            ValidateMotion(axisId, speed, acceleration);
            using (await _resourceLock.AcquireAsync("axis:" + axisId, cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                double current;
                _positions.TryGetValue(axisId, out current);
                _positions[axisId] = current + distance;
            }
        }

        public Task StopAsync(string axisId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }

        private static void ValidateMotion(string axisId, double speed, double acceleration)
        {
            if (string.IsNullOrWhiteSpace(axisId) || speed <= 0 || acceleration <= 0)
            {
                throw new ArgumentException("Axis id, speed and acceleration must be valid.");
            }
        }
    }

    public sealed class SimulatedCylinderService : ICylinderService
    {
        private readonly IDeviceResourceLock _resourceLock;
        private readonly IList<string> _cylinderIds;

        public SimulatedCylinderService(IDeviceResourceLock resourceLock)
        {
            _resourceLock = resourceLock;
            _cylinderIds = new List<string> { "Cylinder-A", "Cylinder-B" };
        }

        public IList<string> GetCylinderIds()
        {
            return _cylinderIds.ToList();
        }

        public async Task MoveAsync(string cylinderId, string action, CancellationToken cancellationToken)
        {
            if (!_cylinderIds.Contains(cylinderId)
                || (action != "extend" && action != "retract"))
            {
                throw new ArgumentException("A cylinder id and extend/retract action are required.");
            }

            using (await _resourceLock.AcquireAsync("cylinder:" + cylinderId, cancellationToken)
                .ConfigureAwait(false))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public sealed class DeviceResourceLock : IDeviceResourceLock
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks;

        public DeviceResourceLock()
        {
            _locks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        }

        public async Task<IDisposable> AcquireAsync(string resourceId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                throw new ArgumentException("A resource id is required.", "resourceId");
            }

            SemaphoreSlim semaphore = _locks.GetOrAdd(resourceId, key => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Releaser(semaphore);
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim _semaphore;

            public Releaser(SemaphoreSlim semaphore)
            {
                _semaphore = semaphore;
            }

            public void Dispose()
            {
                SemaphoreSlim semaphore = Interlocked.Exchange(ref _semaphore, null);
                if (semaphore != null)
                {
                    semaphore.Release();
                }
            }
        }
    }
}
