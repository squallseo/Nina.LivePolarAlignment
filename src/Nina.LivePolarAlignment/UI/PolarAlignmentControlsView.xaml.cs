using System.Windows.Controls;
namespace Nina.LivePolarAlignment.UI;
public partial class PolarAlignmentControlsView : UserControl {
    public PolarAlignmentControlsView() {
        InitializeComponent();
        Loaded+=(_,_)=>{if(DataContext is PolarAlignmentDockableVM vm)vm.AttachDockingHost(this);};
        AlignmentDetailView.VisibleImageRegionChanged+=()=>{
            if(DataContext is not PolarAlignmentDockableVM vm || AlignmentDetailView.Preview is not {} image)return;
            var r=AlignmentDetailView.VisibleImageRegion;
            if(r.Width>0 && r.Height>0)vm.SetManualDetailRegion(new(r.X/image.PixelWidth,r.Y/image.PixelHeight,r.Width/image.PixelWidth,r.Height/image.PixelHeight));
        };
    }
}
