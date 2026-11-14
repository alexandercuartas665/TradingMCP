Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

Partial Public Class CandleAgentControl
    Inherits UserControl

    ' ── Colors  ──────────────────────────────────────────────────
    Private ReadOnly _bull As Color = Color.FromRgb(0, 212, 170)
    Private ReadOnly _bear As Color = Color.FromRgb(255, 71, 87)
    Private ReadOnly _neutral As Color = Color.FromRgb(90, 106, 109)
    Private ReadOnly _cyan As Color = Color.FromRgb(76, 214, 251)

    Public Sub New()
        InitializeComponent()
    End Sub

    ' ── Public API ───────────────────────────────────────────────
    Public Sub UpdateDecision(decision As String, confidencePct As Integer)
        txtDecision.Text = decision

        Dim fg As Color, bg As Color, bd As Color
        Select Case decision.ToUpper()
            Case "COMPRAR", "BUY"
                fg = _bull
                bg = Color.FromArgb(40, 0, 212, 170)
                bd = _bull
            Case "VENDER", "SELL"
                fg = _bear
                bg = Color.FromArgb(40, 255, 71, 87)
                bd = _bear
            Case Else
                fg = _neutral
                bg = Color.FromArgb(25, 90, 106, 109)
                bd = _neutral
        End Select

        decisionFg.Color = fg
        decisionBg.Color = bg
        decisionBorder.Color = bd
        confBarColor.Color = fg

        runConfidence.Text = confidencePct & "%"

        ' Animate bar width (parent border width)
        Dim parentW As Double = bdrDecision.ActualWidth - 36  ' subtract padding
        If parentW > 0 Then
            bdrConfidenceBar.Width = parentW * confidencePct / 100
        End If
    End Sub

    Public Sub UpdateIndicators(rsi As Double, macd As Double, smaBullish As Boolean)
        txtRsi.Text = rsi.ToString("F2")
        txtRsi.Foreground = New SolidColorBrush(
            If(rsi > 70, _bear, If(rsi < 30, _bull, Color.FromRgb(224, 230, 237))))

        Dim macdSign As String = If(macd >= 0, "+", "")
        txtMacd.Text = macdSign & macd.ToString("F4")
        txtMacd.Foreground = New SolidColorBrush(If(macd >= 0, _bull, _bear))

        txtSma.Text = If(smaBullish, "Alcista ▲", "Bajista ▼")
        txtSma.Foreground = New SolidColorBrush(If(smaBullish, _bull, _bear))
    End Sub

    Public Sub UpdateVolume(volume As Double)
        txtVol.Text = FormatVol(volume)
    End Sub

    Public Sub AddSignalLog(decision As String, reason As String)
        Dim bdr As New Border() With {
            .Background = New SolidColorBrush(Color.FromRgb(13, 13, 13)),
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(8, 6, 8, 6),
            .Margin = New Thickness(0, 0, 0, 4)
        }

        Dim accentColor As Color
        Select Case decision.ToUpper()
            Case "COMPRAR", "BUY"  : accentColor = _bull
            Case "VENDER", "SELL"  : accentColor = _bear
            Case Else              : accentColor = _neutral
        End Select

        ' Left accent bar
        bdr.BorderBrush = New SolidColorBrush(accentColor)
        bdr.BorderThickness = New Thickness(3, 0, 0, 0)

        Dim sp As New StackPanel()
        Dim timeBlock As New TextBlock() With {
            .Text = DateTime.Now.ToString("HH:mm") & "  " & reason,
            .FontSize = 9,
            .FontFamily = New FontFamily("Consolas"),
            .Foreground = New SolidColorBrush(Color.FromRgb(90, 106, 109)),
            .TextWrapping = TextWrapping.Wrap
        }
        sp.Children.Add(timeBlock)
        bdr.Child = sp

        pnlLog.Children.Insert(0, bdr)

        ' Keep max 12 entries
        Do While pnlLog.Children.Count > 12
            pnlLog.Children.RemoveAt(pnlLog.Children.Count - 1)
        Loop
    End Sub

    Public Sub SetStatus(online As Boolean)
        ellAgentStatus.Fill = New SolidColorBrush(If(online, _bull, _bear))
        txtAgentStatus.Text = If(online, "ANALIZANDO", "DESCONECTADO")
        txtAgentStatus.Foreground = New SolidColorBrush(If(online, _bull, _bear))
    End Sub

    Private Function FormatVol(v As Double) As String
        If v >= 1_000_000 Then Return (v / 1_000_000).ToString("F1") & "M"
        If v >= 1_000 Then Return (v / 1_000).ToString("F1") & "K"
        Return v.ToString("F0")
    End Function

End Class
