using System.Windows.Controls;
using System;
using System.Windows;

namespace Nina.LivePolarAlignment.UI;

public partial class PolarAlignmentView : UserControl {
    private bool? wideLayout;

    public PolarAlignmentView() {
        InitializeComponent();
        SizeChanged += (_, change) => ArrangeWorkspace(change.NewSize.Width);
        Loaded += (_, _) => ArrangeWorkspace(ActualWidth);
    }
    public new object? FindName(string name)=>base.FindName(name)??ImageView.FindName(name)??ControlsView.FindName(name);

    private void ArrangeWorkspace(double width) {
        bool wide = width >= 900;
        if (wideLayout != wide) {
            wideLayout = wide;
            ControlsColumn.MinWidth = wide ? 270 : 0;
            ControlsColumn.Width = new GridLength(wide ? 290 : 0);
            DividerColumn.Width = new GridLength(wide ? 6 : 0);
            PanelDivider.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            NarrowControlsRow.Height = wide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetRow(ControlsView, wide ? 1 : 2);
            Grid.SetColumn(ControlsView, wide ? 2 : 0);
            ControlsView.Margin = wide ? new Thickness(0) : new Thickness(0, 12, 0, 0);
        }
        ImageRow.Height = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(Math.Clamp(width * .58, 200, 350));
    }
}
