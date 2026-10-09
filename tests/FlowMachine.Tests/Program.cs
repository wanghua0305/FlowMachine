using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlowMachine.Core;
using FlowMachine.Infrastructure;
using FlowMachine.Runtime;

namespace FlowMachine.Tests
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            List<Tuple<string, Func<Task>>> tests = new List<Tuple<string, Func<Task>>>
            {
                Tuple.Create("Start runs only FlowStations", (Func<Task>)StartRunsOnlyFlowStations),
                Tuple.Create("Home runs only HomeStations", (Func<Task>)HomeRunsOnlyHomeStations),
                Tuple.Create("TestStations require manual execution", (Func<Task>)TestStationsRequireManualExecution),
                Tuple.Create("Repeated start shares one task", (Func<Task>)RepeatedStartSharesTask),
                Tuple.Create("Pause waits at a node boundary", (Func<Task>)PauseWaitsAtBoundary),
                Tuple.Create("Condition branches follow their connection labels", (Func<Task>)ConditionBranchesAreExplicit),
                Tuple.Create("Pause can be requested again after resume", (Func<Task>)PauseCanBeRepeated),
                Tuple.Create("Resume does not repeat completed nodes", (Func<Task>)ResumeDoesNotRepeatNodes),
                Tuple.Create("Stop cleans up the active task", (Func<Task>)StopCleansUp),
                Tuple.Create("Fault and timeout reach the bus", (Func<Task>)FaultAndTimeoutPropagate),
                Tuple.Create("Configuration round-trips graph edges", (Func<Task>)ConfigurationRoundTrips),
                Tuple.Create("Demo configuration loads", (Func<Task>)DemoConfigurationLoads),
                Tuple.Create("Hardware configuration checks IO directions", (Func<Task>)HardwareConfigurationChecksDirections)
            };

            int failed = 0;
            foreach (Tuple<string, Func<Task>> test in tests)
            {
                try
                {
                    await test.Item2().ConfigureAwait(false);
                    Console.WriteLine("PASS " + test.Item1);
                }
                catch (Exception exception)
                {
                    failed++;
                    Console.WriteLine("FAIL " + test.Item1 + ": " + exception);
                }
            }

            Console.WriteLine(tests.Count - failed + "/" + tests.Count + " tests passed.");
            return failed == 0 ? 0 : 1;
        }

        private static async Task StartRunsOnlyFlowStations()
        {
            List<IStation> stations = MakeStations(
                MakeConfiguration(StationTypeIds.Flow, "Flow A", 0, 0),
                MakeConfiguration(StationTypeIds.Home, "Home A", 0, 0),
                MakeConfiguration(StationTypeIds.Test, "Test A", 0, 0));
            ConcurrentQueue<RuntimeLogEntry> logs = new ConcurrentQueue<RuntimeLogEntry>();
            BusController bus = CreateBus(stations, logs);

            await bus.StartAsync(CancellationToken.None).ConfigureAwait(false);

            Assert(stations.Single(s => s.TypeId == StationTypeIds.Flow).State == StationState.Completed,
                "FlowStation did not complete.");
            Assert(stations.Single(s => s.TypeId == StationTypeIds.Home).State == StationState.Idle,
                "HomeStation participated in ordinary start.");
            Assert(stations.Single(s => s.TypeId == StationTypeIds.Test).State == StationState.Idle,
                "TestStation participated in ordinary start.");
        }

        private static async Task HomeRunsOnlyHomeStations()
        {
            List<IStation> stations = MakeStations(
                MakeConfiguration(StationTypeIds.Flow, "Flow A", 0, 0),
                MakeConfiguration(StationTypeIds.Home, "Home A", 0, 0),
                MakeConfiguration(StationTypeIds.Test, "Test A", 0, 0));
            BusController bus = CreateBus(stations, null);

            await bus.HomeAsync(CancellationToken.None).ConfigureAwait(false);

            Assert(stations.Single(s => s.TypeId == StationTypeIds.Home).State == StationState.Completed,
                "HomeStation did not complete.");
            Assert(stations.Single(s => s.TypeId == StationTypeIds.Flow).State == StationState.Idle,
                "FlowStation participated in homing.");
            Assert(stations.Single(s => s.TypeId == StationTypeIds.Test).State == StationState.Idle,
                "TestStation participated in homing.");
        }

        private static async Task TestStationsRequireManualExecution()
        {
            StationConfiguration flowConfig = MakeConfiguration(StationTypeIds.Flow, "Flow", 0, 0);
            StationConfiguration testConfig = MakeConfiguration(StationTypeIds.Test, "Test", 0, 0);
            List<IStation> stations = MakeStations(flowConfig, testConfig);
            BusController bus = CreateBus(stations, null);

            await bus.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Assert(stations.Single(s => s.TypeId == StationTypeIds.Test).State == StationState.Idle,
                "TestStation ran during ordinary start.");

            IStation testStation = stations.Single(s => s.TypeId == StationTypeIds.Test);
            await bus.RunTestStationAsync(testStation, CancellationToken.None).ConfigureAwait(false);
            Assert(testStation.State == StationState.Completed, "Manual TestStation run did not complete.");

            bool rejected = false;
            try
            {
                await bus.RunTestStationAsync(stations.Single(s => s.TypeId == StationTypeIds.Flow),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Assert(rejected, "A non-TestStation was accepted by the manual entry point.");
        }

        private static async Task RepeatedStartSharesTask()
        {
            List<IStation> stations = MakeStations(
                MakeConfiguration(StationTypeIds.Flow, "Flow", 400, 0));
            ConcurrentQueue<RuntimeLogEntry> logs = new ConcurrentQueue<RuntimeLogEntry>();
            BusController bus = CreateBus(stations, logs);
            Task first = bus.StartAsync(CancellationToken.None);
            Task second = bus.StartAsync(CancellationToken.None);

            Assert(object.ReferenceEquals(first, second), "Repeated Start did not return the active task.");
            await first.ConfigureAwait(false);
            Assert(logs.Count(log => log.Message == "Started start node.") == 1,
                "Repeated Start executed more than one flow.");
        }

        private static async Task PauseWaitsAtBoundary()
        {
            List<IStation> stations = MakeStations(
                MakeConfiguration(StationTypeIds.Flow, "Flow", 250, 0));
            ConcurrentQueue<RuntimeLogEntry> logs = new ConcurrentQueue<RuntimeLogEntry>();
            BusController bus = CreateBus(stations, logs);
            Task run = bus.StartAsync(CancellationToken.None);
            await Task.Delay(60).ConfigureAwait(false);
            await bus.PauseAsync().ConfigureAwait(false);

            Assert(bus.State == BusState.Paused, "Pause returned before the safe boundary was reached.");
            Assert(!logs.Any(log => log.Message == "Started log node."),
                "A node started after pause was requested.");

            await bus.ResumeAsync().ConfigureAwait(false);
            await run.ConfigureAwait(false);
        }

        private static async Task ResumeDoesNotRepeatNodes()
        {
            List<IStation> stations = MakeStations(
                MakeConfiguration(StationTypeIds.Flow, "Flow", 250, 0));
            ConcurrentQueue<RuntimeLogEntry> logs = new ConcurrentQueue<RuntimeLogEntry>();
            BusController bus = CreateBus(stations, logs);
            Task run = bus.StartAsync(CancellationToken.None);
            await Task.Delay(60).ConfigureAwait(false);
            await bus.PauseAsync().ConfigureAwait(false);
            await bus.ResumeAsync().ConfigureAwait(false);
            await run.ConfigureAwait(false);

            Assert(logs.Count(log => log.Message == "Started delay node.") == 1,
                "The completed delay node was repeated after resume.");
            Assert(logs.Count(log => log.Message == "Started log node.") == 1,
                "The log node did not execute exactly once.");
        }

        private static async Task ConditionBranchesAreExplicit()
        {
            foreach (bool value in new[] { true, false })
            {
                ConcurrentQueue<RuntimeLogEntry> logs = new ConcurrentQueue<RuntimeLogEntry>();
                List<IStation> stations = MakeStations(MakeConditionConfiguration(value));
                BusController bus = CreateBus(stations, logs);
                await bus.StartAsync(CancellationToken.None).ConfigureAwait(false);

                string chosen = value ? "selected true" : "selected false";
                string skipped = value ? "selected false" : "selected true";
                Assert(logs.Any(log => log.Message == chosen), "Condition branch was not followed: " + chosen);
                Assert(!logs.Any(log => log.Message == skipped), "The unselected condition branch executed.");
            }
        }

        private static async Task PauseCanBeRepeated()
        {
            PauseGate gate = new PauseGate();
            int pausedCount = 0;
            Task worker = Task.Run(async delegate
            {
                await gate.WaitIfPauseRequestedAsync(
                    delegate { Interlocked.Increment(ref pausedCount); },
                    delegate { },
                    CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(100).ConfigureAwait(false);
                await gate.WaitIfPauseRequestedAsync(
                    delegate { Interlocked.Increment(ref pausedCount); },
                    delegate { },
                    CancellationToken.None).ConfigureAwait(false);
            });

            gate.RequestPause();
            Assert(await gate.WaitForPauseOrCompletionAsync().ConfigureAwait(false),
                "First pause was not observed.");
            gate.Resume();
            await Task.Delay(20).ConfigureAwait(false);
            gate.RequestPause();
            Assert(await gate.WaitForPauseOrCompletionAsync().ConfigureAwait(false),
                "Second pause was not observed.");
            Assert(pausedCount == 2, "The second pause completed before its boundary.");
            gate.Resume();
            await worker.ConfigureAwait(false);
        }

        private static async Task StopCleansUp()
        {
            List<IStation> stations = MakeStations(
                MakeConfiguration(StationTypeIds.Flow, "Flow", 5000, 0));
            BusController bus = CreateBus(stations, null);
            Task run = bus.StartAsync(CancellationToken.None);
            await Task.Delay(50).ConfigureAwait(false);

            await bus.StopAsync().ConfigureAwait(false);
            await run.ConfigureAwait(false);

            Assert(bus.State == BusState.Idle, "Bus did not return to Idle after Stop.");
            Assert(stations[0].State == StationState.Idle, "Station did not clean up after Stop.");
        }

        private static async Task FaultAndTimeoutPropagate()
        {
            StationConfiguration invalid = MakeConfiguration(StationTypeIds.Flow, "Invalid", 0, 0);
            invalid.Connections.RemoveAt(invalid.Connections.Count - 1);
            List<IStation> invalidStations = MakeStations(invalid);
            BusController invalidBus = CreateBus(invalidStations, null);
            bool failed = false;
            try
            {
                await invalidBus.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (WorkflowValidationException)
            {
                failed = true;
            }

            Assert(failed && invalidBus.State == BusState.Faulted, "Invalid graph fault was not propagated.");
            Assert(invalidBus.FaultStationId == invalidStations[0].Id, "Fault station id was not recorded.");
            await invalidBus.ResetAsync().ConfigureAwait(false);
            Assert(invalidBus.State == BusState.Idle, "Reset did not clear a completed fault.");

            StationConfiguration timeout = MakeConfiguration(StationTypeIds.Flow, "Timeout", 200, 20);
            List<IStation> timeoutStations = MakeStations(timeout);
            BusController timeoutBus = CreateBus(timeoutStations, null);
            bool timedOut = false;
            try
            {
                await timeoutBus.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timedOut = true;
            }

            Assert(timedOut && timeoutBus.State == BusState.Faulted,
                "Delay timeout did not fault the bus.");
        }

        private static Task ConfigurationRoundTrips()
        {
            StationFactory factory = new StationFactory();
            ConfigurationStore store = new ConfigurationStore(factory);
            StationConfiguration original = MakeConfiguration(StationTypeIds.Flow, "Saved", 25, 0);
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                store.Save(path, new[] { original });
                original.Name = "Saved again";
                store.Save(path, new[] { original });
                IList<StationConfiguration> loaded = store.Load(path);
                Assert(loaded.Count == 1, "Station count did not survive configuration save/load.");
                Assert(loaded[0].Name == "Saved again", "Configuration overwrite did not replace old values.");
                Assert(loaded[0].Connections.Count == original.Connections.Count,
                    "Connection count did not survive configuration save/load.");
                Assert(loaded[0].Connections[0].SourceNodeId == original.Connections[0].SourceNodeId
                    && loaded[0].Connections[0].TargetNodeId == original.Connections[0].TargetNodeId,
                    "Connection endpoints did not survive configuration save/load.");
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            return Task.FromResult(0);
        }

        private static Task DemoConfigurationLoads()
        {
            StationFactory factory = new StationFactory();
            ConfigurationStore store = new ConfigurationStore(factory,
                new HardwareConfigurationValidator(new SimulatedDeviceService()));
            string path = Path.Combine(AppContext.BaseDirectory, "demo-flowmachine.xml");
            IList<StationConfiguration> stations = store.Load(path);
            Assert(stations.Count == 3, "Demo configuration should contain all station types.");
            Assert(stations.Any(station => station.StationType == StationTypeIds.Flow
                && station.Connections.Count == 6), "Demo FlowStation branches were not restored.");
            Assert(stations.Any(station => station.StationType == StationTypeIds.Home),
                "Demo HomeStation was not restored.");
            Assert(stations.Any(station => station.StationType == StationTypeIds.Test),
                "Demo TestStation was not restored.");
            return Task.FromResult(0);
        }

        private static Task HardwareConfigurationChecksDirections()
        {
            HardwareConfigurationValidator validator =
                new HardwareConfigurationValidator(new SimulatedDeviceService());
            CylinderNodeConfiguration valid = new CylinderNodeConfiguration
            {
                CylinderId = "Cylinder-A",
                Action = "extend",
                OutputPointId = "DO-01",
                ExtendedInputPointId = "DI-01",
                RetractedInputPointId = "DI-02",
                TimeoutMilliseconds = 1000,
                FaultPolicy = "abort"
            };
            Assert(validator.ValidateCylinder(valid).Count == 0,
                "A valid cylinder binding was rejected.");

            valid.OutputPointId = "DI-01";
            Assert(validator.ValidateCylinder(valid).Any(),
                "An input point was accepted as a cylinder output.");
            return Task.FromResult(0);
        }

        private static BusController CreateBus(IList<IStation> stations,
            ConcurrentQueue<RuntimeLogEntry> logs)
        {
            BusController bus = new BusController(stations,
                new WorkflowExecutor(new WorkflowValidator()),
                new DeterministicStationScheduleStrategy());
            if (logs != null)
            {
                bus.Log += delegate(object sender, RuntimeLogEntry entry) { logs.Enqueue(entry); };
            }

            return bus;
        }

        private static List<IStation> MakeStations(params StationConfiguration[] configurations)
        {
            StationFactory factory = new StationFactory();
            return configurations.Select(factory.Create).ToList();
        }

        private static StationConfiguration MakeConfiguration(string type, string name,
            int delayMilliseconds, int timeoutMilliseconds)
        {
            StationConfiguration configuration = new StationConfiguration
            {
                Name = name,
                StationType = type,
                Enabled = true
            };
            NodeDefinition start = new NodeDefinition { Type = NodeTypeIds.Start };
            NodeDefinition delay = new NodeDefinition
            {
                Type = NodeTypeIds.Delay,
                DelayMilliseconds = delayMilliseconds,
                TimeoutMilliseconds = timeoutMilliseconds
            };
            NodeDefinition log = new NodeDefinition { Type = NodeTypeIds.Log, Message = "Test log" };
            NodeDefinition end = new NodeDefinition { Type = NodeTypeIds.End };
            configuration.Nodes.Add(start);
            configuration.Nodes.Add(delay);
            configuration.Nodes.Add(log);
            configuration.Nodes.Add(end);
            configuration.Connections.Add(new ConnectionDefinition
            {
                SourceNodeId = start.Id,
                TargetNodeId = delay.Id,
                Branch = BranchIds.Next
            });
            configuration.Connections.Add(new ConnectionDefinition
            {
                SourceNodeId = delay.Id,
                TargetNodeId = log.Id,
                Branch = BranchIds.Next
            });
            configuration.Connections.Add(new ConnectionDefinition
            {
                SourceNodeId = log.Id,
                TargetNodeId = end.Id,
                Branch = BranchIds.Next
            });
            return configuration;
        }

        private static StationConfiguration MakeConditionConfiguration(bool conditionValue)
        {
            StationConfiguration configuration = new StationConfiguration
            {
                Name = "Condition",
                StationType = StationTypeIds.Flow,
                Enabled = true
            };
            NodeDefinition start = new NodeDefinition { Type = NodeTypeIds.Start };
            NodeDefinition condition = new NodeDefinition
            {
                Type = NodeTypeIds.Condition,
                ConditionKey = "simulated",
                ConditionValue = conditionValue
            };
            NodeDefinition trueLog = new NodeDefinition
            {
                Type = NodeTypeIds.Log,
                Message = "selected true"
            };
            NodeDefinition falseLog = new NodeDefinition
            {
                Type = NodeTypeIds.Log,
                Message = "selected false"
            };
            NodeDefinition end = new NodeDefinition { Type = NodeTypeIds.End };
            configuration.Nodes.Add(start);
            configuration.Nodes.Add(condition);
            configuration.Nodes.Add(trueLog);
            configuration.Nodes.Add(falseLog);
            configuration.Nodes.Add(end);
            AddConnection(configuration, start, condition, BranchIds.Next);
            AddConnection(configuration, condition, trueLog, BranchIds.True);
            AddConnection(configuration, condition, falseLog, BranchIds.False);
            AddConnection(configuration, trueLog, end, BranchIds.Next);
            AddConnection(configuration, falseLog, end, BranchIds.Next);
            return configuration;
        }

        private static void AddConnection(StationConfiguration configuration, NodeDefinition source,
            NodeDefinition target, string branch)
        {
            configuration.Connections.Add(new ConnectionDefinition
            {
                SourceNodeId = source.Id,
                TargetNodeId = target.Id,
                Branch = branch
            });
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
