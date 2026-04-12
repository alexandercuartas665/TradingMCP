' ============================================================
' MainWindow.Streaming.vb
' Contenido: Tab "Gráfico" (configuración de colores) +
'            Tab "Streaming" (conexión WebSocket pública,
'            bucle de escucha, procesamiento de velas).
' NO toca Trading Live ni descarga histórica.
' ============================================================
Imports System.Net.WebSockets
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
        '  TAB STREAMING - Conexion WebSocket
        ' ===================================================
        Private Async Sub btnConnect_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            btnConnect.IsEnabled = False
            btnDisconnect.IsEnabled = True
            txtSymbol.IsEnabled = False
            cmbTimeframe.IsEnabled = False

            Dim symbol As String = txtSymbol.Text.Trim()
            Dim tfItem As System.Windows.Controls.ComboBoxItem = CType(cmbTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim timeframeStr As String = tfItem.Content.ToString()
            Dim granularity As Integer = TIMEFRAME_MAP(timeframeStr)

            _currentSymbol = symbol
            _currentTimeframe = timeframeStr
            _candles.Clear()
            _currentOpenEpoch = 0
            txtStatus.Text = String.Format("Conectando a {0} ({1})...", symbol, timeframeStr)

            If _cancellationTokenSource IsNot Nothing Then _cancellationTokenSource.Cancel()
            _cancellationTokenSource = New CancellationTokenSource()

            Try
                Await ConectarStreamingWsAsync(symbol, granularity)
#Disable Warning BC42358
                Task.Run(Function() As Task
                             Return ListenLoop()
                         End Function)
#Enable Warning BC42358
            Catch ex As Exception
                System.Windows.MessageBox.Show("Error de conexion: " & ex.Message)
                ResetUI()
            End Try
        End Sub

        ''' <summary>Descarta el WebSocket viejo y crea uno nuevo con keepalive y suscripción de velas.</summary>
        Private Async Function ConectarStreamingWsAsync(symbol As String, granularity As Integer) As Task
            ' Cerrar y descartar el WebSocket anterior limpiamente
            If _webSocket IsNot Nothing Then
                Try
                    If _webSocket.State = WebSocketState.Open Then
                        Await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconectando", CancellationToken.None)
                    End If
                Catch
                End Try
                Try
                    _webSocket.Dispose()
                Catch
                End Try
                _webSocket = Nothing
            End If

            _webSocket = New ClientWebSocket()
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15)
            Await _webSocket.ConnectAsync(New Uri(_apiUri), _cancellationTokenSource.Token)

            Dispatcher.Invoke(Sub() txtStatus.Text = "Conectado. Solicitando historial...")
            Dim reqBytes = Encoding.UTF8.GetBytes(BuildRequestJson(symbol, granularity))
            Await _webSocket.SendAsync(New ArraySegment(Of Byte)(reqBytes), WebSocketMessageType.Text, True, _cancellationTokenSource.Token)
        End Function

        Private Async Sub btnDisconnect_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _cancellationTokenSource IsNot Nothing Then _cancellationTokenSource.Cancel()
            If _webSocket IsNot Nothing AndAlso _webSocket.State = WebSocketState.Open Then
                Try
                    Await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "OK", CancellationToken.None)
                Catch
                End Try
            End If
            Dispatcher.Invoke(AddressOf ResetUI)
        End Sub

        Private Sub ResetUI()
            btnConnect.IsEnabled = True
            btnDisconnect.IsEnabled = False
            txtSymbol.IsEnabled = True
            cmbTimeframe.IsEnabled = True
            txtStatus.Text = "Desconectado"
        End Sub

        Private Shared Function BuildRequestJson(symbol As String, granularity As Integer) As String
            Dim json As New JObject()
            json.Add("ticks_history", New JValue(symbol))
            json.Add("adjust_start_time", New JValue(1))
            json.Add("count", New JValue(50))
            json.Add("style", New JValue("candles"))
            json.Add("end", New JValue("latest"))
            json.Add("granularity", New JValue(granularity))
            json.Add("subscribe", New JValue(1))
            Return json.ToString()
        End Function

        ' ===================================================
        '  BUCLE DE ESCUCHA STREAMING CON RECONEXION
        ' ===================================================
        Private Async Function ListenLoop() As Task
            Dim buffer(16383) As Byte
            Dim intentos As Integer = 0

            Do While Not _cancellationTokenSource.IsCancellationRequested

                ' ── Recepción de mensajes ──
                Dim errorCritico As Boolean = False
                Try
                    Do While _webSocket.State = WebSocketState.Open AndAlso Not _cancellationTokenSource.IsCancellationRequested
                        Dim sb As New StringBuilder()
                        Dim result As WebSocketReceiveResult
                        Do
                            result = Await _webSocket.ReceiveAsync(New ArraySegment(Of Byte)(buffer), _cancellationTokenSource.Token)
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count))
                        Loop While Not result.EndOfMessage

                        If result.MessageType = WebSocketMessageType.Close Then Exit Do

                        intentos = 0
                        Dim data As JObject = JObject.Parse(sb.ToString())

                        If data.ContainsKey("error") Then
                            Dim errMsg As String = data("error")("message").ToString()
                            Dim errCode As String = If(data("error")("code") IsNot Nothing, data("error")("code").ToString(), "")
                            ' Errores críticos: no tiene sentido reconectar (símbolo inválido, mercado cerrado)
                            If errCode = "InvalidSymbol" OrElse errCode = "MarketIsClosed" Then
                                Dispatcher.Invoke(Sub()
                                                      System.Windows.MessageBox.Show("Error API: " & errMsg, "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning)
                                                      ResetUI()
                                                  End Sub)
                                errorCritico = True
                                Exit Do
                            End If
                        End If

                        If data.ContainsKey("candles") Then
                            Dispatcher.Invoke(Sub() HandleHistoricalCandles(CType(data("candles"), JArray)))
                        ElseIf data.ContainsKey("ohlc") Then
                            Dispatcher.Invoke(Sub() HandleLiveTick(CType(data("ohlc"), JObject)))
                        End If
                    Loop
                Catch ex As OperationCanceledException
                    Exit Do   ' usuario presionó Detener — salir limpio
                Catch ex As Exception
                    ' Red caída o servidor cerró sin handshake — reconectar
                    If Not _cancellationTokenSource.IsCancellationRequested Then
                        Dispatcher.Invoke(Sub()
                                              txtStatus.Text = String.Format("⚠ Streaming caído — reconectando...")
                                          End Sub)
                    End If
                End Try

                ' Error crítico o cancelación del usuario = no reconectar
                If errorCritico OrElse _cancellationTokenSource.IsCancellationRequested Then Exit Do

                ' ── Reconexión automática ──
                intentos += 1
                Dim esperaSegs As Integer = If(intentos <= 3, 3, 15)

                Dispatcher.Invoke(Sub()
                                      txtStatus.Text = String.Format("⚠ Reconectando streaming en {0}s... (#{1})", esperaSegs, intentos)
                                  End Sub)

                Await Task.Delay(esperaSegs * 1000, _cancellationTokenSource.Token)
                If _cancellationTokenSource.IsCancellationRequested Then Exit Do

                Dim errReconexion As String = Nothing
                Try
                    Dim gran As Integer = If(TIMEFRAME_MAP.ContainsKey(_currentTimeframe), TIMEFRAME_MAP(_currentTimeframe), 60)
                    Await ConectarStreamingWsAsync(_currentSymbol, gran)
                    intentos = 0
                    Dispatcher.Invoke(Sub()
                                          txtStatus.Text = String.Format("♻ Reconectado: {0} ({1})", _currentSymbol, _currentTimeframe)
                                      End Sub)
                Catch ex As Exception
                    errReconexion = ex.Message
                End Try

                If errReconexion IsNot Nothing Then
                    Dispatcher.Invoke(Sub()
                                          txtStatus.Text = String.Format("❌ Fallo reconexión #{0}: {1}", intentos, errReconexion)
                                      End Sub)
                    Await Task.Delay(5000, _cancellationTokenSource.Token)
                End If
            Loop
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
            End If
        End Sub

    End Class
End Namespace
