using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FlowMachine.Core;
using FlowMachine.Infrastructure;
using FlowMachine.Runtime;

namespace FlowMachine.App.ViewModels
{
    public sealed class StationWorkspaceViewModel : INotifyPropertyChanged
    {
        private readonly IList<IStation> _models;
        private readonly IStationFactory _stationFactory;
        private readonly IBusController _bus;
        private readonly IConfigurationStore _configurationStore;
        private readonly Dispatcher _dispatcher;
        private StationItemViewModel _selectedStation;
        private EditorNodeViewModel _selectedNode;
        private string _statusMessage;
        private BusState _busState;

        public StationWorkspaceViewModel(IList<IStation> stations, IStationFactory stationFactory,
            IBusController bus, IConfigurationStore configurationStore, IDeviceService deviceService)
        {
            _models = stations;
            _stationFactory = stationFactory;
            _bus = bus;
            _configurationStore = configurationStore;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _busState = bus.State;
            PendingConnection = new PendingConnectionViewModel();
            Stations = new ObservableCollection<StationItemViewModel>(
                stations.Select(station => new StationItemViewModel(station)));
            Logs = new ObservableCollection<RuntimeLogEntry>();
            InputPoints = deviceService.Io.GetPoints(IoDirection.Input);
            OutputPoints = deviceService.Io.GetPoints(IoDirection.Output);

            AddStationCommand = new RelayCommand(AddStation);
            RemoveStationCommand = new RelayCommand(RemoveStation, delegate { return SelectedStation != null && !IsBusy; });
            DeleteNodeCommand = new RelayCommand(DeleteSelectedNode,
                delegate { return SelectedStation != null && SelectedNode != null && !IsBusy; });
            StartCommand = new RelayCommand(delegate { RunCommand(StartAsync); }, delegate
            {
                return !IsBusy && (BusState == BusState.Idle || BusState == BusState.Completed);
            });
            HomeCommand = new RelayCommand(delegate { RunCommand(HomeAsync); }, delegate
            {
                return !IsBusy && (BusState == BusState.Idle || BusState == BusState.Completed)
                    && Stations.Any(item => item.Station.CanBeHomed && item.Enabled);
            });
            StopCommand = new RelayCommand(delegate { RunCommand(StopAsync); }, delegate { return IsBusy; });
            PauseCommand = new RelayCommand(delegate { RunCommand(PauseAsync); }, delegate
            {
                return BusState == BusState.Running || BusState == BusState.Starting
                    || BusState == BusState.Homing;
            });
            ResumeCommand = new RelayCommand(delegate { RunCommand(ResumeAsync); },
                delegate { return BusState == BusState.Paused; });
            ResetCommand = new RelayCommand(delegate { RunCommand(ResetAsync); }, delegate
            {
                return !IsBusy && (BusState == BusState.Idle || BusState == BusState.Completed
                    || BusState == BusState.Faulted);
            });
            RunTestCommand = new RelayCommand(delegate
            {
                if (SelectedStation != null)
                {
                    RunCommand(delegate
                    {
                        return _bus.RunTestStationAsync(SelectedStation.Station, CancellationToken.None);
                    });
                }
            }, delegate
            {
                return !IsBusy && SelectedStation != null && SelectedStation.Station.CanRunManually
                    && SelectedStation.Enabled;
            });
            SaveCommand = new RelayCommand(delegate { SaveConfiguration(); }, delegate { return !IsBusy; });
            LoadCommand = new RelayCommand(delegate { LoadConfiguration(); }, delegate { return !IsBusy; });
            StartConnectionCommand = new RelayCommand(StartConnection, CanStartConnection);
            CreateConnectionCommand = new RelayCommand(CreateConnection, CanCreateConnection);
            DisconnectConnectorCommand = new RelayCommand(DisconnectConnector);
            RemoveConnectionCommand = new RelayCommand(RemoveConnection);

            _bus.StateChanged += OnBusStateChanged;
            _bus.Log += OnBusLog;
            StatusMessage = "Ready. Software stop is not a hardware emergency stop.";
        }

        public ObservableCollection<StationItemViewModel> Stations { get; private set; }
        public ObservableCollection<RuntimeLogEntry> Logs { get; private set; }
        public IList<IoPoint> InputPoints { get; private set; }
        public IList<IoPoint> OutputPoints { get; private set; }
        public ICommand AddStationCommand { get; private set; }
        public ICommand RemoveStationCommand { get; private set; }
        public ICommand DeleteNodeCommand { get; private set; }
        public ICommand StartCommand { get; private set; }
        public ICommand StopCommand { get; private set; }
        public ICommand PauseCommand { get; private set; }
        public ICommand ResumeCommand { get; private set; }
        public ICommand ResetCommand { get; private set; }
        public ICommand HomeCommand { get; private set; }
        public ICommand RunTestCommand { get; private set; }
        public ICommand SaveCommand { get; private set; }
        public ICommand LoadCommand { get; private set; }
        public ICommand StartConnectionCommand { get; private set; }
        public ICommand CreateConnectionCommand { get; private set; }
        public ICommand DisconnectConnectorCommand { get; private set; }
        public ICommand RemoveConnectionCommand { get; private set; }
        public PendingConnectionViewModel PendingConnection { get; private set; }
        public bool IsBusy { get { return BusState != BusState.Idle && BusState != BusState.Completed
                && BusState != BusState.Faulted; } }
        public BusState BusState { get { return _busState; } }
        public string BusStateText { get { return BusState.ToString(); } }
        public string StatusMessage
        {
            get { return _statusMessage; }
            private set
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }

        public StationItemViewModel SelectedStation
        {
            get { return _selectedStation; }
            set
            {
                _selectedStation = value;
                SelectedNode = null;
                OnPropertyChanged();
                NotifyCommands();
            }
        }

        public EditorNodeViewModel SelectedNode
        {
            get { return _selectedNode; }
            set
            {
                _selectedNode = value;
                OnPropertyChanged();
                NotifyCommands();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public void AddNodeFromDrop(string type, Point location)
        {
            if (SelectedStation == null || IsBusy)
            {
                return;
            }

            NodeDefinition definition = new NodeDefinition
            {
                Type = type,
                X = location.X,
                Y = location.Y,
                DelayMilliseconds = 500,
                Message = "Simulated action",
                ConditionKey = "simulated",
                ConditionValue = true
            };
            SelectedStation.AddNode(definition);
            SelectedNode = SelectedStation.Nodes.Last();
        }

        public void SaveConfiguration()
        {
            try
            {
                string path = Path.Combine(Environment.CurrentDirectory, "flowmachine.xml");
                _configurationStore.Save(path, _models.Select(station => station.Configuration));
                StatusMessage = "Configuration saved to " + path;
            }
            catch (Exception exception)
            {
                StatusMessage = "Save failed: " + exception.Message;
            }
        }

        public void LoadConfiguration()
        {
            try
            {
                string path = Path.Combine(Environment.CurrentDirectory, "flowmachine.xml");
                IList<StationConfiguration> configurations = _configurationStore.Load(path);
                List<IStation> loaded = configurations.Select(configuration =>
                    _stationFactory.Create(configuration)).ToList();

                foreach (IStation station in _models.ToArray())
                {
                    _models.Remove(station);
                }

                Stations.Clear();
                foreach (IStation station in loaded)
                {
                    _models.Add(station);
                    Stations.Add(new StationItemViewModel(station));
                }

                SelectedStation = Stations.FirstOrDefault();
                StatusMessage = "Configuration loaded from " + path;
            }
            catch (Exception exception)
            {
                StatusMessage = "Load failed: " + exception.Message;
            }
        }

        private void AddStation(object parameter)
        {
            if (IsBusy)
            {
                return;
            }

            string type = parameter as string;
            StationConfiguration configuration = CreateDefaultStation(type);
            IStation station = _stationFactory.Create(configuration);
            _models.Add(station);
            StationItemViewModel item = new StationItemViewModel(station);
            Stations.Add(item);
            SelectedStation = item;
            StatusMessage = type + " station created.";
        }

        private void RemoveStation(object parameter)
        {
            if (SelectedStation == null || IsBusy)
            {
                return;
            }

            _models.Remove(SelectedStation.Station);
            Stations.Remove(SelectedStation);
            SelectedStation = Stations.FirstOrDefault();
        }

        private void DeleteSelectedNode(object parameter)
        {
            if (SelectedStation == null || SelectedNode == null || IsBusy)
            {
                return;
            }

            SelectedStation.RemoveNode(SelectedNode);
            SelectedNode = null;
        }

        private bool CanStartConnection(object parameter)
        {
            EditorConnectorViewModel source = parameter as EditorConnectorViewModel;
            return !IsBusy && source != null && !source.IsInput && SelectedStation != null
                && SelectedStation.Connections.All(connection => connection.Source != source);
        }

        private void StartConnection(object parameter)
        {
            EditorConnectorViewModel source = parameter as EditorConnectorViewModel;
            if (!CanStartConnection(source))
            {
                return;
            }

            PendingConnection.Source = source;
            PendingConnection.IsVisible = true;
        }

        private bool CanCreateConnection(object parameter)
        {
            EditorConnectorViewModel target = parameter as EditorConnectorViewModel;
            EditorConnectorViewModel source = PendingConnection.Source;
            return !IsBusy && SelectedStation != null && source != null && target != null
                && target.IsInput && !target.IsConnected && source.Owner != target.Owner;
        }

        private void CreateConnection(object parameter)
        {
            EditorConnectorViewModel target = parameter as EditorConnectorViewModel;
            if (CanCreateConnection(target))
            {
                SelectedStation.AddConnection(PendingConnection.Source, target);
            }

            PendingConnection.IsVisible = false;
            PendingConnection.Source = null;
        }

        private void DisconnectConnector(object parameter)
        {
            EditorConnectorViewModel connector = parameter as EditorConnectorViewModel;
            if (SelectedStation == null || connector == null)
            {
                return;
            }

            foreach (EditorConnectionViewModel connection in SelectedStation.Connections
                .Where(item => item.Source == connector || item.Target == connector).ToArray())
            {
                SelectedStation.RemoveConnection(connection);
            }
        }

        private void RemoveConnection(object parameter)
        {
            EditorConnectionViewModel connection = parameter as EditorConnectionViewModel;
            if (connection != null)
            {
                connection.Station.RemoveConnection(connection);
            }
        }

        private async void RunCommand(Func<Task> operation)
        {
            try
            {
                await operation();
                StatusMessage = "Bus state: " + BusState;
            }
            catch (Exception exception)
            {
                StatusMessage = exception.Message;
            }
        }

        private Task StartAsync()
        {
            return _bus.StartAsync(CancellationToken.None);
        }

        private Task HomeAsync()
        {
            return _bus.HomeAsync(CancellationToken.None);
        }

        private Task StopAsync()
        {
            return _bus.StopAsync();
        }

        private Task PauseAsync()
        {
            return _bus.PauseAsync();
        }

        private Task ResumeAsync()
        {
            return _bus.ResumeAsync();
        }

        private Task ResetAsync()
        {
            return _bus.ResetAsync();
        }

        private void OnBusStateChanged(object sender, BusStateChangedEventArgs args)
        {
            Dispatch(delegate
            {
                _busState = args.Current;
                OnPropertyChanged("BusState");
                OnPropertyChanged("BusStateText");
                OnPropertyChanged("IsBusy");
                NotifyCommands();
            });
        }

        private void OnBusLog(object sender, RuntimeLogEntry entry)
        {
            Dispatch(delegate
            {
                Logs.Add(entry);
                while (Logs.Count > 1000)
                {
                    Logs.RemoveAt(0);
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

        private void NotifyCommands()
        {
            RelayCommand[] commands = { (RelayCommand)AddStationCommand, (RelayCommand)RemoveStationCommand,
                (RelayCommand)DeleteNodeCommand, (RelayCommand)StartCommand, (RelayCommand)StopCommand,
                (RelayCommand)PauseCommand, (RelayCommand)ResumeCommand, (RelayCommand)ResetCommand,
                (RelayCommand)HomeCommand, (RelayCommand)RunTestCommand, (RelayCommand)SaveCommand,
                (RelayCommand)LoadCommand };
            foreach (RelayCommand command in commands)
            {
                command.RaiseCanExecuteChanged();
            }
        }

        private static StationConfiguration CreateDefaultStation(string type)
        {
            string prefix = type == StationTypeIds.Home ? "Home" : type == StationTypeIds.Test ? "Test" : "Flow";
            StationConfiguration configuration = new StationConfiguration
            {
                Name = prefix + " Station",
                StationType = type,
                Enabled = true
            };

            NodeDefinition start = new NodeDefinition { Type = NodeTypeIds.Start, X = 80, Y = 160 };
            NodeDefinition log = new NodeDefinition
            {
                Type = NodeTypeIds.Log,
                X = 340,
                Y = 160,
                Message = "Simulated " + prefix.ToLowerInvariant() + " station action."
            };
            NodeDefinition end = new NodeDefinition { Type = NodeTypeIds.End, X = 620, Y = 160 };
            configuration.Nodes.Add(start);
            configuration.Nodes.Add(log);
            configuration.Nodes.Add(end);
            configuration.Connections.Add(new ConnectionDefinition
            {
                SourceNodeId = start.Id,
                TargetNodeId = log.Id,
                Branch = BranchIds.Next
            });
            configuration.Connections.Add(new ConnectionDefinition
            {
                SourceNodeId = log.Id,
                TargetNodeId = end.Id,
                Branch = BranchIds.Next
            });

            if (type == StationTypeIds.Flow || type == StationTypeIds.Test)
            {
                NodeDefinition delay = new NodeDefinition
                {
                    Type = NodeTypeIds.Delay,
                    X = 340,
                    Y = 330,
                    DelayMilliseconds = 300
                };
                configuration.Nodes.Insert(2, delay);
                configuration.Connections.RemoveAt(1);
                configuration.Connections.Add(new ConnectionDefinition
                {
                    SourceNodeId = log.Id,
                    TargetNodeId = delay.Id,
                    Branch = BranchIds.Next
                });
                configuration.Connections.Add(new ConnectionDefinition
                {
                    SourceNodeId = delay.Id,
                    TargetNodeId = end.Id,
                    Branch = BranchIds.Next
                });
            }

            return configuration;
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

    public sealed class PendingConnectionViewModel : INotifyPropertyChanged
    {
        private EditorConnectorViewModel _source;
        private bool _isVisible;
        private Point _targetLocation;

        public EditorConnectorViewModel Source
        {
            get { return _source; }
            set
            {
                _source = value;
                OnPropertyChanged();
            }
        }

        public EditorConnectorViewModel Target { get; set; }

        public bool IsVisible
        {
            get { return _isVisible; }
            set
            {
                _isVisible = value;
                OnPropertyChanged();
            }
        }

        public Point TargetLocation
        {
            get { return _targetLocation; }
            set
            {
                _targetLocation = value;
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

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }

        public void Execute(object parameter)
        {
            _execute(parameter);
        }

        public void RaiseCanExecuteChanged()
        {
            EventHandler handler = CanExecuteChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
