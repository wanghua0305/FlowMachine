using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using FlowMachine.Core;
using FlowMachine.Runtime;

namespace FlowMachine.Infrastructure
{
    [XmlRoot("FlowMachineConfiguration")]
    public sealed class ConfigurationDocument
    {
        public ConfigurationDocument()
        {
            FormatVersion = ConfigurationStore.CurrentFormatVersion;
            Stations = new List<StationConfiguration>();
        }

        [XmlAttribute("formatVersion")]
        public int FormatVersion { get; set; }

        [XmlArray("Stations")]
        [XmlArrayItem("Station")]
        public List<StationConfiguration> Stations { get; set; }
    }

    public interface IConfigurationStore
    {
        void Save(string path, IEnumerable<StationConfiguration> stations);
        IList<StationConfiguration> Load(string path);
    }

    public sealed class ConfigurationStore : IConfigurationStore
    {
        public const int CurrentFormatVersion = 1;
        private readonly IStationFactory _stationFactory;
        private readonly HardwareConfigurationValidator _hardwareValidator;

        public ConfigurationStore(IStationFactory stationFactory)
            : this(stationFactory, null)
        {
        }

        public ConfigurationStore(IStationFactory stationFactory,
            HardwareConfigurationValidator hardwareValidator)
        {
            _stationFactory = stationFactory;
            _hardwareValidator = hardwareValidator;
        }

        public void Save(string path, IEnumerable<StationConfiguration> stations)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A configuration file path is required.", "path");
            }

            if (stations == null)
            {
                throw new ArgumentNullException("stations");
            }

            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            ConfigurationDocument document = new ConfigurationDocument
            {
                FormatVersion = CurrentFormatVersion,
                Stations = stations.Select(CloneStation).ToList()
            };
            string temporaryPath = fullPath + ".tmp";
            XmlSerializer serializer = new XmlSerializer(typeof(ConfigurationDocument));
            using (FileStream stream = new FileStream(temporaryPath, FileMode.Create,
                FileAccess.Write, FileShare.None))
            {
                serializer.Serialize(stream, document);
                stream.Flush();
            }

            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, null);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }

        public IList<StationConfiguration> Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The configuration file was not found.", path);
            }

            ConfigurationDocument document;
            XmlSerializer serializer = new XmlSerializer(typeof(ConfigurationDocument));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                document = (ConfigurationDocument)serializer.Deserialize(stream);
            }

            if (document.FormatVersion != CurrentFormatVersion)
            {
                throw new InvalidDataException("Unsupported configuration format version "
                    + document.FormatVersion + "; expected " + CurrentFormatVersion + ".");
            }

            if (document.Stations == null)
            {
                throw new InvalidDataException("Configuration is missing its station list.");
            }

            HashSet<string> stationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (StationConfiguration station in document.Stations)
            {
                if (station == null || string.IsNullOrWhiteSpace(station.Id)
                    || !stationIds.Add(station.Id))
                {
                    throw new InvalidDataException("Every station must have a unique non-empty id.");
                }

                station.LoadWarnings.Clear();
                try
                {
                    _stationFactory.Create(station);
                }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidDataException("Configuration contains an unknown station type: "
                        + station.StationType + ".", exception);
                }

                if (station.Nodes == null || station.Connections == null)
                {
                    throw new InvalidDataException("Station " + station.Name
                        + " is missing its nodes or connections.");
                }

                HashSet<string> nodeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (NodeDefinition node in station.Nodes)
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.Id) || !nodeIds.Add(node.Id))
                    {
                        throw new InvalidDataException("Station " + station.Name
                            + " contains a node with a missing or duplicate id.");
                    }

                    if (!IsKnownNodeType(node.Type))
                    {
                        throw new InvalidDataException("Station " + station.Name
                            + " contains unknown node type '" + node.Type + "'.");
                    }
                }

                try
                {
                    new WorkflowValidator().Validate(new WorkflowSnapshot(station.Id,
                        station.Name, station.StationType, station.Nodes, station.Connections), true);
                }
                catch (WorkflowValidationException exception)
                {
                    station.LoadWarnings.Add("Workflow is incomplete or invalid: " + exception.Message);
                }

                if (_hardwareValidator != null)
                {
                    foreach (NodeDefinition node in station.Nodes)
                    {
                        IList<string> hardwareErrors = node.Type == NodeTypeIds.Cylinder
                            ? _hardwareValidator.ValidateCylinder(node.Cylinder)
                            : node.Type == NodeTypeIds.Axis
                                ? _hardwareValidator.ValidateAxis(node.Axis)
                                : new List<string>();
                        if (hardwareErrors.Count != 0)
                        {
                            station.LoadWarnings.Add("Hardware configuration for node " + node.Id
                                + " is invalid: " + string.Join(" ", hardwareErrors));
                        }
                    }
                }

                foreach (ConnectionDefinition connection in station.Connections)
                {
                    if (connection == null || !nodeIds.Contains(connection.SourceNodeId)
                        || !nodeIds.Contains(connection.TargetNodeId))
                    {
                        throw new InvalidDataException("Station " + station.Name
                            + " contains a connection to a missing node.");
                    }
                }
            }

            return document.Stations;
        }

        private static bool IsKnownNodeType(string type)
        {
            return type == NodeTypeIds.Start || type == NodeTypeIds.End
                || type == NodeTypeIds.Delay || type == NodeTypeIds.Log
                || type == NodeTypeIds.Condition || type == NodeTypeIds.Cylinder
                || type == NodeTypeIds.Axis;
        }

        private static StationConfiguration CloneStation(StationConfiguration station)
        {
            WorkflowSnapshot snapshot = new WorkflowSnapshot(station.Id, station.Name,
                station.StationType, station.Nodes, station.Connections);
            StationConfiguration clone = new StationConfiguration
            {
                Id = station.Id,
                Name = station.Name,
                StationType = station.StationType,
                Enabled = station.Enabled,
                Nodes = snapshot.Nodes.ToList(),
                Connections = snapshot.Connections.ToList()
            };
            return clone;
        }
    }
}
