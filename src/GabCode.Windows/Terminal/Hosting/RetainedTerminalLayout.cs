using System.Windows;
using System.Windows.Controls;

namespace GabCode.Windows.Terminal.Hosting;

internal sealed class RetainedTerminalLayout
{
    private readonly ContentControl mainRegion;
    private readonly ContentControl bottomRegion;

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

    internal void ShowPiInMain() => Place(PiView, CommandsView, piInMain: true);

    internal void ShowCommandsInMain() => Place(CommandsView, PiView, piInMain: false);

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
