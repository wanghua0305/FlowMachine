using System;
using System.Collections.Generic;
using System.Linq;
using FlowMachine.Core;

namespace FlowMachine.Runtime
{
    public sealed class WorkflowValidationException : Exception
    {
        public WorkflowValidationException(IEnumerable<string> errors)
            : base(string.Join(Environment.NewLine, errors))
        {
            Errors = errors.ToArray();
        }

        public IList<string> Errors { get; private set; }
    }

    public sealed class WorkflowValidator
    {
        public void Validate(WorkflowSnapshot workflow, bool allowReservedHardwareNodes = false)
        {
            if (workflow == null)
            {
                throw new ArgumentNullException("workflow");
            }

            List<string> errors = new List<string>();
            Dictionary<string, NodeDefinition> nodes = new Dictionary<string, NodeDefinition>(
                StringComparer.Ordinal);
            foreach (NodeDefinition node in workflow.Nodes)
            {
                if (node == null || string.IsNullOrWhiteSpace(node.Id))
                {
                    errors.Add("Every node must have an id.");
                    continue;
                }

                if (nodes.ContainsKey(node.Id))
                {
                    errors.Add("Node id is duplicated: " + node.Id + ".");
                }
                else
                {
                    nodes.Add(node.Id, node);
                }

                ValidateNode(node, errors, allowReservedHardwareNodes);
            }

            NodeDefinition[] starts = nodes.Values.Where(n => n.Type == NodeTypeIds.Start).ToArray();
            NodeDefinition[] ends = nodes.Values.Where(n => n.Type == NodeTypeIds.End).ToArray();
            if (starts.Length != 1)
            {
                errors.Add("A workflow must contain exactly one Start node.");
            }

            if (ends.Length != 1)
            {
                errors.Add("A workflow must contain exactly one End node.");
            }

            Dictionary<string, List<ConnectionDefinition>> outgoing =
                nodes.Keys.ToDictionary(id => id, id => new List<ConnectionDefinition>(),
                    StringComparer.Ordinal);
            Dictionary<string, int> incomingCounts = nodes.Keys.ToDictionary(id => id, id => 0,
                StringComparer.Ordinal);

            foreach (ConnectionDefinition connection in workflow.Connections)
            {
                if (connection == null || string.IsNullOrWhiteSpace(connection.SourceNodeId)
                    || string.IsNullOrWhiteSpace(connection.TargetNodeId))
                {
                    errors.Add("A connection is missing a source or target.");
                    continue;
                }

                NodeDefinition source;
                NodeDefinition target;
                if (!nodes.TryGetValue(connection.SourceNodeId, out source)
                    || !nodes.TryGetValue(connection.TargetNodeId, out target))
                {
                    errors.Add("Connection references a missing node: " + connection.SourceNodeId
                        + " -> " + connection.TargetNodeId + ".");
                    continue;
                }

                if (source.Id == target.Id)
                {
                    errors.Add("A node cannot connect to itself: " + source.Id + ".");
                }

                if (!IsLegalBranch(source, connection.Branch))
                {
                    errors.Add("Illegal branch '" + connection.Branch + "' from node " + source.Id + ".");
                }

                outgoing[source.Id].Add(connection);
                incomingCounts[target.Id]++;
            }

            foreach (NodeDefinition node in nodes.Values)
            {
                List<ConnectionDefinition> edges = outgoing[node.Id];
                if (node.Type == NodeTypeIds.End)
                {
                    if (edges.Count != 0)
                    {
                        errors.Add("End node " + node.Id + " cannot have outgoing connections.");
                    }
                }
                else if (node.Type == NodeTypeIds.Condition)
                {
                    if (edges.Count != 2 || edges.Count(e => e.Branch == BranchIds.True) != 1
                        || edges.Count(e => e.Branch == BranchIds.False) != 1)
                    {
                        errors.Add("Condition node " + node.Id + " requires one True and one False connection.");
                    }
                }
                else if (edges.Count != 1 || (edges.Count == 1 && edges[0].Branch != BranchIds.Next))
                {
                    errors.Add("Node " + node.Id + " requires exactly one Next connection.");
                }

                if (node.Type == NodeTypeIds.Start && incomingCounts[node.Id] != 0)
                {
                    errors.Add("Start node " + node.Id + " cannot have incoming connections.");
                }
                else if (node.Type != NodeTypeIds.Start && incomingCounts[node.Id] == 0)
                {
                    errors.Add("Node " + node.Id + " requires at least one incoming connection.");
                }
            }

            if (starts.Length == 1 && ends.Length == 1)
            {
                CheckReachability(starts[0], ends[0], nodes, outgoing, errors);
                CheckAcyclic(nodes, outgoing, errors);
            }

            if (errors.Count != 0)
            {
                throw new WorkflowValidationException(errors);
            }
        }

        private static bool IsLegalBranch(NodeDefinition source, string branch)
        {
            if (source.Type == NodeTypeIds.Condition)
            {
                return branch == BranchIds.True || branch == BranchIds.False;
            }

            return branch == BranchIds.Next;
        }

        private static void ValidateNode(NodeDefinition node, ICollection<string> errors,
            bool allowReservedHardwareNodes)
        {
            switch (node.Type)
            {
                case NodeTypeIds.Start:
                case NodeTypeIds.End:
                    break;
                case NodeTypeIds.Delay:
                    if (node.DelayMilliseconds < 0 || node.DelayMilliseconds > 600000)
                    {
                        errors.Add("Delay node " + node.Id + " must be between 0 and 600000 ms.");
                    }
                    if (node.TimeoutMilliseconds < 0 || node.TimeoutMilliseconds > 600000)
                    {
                        errors.Add("Delay timeout at node " + node.Id + " must be between 0 and 600000 ms.");
                    }
                    break;
                case NodeTypeIds.Log:
                    if (node.Message == null)
                    {
                        errors.Add("Log node " + node.Id + " requires a message.");
                    }
                    break;
                case NodeTypeIds.Condition:
                    if (string.IsNullOrWhiteSpace(node.ConditionKey))
                    {
                        errors.Add("Condition node " + node.Id + " requires a condition key.");
                    }
                    break;
                case NodeTypeIds.Cylinder:
                case NodeTypeIds.Axis:
                    if (!allowReservedHardwareNodes)
                    {
                        errors.Add("Hardware node " + node.Id
                            + " is configured for a future phase and cannot run in this simulator.");
                    }
                    break;
                default:
                    errors.Add("Unknown node type '" + node.Type + "' at node " + node.Id + ".");
                    break;
            }
        }

        private static void CheckReachability(NodeDefinition start, NodeDefinition end,
            IDictionary<string, NodeDefinition> nodes,
            IDictionary<string, List<ConnectionDefinition>> outgoing, ICollection<string> errors)
        {
            HashSet<string> reached = new HashSet<string>(StringComparer.Ordinal);
            Stack<string> pending = new Stack<string>();
            pending.Push(start.Id);
            while (pending.Count != 0)
            {
                string current = pending.Pop();
                if (!reached.Add(current))
                {
                    continue;
                }

                foreach (ConnectionDefinition edge in outgoing[current])
                {
                    if (nodes.ContainsKey(edge.TargetNodeId))
                    {
                        pending.Push(edge.TargetNodeId);
                    }
                }
            }

            foreach (string id in nodes.Keys)
            {
                if (!reached.Contains(id))
                {
                    errors.Add("Node " + id + " is unreachable from Start.");
                }
            }

            HashSet<string> canReachEnd = new HashSet<string>(StringComparer.Ordinal);
            bool changed;
            canReachEnd.Add(end.Id);
            do
            {
                changed = false;
                foreach (KeyValuePair<string, List<ConnectionDefinition>> entry in outgoing)
                {
                    if (!canReachEnd.Contains(entry.Key)
                        && entry.Value.Any(edge => canReachEnd.Contains(edge.TargetNodeId)))
                    {
                        canReachEnd.Add(entry.Key);
                        changed = true;
                    }
                }
            } while (changed);

            foreach (string id in nodes.Keys)
            {
                if (!canReachEnd.Contains(id))
                {
                    errors.Add("Node " + id + " cannot reach End.");
                }
            }
        }

        private static void CheckAcyclic(IDictionary<string, NodeDefinition> nodes,
            IDictionary<string, List<ConnectionDefinition>> outgoing, ICollection<string> errors)
        {
            Dictionary<string, int> indegree = nodes.Keys.ToDictionary(id => id, id => 0,
                StringComparer.Ordinal);
            foreach (List<ConnectionDefinition> edges in outgoing.Values)
            {
                foreach (ConnectionDefinition edge in edges)
                {
                    if (indegree.ContainsKey(edge.TargetNodeId))
                    {
                        indegree[edge.TargetNodeId]++;
                    }
                }
            }

            Queue<string> ready = new Queue<string>(indegree.Where(pair => pair.Value == 0)
                .Select(pair => pair.Key));
            int visited = 0;
            while (ready.Count != 0)
            {
                string id = ready.Dequeue();
                visited++;
                foreach (ConnectionDefinition edge in outgoing[id])
                {
                    if (!indegree.ContainsKey(edge.TargetNodeId))
                    {
                        continue;
                    }

                    indegree[edge.TargetNodeId]--;
                    if (indegree[edge.TargetNodeId] == 0)
                    {
                        ready.Enqueue(edge.TargetNodeId);
                    }
                }
            }

            if (visited != nodes.Count)
            {
                errors.Add("Cycles are not supported in this workflow version.");
            }
        }
    }
}
