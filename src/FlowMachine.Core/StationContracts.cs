using System;

namespace FlowMachine.Core
{
    public interface IStation
    {
        StationConfiguration Configuration { get; }
        string Id { get; }
        string Name { get; }
        string TypeId { get; }
        bool CanStartAutomatically { get; }
        bool CanBeHomed { get; }
        bool CanRunManually { get; }
        StationState State { get; }
        string CurrentNodeId { get; }
        string FaultMessage { get; }
        event EventHandler<StationStateChangedEventArgs> StateChanged;
        event EventHandler<NodeStatusChangedEventArgs> NodeStatusChanged;
        void SetNodeStatus(string nodeId, NodeExecutionStatus status);
        void SetFault(string message);
        void TransitionTo(StationState state);
        bool TryTransitionTo(StationState state);
    }

    public abstract class StationBase : IStation
    {
        private readonly object _sync = new object();
        private StationState _state;
        private string _currentNodeId;
        private string _faultMessage;

        protected StationBase(StationConfiguration configuration)
        {
            Configuration = configuration;
            _state = StationState.Idle;
        }

        public StationConfiguration Configuration { get; private set; }
        public string Id { get { return Configuration.Id; } }
        public string Name { get { return Configuration.Name; } }
        public abstract string TypeId { get; }
        public abstract bool CanStartAutomatically { get; }
        public abstract bool CanBeHomed { get; }
        public abstract bool CanRunManually { get; }
        public StationState State
        {
            get { lock (_sync) { return _state; } }
        }
        public string CurrentNodeId
        {
            get { lock (_sync) { return _currentNodeId; } }
        }
        public string FaultMessage
        {
            get { lock (_sync) { return _faultMessage; } }
        }
        public event EventHandler<StationStateChangedEventArgs> StateChanged;
        public event EventHandler<NodeStatusChangedEventArgs> NodeStatusChanged;

        public void TransitionTo(StationState state)
        {
            if (!TryTransitionTo(state))
            {
                throw new InvalidOperationException("Illegal station state transition to " + state + ".");
            }
        }

        public bool TryTransitionTo(StationState state)
        {
            StationState previous;
            lock (_sync)
            {
                previous = _state;
                if (!IsAllowedTransition(previous, state))
                {
                    return false;
                }

                _state = state;
                if (state == StationState.Idle || state == StationState.Completed)
                {
                    _currentNodeId = null;
                }
            }

            EventHandler<StationStateChangedEventArgs> handler = StateChanged;
            if (handler != null)
            {
                handler(this, new StationStateChangedEventArgs(previous, state));
            }

            return true;
        }

        public void SetNodeStatus(string nodeId, NodeExecutionStatus status)
        {
            lock (_sync)
            {
                if (status == NodeExecutionStatus.Running)
                {
                    _currentNodeId = nodeId;
                }
            }

            EventHandler<NodeStatusChangedEventArgs> handler = NodeStatusChanged;
            if (handler != null)
            {
                handler(this, new NodeStatusChangedEventArgs(nodeId, status));
            }
        }

        public void SetFault(string message)
        {
            lock (_sync)
            {
                _faultMessage = message;
            }
        }

        private static bool IsAllowedTransition(StationState from, StationState to)
        {
            if (from == to)
            {
                return false;
            }

            switch (from)
            {
                case StationState.Idle:
                    return to == StationState.Starting || to == StationState.Homing;
                case StationState.Starting:
                case StationState.Homing:
                    return to == StationState.Running || to == StationState.Stopping
                        || to == StationState.Faulted;
                case StationState.Running:
                    return to == StationState.Pausing || to == StationState.Stopping
                        || to == StationState.Completed || to == StationState.Faulted;
                case StationState.Pausing:
                    return to == StationState.Paused || to == StationState.Stopping
                        || to == StationState.Faulted || to == StationState.Completed;
                case StationState.Paused:
                    return to == StationState.Running || to == StationState.Stopping
                        || to == StationState.Faulted;
                case StationState.Stopping:
                    return to == StationState.Idle || to == StationState.Faulted;
                case StationState.Completed:
                case StationState.Faulted:
                    return to == StationState.Idle;
                default:
                    return false;
            }
        }
    }

    public sealed class HomeStation : StationBase
    {
        public HomeStation(StationConfiguration configuration) : base(configuration) { }
        public override string TypeId { get { return StationTypeIds.Home; } }
        public override bool CanStartAutomatically { get { return false; } }
        public override bool CanBeHomed { get { return true; } }
        public override bool CanRunManually { get { return false; } }
    }

    public sealed class FlowStation : StationBase
    {
        public FlowStation(StationConfiguration configuration) : base(configuration) { }
        public override string TypeId { get { return StationTypeIds.Flow; } }
        public override bool CanStartAutomatically { get { return true; } }
        public override bool CanBeHomed { get { return false; } }
        public override bool CanRunManually { get { return false; } }
    }

    public sealed class TestStation : StationBase
    {
        public TestStation(StationConfiguration configuration) : base(configuration) { }
        public override string TypeId { get { return StationTypeIds.Test; } }
        public override bool CanStartAutomatically { get { return false; } }
        public override bool CanBeHomed { get { return false; } }
        public override bool CanRunManually { get { return true; } }
    }

    public interface IStationFactory
    {
        IStation Create(StationConfiguration configuration);
        void Register(string stationTypeId, Func<StationConfiguration, IStation> creator);
    }
}
