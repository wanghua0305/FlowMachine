using System;
using System.Collections.Generic;

namespace FlowMachine.Core
{
    public static class StationTypeIds
    {
        public const string Home = "home";
        public const string Flow = "flow";
        public const string Test = "test";
    }

    public static class NodeTypeIds
    {
        public const string Start = "start";
        public const string End = "end";
        public const string Delay = "delay";
        public const string Log = "log";
        public const string Condition = "condition";
        public const string Cylinder = "cylinder";
        public const string Axis = "axis";
    }

    public static class BranchIds
    {
        public const string Next = "next";
        public const string True = "true";
        public const string False = "false";
    }

    public sealed class StationConfiguration
    {
        public StationConfiguration()
        {
            Id = Guid.NewGuid().ToString("N");
            Nodes = new List<NodeDefinition>();
            Connections = new List<ConnectionDefinition>();
            Enabled = true;
        }

        public string Id { get; set; }
        public string Name { get; set; }
        public string StationType { get; set; }
        public bool Enabled { get; set; }
        public List<NodeDefinition> Nodes { get; set; }
        public List<ConnectionDefinition> Connections { get; set; }
    }

    public sealed class NodeDefinition
    {
        public NodeDefinition()
        {
            Id = Guid.NewGuid().ToString("N");
            Type = NodeTypeIds.Log;
            Message = string.Empty;
            ConditionKey = "simulated";
            Cylinder = new CylinderNodeConfiguration();
            Axis = new AxisNodeConfiguration();
        }

        public string Id { get; set; }
        public string Type { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public int DelayMilliseconds { get; set; }
        public string Message { get; set; }
        public string ConditionKey { get; set; }
        public bool ConditionValue { get; set; }
        public CylinderNodeConfiguration Cylinder { get; set; }
        public AxisNodeConfiguration Axis { get; set; }
    }

    public sealed class ConnectionDefinition
    {
        public string SourceNodeId { get; set; }
        public string TargetNodeId { get; set; }
        public string Branch { get; set; }
    }

    public sealed class CylinderNodeConfiguration
    {
        public string CylinderId { get; set; }
        public string Action { get; set; }
        public string OutputPointId { get; set; }
        public string ExtendedInputPointId { get; set; }
        public string RetractedInputPointId { get; set; }
        public int TimeoutMilliseconds { get; set; }
        public string FaultPolicy { get; set; }
    }

    public sealed class AxisNodeConfiguration
    {
        public string AxisId { get; set; }
        public string Action { get; set; }
        public double TargetPosition { get; set; }
        public double Speed { get; set; }
        public double Acceleration { get; set; }
        public int TimeoutMilliseconds { get; set; }
        public string FaultPolicy { get; set; }
    }

    public sealed class WorkflowSnapshot
    {
        public WorkflowSnapshot(string stationId, string stationName, string stationType,
            IList<NodeDefinition> nodes, IList<ConnectionDefinition> connections)
        {
            StationId = stationId;
            StationName = stationName;
            StationType = stationType;
            Nodes = new List<NodeDefinition>();
            Connections = new List<ConnectionDefinition>();

            foreach (NodeDefinition node in nodes)
            {
                Nodes.Add(CloneNode(node));
            }

            foreach (ConnectionDefinition connection in connections)
            {
                Connections.Add(new ConnectionDefinition
                {
                    SourceNodeId = connection.SourceNodeId,
                    TargetNodeId = connection.TargetNodeId,
                    Branch = connection.Branch
                });
            }
        }

        public string StationId { get; private set; }
        public string StationName { get; private set; }
        public string StationType { get; private set; }
        public IList<NodeDefinition> Nodes { get; private set; }
        public IList<ConnectionDefinition> Connections { get; private set; }

        private static NodeDefinition CloneNode(NodeDefinition node)
        {
            return new NodeDefinition
            {
                Id = node.Id,
                Type = node.Type,
                X = node.X,
                Y = node.Y,
                DelayMilliseconds = node.DelayMilliseconds,
                Message = node.Message,
                ConditionKey = node.ConditionKey,
                ConditionValue = node.ConditionValue,
                Cylinder = node.Cylinder == null ? null : new CylinderNodeConfiguration
                {
                    CylinderId = node.Cylinder.CylinderId,
                    Action = node.Cylinder.Action,
                    OutputPointId = node.Cylinder.OutputPointId,
                    ExtendedInputPointId = node.Cylinder.ExtendedInputPointId,
                    RetractedInputPointId = node.Cylinder.RetractedInputPointId,
                    TimeoutMilliseconds = node.Cylinder.TimeoutMilliseconds,
                    FaultPolicy = node.Cylinder.FaultPolicy
                },
                Axis = node.Axis == null ? null : new AxisNodeConfiguration
                {
                    AxisId = node.Axis.AxisId,
                    Action = node.Axis.Action,
                    TargetPosition = node.Axis.TargetPosition,
                    Speed = node.Axis.Speed,
                    Acceleration = node.Axis.Acceleration,
                    TimeoutMilliseconds = node.Axis.TimeoutMilliseconds,
                    FaultPolicy = node.Axis.FaultPolicy
                }
            };
        }
    }
}
