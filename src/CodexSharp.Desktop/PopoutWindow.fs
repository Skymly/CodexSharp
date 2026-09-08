namespace CodexSharp.Desktop

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open CodexSharp.Runtime

type PopoutWindow(threadId: string, header: string, onSend: string -> unit) as this =
    inherit Window()

    do
        this.Title <- "CodexSharp pop-out"
        this.Width <- 420.
        this.Height <- 520.
        this.MinWidth <- 320.
        this.MinHeight <- 240.
        this.Topmost <- true
        this.CanResize <- true
        let status =
            TextBlock(
                Text = header + Environment.NewLine + "thread " + threadId + "  ·  always on top",
                TextWrapping = TextWrapping.Wrap)
        let composer = TextBox(AcceptsReturn = true)
        let send = Button(Content = "Send")
        send.Click.Add(fun _ ->
            let text = composer.Text
            if not (String.IsNullOrWhiteSpace text) then
                onSend text
                composer.Text <- "")
        let panel = StackPanel(Margin = Thickness(12.), Spacing = 8.)
        panel.Children.Add(status)
        panel.Children.Add(composer)
        panel.Children.Add(send)
        this.Content <- panel
