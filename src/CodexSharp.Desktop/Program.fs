namespace CodexSharp.Desktop

open System
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.Styling
open Avalonia.Themes.Fluent
open Avalonia.Threading
open CodexSharp.Runtime

type MainWindow() as this =
    inherit HostWindow()
    do
        base.Title <- "CodexSharp"
        base.Width <- 1280.
        base.Height <- 820.
        base.MinWidth <- 960.
        base.MinHeight <- 640.
        this.Content <- MainView.create ()

type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- ThemeVariant.Dark

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            desktop.MainWindow <- MainWindow()
        | _ -> ()
        base.OnFrameworkInitializationCompleted()

module Program =
    let private activateMainWindow () =
        match Application.Current with
        | null -> ()
        | app ->
            match app.ApplicationLifetime with
            | :? IClassicDesktopStyleApplicationLifetime as desk when not (isNull desk.MainWindow) ->
                desk.MainWindow.Show()
                desk.MainWindow.WindowState <- WindowState.Normal
                desk.MainWindow.Activate()
            | _ -> ()

    [<EntryPoint; STAThread>]
    let main argv =
        let payload = DesktopDeepLink.FirstPayload argv
        if not (DesktopActivation.TryBecomePrimary()) then
            DesktopActivation.TrySendToPrimary(payload) |> ignore
            0
        else
            DesktopDeepLink.TryRegisterCodexSharp() |> ignore
            DesktopActivation.SetStartup(DesktopDeepLink.Parse(payload))
            use cts = new CancellationTokenSource()
            DesktopActivation.Listen(
                (fun msg ->
                    let link = DesktopDeepLink.Parse(msg)
                    Dispatcher.UIThread.Post(fun () -> activateMainWindow ())
                    DesktopActivation.Publish(link)),
                cts.Token)
            try
                AppBuilder
                    .Configure<App>()
                    .UsePlatformDetect()
                    .WithInterFont()
                    .LogToTrace()
                    .StartWithClassicDesktopLifetime(argv)
            finally
                cts.Cancel()
