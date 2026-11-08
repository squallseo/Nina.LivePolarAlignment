using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using AvalonDock;
using AvalonDock.Layout;
using NINA.Core.Utility;
using NINA.Profile;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace Nina.LivePolarAlignment.UI;
public sealed partial class PolarAlignmentDockableVM {
    private WeakReference<DockingManager>? dockingHost;
    private const string PanelLayoutKey="SeparatePanels.LayoutV1";
    public void AttachDockingHost(FrameworkElement view) {
        for(DependencyObject? parent=view;parent!=null;parent=VisualTreeHelper.GetParent(parent)) {
            if(parent is not DockingManager manager)continue;
            dockingHost=new(manager);PolarDockLayoutObserver.Attach(manager,this);return;
        }
    }
    internal void ArrangeDockPanels(bool force=false) {
        if(dockingHost?.TryGetTarget(out var manager)!=true || manager.DataContext is not IDockManagerVM host)return;
        var settings=profiles.ActiveProfile.PluginSettings;
        if(settings==null || imagePanels.Count==0)return;
        try {
            if(!force && settings.TryGetValue(Plugin.SettingsId,PanelLayoutKey,out bool placed) && placed)return;
            if(!force) {
                var folder=ProfileService.PROFILEFOLDER;
                var files=new[]{Path.Combine(folder,$"{profiles.ActiveProfile.Id}.dock.config"),Path.Combine(folder,"GLOBAL.dock.config")};
                // Adopt previously saved separate panels without moving the user's layout.
                if(files.Where(File.Exists).Any(p=>XDocument.Load(p).Descendants().Any(e=>(string?)e.Attribute("ContentId")==imagePanels[0].ContentId))) {
                    settings.SetValue(Plugin.SettingsId,PanelLayoutKey,true);return;
                }
            }
            var placements=new[]{(Panel:(NINA.Equipment.Interfaces.ViewModel.IDockableVM)this,Target:"ImagingVM"),
                (Panel:(NINA.Equipment.Interfaces.ViewModel.IDockableVM)imagePanels[0],Target:"ImageControlVM")};
            // Inspect both destinations before changing either panel.
            var moves=placements.Select(p=>(p.Panel,Target:manager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a=>a.ContentId==p.Target))).ToArray();
            if(moves.Any(m=>m.Target?.Parent is not LayoutAnchorablePane || m.Target.FindParent<LayoutFloatingWindow>()!=null))return;
            foreach(var move in moves){move.Panel.IsClosed=false;move.Panel.IsVisible=true;}
            var sources=moves.Select(m=>manager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a=>ReferenceEquals(a.Content,m.Panel))).ToArray();
            if(sources.Any(s=>s==null))return;
            for(int i=0;i<moves.Length;i++) {
                var source=sources[i]!;var target=moves[i].Target!;var pane=(LayoutAnchorablePane)target.Parent;
                if(source.Parent!=pane || pane.Children.IndexOf(source)!=pane.Children.IndexOf(target)+1) {
                    source.Parent?.RemoveChild(source);pane.Children.Insert(pane.Children.IndexOf(target)+1,source);
                }
                source.IsSelected=true;
            }
            settings.SetValue(Plugin.SettingsId,PanelLayoutKey,true);
            host.SaveAvalonDockLayout();
        } catch(Exception error){Logger.Warning("Polar panel placement: "+error.Message);}
    }
}

// NINA may deserialize a new layout after a view's Loaded event. Wait for
// its public load-command completion and retry when the layout is replaced.
internal sealed class PolarDockLayoutObserver:DependencyObject {
    private static readonly ConditionalWeakTable<DockingManager,PolarDockLayoutObserver> observers=new();
    private readonly DockingManager manager;
    private readonly PolarAlignmentDockableVM session;
    private LayoutRoot? observed;
    private bool queued,applying;
    private static readonly DependencyProperty ReadyProperty=DependencyProperty.Register("Ready",typeof(bool),typeof(PolarDockLayoutObserver),new PropertyMetadata(false,(d,_)=>((PolarDockLayoutObserver)d).Queue()));
    internal static void Attach(DockingManager manager,PolarAlignmentDockableVM session)=>observers.GetValue(manager,m=>new(m,session)).Queue();
    private PolarDockLayoutObserver(DockingManager manager,PolarAlignmentDockableVM session) {
        this.manager=manager;this.session=session;
        BindingOperations.SetBinding(this,ReadyProperty,new Binding("DataContext.LoadAvalonDockLayoutCommand.Execution.IsCompleted"){Source=manager,FallbackValue=false,TargetNullValue=false});
        manager.LayoutChanged+=(_,_)=>Observe();Observe();
    }
    private void Observe(){if(observed!=null)observed.Updated-=LayoutUpdated;observed=manager.Layout;if(observed!=null)observed.Updated+=LayoutUpdated;Queue();}
    private void LayoutUpdated(object? sender,EventArgs e)=>Queue();
    private void Queue() {
        if(queued||applying||!(bool)GetValue(ReadyProperty))return;
        queued=true;
        manager.Dispatcher.BeginInvoke((Action)(()=>{queued=false;if(!(bool)GetValue(ReadyProperty))return;applying=true;try{if(manager.DataContext is IDockManagerVM host)PolarToolbar.Configure(host);session.ArrangeDockPanels();}finally{applying=false;}}),DispatcherPriority.ContextIdle);
    }
}

internal static class PolarToolbar {
    private static readonly ConditionalWeakTable<System.ComponentModel.ICollectionView,object> configured=new();
    internal static void Configure(IDockManagerVM host) {
        var tools=host.AnchorableTools;
        if(tools==null || ReferenceEquals(tools,host.Anchorables))return;
        var view=CollectionViewSource.GetDefaultView(tools);
        if(!view.CanFilter || configured.TryGetValue(view,out _))return;
        var previous=view.Filter;
        view.Filter=item=>item is not PolarAlignmentImageDockableVM && (previous?.Invoke(item)??true);
        configured.Add(view,new object());
    }
}
