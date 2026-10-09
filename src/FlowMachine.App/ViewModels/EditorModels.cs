using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using FlowMachine.Core;

namespace FlowMachine.App.ViewModels
{
    public sealed class EditorConnectorViewModel : INotifyPropertyChanged
    {
        private Point _anchor;
        private bool _isConnected;

        public EditorConnectorViewModel(EditorNodeViewModel owner, bool isInput, string branch,
            bool isVisible = true)
        {
            Owner = owner;
            IsInput = isInput;
            Branch = branch;
            IsVisible = isVisible;
        }

        public EditorNodeViewModel Owner { get; private set; }
        public bool IsInput { get; private set; }
        public string Branch { get; private set; }
        public bool IsVisible { get; private set; }

        public Point Anchor
        {
            get { return _anchor; }
            set
            {
                _anchor = value;
                OnPropertyChanged();
            }
        }

        public bool IsConnected
        {
            get { return _isConnected; }
            set
            {
                _isConnected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    public sealed class EditorNodeViewModel : INotifyPropertyChanged
    {
        private string _status;

        public EditorNodeViewModel(NodeDefinition configuration)
        {
            Configuration = configuration;
            Status = "Pending";
            Input = new EditorConnectorViewModel(this, true, null, configuration.Type != NodeTypeIds.Start);
            OutputNextVisible = configuration.Type != NodeTypeIds.Condition
                && configuration.Type != NodeTypeIds.End;
            OutputTrueVisible = configuration.Type == NodeTypeIds.Condition;
            OutputFalseVisible = configuration.Type == NodeTypeIds.Condition;
            OutputNext = new EditorConnectorViewModel(this, false, BranchIds.Next, OutputNextVisible);
            OutputTrue = new EditorConnectorViewModel(this, false, BranchIds.True, OutputTrueVisible);
            OutputFalse = new EditorConnectorViewModel(this, false, BranchIds.False, OutputFalseVisible);
        }

        public NodeDefinition Configuration { get; private set; }
        public string Id { get { return Configuration.Id; } }
        public string Type { get { return Configuration.Type; } }
        public string Title { get { return DisplayName(Configuration.Type); } }
        public EditorConnectorViewModel Input { get; private set; }
        public EditorConnectorViewModel OutputNext { get; private set; }
        public EditorConnectorViewModel OutputTrue { get; private set; }
        public EditorConnectorViewModel OutputFalse { get; private set; }
        public bool OutputNextVisible { get; private set; }
        public bool OutputTrueVisible { get; private set; }
        public bool OutputFalseVisible { get; private set; }

        public Point Location
        {
            get { return new Point(Configuration.X, Configuration.Y); }
            set
            {
                Configuration.X = value.X;
                Configuration.Y = value.Y;
                OnPropertyChanged();
            }
        }

        public int DelayMilliseconds
        {
            get { return Configuration.DelayMilliseconds; }
            set
            {
                Configuration.DelayMilliseconds = value;
                OnPropertyChanged();
            }
        }

        public int TimeoutMilliseconds
        {
            get { return Configuration.TimeoutMilliseconds; }
            set
            {
                Configuration.TimeoutMilliseconds = value;
                OnPropertyChanged();
            }
        }

        public string Message
        {
            get { return Configuration.Message; }
            set
            {
                Configuration.Message = value;
                OnPropertyChanged();
            }
        }

        public string ConditionKey
        {
            get { return Configuration.ConditionKey; }
            set
            {
                Configuration.ConditionKey = value;
                OnPropertyChanged();
            }
        }

        public bool ConditionValue
        {
            get { return Configuration.ConditionValue; }
            set
            {
                Configuration.ConditionValue = value;
                OnPropertyChanged();
            }
        }

        public string Status
        {
            get { return _status; }
            set
            {
                _status = value;
                OnPropertyChanged();
            }
        }

        public void SetNodeStatus(NodeExecutionStatus status)
        {
            Status = status.ToString();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private static string DisplayName(string type)
        {
            if (type == NodeTypeIds.Start) return "Start";
            if (type == NodeTypeIds.End) return "End";
            if (type == NodeTypeIds.Delay) return "Delay";
            if (type == NodeTypeIds.Log) return "Log";
            if (type == NodeTypeIds.Condition) return "Condition";
            if (type == NodeTypeIds.Cylinder) return "Cylinder (reserved)";
            if (type == NodeTypeIds.Axis) return "Axis (reserved)";
            return type;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    public sealed class EditorConnectionViewModel
    {
        public EditorConnectionViewModel(ConnectionDefinition definition,
            EditorConnectorViewModel source, EditorConnectorViewModel target,
            StationItemViewModel station)
        {
            Definition = definition;
            Source = source;
            Target = target;
            Station = station;
            Source.IsConnected = true;
            Target.IsConnected = true;
        }

        public ConnectionDefinition Definition { get; private set; }
        public EditorConnectorViewModel Source { get; private set; }
        public EditorConnectorViewModel Target { get; private set; }
        public StationItemViewModel Station { get; private set; }

        public void Delete()
        {
            Station.RemoveConnection(this);
        }
    }

    public sealed class StationItemViewModel : INotifyPropertyChanged
    {
        private readonly Dispatcher _dispatcher;

        public StationItemViewModel(IStation station)
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            Station = station;
            Nodes = new System.Collections.ObjectModel.ObservableCollection<EditorNodeViewModel>();
            Connections = new System.Collections.ObjectModel.ObservableCollection<EditorConnectionViewModel>();
            foreach (NodeDefinition definition in station.Configuration.Nodes)
            {
                Nodes.Add(new EditorNodeViewModel(definition));
            }

            foreach (ConnectionDefinition definition in station.Configuration.Connections)
            {
                EditorNodeViewModel source = FindNode(definition.SourceNodeId);
                EditorNodeViewModel target = FindNode(definition.TargetNodeId);
                if (source != null && target != null)
                {
                    Connections.Add(new EditorConnectionViewModel(definition,
                        GetOutput(source, definition.Branch), target.Input, this));
                }
            }

            Station.StateChanged += OnStationStateChanged;
            Station.NodeStatusChanged += OnNodeStatusChanged;
        }

        public IStation Station { get; private set; }
        public System.Collections.ObjectModel.ObservableCollection<EditorNodeViewModel> Nodes { get; private set; }
        public System.Collections.ObjectModel.ObservableCollection<EditorConnectionViewModel> Connections { get; private set; }
        public string Id { get { return Station.Id; } }
        public string Name
        {
            get { return Station.Configuration.Name; }
            set
            {
                Station.Configuration.Name = value;
                OnPropertyChanged();
            }
        }

        public bool Enabled
        {
            get { return Station.Configuration.Enabled; }
            set
            {
                Station.Configuration.Enabled = value;
                OnPropertyChanged();
            }
        }

        public string TypeName { get { return Station.TypeId.ToUpperInvariant(); } }
        public string StateName { get { return Station.State.ToString(); } }
        public string FaultMessage { get { return Station.FaultMessage; } }

        public void AddNode(NodeDefinition definition)
        {
            Station.Configuration.Nodes.Add(definition);
            Nodes.Add(new EditorNodeViewModel(definition));
        }

        public bool AddConnection(EditorConnectorViewModel source, EditorConnectorViewModel target)
        {
            if (source == null || target == null || source.IsInput || !target.IsInput
                || source.Owner == target.Owner
                || Connections.Any(connection => connection.Source == source))
            {
                return false;
            }

            ConnectionDefinition definition = new ConnectionDefinition
            {
                SourceNodeId = source.Owner.Id,
                TargetNodeId = target.Owner.Id,
                Branch = source.Branch
            };
            Station.Configuration.Connections.Add(definition);
            Connections.Add(new EditorConnectionViewModel(definition, source, target, this));
            return true;
        }

        public void RemoveConnection(EditorConnectionViewModel connection)
        {
            if (connection == null)
            {
                return;
            }

            Station.Configuration.Connections.Remove(connection.Definition);
            Connections.Remove(connection);
            connection.Source.IsConnected = Connections.Any(item => item.Source == connection.Source);
            connection.Target.IsConnected = Connections.Any(item => item.Target == connection.Target);
        }

        public void RemoveNode(EditorNodeViewModel node)
        {
            if (node == null || node.Type == NodeTypeIds.Start || node.Type == NodeTypeIds.End)
            {
                return;
            }

            foreach (EditorConnectionViewModel connection in Connections
                .Where(item => item.Source.Owner == node || item.Target.Owner == node).ToArray())
            {
                RemoveConnection(connection);
            }

            Station.Configuration.Nodes.Remove(node.Configuration);
            Nodes.Remove(node);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private EditorNodeViewModel FindNode(string id)
        {
            return Nodes.FirstOrDefault(node => node.Id == id);
        }

        private static EditorConnectorViewModel GetOutput(EditorNodeViewModel node, string branch)
        {
            if (branch == BranchIds.True) return node.OutputTrue;
            if (branch == BranchIds.False) return node.OutputFalse;
            return node.OutputNext;
        }

        private void OnStationStateChanged(object sender, StationStateChangedEventArgs args)
        {
            Dispatch(delegate
            {
                OnPropertyChanged("StateName");
                OnPropertyChanged("FaultMessage");
            });
        }

        private void OnNodeStatusChanged(object sender, NodeStatusChangedEventArgs args)
        {
            Dispatch(delegate
            {
                EditorNodeViewModel node = FindNode(args.NodeId);
                if (node != null)
                {
                    node.SetNodeStatus(args.Status);
                }
            });
        }

        private void Dispatch(Action action)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _dispatcher.BeginInvoke(action);
            }
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
