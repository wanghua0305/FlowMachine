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
                Tuple.Create("Resume does not repeat completed nodes", (Func<Task>)ResumeDoesNotRepeatNodes),
                Tuple.Create("Stop cleans up the active task", (Func<Task>)StopCleansUp),
                Tuple.Create("Fault and timeout reach the bus", (Func<Task>)FaultAndTimeoutPropagate),
                Tuple.Create("Configuration round-trips graph edges", (Func<Task>)ConfigurationRoundTrips)
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
                IList<StationConfiguration> loaded = store.Load(path);
                Assert(loaded.Count == 1, "Station count did not survive configuration save/load.");
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

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
