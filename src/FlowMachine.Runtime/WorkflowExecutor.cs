using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlowMachine.Core;

namespace FlowMachine.Runtime
{
    public interface IWorkflowExecutor
    {
        Task ExecuteAsync(IStation station, WorkflowSnapshot workflow, PauseGate pauseGate,
            CancellationToken cancellationToken, bool isHoming);
    }

    public sealed class WorkflowExecutor : IWorkflowExecutor
    {
        private readonly WorkflowValidator _validator;

        public WorkflowExecutor(WorkflowValidator validator)
        {
            _validator = validator;
        }

        public event EventHandler<RuntimeLogEntry> Log;

        public async Task ExecuteAsync(IStation station, WorkflowSnapshot workflow, PauseGate pauseGate,
            CancellationToken cancellationToken, bool isHoming)
        {
            if (station == null)
            {
                throw new ArgumentNullException("station");
            }

            if (station.State == StationState.Completed || station.State == StationState.Faulted)
            {
                station.TransitionTo(StationState.Idle);
                station.SetFault(null);
            }

            station.TransitionTo(isHoming ? StationState.Homing : StationState.Starting);
            try
            {
                _validator.Validate(workflow);
                station.TransitionTo(StationState.Running);
                Dictionary<string, NodeDefinition> nodes = workflow.Nodes.ToDictionary(n => n.Id,
                    StringComparer.Ordinal);
                Dictionary<string, List<ConnectionDefinition>> outgoing =
                    workflow.Connections.GroupBy(c => c.SourceNodeId, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
                NodeDefinition current = nodes.Values.Single(n => n.Type == NodeTypeIds.Start);
                int executedCount = 0;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await pauseGate.WaitIfPauseRequestedAsync(
                        delegate
                        {
                            if (station.State == StationState.Running)
                            {
                                station.TransitionTo(StationState.Pausing);
                            }

                            if (station.State == StationState.Pausing)
                            {
                                station.TransitionTo(StationState.Paused);
                            }
                        },
                        delegate
                        {
                            if (station.State == StationState.Paused)
                            {
                                station.TransitionTo(StationState.Running);
                            }
                        },
                        cancellationToken).ConfigureAwait(false);

                    if (++executedCount > nodes.Count)
                    {
                        throw new InvalidOperationException("Execution exceeded the node count; a cycle may exist.");
                    }

                    station.SetNodeStatus(current.Id, NodeExecutionStatus.Running);
                    WriteLog(station, current.Id, "Info", "Started " + current.Type + " node.");
                    try
                    {
                        await ExecuteNodeAsync(station, current, cancellationToken).ConfigureAwait(false);
                        station.SetNodeStatus(current.Id, NodeExecutionStatus.Succeeded);
                        WriteLog(station, current.Id, "Info", "Completed " + current.Type + " node.");
                        if (current.Type == NodeTypeIds.Log)
                        {
                            WriteLog(station, current.Id, "Info", current.Message);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        station.SetNodeStatus(current.Id, NodeExecutionStatus.Cancelled);
                        throw;
                    }
                    catch (Exception exception)
                    {
                        station.SetNodeStatus(current.Id, NodeExecutionStatus.Failed);
                        station.SetFault(exception.Message);
                        WriteLog(station, current.Id, "Error", exception.Message);
                        throw;
                    }

                    if (current.Type == NodeTypeIds.End)
                    {
                        station.TransitionTo(StationState.Completed);
                        WriteLog(station, current.Id, "Info", "Station execution completed.");
                        return;
                    }

                    string branch = current.Type == NodeTypeIds.Condition
                        ? (current.ConditionValue ? BranchIds.True : BranchIds.False)
                        : BranchIds.Next;
                    current = nodes[outgoing[current.Id].Single(edge => edge.Branch == branch).TargetNodeId];
                }
            }
            catch (OperationCanceledException)
            {
                if (station.State != StationState.Stopping)
                {
                    station.TransitionTo(StationState.Stopping);
                }

                station.TransitionTo(StationState.Idle);
                WriteLog(station, null, "Warning", "Execution cancelled and cleaned up.");
                throw;
            }
            catch (Exception exception)
            {
                station.SetFault(exception.Message);
                if (station.State != StationState.Faulted)
                {
                    station.TransitionTo(StationState.Faulted);
                }

                WriteLog(station, null, "Error", "Station faulted: " + exception.Message);
                throw;
            }
        }

        private static async Task ExecuteNodeAsync(IStation station, NodeDefinition node,
            CancellationToken cancellationToken)
        {
            switch (node.Type)
            {
                case NodeTypeIds.Start:
                case NodeTypeIds.End:
                    return;
                case NodeTypeIds.Delay:
                    await Task.Delay(node.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
                    return;
                case NodeTypeIds.Log:
                    return;
                case NodeTypeIds.Condition:
                    return;
                default:
                    throw new InvalidOperationException("Unsupported node type: " + node.Type + ".");
            }
        }

        private void WriteLog(IStation station, string nodeId, string level, string message)
        {
            EventHandler<RuntimeLogEntry> handler = Log;
            if (handler != null)
            {
                handler(this, new RuntimeLogEntry(DateTime.Now, station.Id, station.Name,
                    nodeId, level, message));
            }
        }
    }
}
