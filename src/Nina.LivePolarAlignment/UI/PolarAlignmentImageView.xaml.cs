using System.Windows.Controls;
namespace Nina.LivePolarAlignment.UI;
public partial class PolarAlignmentImageView : UserControl {
    public PolarAlignmentImageView() {
        InitializeComponent();
        Loaded+=(_,_)=>{if(DataContext is PolarAlignmentDockableVM vm)vm.AttachDockingHost(this);};
        SkyField.VisibleImageRegionChanged+=()=>{if(DataContext is PolarAlignmentDockableVM vm)vm.SetMainImageScale(SkyField.ImageScale);};
        SkyField.MouseLeftButtonDown+=(_,e)=>{
            if(DataContext is not PolarAlignmentDockableVM vm || SkyField.Preview==null)return;
            var p=e.GetPosition(SkyField);var r=SkyField.VisibleImageRegion;
            vm.SelectPolarisCandidate((r.X+p.X/SkyField.ActualWidth*r.Width)/SkyField.Preview.PixelWidth,
                (r.Y+p.Y/SkyField.ActualHeight*r.Height)/SkyField.Preview.PixelHeight);
        };
    }
}
