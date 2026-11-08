using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;

namespace Nina.LivePolarAlignment.UI;
[Export(typeof(IDockableVM))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class PolarAlignmentImageDockableVM : DockableVM {
    public PolarAlignmentDockableVM Session {get;}
    public override bool IsTool=>true;
    [ImportingConstructor]
    public PolarAlignmentImageDockableVM(IProfileService profiles,PolarAlignmentDockableVM session):base(profiles) {
        Title="PA Image";Session=session;ImageGeometry=session.ImageGeometry;
        session.RegisterImagePanel(this);
    }
}
public sealed partial class PolarAlignmentDockableVM {
    private readonly List<PolarAlignmentImageDockableVM> imagePanels=new();
    private bool changingPanelVisibility;
    private bool focusImage;
    private double mainImageScale=1;
    public bool FocusImage {get=>focusImage;set{if(focusImage==value)return;focusImage=value;RaisePropertyChanged();}}
    public double MainImageScale=>mainImageScale;
    public void SetMainImageScale(double scale) {if(!double.IsFinite(scale)||scale<=0||Math.Abs(scale-mainImageScale)<1e-9)return;mainImageScale=scale;RaisePropertyChanged(nameof(MainImageScale));}
    internal void RegisterImagePanel(PolarAlignmentImageDockableVM panel) {
        if(!uiDispatcher.CheckAccess()){uiDispatcher.BeginInvoke((Action)(()=>RegisterImagePanel(panel)));return;}
        imagePanels.Add(panel);
        panel.PropertyChanged+=PanelVisibilityChanged;
    }
    private void PanelVisibilityChanged(object? sender,PropertyChangedEventArgs e) {
        if(e.PropertyName is nameof(IsVisible) or nameof(IsClosed))UpdateWorkspaceVisibility();
    }
    private void UpdateWorkspaceVisibility() {
        if(changingPanelVisibility)return;
        if(!uiDispatcher.CheckAccess()){uiDispatcher.BeginInvoke((Action)UpdateWorkspaceVisibility);return;}
        bool visible=(!IsClosed&&IsVisible)||imagePanels.Any(p=>!p.IsClosed&&p.IsVisible);
        if(previewSuspended==!visible)return;
        previewSuspended=!visible;
        if(!visible)RequestStopLive();else previewAttempted=false;
        UpdatePoleMasterPreview();
    }
    public override void Hide(object o) {
        bool show=IsClosed || !IsVisible || imagePanels.Any(p=>p.IsClosed || !p.IsVisible);
        changingPanelVisibility=true;
        try {
            if(show)IsClosed=false;
            IsVisible=show;
            foreach(var panel in imagePanels){if(show)panel.IsClosed=false;panel.IsVisible=show;}
        } finally {changingPanelVisibility=false;}
        UpdateWorkspaceVisibility();
    }
    private RelayCommand? arrangePanelsCommand;
    public ICommand ArrangePanelsCommand=>arrangePanelsCommand??=new(()=>ArrangeDockPanels(true));
    private void DisposePanels(){foreach(var panel in imagePanels)panel.PropertyChanged-=PanelVisibilityChanged;imagePanels.Clear();}
}
