using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FlowMachine.Core
{
    public enum IoDirection
    {
        Input,
        Output
    }

    public sealed class IoPoint
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public IoDirection Direction { get; set; }
    }

    public interface IDeviceService
    {
        IIoService Io { get; }
        IAxisService Axes { get; }
        ICylinderService Cylinders { get; }
    }

    public interface IIoService
    {
        IList<IoPoint> GetPoints(IoDirection direction);
        Task<bool> ReadAsync(string pointId, CancellationToken cancellationToken);
        Task WriteAsync(string pointId, bool value, CancellationToken cancellationToken);
    }

    public interface IAxisService
    {
        Task HomeAsync(string axisId, CancellationToken cancellationToken);
        Task MoveAbsoluteAsync(string axisId, double position, double speed, double acceleration,
            CancellationToken cancellationToken);
        Task MoveRelativeAsync(string axisId, double distance, double speed, double acceleration,
            CancellationToken cancellationToken);
        Task StopAsync(string axisId, CancellationToken cancellationToken);
    }

    public interface ICylinderService
    {
        Task MoveAsync(string cylinderId, string action, CancellationToken cancellationToken);
    }

    public interface IDeviceResourceLock
    {
        Task<IDisposable> AcquireAsync(string resourceId, CancellationToken cancellationToken);
    }
}
