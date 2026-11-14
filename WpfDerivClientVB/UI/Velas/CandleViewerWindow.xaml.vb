Imports System.Windows
Imports WpfDerivClientVB.WpfDerivClientVB

' ================================================================
'  CandleViewerWindow
'  ─ Child-window autónoma: puede abrirse múltiples instancias.
'  ─ Cada instancia muestra un activo/timeframe diferente.
'  ─ Inyecta controles via ContentControl (misma lógica que
'    AdminDashboard con AdminSidebar).
' ================================================================
Partial Public Class CandleViewerWindow
    Inherits Window

    ' ── Sub-controls ─────────────────────────────────────────────
    Private _header       As CandleHeaderControl
    Private _chart        As CandleChartControl
    Private _agent        As CandleAgentControl
    Private _transactions As CandleTransactionsControl

    ' ── Window state ─────────────────────────────────────────────
    Private _symbol      As String = "BTC/USD"
    Private _granularity As Integer = 900   ' default 15m
    Private _candles     As New List(Of CandleBar)()

    ' ── Constructor ──────────────────────────────────────────────
    Public Sub New()
        InitializeComponent()
        InyectarControles()
        ConectarEventos()
        CargarDatosDemo()
    End Sub

    Protected Overrides Sub OnSourceInitialized(e As EventArgs)
        MyBase.OnSourceInitialized(e)
        ThemeHelper.ApplyDarkTitleBar(Me)
    End Sub

    ''' <summary>
    ''' Constructor para abrir con un activo específico.
    ''' Usar desde el Dashboard: New CandleViewerWindow("EUR/USD", 300)
    ''' </summary>
    Public Sub New(symbol As String, granularity As Integer)
        InitializeComponent()
        _symbol      = symbol
        _granularity = granularity
        Me.Title     = "Pro Trading AI — " & symbol
        InyectarControles()
        ConectarEventos()
        CargarDatosDemo()
    End Sub

    ' ── Inyección de controles ────────────────────────────────────
    Private Sub InyectarControles()
        ' Igual que AdminDashboard inyecta AdminSidebar en SidebarContainer
        _header       = New CandleHeaderControl()
        _chart        = New CandleChartControl()
        _agent        = New CandleAgentControl()
        _transactions = New CandleTransactionsControl()

        HeaderContainer.Content       = _header
        ChartContainer.Content        = _chart
        AgentContainer.Content        = _agent
        TransactionsContainer.Content = _transactions
    End Sub

    ' ── Conexión de eventos entre controles ───────────────────────
    Private Sub ConectarEventos()
        AddHandler _header.TimeframeChanged, AddressOf OnTimeframeChanged
        AddHandler _header.OpenSettingsRequested, AddressOf OnOpenSettingsRequested
    End Sub

    ' ── Evento de apertura de configuración de trading ───────────
    Private Sub OnOpenSettingsRequested()
        Dim modal As New TradingSettingsModal()
        modal.Owner = Me
        If modal.ShowDialog() = True Then
            Dim client    = modal.SelectedClient
            Dim platform  = modal.SelectedPlatform
            Dim accType   = modal.SelectedAccountType
            Dim symbol    = modal.SelectedAssetSymbol
            Dim assetName = modal.SelectedAssetName

            ' Actualizar símbolo activo del visor
            _symbol = symbol
            Me.Title = "Pro Trading AI — " & assetName

            ' Actualizar header del visor
            Dim assetType = If(symbol.StartsWith("BOOM") OrElse symbol.StartsWith("CRASH"), "Boom/Crash",
                            If(symbol.StartsWith("JD"), "Jump Index", "Volatility Index"))
            _header.SetAsset(assetName, assetType)

            ' Log en el panel del agente
            Dim clientLabel = If(client IsNot Nothing, client.Name, "?")
            Dim tipoLabel   = If(accType = "demo", "Demo", "Real")
            _agent.AddSignalLog("SYSTEM",
                String.Format("Sesión → Cliente: {0} | {1} | {2} | {3}",
                              clientLabel, platform.ToUpper(), tipoLabel, symbol))
        End If
    End Sub

    ' ── Datos demo (visualización inicial) ───────────────────────
    Private Sub CargarDatosDemo()
        _header.SetAsset(_symbol, "Crypto")
        _header.SetPrice(90566.0D, 2.45D)
        _agent.SetStatus(True)
        _agent.AddSignalLog("INFO", "Sistema inicializado")
        _agent.AddSignalLog("INFO", "Cargando datos históricos...")

        ' Generar candles de muestra
        _candles = GenerarCandlesDemo()

        ' Normalizar MaxVolume
        Dim maxVol = _candles.Max(Function(c) c.Volume)
        For Each c In _candles
            c.MaxVolume = maxVol
        Next

        _chart.SetCandles(_candles)

        ' Análisis de demo
        ActualizarAgente()
        _agent.AddSignalLog("HOLD", "Esperando confirmación de tendencia")
    End Sub

    ' ── Evento del selector de timeframe ─────────────────────────
    Private Sub OnTimeframeChanged(granularity As Integer)
        _granularity = granularity
        ' Aquí conectarás la descarga real de datos en la siguiente fase
        _agent.AddSignalLog("INFO", "Timeframe cambiado a " & GranularityLabel(granularity))
    End Sub

    ' ── Análisis demo ────────────────────────────────────────────
    Private Sub ActualizarAgente()
        If _candles.Count < 20 Then Return

        ' RSI simplificado
        Dim gains As Double = 0, losses As Double = 0
        For i As Integer = _candles.Count - 14 To _candles.Count - 1
            Dim change As Double = _candles(i).Close - _candles(i).Open
            If change > 0 Then gains += change Else losses -= change
        Next
        Dim rs As Double = gains / If(losses = 0, 1, losses)
        Dim rsi As Double = 100 - (100 / (1 + rs))

        ' SMA 20/50
        Dim sma20 As Double = _candles.Skip(Math.Max(0, _candles.Count - 20)).Average(Function(c) c.Close)
        Dim sma50 As Double = _candles.Skip(Math.Max(0, _candles.Count - 50)).Average(Function(c) c.Close)
        Dim macd  As Double = sma20 - sma50

        ' Decisión
        Dim score As Integer = 0
        If sma20 > sma50 Then score += 20 Else score -= 20
        If rsi < 30 Then
            score += 25
        ElseIf rsi > 70 Then
            score -= 25
        End If

        Dim decision As String = If(score > 25, "COMPRAR",
                                  If(score < -25, "VENDER", "NEUTRO"))
        Dim confidence As Integer = Math.Min(Math.Abs(score) + 55, 95)

        _agent.UpdateDecision(decision, confidence)
        _agent.UpdateIndicators(rsi, macd, sma20 > sma50)
        _agent.UpdateVolume(_candles.Last().Volume)
    End Sub

    ' ── Generador de datos demo ──────────────────────────────────
    Private Function GenerarCandlesDemo() As List(Of CandleBar)
        Dim list As New List(Of CandleBar)()
        Dim rng As New Random(42)
        Dim price As Double = 89000
        Dim t As DateTime = DateTime.UtcNow.AddMinutes(-(_granularity / 60.0) * 80)

        For i As Integer = 0 To 79
            Dim vol As Double = 0.001 * price
            Dim trend As Double = Math.Sin(i / 10.0) * 120

            Dim o As Double = price + (rng.NextDouble() - 0.5) * vol
            Dim c As Double = o + (rng.NextDouble() - 0.5) * vol + trend * 0.15
            Dim h As Double = Math.Max(o, c) + rng.NextDouble() * vol * 0.4
            Dim l As Double = Math.Min(o, c) - rng.NextDouble() * vol * 0.4
            Dim v As Double = rng.NextDouble() * 900000 + 100000

            list.Add(New CandleBar() With {
                .Time   = t,
                .Open   = Math.Round(o, 2),
                .High   = Math.Round(h, 2),
                .Low    = Math.Round(l, 2),
                .Close  = Math.Round(c, 2),
                .Volume = Math.Round(v, 0)
            })

            price = c
            t     = t.AddSeconds(_granularity)
        Next

        Return list
    End Function

    ' ── Helpers ──────────────────────────────────────────────────
    Private Function GranularityLabel(g As Integer) As String
        Select Case g
            Case 60     : Return "1m"
            Case 300    : Return "5m"
            Case 900    : Return "15m"
            Case 3600   : Return "1h"
            Case 14400  : Return "4h"
            Case 86400  : Return "1d"
            Case Else   : Return g & "s"
        End Select
    End Function

    ' ── Cleanup ──────────────────────────────────────────────────
    Private Sub CandleViewerWindow_Closing(sender As Object,
                                            e As System.ComponentModel.CancelEventArgs) Handles Me.Closing
        If _header IsNot Nothing Then _header.StopClock()
    End Sub

End Class
