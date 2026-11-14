' ============================================================
' MainWindow.Streaming.vb
' Contenido: Tab "Gráfico" (configuración de colores) +
'            Tab "Streaming" (velas en vivo por la conexion
'            autenticada de DerivTradingService).
' NO toca Trading Live ni descarga histórica.
' ============================================================
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

    Partial Public Class MainWindow

        ' ===================================================
        '  TAB GRAFICO - Configuracion de Colores
        ' ===================================================
        Private Sub ApplySettingsToUI()
            _suppressColorChange = True
            txtBullColor.Text = _settings.BullishColor
            txtBearColor.Text = _settings.BearishColor
            txtWickColor.Text = _settings.WickColor
            txtBgColor.Text = _settings.BackgroundColor
            UpdateColorPreviews()
            _suppressColorChange = False
        End Sub

        Private Sub UpdateColorPreviews()
            SetPreviewColor(previewBull, txtBullColor.Text)
            SetPreviewColor(previewBear, txtBearColor.Text)
            SetPreviewColor(previewWick, txtWickColor.Text)
        End Sub

        Private Shared Sub SetPreviewColor(border As System.Windows.Controls.Border, hex As String)
            Try
                Dim c As Color = CType(ColorConverter.ConvertFromString(hex), Color)
                border.Background = New SolidColorBrush(c)
            Catch
                border.Background = New SolidColorBrush(Colors.Gray)
            End Try
        End Sub

        Private Sub ColorTextBox_Changed(sender As Object, e As System.Windows.Controls.TextChangedEventArgs)
            If _suppressColorChange Then Return
            UpdateColorPreviews()
        End Sub

        Private Sub btnSaveColors_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            _settings.BullishColor = txtBullColor.Text.Trim()
            _settings.BearishColor = txtBearColor.Text.Trim()
            _settings.WickColor = txtWickColor.Text.Trim()
            _settings.BackgroundColor = txtBgColor.Text.Trim()
            Dim all = SettingsManager.Load()
            all.Chart = _settings
            SettingsManager.Save(all)
            _chartCanvas.SetData(_candles, _settings)
            System.Windows.MessageBox.Show("Configuracion guardada.", "OK", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information)
        End Sub

        Private Sub ChartHost_SizeChanged(sender As Object, e As System.Windows.SizeChangedEventArgs)
            _chartCanvas.Redraw()
        End Sub

        Private Sub SyncChartSize(sender As Object, e As System.Windows.SizeChangedEventArgs)
            _chartCanvas.Width = chartHost.ActualWidth
            _chartCanvas.Height = chartHost.ActualHeight
            _chartCanvas.Redraw()
        End Sub

        ' ===================================================
        '  TAB STREAMING - Velas en vivo por la conexion autenticada
        '  (la API publica antigua de Deriv ya no existe). Requiere
        '  la cuenta conectada; al reconectar se re-suscribe sola.
        ' ===================================================
        Private _streamSubId As String = Nothing
        Private _streamActivo As Boolean = False
        Private _streamEventoCableado As Boolean = False

        Private Async Sub btnConnect_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim symbol As String = txtSymbol.Text.Trim()
            Dim tfItem As System.Windows.Controls.ComboBoxItem = CType(cmbTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim timeframeStr As String = tfItem.Content.ToString()
            Await IniciarStreamingAsync(symbol, timeframeStr, mostrarErrores:=True)
        End Sub

        ''' <summary>Suscribe el grafico a (symbol, timeframe). Devuelve True si quedo en vivo.</summary>
        Private Async Function IniciarStreamingAsync(symbol As String, timeframe As String, mostrarErrores As Boolean) As Task(Of Boolean)
            If Not TIMEFRAME_MAP.ContainsKey(timeframe) Then Return False
            CablearEventoStreaming()
            Await DetenerStreamingAsync()

            btnConnect.IsEnabled = False
            btnDisconnect.IsEnabled = True
            txtSymbol.IsEnabled = False
            cmbTimeframe.IsEnabled = False
            _currentSymbol = symbol
            _currentTimeframe = timeframe
            _candles.Clear()
            _currentOpenEpoch = 0
            _streamActivo = True

            Dim svc = Servidor.DerivTradingService.Instance
            If Not svc.EstaConectado Then
                _streamActivo = False
                ResetUI()
                txtStatus.Text = "Sin conexion a Deriv: conecta la cuenta en la pestana Trading."
                If mostrarErrores Then
                    System.Windows.MessageBox.Show("Conecta primero la cuenta (Demo o Real) en la pestana Trading: las velas llegan por esa conexion.",
                                                   "Streaming", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information)
                End If
                Return False
            End If

            txtStatus.Text = String.Format("Conectando a {0} ({1})...", symbol, timeframe)
            Dim r = Await svc.SuscribirVelasAsync(symbol, TIMEFRAME_MAP(timeframe), 50,
                Sub(ohlc) Dispatcher.BeginInvoke(Sub() HandleLiveTick(ohlc), System.Windows.Threading.DispatcherPriority.Background))
            If r Is Nothing OrElse r("ok") Is Nothing OrElse Not CBool(r("ok")) Then
                Dim err = If(r?("error")?.ToString(), "sin respuesta")
                _streamActivo = False
                ResetUI()
                txtStatus.Text = "Error de streaming: " & err
                If mostrarErrores Then
                    System.Windows.MessageBox.Show("Error API: " & err, "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning)
                End If
                Return False
            End If
            _streamSubId = r("subscription")?("id")?.ToString()
            Dim velas = TryCast(r("candles"), JArray)
            If velas IsNot Nothing Then HandleHistoricalCandles(velas)
            Return True
        End Function

        ''' <summary>Cancela la suscripcion actual del grafico (si hay).</summary>
        Private Async Function DetenerStreamingAsync() As Task
            Dim id = _streamSubId
            _streamSubId = Nothing
            If id IsNot Nothing Then Await Servidor.DerivTradingService.Instance.CancelarSuscripcionVelasAsync(id)
        End Function

        ''' <summary>Al reconectar el WebSocket de trading las suscripciones se pierden: re-suscribir.</summary>
        Private Sub CablearEventoStreaming()
            If _streamEventoCableado Then Return
            _streamEventoCableado = True
            AddHandler Servidor.DerivTradingService.Instance.SuscripcionesVelasPerdidas, AddressOf OnSuscripcionesVelasPerdidas
        End Sub

        Private Sub DescablearEventoStreaming()
            If Not _streamEventoCableado Then Return
            _streamEventoCableado = False
            RemoveHandler Servidor.DerivTradingService.Instance.SuscripcionesVelasPerdidas, AddressOf OnSuscripcionesVelasPerdidas
        End Sub

        Private Sub OnSuscripcionesVelasPerdidas()
            Dispatcher.BeginInvoke(New Action(AddressOf ReintentarStreamingTrasPerdida))
        End Sub

        Private Async Sub ReintentarStreamingTrasPerdida()
            If Not _streamActivo Then Return
            _streamSubId = Nothing
            If Servidor.DerivTradingService.Instance.EstaConectado Then
                txtStatus.Text = "Reconectado: recuperando velas..."
                Await IniciarStreamingAsync(_currentSymbol, _currentTimeframe, mostrarErrores:=False)
            Else
                txtStatus.Text = "Conexion a Deriv perdida: esperando reconexion..."
            End If
        End Sub

        Private Async Sub btnDisconnect_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            _streamActivo = False
            Await DetenerStreamingAsync()
            ResetUI()
        End Sub

        Private Sub ResetUI()
            btnConnect.IsEnabled = True
            btnDisconnect.IsEnabled = False
            txtSymbol.IsEnabled = True
            cmbTimeframe.IsEnabled = True
            txtStatus.Text = "Desconectado"
        End Sub

        ''' <summary>
        ''' Asegura que el streaming superior este en el symbol/timeframe indicados.
        ''' Si ya esta sincronizado, no hace nada. Usado por el modo Live de Estrategias
        ''' Dinamicas para que el grafico muestre la estrategia en ejecucion.
        ''' </summary>
        Public Async Function SincronizarStreamingAsync(symbol As String, timeframe As String) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(symbol) OrElse String.IsNullOrWhiteSpace(timeframe) Then Return False
            If Not TIMEFRAME_MAP.ContainsKey(timeframe) Then Return False
            If _streamActivo AndAlso _streamSubId IsNot Nothing AndAlso _currentSymbol = symbol AndAlso _currentTimeframe = timeframe Then Return True

            ' Ajustar controles superiores
            txtSymbol.Text = symbol
            For i As Integer = 0 To cmbTimeframe.Items.Count - 1
                Dim ci = TryCast(cmbTimeframe.Items(i), System.Windows.Controls.ComboBoxItem)
                If ci IsNot Nothing AndAlso ci.Content?.ToString() = timeframe Then
                    cmbTimeframe.SelectedIndex = i
                    Exit For
                End If
            Next
            Return Await IniciarStreamingAsync(symbol, timeframe, mostrarErrores:=False)
        End Function

        ' ===================================================
        '  PROCESAR DATOS DE STREAMING
        ' ===================================================
        Private Sub HandleHistoricalCandles(arr As JArray)
            Dim total As Integer = arr.Count
            If total = 0 Then Return

            Dim idx As Integer = 0
            Do While idx <= total - 2
                Dim item As JObject = CType(arr(idx), JObject)
                Dim m As New CandleModel()
                m.Epoch = CLng(item("epoch"))
                m.OpenTime = EpochToString(m.Epoch)
                m.OpenPrice = CDec(item("open"))
                m.High = CDec(item("high"))
                m.Low = CDec(item("low"))
                m.ClosePrice = CDec(item("close"))
                m.Status = "[Cerrada]"
                _candles.Add(m)
                idx += 1
            Loop

            Dim last As JObject = CType(arr(total - 1), JObject)
            Dim live As New CandleModel()
            live.Epoch = CLng(last("epoch"))
            live.OpenTime = EpochToString(live.Epoch)
            live.OpenPrice = CDec(last("open"))
            live.High = CDec(last("high"))
            live.Low = CDec(last("low"))
            live.ClosePrice = CDec(last("close"))
            live.Status = "Actualizandose..."
            _candles.Add(live)
            _currentOpenEpoch = live.Epoch

            txtStatus.Text = String.Format("En vivo: {0} ({1})", _currentSymbol, _currentTimeframe)
            RefreshChart()
            GoToBottom()

            Dim closedCandles As New List(Of CandleModel)(_candles.Take(_candles.Count - 1))
            Dim sym As String = _currentSymbol
            Dim tf As String = _currentTimeframe
            Task.Run(Function() As Task
                         Return _repo.SaveBatchAsync(PLATFORM, sym, tf, closedCandles, isClosed:=True)
                     End Function)
        End Sub

        Private Sub HandleLiveTick(ohlcObj As JObject)
            Dim tickEpoch As Long = CLng(ohlcObj("open_time"))
            Dim isNewCandle As Boolean = (tickEpoch <> _currentOpenEpoch)

            If isNewCandle Then
                If _candles.Count > 0 Then
                    _candles(_candles.Count - 1).Status = "[Cerrada]"
                End If
                Dim m As New CandleModel()
                m.Epoch = tickEpoch
                m.OpenTime = EpochToString(tickEpoch)
                m.OpenPrice = CDec(ohlcObj("open"))
                m.High = CDec(ohlcObj("high"))
                m.Low = CDec(ohlcObj("low"))
                m.ClosePrice = CDec(ohlcObj("close"))
                m.Status = "Actualizandose..."
                _candles.Add(m)
                _currentOpenEpoch = tickEpoch
                If _candles.Count > 60 Then _candles.RemoveAt(0)
            Else
                If _candles.Count > 0 Then
                    Dim liveCandle As CandleModel = _candles(_candles.Count - 1)
                    liveCandle.High = CDec(ohlcObj("high"))
                    liveCandle.Low = CDec(ohlcObj("low"))
                    liveCandle.ClosePrice = CDec(ohlcObj("close"))
                End If
            End If

            RefreshChart()
            GoToBottom()

            If isNewCandle AndAlso _candles.Count >= 2 Then
                Dim closedCandle As CandleModel = _candles(_candles.Count - 2)
                Dim sym As String = _currentSymbol
                Dim tf As String = _currentTimeframe
                Dim snap As CandleModel = closedCandle
                Task.Run(Function() As Task
                             Return _repo.SaveCandleAsync(PLATFORM, sym, tf, snap, isClosed:=True)
                         End Function)
                ' Notificar a TODOS los motores live registrados (cada uno filtra por su symbol/tf)
                ' RaiseEvent envia la cadena al dispatcher, que internamente usa Task.Run por motor.
                ' El dispatcher en si es liviano (snapshot + queue), no bloquea la UI.
                RaiseEvent OnCandleCerrada(snap, sym, tf)
            End If
        End Sub

    End Class
End Namespace
