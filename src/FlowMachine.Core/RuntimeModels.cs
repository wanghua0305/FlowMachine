using System;

namespace FlowMachine.Core
{
    public enum StationState
    {
        Idle,
        Starting,
        Running,
        Pausing,
        Paused,
        Stopping,
        Completed,
        Faulted,
        Homing
    }

    public enum BusState
    {
        Idle,
        Starting,
        Running,
        Pausing,
        Paused,
        Stopping,
        Completed,
        Faulted,
        Homing
    }

    public enum NodeExecutionStatus
    {
        Pending,
        Running,
        Succeeded,
        Failed,
        Cancelled,
        Skipped
    }

    public sealed class StationStateChangedEventArgs : EventArgs
    {
        public StationStateChangedEventArgs(StationState previous, StationState current)
        {
            Previous = previous;
            Current = current;
        }

        public StationState Previous { get; private set; }
        public StationState Current { get; private set; }
    }

    public sealed class NodeStatusChangedEventArgs : EventArgs
    {
        public NodeStatusChangedEventArgs(string nodeId, NodeExecutionStatus status)
        {
            NodeId = nodeId;
            Status = status;
        }

        public string NodeId { get; private set; }
        public NodeExecutionStatus Status { get; private set; }
    }

    public sealed class RuntimeLogEntry
    {
        public RuntimeLogEntry(DateTime timestamp, string stationId, string stationName,
            string nodeId, string level, string message)
        {
            Timestamp = timestamp;
            StationId = stationId;
            StationName = stationName;
            NodeId = nodeId;
            Level = level;
            Message = message;
        }

        public DateTime Timestamp { get; private set; }
        public string StationId { get; private set; }
        public string StationName { get; private set; }
        public string NodeId { get; private set; }
        public string Level { get; private set; }
        public string Message { get; private set; }
    }
}
