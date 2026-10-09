using System.Collections.Generic;
using System.Linq;
using FlowMachine.App.Views;
using FlowMachine.Core;
using FlowMachine.Infrastructure;
using FlowMachine.Runtime;
using Prism.Ioc;
using Prism.Modularity;
using Prism.Regions;
using FlowConfigurationStore = FlowMachine.Infrastructure.ConfigurationStore;
using FlowConfigurationStoreContract = FlowMachine.Infrastructure.IConfigurationStore;

namespace FlowMachine.App.Modules
{
    public sealed class FlowMachineModule : IModule
    {
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            IStationFactory stationFactory = new StationFactory();
            IList<IStation> stations = CreateInitialStations(stationFactory);
            IWorkflowExecutor executor = new WorkflowExecutor(new WorkflowValidator());
            IBusController bus = new BusController(stations, executor,
                new DeterministicStationScheduleStrategy());
            IDeviceService devices = new SimulatedDeviceService();

            containerRegistry.RegisterInstance<IStationFactory>(stationFactory);
            containerRegistry.RegisterInstance<IList<IStation>>(stations);
            containerRegistry.RegisterInstance<IBusController>(bus);
            containerRegistry.RegisterInstance<FlowConfigurationStoreContract>(
                new FlowConfigurationStore(stationFactory,
                    new HardwareConfigurationValidator(devices)));
            containerRegistry.RegisterInstance<IDeviceService>(devices);
            containerRegistry.RegisterForNavigation<StationWorkspaceView>();
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
            containerProvider.Resolve<IRegionManager>()
                .RequestNavigate("MainRegion", nameof(StationWorkspaceView));
        }

        private static IList<IStation> CreateInitialStations(IStationFactory factory)
        {
            return new[]
            {
                factory.Create(CreateConfiguration(StationTypeIds.Flow, "Flow Station 1", true)),
                factory.Create(CreateConfiguration(StationTypeIds.Home, "Home Station 1", false)),
                factory.Create(CreateConfiguration(StationTypeIds.Test, "Test Station 1", true))
            }.ToList();
        }

        private static StationConfiguration CreateConfiguration(string type, string name, bool includeDelay)
        {
            StationConfiguration configuration = new StationConfiguration
            {
                Name = name,
                StationType = type,
                Enabled = true
            };
            NodeDefinition start = new NodeDefinition
            {
                Type = NodeTypeIds.Start,
                X = 60,
                Y = 120
            };
            NodeDefinition log = new NodeDefinition
            {
                Type = NodeTypeIds.Log,
                X = 330,
                Y = 120,
                Message = "Simulated " + type + " action."
            };
            NodeDefinition end = new NodeDefinition
            {
                Type = NodeTypeIds.End,
                X = includeDelay ? 900 : 650,
                Y = 120
            };

            configuration.Nodes.Add(start);
            configuration.Nodes.Add(log);
            if (includeDelay)
            {
                NodeDefinition delay = new NodeDefinition
                {
                    Type = NodeTypeIds.Delay,
                    X = 610,
                    Y = 120,
                    DelayMilliseconds = 500
                };
                configuration.Nodes.Add(delay);
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
            else
            {
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
            }

            return configuration;
        }
    }
}
