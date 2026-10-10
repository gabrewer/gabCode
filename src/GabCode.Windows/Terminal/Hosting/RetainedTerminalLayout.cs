using System.Windows;
using System.Windows.Controls;

namespace GabCode.Windows.Terminal.Hosting;

internal sealed class RetainedTerminalLayout
{
    private readonly ContentControl mainRegion;
    private readonly ContentControl bottomRegion;
    private TabControl? documentTabs;
    private bool restorePiInMain;

    internal RetainedTerminalLayout(
        ContentControl mainRegion,
        ContentControl bottomRegion,
        FrameworkElement piView,
        FrameworkElement commandsView)
    {
        this.mainRegion = mainRegion ?? throw new ArgumentNullException(nameof(mainRegion));
        this.bottomRegion = bottomRegion ?? throw new ArgumentNullException(nameof(bottomRegion));
        PiView = piView ?? throw new ArgumentNullException(nameof(piView));
        CommandsView = commandsView ?? throw new ArgumentNullException(nameof(commandsView));
    }

    internal FrameworkElement PiView { get; }

    internal FrameworkElement CommandsView { get; }

    internal bool IsPiInMain { get; private set; }

    internal bool HasDocument => documentTabs is not null;

    internal void ShowPiInMain()
    {
        if (HasDocument) return;
        Place(PiView, CommandsView, piInMain: true);
    }

    internal void ShowCommandsInMain()
    {
        if (HasDocument) return;
        Place(CommandsView, PiView, piInMain: false);
    }

    internal void OpenDocument(FrameworkElement document, TabControl tabs)
    {
        if (documentTabs is not null)
        {
            if (!ReferenceEquals(documentTabs, tabs)) throw new InvalidOperationException("The document terminal host cannot change while a document is open.");
            mainRegion.Content = document;
            return;
        }
        if (tabs.Items.Count != 2 || tabs.Items[0] is not TabItem first || tabs.Items[1] is not TabItem second)
            throw new ArgumentException("A document requires exactly two terminal tabs.", nameof(tabs));
        restorePiInMain = IsPiInMain;
        mainRegion.Content = null;
        bottomRegion.Content = null;
        first.Content = PiView;
        second.Content = CommandsView;
        tabs.SelectedIndex = restorePiInMain ? 0 : 1;
        bottomRegion.Content = tabs;
        mainRegion.Content = document;
        documentTabs = tabs;
    }

    internal void CloseDocument()
    {
        if (documentTabs is null) return;
        var tabs = documentTabs;
        mainRegion.Content = null;
        bottomRegion.Content = null;
        ((TabItem)tabs.Items[0]).Content = null;
        ((TabItem)tabs.Items[1]).Content = null;
        documentTabs = null;
        if (restorePiInMain) ShowPiInMain(); else ShowCommandsInMain();
    }

    private void Place(FrameworkElement mainView, FrameworkElement bottomView, bool piInMain)
    {
        if (ReferenceEquals(mainRegion.Content, mainView) && ReferenceEquals(bottomRegion.Content, bottomView))
        {
            IsPiInMain = piInMain;
            return;
        }

        mainRegion.Content = null;
        bottomRegion.Content = null;
        mainRegion.Content = mainView;
        bottomRegion.Content = bottomView;
        IsPiInMain = piInMain;
    }
}
