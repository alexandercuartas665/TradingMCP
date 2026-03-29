Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading

Partial Public Class CandleHeaderControl
    Inherits UserControl

    ' ── Eventos públicos ──────────────────────────────────────────
    Public Event TimeframeChanged(granularity As Integer)
    Public Event OpenSettingsRequested()

    Private _clockTimer As DispatcherTimer
    Private _activeBtn As Button

    ' ── Constructor ──────────────────────────────────────────────
    Public Sub New()
        InitializeComponent()
        _activeBtn = btn15m
        IniciarReloj()
    End Sub

    ' ── API pública ───────────────────────────────────────────────
    Public Sub SetAsset(name As String, assetType As String)
        txtAssetName.Text = name
        txtAssetType.Text = assetType.ToUpper() & " • DERIV"
    End Sub

    Public Sub SetPrice(price As Decimal, changePct As Decimal)
        txtPrice.Text = "$" & price.ToString("N2")
        Dim sign As String = If(changePct >= 0, "+", "")
        txtChange.Text = sign & changePct.ToString("F2") & "%"

        If changePct >= 0 Then
            txtChange.Foreground = New Media.SolidColorBrush(Media.Color.FromRgb(0, 212, 170))
            bdrChange.Background = New Media.SolidColorBrush(
                Media.Color.FromArgb(25, 0, 212, 170))
        Else
            txtChange.Foreground = New Media.SolidColorBrush(Media.Color.FromRgb(255, 71, 87))
            bdrChange.Background = New Media.SolidColorBrush(
                Media.Color.FromArgb(25, 255, 71, 87))
        End If
    End Sub

    ' ── Reloj ────────────────────────────────────────────────────
    Private Sub IniciarReloj()
        _clockTimer = New DispatcherTimer() With {.Interval = TimeSpan.FromSeconds(1)}
        AddHandler _clockTimer.Tick, Sub(s, e)
            txtClock.Text = DateTime.UtcNow.ToString("HH:mm:ss")
        End Sub
        _clockTimer.Start()
        txtClock.Text = DateTime.UtcNow.ToString("HH:mm:ss")
    End Sub

    ' ── Timeframe buttons ─────────────────────────────────────────
    Private Sub TfBtn_Click(sender As Object, e As RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return

        ' Reset previous active
        If _activeBtn IsNot Nothing Then
            _activeBtn.Style = TryCast(FindResource("TfBtn"), Style)
        End If

        btn.Style = TryCast(FindResource("TfBtnActive"), Style)
        _activeBtn = btn

        Dim granularity As Integer = CInt(btn.Tag)
        RaiseEvent TimeframeChanged(granularity)
    End Sub

    ' ── Settings Button ──────────────────────────────────────────
    Private Sub BtnSettings_Click(sender As Object, e As RoutedEventArgs)
        RaiseEvent OpenSettingsRequested()
    End Sub

    ' ── Cleanup ──────────────────────────────────────────────────
    Public Sub StopClock()
        If _clockTimer IsNot Nothing Then _clockTimer.Stop()
    End Sub

End Class
