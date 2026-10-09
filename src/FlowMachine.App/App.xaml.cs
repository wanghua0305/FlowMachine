using System.Windows;
using FlowMachine.App.Modules;
using Prism.DryIoc;
using Prism.Ioc;
using Prism.Modularity;

namespace FlowMachine.App
{
    public partial class App : PrismApplication
    {
        protected override Window CreateShell()
        {
            return Container.Resolve<MainWindow>();
        }

        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
        }

        protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
        {
            moduleCatalog.AddModule<FlowMachineModule>();
        }
    }
}
