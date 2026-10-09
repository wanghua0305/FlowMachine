using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlowMachine.App.ViewModels;

namespace FlowMachine.App.Views
{
    public partial class StationWorkspaceView : UserControl
    {
        public StationWorkspaceView()
        {
            InitializeComponent();
        }

        private void OnToolboxMouseMove(object sender, MouseEventArgs args)
        {
            if (args.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            ListBoxItem item = FindAncestor<ListBoxItem>(args.OriginalSource as DependencyObject);
            if (item != null && item.Tag is string)
            {
                DragDrop.DoDragDrop(item, item.Tag, DragDropEffects.Copy);
            }
        }

        private void OnEditorDrop(object sender, DragEventArgs args)
        {
            string type = args.Data.GetData(typeof(string)) as string;
            StationWorkspaceViewModel viewModel = DataContext as StationWorkspaceViewModel;
            if (type != null && viewModel != null && sender is Nodify.NodifyEditor)
            {
                Nodify.NodifyEditor editor = (Nodify.NodifyEditor)sender;
                viewModel.AddNodeFromDrop(type, editor.GetLocationInsideEditor(args));
                args.Handled = true;
            }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                T match = current as T;
                if (match != null)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
