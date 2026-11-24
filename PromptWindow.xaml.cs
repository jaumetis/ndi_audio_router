using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NdiAudioRouter;

/// <summary>Diàleg amb botons en l'idioma de l'app (els MessageBox surten en l'idioma de Windows).</summary>
public partial class PromptWindow : Window
{
    readonly int _cancelIndex;

    /// <summary>Índex del botó premut (o del darrer botó si es tanca amb Esc / la X).</summary>
    public int Result { get; private set; }

    PromptWindow(string title, string message, string[] buttons, int danger, int accent, int defaultIdx)
    {
        InitializeComponent();
        Title = title;
        Msg.Text = message;
        _cancelIndex = buttons.Length - 1;
        Result = _cancelIndex;

        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            string style = i == danger ? "DangerButton" : i == accent ? "PrimaryButton" : "GhostButton";
            var b = new Button
            {
                Content = buttons[i],
                MinWidth = 110,
                Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0),
                Style = (Style)FindResource(style),
                IsDefault = i == defaultIdx
            };
            b.Click += (_, _) => { Result = idx; Close(); };
            ButtonsPanel.Children.Add(b);
        }
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Result = _cancelIndex; Close(); }
    }

    /// <summary>Mostra el diàleg i retorna l'índex del botó triat. L'últim botó és el de cancel·lar.</summary>
    public static int Ask(Window? owner, string title, string message, string[] buttons,
                          int danger = -1, int accent = -1, int defaultIdx = 0)
    {
        var w = new PromptWindow(title, message, buttons, danger, accent, defaultIdx);
        if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
        return w.Result;
    }
}
