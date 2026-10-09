using System;
using System.Collections.Generic;
using System.Linq;
using FlowMachine.Core;

namespace FlowMachine.Infrastructure
{
    public sealed class HardwareConfigurationValidator
    {
        private readonly IDeviceService _deviceService;

        public HardwareConfigurationValidator(IDeviceService deviceService)
        {
            _deviceService = deviceService;
        }

        public IList<string> ValidateCylinder(CylinderNodeConfiguration configuration)
        {
            List<string> errors = new List<string>();
            if (configuration == null || string.IsNullOrWhiteSpace(configuration.CylinderId))
            {
                errors.Add("A cylinder identifier is required.");
                return errors;
            }

            if (!_deviceService.Cylinders.GetCylinderIds().Contains(configuration.CylinderId))
            {
                errors.Add("Cylinder '" + configuration.CylinderId + "' is not available.");
            }

            if (configuration.Action != "extend" && configuration.Action != "retract")
            {
                errors.Add("Cylinder action must be extend or retract.");
            }

            if (configuration.TimeoutMilliseconds <= 0)
            {
                errors.Add("Cylinder timeout must be positive.");
            }

            if (string.IsNullOrWhiteSpace(configuration.FaultPolicy))
            {
                errors.Add("A cylinder fault policy is required.");
            }

            if (!_deviceService.Io.GetPoints(IoDirection.Output)
                .Any(point => point.Id == configuration.OutputPointId))
            {
                errors.Add("Cylinder output must reference an available output point.");
            }

            if (!_deviceService.Io.GetPoints(IoDirection.Input)
                .Any(point => point.Id == configuration.ExtendedInputPointId))
            {
                errors.Add("Extended-position sensor must reference an available input point.");
            }

            if (!_deviceService.Io.GetPoints(IoDirection.Input)
                .Any(point => point.Id == configuration.RetractedInputPointId))
            {
                errors.Add("Retracted-position sensor must reference an available input point.");
            }

            return errors;
        }

        public IList<string> ValidateAxis(AxisNodeConfiguration configuration)
        {
            List<string> errors = new List<string>();
            if (configuration == null || string.IsNullOrWhiteSpace(configuration.AxisId))
            {
                errors.Add("An axis identifier is required.");
                return errors;
            }

            if (!_deviceService.Axes.GetAxisIds().Contains(configuration.AxisId))
            {
                errors.Add("Axis '" + configuration.AxisId + "' is not available.");
            }

            if (configuration.TimeoutMilliseconds <= 0)
            {
                errors.Add("Axis timeout must be positive.");
            }

            if (configuration.Action != "home" && configuration.Action != "absolute"
                && configuration.Action != "relative" && configuration.Action != "stop")
            {
                errors.Add("Axis action must be home, absolute, relative or stop.");
            }

            if ((configuration.Action == "absolute" || configuration.Action == "relative")
                && (configuration.Speed <= 0 || configuration.Acceleration <= 0))
            {
                errors.Add("Motion speed and acceleration must be positive.");
            }

            if (string.IsNullOrWhiteSpace(configuration.FaultPolicy))
            {
                errors.Add("An axis fault policy is required.");
            }

            return errors;
        }
    }
}
