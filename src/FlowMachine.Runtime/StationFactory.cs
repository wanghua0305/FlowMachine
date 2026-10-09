using System;
using System.Collections.Generic;
using FlowMachine.Core;

namespace FlowMachine.Runtime
{
    public sealed class StationFactory : IStationFactory
    {
        private readonly Dictionary<string, Func<StationConfiguration, IStation>> _creators;

        public StationFactory()
        {
            _creators = new Dictionary<string, Func<StationConfiguration, IStation>>(
                StringComparer.OrdinalIgnoreCase);
            Register(StationTypeIds.Flow, delegate(StationConfiguration configuration)
            {
                return new FlowStation(configuration);
            });
            Register(StationTypeIds.Home, delegate(StationConfiguration configuration)
            {
                return new HomeStation(configuration);
            });
            Register(StationTypeIds.Test, delegate(StationConfiguration configuration)
            {
                return new TestStation(configuration);
            });
        }

        public void Register(string stationTypeId, Func<StationConfiguration, IStation> creator)
        {
            if (string.IsNullOrWhiteSpace(stationTypeId))
            {
                throw new ArgumentException("A station type id is required.", "stationTypeId");
            }

            if (creator == null)
            {
                throw new ArgumentNullException("creator");
            }

            _creators[stationTypeId] = creator;
        }

        public IStation Create(StationConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }

            Func<StationConfiguration, IStation> creator;
            if (configuration.StationType == null
                || !_creators.TryGetValue(configuration.StationType, out creator))
            {
                throw new InvalidOperationException(
                    "Unknown station type: " + configuration.StationType + ".");
            }

            return creator(configuration);
        }
    }
}
