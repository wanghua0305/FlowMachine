using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlowMachine.Core;

namespace FlowMachine.Runtime
{
    public interface IBusController
    {
        BusState State { get; }
        string FaultStationId { get; }
        event EventHandler<BusStateChangedEventArgs> StateChanged;
        event EventHandler<RuntimeLogEntry> Log;
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync();
        Task PauseAsync();
        Task ResumeAsync();
        Task ResetAsync();
        Task HomeAsync(CancellationToken cancellationToken);
        Task RunTestStationAsync(IStation station, CancellationToken cancellationToken);
    }

    public interface IStationScheduleStrategy
    {
        IList<IStation> Order(IEnumerable<IStation> stations);
    }

    public sealed class DeterministicStationScheduleStrategy : IStationScheduleStrategy
    {
        public IList<IStation> Order(IEnumerable<IStation> stations)
        {
            return stations.OrderBy(station => station.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(station => station.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    public sealed class BusStateChangedEventArgs : EventArgs
    {
        public BusStateChangedEventArgs(BusState previous, BusState current)
        {
            Previous = previous;
            Current = current;
        }

        public BusState Previous { get; private set; }
        public BusState Current { get; private set; }
    }

    public sealed class BusController : IBusController
    {
        private readonly object _sync = new object();
        private readonly IList<IStation> _stations;
        private readonly IWorkflowExecutor _executor;
        private readonly IStationScheduleStrategy _schedule;
        private Task _activeTask;
        private CancellationTokenSource _activeCancellation;
        private PauseGate _activePauseGate;
        private IStation _activeStation;
        private string _activeKind;
        private BusState _state;
        private string _faultStationId;

        public BusController(IEnumerable<IStation> stations, IWorkflowExecutor executor,
            IStationScheduleStrategy schedule)
        {
            _stations = stations as IList<IStation> ?? stations.ToList();
            _executor = executor;
            _schedule = schedule;
            _state = BusState.Idle;

            WorkflowExecutor workflowExecutor = executor as WorkflowExecutor;
            if (workflowExecutor != null)
            {
                workflowExecutor.Log += ForwardLog;
            }
        }

        public BusState State
        {
            get
            {
                lock (_sync)
                {
                    return _state;
                }
            }
        }

        public string FaultStationId
        {
            get
            {
                lock (_sync)
                {
                    return _faultStationId;
                }
            }
        }

        public event EventHandler<BusStateChangedEventArgs> StateChanged;
        public event EventHandler<RuntimeLogEntry> Log;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            IList<IStation> selected = _schedule.Order(
                _stations.Where(station => station.Configuration.Enabled && station.CanStartAutomatically));
            return BeginExecution("flow", selected, BusState.Starting, false, cancellationToken);
        }

        public Task HomeAsync(CancellationToken cancellationToken)
        {
            IList<IStation> selected = _schedule.Order(
                _stations.Where(station => station.Configuration.Enabled && station.CanBeHomed));
            return BeginExecution("home", selected, BusState.Homing, true, cancellationToken);
        }

        public Task RunTestStationAsync(IStation station, CancellationToken cancellationToken)
        {
            if (station == null || !station.CanRunManually)
            {
                throw new InvalidOperationException("Only a TestStation can be run manually.");
            }

            if (!station.Configuration.Enabled)
            {
                throw new InvalidOperationException("The selected TestStation is disabled.");
            }

            return BeginExecution("test:" + station.Id, new[] { station }, BusState.Starting,
                false, cancellationToken);
        }

        public async Task StopAsync()
        {
            Task active;
            CancellationTokenSource cancellation;
            PauseGate gate;
            lock (_sync)
            {
                active = _activeTask;
                cancellation = _activeCancellation;
                gate = _activePauseGate;
                if (active == null)
                {
                    return;
                }
            }

            if (State != BusState.Completed && State != BusState.Faulted)
            {
                ChangeState(BusState.Stopping);
            }
            if (gate != null)
            {
                gate.Cancel();
            }

            if (cancellation != null)
            {
                cancellation.Cancel();
            }

            try
            {
                await active.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
        }

        public async Task PauseAsync()
        {
            PauseGate gate;
            lock (_sync)
            {
                if (_activeTask == null || _activeTask.IsCompleted)
                {
                    throw new InvalidOperationException("There is no active operation to pause.");
                }

                if (_state == BusState.Paused || _state == BusState.Pausing)
                {
                    return;
                }

                if (_state != BusState.Running && _state != BusState.Starting
                    && _state != BusState.Homing)
                {
                    throw new InvalidOperationException("The active operation cannot be paused in " + _state + ".");
                }

                gate = _activePauseGate;
                if (gate == null)
                {
                    throw new InvalidOperationException("The operation has no pause gate.");
                }
            }

            ChangeState(BusState.Pausing);
            IStation current;
            lock (_sync)
            {
                current = _activeStation;
            }

            if (current != null && current.State == StationState.Running)
            {
                current.TransitionTo(StationState.Pausing);
            }

            gate.RequestPause();
            bool paused = await gate.WaitForPauseOrCompletionAsync().ConfigureAwait(false);
            if (paused)
            {
                lock (_sync)
                {
                    if (_activeTask != null && !_activeTask.IsCompleted && _state == BusState.Pausing)
                    {
                        ChangeState(BusState.Paused);
                    }
                }
            }
        }

        public Task ResumeAsync()
        {
            PauseGate gate;
            lock (_sync)
            {
                if (_state != BusState.Paused || _activeTask == null)
                {
                    throw new InvalidOperationException("The bus is not paused.");
                }

                gate = _activePauseGate;
            }

            gate.Resume();
            ChangeState(BusState.Running);
            return Task.FromResult(0);
        }

        public Task ResetAsync()
        {
            lock (_sync)
            {
                if (_activeTask != null && !_activeTask.IsCompleted)
                {
                    throw new InvalidOperationException("Stop and wait for the active operation before reset.");
                }

                if (_state != BusState.Idle && _state != BusState.Completed && _state != BusState.Faulted)
                {
                    throw new InvalidOperationException("Reset is not allowed in " + _state + ".");
                }

                foreach (IStation station in _stations)
                {
                    if (station.State == StationState.Completed || station.State == StationState.Faulted)
                    {
                        station.TransitionTo(StationState.Idle);
                    }

                    station.SetFault(null);
                }

                _faultStationId = null;
            }

            ChangeState(BusState.Idle);
            WriteLog(null, null, "Info", "Bus state reset; no station was started or homed.");
            return Task.FromResult(0);
        }

        private Task BeginExecution(string kind, IList<IStation> stations, BusState initialState,
            bool isHoming, CancellationToken cancellationToken)
        {
            if (stations.Count == 0)
            {
                return Task.FromResult(0);
            }

            lock (_sync)
            {
                if (_activeTask != null && !_activeTask.IsCompleted)
                {
                    if (_activeKind == kind)
                    {
                        return _activeTask;
                    }

                    throw new InvalidOperationException(
                        "Another bus operation is active; stop it before starting " + kind + ".");
                }

                if (_state == BusState.Faulted)
                {
                    throw new InvalidOperationException("Reset the bus after a fault before starting again.");
                }

                _activeKind = kind;
                _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activePauseGate = new PauseGate();
                _faultStationId = null;
                ChangeState(initialState);
                CancellationTokenSource cancellation = _activeCancellation;
                PauseGate pauseGate = _activePauseGate;
                _activeTask = Task.Run(delegate
                {
                    return RunSessionAsync(stations, pauseGate, cancellation, isHoming);
                });
                return _activeTask;
            }
        }

        private async Task RunSessionAsync(IList<IStation> stations, PauseGate pauseGate,
            CancellationTokenSource cancellation, bool isHoming)
        {
            bool failed = false;
            try
            {
                lock (_sync)
                {
                    if (_state == BusState.Starting)
                    {
                        ChangeState(BusState.Running);
                    }
                    else if (_state == BusState.Homing)
                    {
                        ChangeState(BusState.Homing);
                    }
                }
                IList<KeyValuePair<IStation, WorkflowSnapshot>> snapshots = stations.Select(station =>
                    new KeyValuePair<IStation, WorkflowSnapshot>(station,
                        new WorkflowSnapshot(station.Id, station.Name, station.TypeId,
                            station.Configuration.Nodes, station.Configuration.Connections))).ToList();

                foreach (KeyValuePair<IStation, WorkflowSnapshot> item in snapshots)
                {
                    IStation station = item.Key;
                    cancellation.Token.ThrowIfCancellationRequested();
                    lock (_sync)
                    {
                        _activeStation = station;
                    }

                    await _executor.ExecuteAsync(station, item.Value, pauseGate,
                        cancellation.Token, isHoming && station.CanBeHomed).ConfigureAwait(false);
                }

                ChangeState(BusState.Completed);
            }
            catch (OperationCanceledException)
            {
                ChangeState(BusState.Idle);
            }
            catch (Exception exception)
            {
                failed = true;
                lock (_sync)
                {
                    _faultStationId = _activeStation == null ? null : _activeStation.Id;
                }

                WriteLog(_activeStation, null, "Error", "Bus operation stopped after station failure: "
                    + exception.Message);
                ChangeState(BusState.Faulted);
                throw;
            }
            finally
            {
                pauseGate.Complete();
                lock (_sync)
                {
                    _activeTask = null;
                    _activeStation = null;
                    _activePauseGate = null;
                    _activeKind = null;
                    _activeCancellation = null;
                }

                cancellation.Dispose();
                if (!failed && State == BusState.Stopping)
                {
                    ChangeState(BusState.Idle);
                }
            }
        }

        private void ChangeState(BusState state)
        {
            BusState previous;
            lock (_sync)
            {
                previous = _state;
                if (previous == BusState.Stopping && state == BusState.Completed)
                {
                    return;
                }

                if (!IsAllowedTransition(previous, state))
                {
                    throw new InvalidOperationException(
                        "Illegal bus state transition: " + previous + " -> " + state + ".");
                }

                _state = state;
            }

            if (previous != state)
            {
                EventHandler<BusStateChangedEventArgs> handler = StateChanged;
                if (handler != null)
                {
                    handler(this, new BusStateChangedEventArgs(previous, state));
                }
            }
        }

        private static bool IsAllowedTransition(BusState from, BusState to)
            {
                if (from == to)
                {
                    return true;
                }

                switch (from)
                {
                    case BusState.Idle:
                        return to == BusState.Starting || to == BusState.Homing;
                    case BusState.Starting:
                        return to == BusState.Running || to == BusState.Pausing
                            || to == BusState.Stopping || to == BusState.Completed
                            || to == BusState.Faulted || to == BusState.Idle;
                    case BusState.Running:
                    case BusState.Homing:
                        return to == BusState.Pausing || to == BusState.Stopping
                            || to == BusState.Completed || to == BusState.Faulted
                            || to == BusState.Idle;
                    case BusState.Pausing:
                        return to == BusState.Paused || to == BusState.Stopping
                            || to == BusState.Completed || to == BusState.Faulted
                            || to == BusState.Idle;
                    case BusState.Paused:
                        return to == BusState.Running || to == BusState.Stopping
                            || to == BusState.Completed || to == BusState.Faulted
                            || to == BusState.Idle;
                    case BusState.Stopping:
                        return to == BusState.Idle || to == BusState.Faulted;
                    case BusState.Completed:
                        return to == BusState.Idle || to == BusState.Starting || to == BusState.Homing;
                    case BusState.Faulted:
                        return to == BusState.Idle;
                    default:
                        return false;
            }
        }

        private void ForwardLog(object sender, RuntimeLogEntry entry)
        {
            EventHandler<RuntimeLogEntry> handler = Log;
            if (handler != null)
            {
                handler(this, entry);
            }
        }

        private void WriteLog(IStation station, string nodeId, string level, string message)
        {
            EventHandler<RuntimeLogEntry> handler = Log;
            if (handler != null)
            {
                handler(this, new RuntimeLogEntry(DateTime.Now,
                    station == null ? null : station.Id, station == null ? null : station.Name,
                    nodeId, level, message));
            }
        }
    }
}
