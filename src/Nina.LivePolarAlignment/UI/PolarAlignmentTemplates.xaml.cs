using System.ComponentModel.Composition;
using System.Windows;

namespace Nina.LivePolarAlignment.UI;

[Export(typeof(ResourceDictionary))]
public partial class PolarAlignmentTemplates : ResourceDictionary {
    public PolarAlignmentTemplates() => InitializeComponent();
}
